using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KoachCallTake;

/// <summary>
/// Trip-specific client for Koach Dispatch API.
/// Uses KoachApiService for authentication/JWT caching.
///
/// MODULAR DESIGN:
/// - This file is intentionally small and acts as the DI entrypoint / orchestrator.
/// - Each API method lives in its own file as an extension method on KoachTripClient:
///     - GetLastReservationStatusByPhoneAsync -> its own file
///     - GetReservationByRidAsync            -> its own file
///
/// This keeps each endpoint implementation isolated and easy to maintain.
/// </summary>
public sealed partial class KoachTripClient
{
    private readonly KoachApiService _koachApi;

    public KoachTripClient(KoachApiService koachApi)
    {
        // Comment: KoachApiService owns JWT auth + caching.
        _koachApi = koachApi ?? throw new ArgumentNullException(nameof(koachApi));
    }

    /// <summary>
    /// Internal helper for method-files to create an authenticated HttpClient.
    /// We keep this centralized so method-files don't need to know about JWT internals.
    /// </summary>
    internal Task<HttpClient> CreateAuthedClientAsync(
        string? baseUrl,
        string tenant,
        string username,
        CancellationToken ct)
    {
        return _koachApi.CreateAuthedClientAsync(baseUrl, tenant, username, ct);
    }
}
