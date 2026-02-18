using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KoachCallTake;

/// <summary>
/// Koach Trip endpoint: GetLastReservationStatusByPhone
///
/// MODULAR DESIGN:
/// - This is an extension method file.
/// - It adds GetLastReservationStatusByPhoneAsync(...) onto KoachTripClient.
/// - Parsing helpers are kept local to this file.
/// </summary>
public static class GetLastReservationStatusByPhone
{
    /// <summary>
    /// Result wrapper for reservation-status lookup by phone.
    /// RawJson is retained for debugging until schema is finalized.
    /// </summary>
    public sealed class KoachLastReservationStatusResult
    {
        public bool Success { get; set; }

        // Correlation/debug
        public string TenantId { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;

        // Full raw response from Koach API
        public string RawJson { get; set; } = string.Empty;

        // Best-effort extracted fields
        public string? ReservationId { get; set; }
        public string? Status { get; set; }
        public string? Pickup { get; set; }
        public string? Dropoff { get; set; }
        public string? ScheduledTime { get; set; }

        // Safe error message (no JWT, no password)
        public string? Error { get; set; }
    }

    /// <summary>
    /// Attempts to call Koach "last reservation status by phone" endpoint using GET only.
    ///
    /// Endpoint (your current working pattern):
    ///   Api/Trip/GetLastReservationStatusByPhone?phone=...
    /// </summary>
    public static async Task<KoachLastReservationStatusResult> GetLastReservationStatusByPhoneAsync(
        this KoachTripClient tripClient,
        string? baseUrl,
        string tenant,
        string username,
        string phone,
        CancellationToken ct = default)
    {
        // Normalize values so we never throw NullReferenceException.
        var safeTenant = tenant ?? string.Empty;
        var safePhone = phone ?? string.Empty;

        var result = new KoachLastReservationStatusResult
        {
            Success = false,
            TenantId = safeTenant,
            Phone = safePhone
        };

        if (string.IsNullOrWhiteSpace(safePhone))
        {
            result.Error = "Phone is missing/empty; lookup skipped.";
            return result;
        }

        // Acquire authenticated client (JWT is handled inside KoachApiService).
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

        // Build GET url (confirmed working in your environment).
        var encodedPhone = Uri.EscapeDataString(safePhone.Trim());
        var relativeUrl = $"Api/Trip/GetLastReservationStatusByPhone?phone={encodedPhone}";

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
            result.Error = $"Koach Trip lookup failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={body}";
            return result;
        }

        result.RawJson = body;
        result.Success = true;

        // Best-effort extraction (tolerant).
        TryExtractCommonReservationFields(body, result);

        return result;
    }

    /// <summary>
    /// Best-effort JSON parsing helper.
    /// We do not assume the schema is stable yet.
    /// </summary>
    private static void TryExtractCommonReservationFields(string rawJson, KoachLastReservationStatusResult result)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Sometimes responses are nested in "data" or "result".
            var obj = root;
            if (obj.ValueKind == JsonValueKind.Object)
            {
                if (obj.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    obj = data;
                }
                else if (obj.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                {
                    obj = res;
                }
            }

            // Try common property names.
            result.ReservationId = TryGetStringAny(obj, "RID", "rid", "Rid", "reservationId", "ReservationId");
            result.Status = TryGetStringAny(obj, "status", "Status", "reservationStatus", "ReservationStatus");
            result.Pickup = TryGetStringAny(obj, "pickup", "Pickup", "pickupAddress", "PickupAddress");
            result.Dropoff = TryGetStringAny(obj, "dropoff", "Dropoff", "dropoffAddress", "DropoffAddress");
            result.ScheduledTime = TryGetStringAny(obj, "time", "Time", "scheduledTime", "ScheduledTime");
        }
        catch
        {
            // Ignore parse errors; RawJson is still available.
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

            if (prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }

            // Sometimes IDs might be numeric; attempt to convert.
            if (prop.ValueKind == JsonValueKind.Number)
            {
                var s = prop.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }

            // Sometimes the value exists but is not a string/number; ToString() is safe.
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
