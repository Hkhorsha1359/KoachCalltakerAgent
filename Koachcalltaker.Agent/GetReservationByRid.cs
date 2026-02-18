using System.Text.Json;

namespace KoachCallTake;

/// <summary>
/// MODULAR METHOD FILE:
/// - Provides KoachTripClient.GetReservationByRidAsync(...)
/// - Provides the DTO: GetReservationByRid.KoachReservationByRidResult
///
/// API DOCS CONFIRM:
/// GET /Trip/GetReservationByRid/{rid}
///
/// The fields you want are present under these names:
/// - PUAddress (pickup)
/// - DOAddress (dropoff)
/// - PUTime (pickup time)
/// - EnteredTime (entered time)
/// </summary>
public static class GetReservationByRid
{
    /// <summary>
    /// Result wrapper for reservation detail lookup by RID.
    /// RawJson is always retained for debugging until schema is finalized.
    /// </summary>
    public sealed class KoachReservationByRidResult
    {
        public bool Success { get; set; }

        // Correlation/debug info
        public string TenantId { get; set; } = string.Empty;
        public string Rid { get; set; } = string.Empty;

        // Full raw response from Koach API
        public string RawJson { get; set; } = string.Empty;

        // Best-effort extracted fields (optional)
        public string? ReservationId { get; set; }
        public string? Status { get; set; }

        public string? Pickup { get; set; }
        public string? Dropoff { get; set; }
        public string? ScheduledTime { get; set; }   // PUTime is preferred; EnteredTime is fallback

        public string? PassengerName { get; set; }
        public string? VehicleNumber { get; set; }   // Often TagNum
        public string? DriverName { get; set; }
        public string? PaymentType { get; set; }     // Only if present in payload

        // Safe error message (no JWT, no password)
        public string? Error { get; set; }
    }

    // -------------------- Helpers --------------------

    /// <summary>
    /// Best-effort JSON parsing helper. We do not assume the schema is stable yet.
    /// </summary>
    internal static void TryExtractReservationByRidFields(string rawJson, KoachReservationByRidResult result)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Sometimes responses are nested in "data" or "result"
            var obj = root;
            if (obj.ValueKind == JsonValueKind.Object)
            {
                if (obj.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    obj = data;
                else if (obj.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                    obj = res;
            }

            // IMPORTANT: Field names confirmed in API docs example:
            // RID, Name, Phone, PUTime, PUAddress, DOAddress, EnteredTime, TagNum, DriverName, Status, etc.

            result.ReservationId = TryGetStringAny(obj, "RID", "rid", "Rid", "ReservationId", "reservationId");
            result.Status = TryGetStringAny(obj, "Status", "status", "ReservationStatus", "reservationStatus");

            result.Pickup = TryGetStringAny(obj,
                "PUAddress", "PickupAddress", "pickupAddress", "Pickup", "pickup");

            result.Dropoff = TryGetStringAny(obj,
                "DOAddress", "DropoffAddress", "dropoffAddress", "Dropoff", "dropoff");

            // Prefer PUTime, fallback to EnteredTime
            result.ScheduledTime = TryGetStringAny(obj,
                "PUTime", "puTime", "PickupTime", "pickupTime", "EnteredTime", "enteredTime", "Time", "time");

            result.PassengerName = TryGetStringAny(obj, "Name", "PassengerName", "passengerName");
            result.DriverName = TryGetStringAny(obj, "DriverName", "driverName");

            // Vehicle number is commonly TagNum in the docs
            result.VehicleNumber = TryGetStringAny(obj,
                "TagNum", "tagNum", "VehicleNumber", "vehicleNumber", "CabNumber", "cabNumber");

            // Payment type might not exist in your current payload; we try anyway
            result.PaymentType = TryGetStringAny(obj,
                "PaymentType", "paymentType", "PayType", "payType", "MethodOfPayment", "methodOfPayment");
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

            // Sometimes IDs are numeric; convert to string
            if (prop.ValueKind == JsonValueKind.Number)
            {
                var s = prop.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }
}

/// <summary>
/// MODULAR METHOD IMPLEMENTATION.
/// NOTE: This assumes your KoachTripClient already has access to KoachApiService (like your phone lookup).
/// </summary>
public sealed partial class KoachTripClient
{
    /// <summary>
    /// Calls Koach reservation detail endpoint by RID.
    ///
    /// API docs show:
    /// GET /Trip/GetReservationByRid/{rid}
    ///
    /// Some deployments prefix "Api/". To be robust, we try both:
    /// - Trip/GetReservationByRid/{rid}
    /// - Api/Trip/GetReservationByRid/{rid}
    /// </summary>
    public async Task<GetReservationByRid.KoachReservationByRidResult> GetReservationByRidAsync(
        string? baseUrl,
        string tenant,
        string username,
        string rid,
        CancellationToken ct = default)
    {
        // Normalize values so we never throw NullReferenceException.
        var safeTenant = tenant ?? string.Empty;
        var safeRid = rid ?? string.Empty;

        var result = new GetReservationByRid.KoachReservationByRidResult
        {
            Success = false,
            TenantId = safeTenant,
            Rid = safeRid
        };

        if (string.IsNullOrWhiteSpace(safeRid))
        {
            result.Error = "RID is missing/empty; lookup skipped.";
            return result;
        }

        // Acquire authenticated client (JWT handled/cached in KoachApiService)
        HttpClient client;
        try
        {
            client = await _koachApi.CreateAuthedClientAsync(baseUrl, safeTenant, username, ct);
        }
        catch (Exception ex)
        {
            result.Error = $"JWT acquisition failed: {ex.Message}";
            return result;
        }

        // Try both path variants because your environment may differ by casing/prefix.
        var encodedRid = Uri.EscapeDataString(safeRid.Trim());

        var candidates = new[]
        {
            $"Trip/GetReservationByRid/{encodedRid}",
            $"Api/Trip/GetReservationByRid/{encodedRid}"
        };

        HttpResponseMessage? resp = null;
        string body = "";

        foreach (var relativeUrl in candidates)
        {
            try
            {
                resp = await client.GetAsync(relativeUrl, ct);
                body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    // Success - stop trying paths
                    result.RawJson = body;
                    result.Success = true;

                    // Best-effort extraction (tolerant)
                    GetReservationByRid.TryExtractReservationByRidFields(body, result);

                    // Ensure ReservationId is set even if parser didn’t find it
                    if (string.IsNullOrWhiteSpace(result.ReservationId))
                        result.ReservationId = safeRid.Trim();

                    return result;
                }

                // If not success, continue to next candidate path
            }
            catch (Exception ex)
            {
                // Network/TLS errors should stop immediately (no point trying other paths)
                result.Error = $"Koach API call failed (network/TLS/etc): {ex.Message}";
                return result;
            }
        }

        // If we get here, all candidates failed with non-success status codes
        if (resp != null)
        {
            result.Error = $"Koach RID lookup failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={body}";
        }
        else
        {
            result.Error = "Koach RID lookup failed: no response.";
        }

        return result;
    }
}
