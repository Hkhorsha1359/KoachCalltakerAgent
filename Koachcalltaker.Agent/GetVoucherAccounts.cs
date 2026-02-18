using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KoachCallTake;

/// <summary>
/// Koach Voucher endpoint: GetAccounts
///
/// MODULAR DESIGN:
/// - This is a single-purpose method file.
/// - It adds GetVoucherAccountsAsync(...) onto KoachTripClient as an extension method,
///   matching your existing modular pattern (one API call per file).
///
/// Endpoint:
///   GET /Api/Voucher/GetAccounts
/// Full path:
///   https://apicall.koachapp.com/Api/Voucher/GetAccounts
/// </summary>
public static class GetVoucherAccounts
{
    /// <summary>
    /// DTO for each voucher account returned by Koach.
    /// Per your notes:
    /// - Id: internal account ID (used as "Vaid" on reservation update)
    /// - Company: client's name
    /// - Abbreviation: shows in reservation dropdown
    /// </summary>
    public sealed class VoucherAccount
    {
        public string Id { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result wrapper for voucher accounts lookup.
    /// RawJson retained for debugging while schema is being finalized.
    /// </summary>
    public sealed class KoachVoucherAccountsResult
    {
        public bool Success { get; set; }

        // Correlation/debug
        public string TenantId { get; set; } = string.Empty;

        // Full raw response from Koach API (for debugging)
        public string RawJson { get; set; } = string.Empty;

        // Parsed accounts (best-effort)
        public List<VoucherAccount> Accounts { get; set; } = new();

        // Safe error message (no JWT, no password)
        public string? Error { get; set; }
    }

    /// <summary>
    /// Calls Koach voucher accounts endpoint.
    ///
    /// We keep this as GET only, matching the endpoint design.
    /// Relative URL used:
    ///   Api/Voucher/GetAccounts
    ///
    /// NOTE:
    /// - Requires JWT, which is acquired/cached by KoachApiService.
    /// - Uses KoachTripClient.CreateAuthedClientAsync(...) so this file doesn't touch JWT internals.
    /// </summary>
    public static async Task<KoachVoucherAccountsResult> GetVoucherAccountsAsync(
        this KoachTripClient tripClient,
        string? baseUrl,
        string tenant,
        string username,
        CancellationToken ct = default)
    {
        // Comment: Normalize values so we never throw NullReferenceException.
        var safeTenant = tenant ?? string.Empty;

        var result = new KoachVoucherAccountsResult
        {
            Success = false,
            TenantId = safeTenant
        };

        // Comment: Tenant is required to authenticate and route correctly in Koach API.
        if (string.IsNullOrWhiteSpace(safeTenant))
        {
            result.Error = "Tenant is missing/empty; voucher accounts lookup skipped.";
            return result;
        }

        // Comment: Acquire authenticated client (JWT is handled inside KoachApiService).
        HttpClient client;
        try
        {
            client = await tripClient.CreateAuthedClientAsync(baseUrl, safeTenant, username, ct);
        }
        catch (Exception ex)
        {
            result.Error = $"JWT acquisition failed: {ex.Message}";
            return result;
        }

        // Comment: Endpoint is fixed. No query params.
        var relativeUrl = "Api/Voucher/GetAccounts";

        HttpResponseMessage resp;
        string body;

        try
        {
            resp = await client.GetAsync(relativeUrl, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            result.Error = $"Koach API call failed (network/TLS/etc): {ex.Message}";
            return result;
        }

        if (!resp.IsSuccessStatusCode)
        {
            result.Error = $"Koach voucher accounts lookup failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={body}";
            return result;
        }

        // Comment: Save raw payload for debugging until we lock the schema.
        result.RawJson = body;

        // Comment: Parse accounts list (best-effort and tolerant to schema changes).
        TryParseAccounts(body, result);

        // Comment: Success means the HTTP call succeeded; Accounts may still be empty.
        result.Success = true;

        return result;
    }

    // -------------------- Parsing Helpers --------------------

    /// <summary>
    /// Best-effort JSON parsing helper for voucher accounts response.
    /// Handles common shapes:
    /// - Array of objects: [ { ... }, { ... } ]
    /// - Wrapped object: { data: [ ... ] } or { result: [ ... ] }
    /// </summary>
    private static void TryParseAccounts(string rawJson, KoachVoucherAccountsResult result)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Comment: Unwrap if payload is nested under "data" or "result".
            JsonElement accountsElement = root;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data))
                {
                    accountsElement = data;
                }
                else if (root.TryGetProperty("result", out var res))
                {
                    accountsElement = res;
                }
            }

            // Comment: Expect an array of accounts.
            if (accountsElement.ValueKind != JsonValueKind.Array)
            {
                // Comment: Schema not as expected; keep RawJson for debugging.
                return;
            }

            var list = new List<VoucherAccount>();

            foreach (var item in accountsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                // Comment: Accept common name variants to be resilient.
                // We prefer "ID" but allow "Id"/"id" etc.
                var id = TryGetStringAny(item, "ID", "Id", "id");
                var company = TryGetStringAny(item, "Company", "company", "Client", "client", "Name", "name");
                var abbr = TryGetStringAny(item, "Abbreviation", "abbreviation", "Abbrev", "abbrev", "Short", "short");

                // Comment: Only add if it has a meaningful ID or abbreviation.
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(abbr))
                    continue;

                list.Add(new VoucherAccount
                {
                    Id = id ?? string.Empty,
                    Company = company ?? string.Empty,
                    Abbreviation = abbr ?? string.Empty
                });
            }

            result.Accounts = list;
        }
        catch
        {
            // Comment: Ignore parse errors; RawJson is retained in result.
        }
    }

    /// <summary>
    /// Attempts to read a string (or number) from one of several possible property names.
    /// Returns the first non-empty value found.
    /// </summary>
    private static string? TryGetStringAny(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var prop))
                continue;

            // Comment: If the value is already a string, return it.
            if (prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }

            // Comment: If the value is numeric, convert to string.
            if (prop.ValueKind == JsonValueKind.Number)
            {
                var s = prop.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }

            // Comment: For other kinds (bool/object/etc.), ToString() can still be informative.
            if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined)
            {
                var s = prop.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }
}
