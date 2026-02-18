using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace KoachCallTake
{
    /// <summary>
    /// KoachApiService handles:
    ///  1) Authenticating to the Koach Dispatch API (/login/authenticate or /api/login/authenticate)
    ///  2) Caching JWTs per (tenant + username) with a TTL to avoid re-auth on every request
    ///  3) Creating an HttpClient pre-configured with Authorization: Bearer <jwt>
    ///
    /// IMPORTANT:
    ///  - The auth request contract is case-sensitive in some backends.
    ///  - Your working example proves the API expects: Username, Password, TenantId (exact names).
    /// </summary>
    public sealed class KoachApiService
    {
        // Comment: Default API base (if config is missing). Keep it consistent across the app.
        private const string DefaultApiBaseUrl = "https://apicall.koachapp.com";

        // Comment: Cache entry stores the JWT and when it should be considered expired.
        private sealed class JwtCacheEntry
        {
            public string Jwt { get; init; } = string.Empty;

            // Comment: UTC expiration moment (soft TTL). We refresh before or at expiry.
            public DateTimeOffset ExpiresAtUtc { get; init; }
        }

        // Comment: This payload MUST match the Koach API auth contract exactly.
        // Based on your screenshot (working auth):
        // {
        //   "Username": "...",
        //   "Password": "...",
        //   "TenantId": "Koach"
        // }
        private sealed class KoachAuthRequest
        {
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string TenantId { get; set; } = string.Empty;
        }

        // Comment: Cache keyed by "tenant::username" (normalized to lower) so we can reuse tokens safely.
        private readonly ConcurrentDictionary<string, JwtCacheEntry> _jwtCache = new();

        // Comment: Shared password is typically stored in User Secrets / environment variables.
        private readonly string _agentSharedPassword;

        // Comment: Default TTL. Koach tokens may last longer, but we keep a safe refresh window.
        private readonly TimeSpan _jwtTtl;

        public KoachApiService(IConfiguration configuration)
        {
            // Comment: Load the shared password from configuration (User Secrets/env).
            _agentSharedPassword = configuration["Koach:AgentSharedPassword"] ?? string.Empty;

            // Comment: Optional override for TTL (minutes). If not set, default to 20 minutes.
            // This is a *soft TTL* we control for refresh cadence.
            var ttlMinutesStr = configuration["Koach:JwtCacheTtlMinutes"];
            if (int.TryParse(ttlMinutesStr, out var ttlMinutes) && ttlMinutes > 0)
            {
                _jwtTtl = TimeSpan.FromMinutes(ttlMinutes);
            }
            else
            {
                _jwtTtl = TimeSpan.FromMinutes(20);
            }
        }

        /// <summary>
        /// Creates an HttpClient configured with Authorization: Bearer <jwt>
        /// so callers can immediately call Koach endpoints.
        /// </summary>
        public async Task<HttpClient> CreateAuthedClientAsync(string? baseUrl, string tenant, string username, CancellationToken ct = default)
        {
            // Comment: Acquire a JWT (cached or fresh) first.
            var jwt = await GetJwtAsync(baseUrl, tenant, username, ct);

            // Comment: Create a client for the caller (you can reuse HttpClientFactory later if you want).
            var client = new HttpClient
            {
                BaseAddress = new Uri(NormalizeBaseUrl(baseUrl))
            };

            // Comment: Attach the JWT bearer header.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

            return client;
        }

        /// <summary>
        /// Gets a JWT for (tenant + username). Uses cache if not expired; otherwise authenticates.
        /// </summary>
        public async Task<string> GetJwtAsync(string? baseUrl, string tenant, string username, CancellationToken ct = default)
        {
            // =========================
            // Guardrails (fail fast)
            // =========================

            var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);

            if (string.IsNullOrWhiteSpace(tenant))
            {
                // Comment: Tenant is required; without it the API will reject authentication.
                throw new InvalidOperationException("Koach API auth: tenant is missing/empty.");
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                // Comment: Username (email) is required; missing username will always fail.
                throw new InvalidOperationException("Koach API auth: username is missing/empty.");
            }

            if (string.IsNullOrWhiteSpace(_agentSharedPassword))
            {
                // Comment: Shared password must be provided from secrets/env; otherwise auth will 401.
                throw new InvalidOperationException("Koach API auth: Koach:AgentSharedPassword is missing/empty.");
            }

            // =========================
            // Normalize tenant values
            // =========================

            // Comment: Your working example uses "Koach" (capital K).
            // Normalize internal configs like "koach" -> "Koach".
            // For DOB, we keep "DOB" (already uppercase).
            var normalizedTenant = NormalizeTenantForApi(tenant);

            // Comment: Cache key should be stable and case-insensitive.
            var cacheKey = $"{normalizedTenant}::{username}".ToLowerInvariant();

            // =========================
            // Cache lookup
            // =========================

            if (_jwtCache.TryGetValue(cacheKey, out var entry))
            {
                // Comment: If token still within TTL, use it.
                // We consider it valid if "now < ExpiresAtUtc".
                if (DateTimeOffset.UtcNow < entry.ExpiresAtUtc)
                {
                    return entry.Jwt;
                }
            }

            // =========================
            // Authenticate (fresh)
            // =========================

            // Comment: Build the auth URL carefully.
            // Some deployments may have baseUrl ending with "/api".
            // Our goal is to hit either:
            //   {base}/api/login/authenticate  OR  {base}/login/authenticate
            // depending on whether base already includes "/api".
            var authUrl = BuildAuthUrl(normalizedBaseUrl);

            using var http = new HttpClient();

            // Comment: IMPORTANT — payload names must match exactly: Username, Password, TenantId
            var payload = new KoachAuthRequest
            {
                Username = username,
                Password = _agentSharedPassword,
                TenantId = normalizedTenant
            };

            HttpResponseMessage resp;
            try
            {
                resp = await http.PostAsJsonAsync(authUrl, payload, ct);
            }
            catch (Exception ex)
            {
                // Comment: Network/DNS/TLS issues show here (not 401).
                throw new Exception($"Koach auth call failed to reach endpoint. authUrl='{authUrl}'. Error={ex.Message}", ex);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Comment: Do NOT log passwords or tokens. tenant+username+url are safe for debugging.
                throw new Exception(
                    $"Koach auth failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                    $"tenant='{normalizedTenant}', username='{username}', authUrl='{authUrl}'. " +
                    $"Body={body}"
                );
            }

            // =========================
            // Extract JWT
            // =========================

            // Comment: Koach may return:
            //   1) a raw JWT string (your screenshot shows this style), OR
            //   2) a JSON object containing token/jwt/access_token fields.
            var jwt = ExtractJwt(body);

            if (string.IsNullOrWhiteSpace(jwt))
            {
                // Comment: If we cannot extract a JWT, treat that as a hard failure.
                throw new Exception("Koach auth succeeded but token extraction returned empty.");
            }

            // =========================
            // Save in cache with TTL
            // =========================

            var newEntry = new JwtCacheEntry
            {
                Jwt = jwt,
                ExpiresAtUtc = DateTimeOffset.UtcNow.Add(_jwtTtl)
            };

            _jwtCache[cacheKey] = newEntry;

            return jwt;
        }

        // ============================================================
        // Helpers
        // ============================================================

        /// <summary>
        /// Normalizes baseUrl and guarantees a non-empty absolute URL string.
        /// </summary>
        private static string NormalizeBaseUrl(string? baseUrl)
        {
            // Comment: Prefer configured baseUrl, otherwise fall back to default.
            var normalized = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultApiBaseUrl : baseUrl).Trim();

            // Comment: Remove trailing slashes to simplify downstream URL building.
            normalized = normalized.TrimEnd('/');

            return normalized;
        }

        /// <summary>
        /// Normalizes tenant for API expectations (e.g., "koach" -> "Koach").
        /// </summary>
        private static string NormalizeTenantForApi(string tenant)
        {
            if (string.Equals(tenant, "koach", StringComparison.OrdinalIgnoreCase))
            {
                return "Koach";
            }

            // Comment: Leave DOB or other tenants as provided (often case-sensitive).
            return tenant;
        }

        /// <summary>
        /// Builds the correct auth endpoint URL. If base ends with "/api", use "/login/authenticate".
        /// Otherwise use "/api/login/authenticate".
        /// </summary>
        private static string BuildAuthUrl(string normalizedBaseUrl)
        {
            // Comment: If base already ends with "/api", don't append another "/api".
            if (normalizedBaseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                return $"{normalizedBaseUrl}/login/authenticate";
            }

            return $"{normalizedBaseUrl}/api/login/authenticate";
        }

        /// <summary>
        /// Extracts a JWT token from either a raw string response or JSON response.
        /// </summary>
        private static string ExtractJwt(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            // Comment: Many APIs return the JWT as a raw string, sometimes JSON-quoted.
            // Example:
            //   "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...."
            // or without quotes:
            //   eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9....
            var candidate = raw.Trim().Trim('"');

            // Comment: A JWT should have at least two dots (three segments).
            if (candidate.Count(c => c == '.') >= 2)
            {
                return candidate;
            }

            // Comment: Otherwise try parse JSON and look for common token fields.
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                // Comment: Support a few common names.
                if (TryGetString(root, "token", out var token)) return token;
                if (TryGetString(root, "jwt", out token)) return token;
                if (TryGetString(root, "access_token", out token)) return token;

                // Comment: Some APIs nest tokens; add more paths here if needed.
            }
            catch
            {
                // Comment: If JSON parsing fails, we already tried raw token path above.
            }

            return string.Empty;
        }

        /// <summary>
        /// Helper to safely fetch a string property from a JsonElement.
        /// </summary>
        private static bool TryGetString(JsonElement root, string propertyName, out string value)
        {
            value = string.Empty;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty(propertyName, out var prop))
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }

            return false;
        }
    }
}
