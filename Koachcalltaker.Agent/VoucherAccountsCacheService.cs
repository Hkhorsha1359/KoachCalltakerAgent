using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KoachCallTake;

/// <summary>
/// VoucherAccountsCacheService provides:
///  - In-memory caching of voucher accounts per tenant
///  - TTL-based refresh (default 30 minutes)
///  - SemaphoreSlim per tenant to prevent refresh storms
///
/// IMPORTANT RULE (per your requirement):
///  - Only Program.cs (main code) should call into this service.
///  - Other code should NOT call GetVoucherAccountsAsync directly.
/// </summary>
public sealed class VoucherAccountsCacheService
{
    // Comment: Default refresh interval/TTL (30 minutes).
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    // Comment: We reuse KoachTripClient, but ONLY this service will call the voucher API method-file.
    private readonly KoachTripClient _tripClient;

    // Comment: TTL for cache entries; configurable if you want later.
    private readonly TimeSpan _ttl;

    // Comment: Cache entries keyed by tenant (normalized).
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // Comment: Per-tenant locks to prevent multiple simultaneous refresh calls.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public VoucherAccountsCacheService(KoachTripClient tripClient)
        : this(tripClient, DefaultTtl)
    {
        // Comment: Default ctor uses 30 minute TTL.
    }

    public VoucherAccountsCacheService(KoachTripClient tripClient, TimeSpan ttl)
    {
        _tripClient = tripClient ?? throw new ArgumentNullException(nameof(tripClient));

        // Comment: Guardrail: never allow non-positive TTL.
        _ttl = ttl <= TimeSpan.Zero ? DefaultTtl : ttl;
    }

    /// <summary>
    /// Returns voucher accounts for a tenant.
    ///
    /// Behavior:
    /// - If cache is present and not expired => return cached list immediately.
    /// - If expired/missing => refresh once (per tenant lock) => store => return.
    ///
    /// Parameters:
    /// - baseUrl: Koach API base (ex: https://apicall.koachapp.com)
    /// - tenant: companyCfg.Tenant (ex: "koach" or "DOB")
    /// - username: agent email used for Koach auth (from agents.json)
    /// </summary>
    public async Task<IReadOnlyList<GetVoucherAccounts.VoucherAccount>> GetAccountsAsync(
        string? baseUrl,
        string tenant,
        string username,
        CancellationToken ct = default)
    {
        // Comment: Normalize tenant key so cache is stable.
        var tenantKey = NormalizeTenantKey(tenant);

        // -------------------- Fast path: valid cache --------------------
        if (_cache.TryGetValue(tenantKey, out var existing) &&
            !existing.IsExpiredUtc())
        {
            return existing.Accounts;
        }

        // -------------------- Slow path: refresh with per-tenant lock --------------------
        var gate = _locks.GetOrAdd(tenantKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            // Comment: Double-check after acquiring lock (another request may have refreshed).
            if (_cache.TryGetValue(tenantKey, out existing) &&
                !existing.IsExpiredUtc())
            {
                return existing.Accounts;
            }

            // Comment: Call the method-file endpoint. This is the ONLY place that should do this.
            var apiResult = await _tripClient.GetVoucherAccountsAsync(
                baseUrl: baseUrl,
                tenant: tenant,
                username: username,
                ct: ct
            );

            // Comment: If the call failed, we do NOT overwrite a previously-good cache.
            // If we have an existing cache (even expired), we can serve it as best-effort.
            if (!apiResult.Success)
            {
                // Comment: Return stale cache if it exists, otherwise return empty list.
                if (existing != null && existing.Accounts.Count > 0)
                {
                    return existing.Accounts;
                }

                return Array.Empty<GetVoucherAccounts.VoucherAccount>();
            }

            // Comment: Successful call. Cache parsed accounts (even if empty).
            var now = DateTimeOffset.UtcNow;

            var newEntry = new CacheEntry(
                accounts: apiResult.Accounts ?? new List<GetVoucherAccounts.VoucherAccount>(),
                fetchedAtUtc: now,
                expiresAtUtc: now.Add(_ttl)
            );

            _cache[tenantKey] = newEntry;

            return newEntry.Accounts;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Clears cache for a tenant (useful for manual testing).
    /// </summary>
    public void InvalidateTenant(string tenant)
    {
        var tenantKey = NormalizeTenantKey(tenant);

        _cache.TryRemove(tenantKey, out _);

        // Comment: Lock can remain; it's fine to reuse.
    }

    /// <summary>
    /// Clears ALL cached tenants.
    /// </summary>
    public void InvalidateAll()
    {
        _cache.Clear();
        // Comment: Locks can remain; they are lightweight and reused.
    }

    // -------------------- Internal Types --------------------

    private sealed class CacheEntry
    {
        public IReadOnlyList<GetVoucherAccounts.VoucherAccount> Accounts { get; }
        public DateTimeOffset FetchedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }

        public CacheEntry(
            IReadOnlyList<GetVoucherAccounts.VoucherAccount> accounts,
            DateTimeOffset fetchedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            Accounts = accounts ?? Array.Empty<GetVoucherAccounts.VoucherAccount>();
            FetchedAtUtc = fetchedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        // Comment: Cache is considered valid if now < ExpiresAtUtc.
        public bool IsExpiredUtc()
        {
            return DateTimeOffset.UtcNow >= ExpiresAtUtc;
        }
    }

    // -------------------- Helpers --------------------

    /// <summary>
    /// Normalizes tenant key so "koach" and "Koach" share the same cache bucket.
    /// DOB remains "dob" bucket (case-insensitive).
    /// </summary>
    private static string NormalizeTenantKey(string tenant)
    {
        return (tenant ?? string.Empty).Trim().ToLowerInvariant();
    }
}
