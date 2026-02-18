using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace KoachCallTake;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // -------------------- Configuration --------------------
        // Load runtime-editable config files (no rebuild needed).
        // NOTE: reloadOnChange reloads IConfiguration, but we still re-bind per request below.
        builder.Configuration.AddJsonFile("agents.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile("companies.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile("prompts.json", optional: false, reloadOnChange: true);

        // -------------------- Services --------------------
        // Swagger
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // HttpClientFactory (used for OpenAI + Koach API)
        builder.Services.AddHttpClient();

        // Koach Dispatch API wrapper service (handles JWT auth + caching)
        // IMPORTANT: This MUST be registered BEFORE builder.Build().
        builder.Services.AddSingleton<KoachApiService>();

        // Trip endpoints client (DI entrypoint; methods are extension methods in separate files)
        builder.Services.AddSingleton<KoachTripClient>();

        // Voucher accounts cache (30 min per tenant)
        builder.Services.AddSingleton<VoucherAccountsCacheService>();

        // -------------------- Build App --------------------
        var app = builder.Build();

        // ==================== TEMP DEBUG ENDPOINT (Swagger) ====================
        // Tests VoucherAccountsCacheService (cached per tenant for 30 minutes).
        app.MapGet("/debug/vouchers-cached", async (
            [FromQuery] string? extension,
            [FromQuery] string? agentUid,
            [FromServices] IConfiguration config,
            [FromServices] VoucherAccountsCacheService voucherCache,
            CancellationToken ct) =>
        {
            // Load latest config per request
            var currentAgents = new Program.AgentsFile();
            config.GetSection("Agents").Bind(currentAgents.Agents);

            var currentCompanies = new Program.CompaniesFile();
            config.GetSection("Companies").Bind(currentCompanies.Companies);

            var companyCfg = Program.ResolveCompanyFromExtension(extension, currentCompanies);
            if (companyCfg == null)
                return Results.BadRequest(new { error = "Unknown/missing extension.", extension_received = extension });

            // Resolve agent email from UID
            var safeAgentUid = (agentUid ?? "").Trim();
            string agentEmail = "";

            if (!string.IsNullOrWhiteSpace(safeAgentUid) &&
                currentAgents.Agents.TryGetValue(safeAgentUid, out var agentCfg) &&
                agentCfg != null)
            {
                agentEmail = agentCfg.AgentEmail;
            }

            if (string.IsNullOrWhiteSpace(agentEmail))
                return Results.BadRequest(new { error = "AgentEmail missing for agentUid.", agent_uid_received = agentUid });

            // Base URL
            var baseUrl = config["Koach:ApiBaseUrl"] ?? "https://apicall.koachapp.com";

            // Call cache service (refreshes only if expired/missing)
            var accounts = await voucherCache.GetAccountsAsync(
                baseUrl: baseUrl,
                tenant: companyCfg.Tenant,
                username: agentEmail,
                ct: ct
            );

            return Results.Ok(new
            {
                company = companyCfg.CompanyName,
                tenant = companyCfg.Tenant,
                count = accounts.Count,
                accounts
            });
        })
        .WithName("DebugVouchersCached")
        .WithTags("Debug");

        // -------------------- Middleware --------------------
        // Swagger (always on for now)
        app.UseSwagger();
        app.UseSwaggerUI();

        app.UseHttpsRedirection();

        // -------------------- Routes --------------------
        app.MapPost("/agent/message", async (
            // Request payload DTO
            [FromBody] AgentMessageRequest request,

            // Services
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] IConfiguration config,
            [FromServices] KoachApiService koachApiService,
            [FromServices] KoachTripClient koachTripClient,
            [FromServices] VoucherAccountsCacheService voucherAccountsCache,

            // Cancellation
            CancellationToken ct) =>
        {
            // -------------------- Input handling --------------------
            // For a call-taker, the first webhook/hit may be a "call connected" event.
            // In that case we may have routing context (extension/uid/callerPhone) but no spoken text yet.
            var userMessage = request.Message?.Trim() ?? "";

            // If we have no message, we still return a valid agent response (the greeting)
            // and a single next question to start the call.
            var isCallConnect = string.IsNullOrWhiteSpace(userMessage);

            // -------------------- OpenAI API key validation --------------------
            // API key from User Secrets or env var
            var apiKey = config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.Problem(
                    "OpenAI API key is not configured. Set OpenAI:ApiKey (User Secrets) or OPENAI_API_KEY.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // -------------------- Load latest config per request --------------------
            // IMPORTANT: We bind per request so edits to JSON files are picked up without restarting.
            var currentAgents = new AgentsFile();
            config.GetSection("Agents").Bind(currentAgents.Agents);

            var currentCompanies = new CompaniesFile();
            config.GetSection("Companies").Bind(currentCompanies.Companies);

            if (currentCompanies.Companies == null || currentCompanies.Companies.Count == 0)
            {
                return Results.Problem(
                    "companies.json did not load correctly (0 companies). Check that the root is { \"Companies\": { ... } } and the file is copied to output.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // -------------------- Prompts (from prompts.json) --------------------
            // Bind per request so edits apply without restart.
            var currentPrompts = new PromptsFile();
            config.GetSection("Prompts").Bind(currentPrompts.Prompts);

            // Validate prompts.json loaded correctly.
            if (string.IsNullOrWhiteSpace(currentPrompts.Prompts.InitialCallTemplate) ||
                string.IsNullOrWhiteSpace(currentPrompts.Prompts.OngoingCallTemplate))
            {
                return Results.Problem(
                    "prompts.json did not load correctly (InitialCallTemplate or OngoingCallTemplate is empty). Check prompts.json format and ensure it's copied to output.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // -------------------- Company routing (from companies.json) --------------------
            var companyCfg = ResolveCompanyFromExtension(request.Extension, currentCompanies);
            if (companyCfg == null)
            {
                // Guardrail: don't let the model guess company
                return GuardrailUnknownCompany(request);
            }

            var company = companyCfg.CompanyName;

            // -------------------- Agent identity (from agents.json) --------------------
            var agent = ResolveAgent(request.AgentUid, currentAgents);

            // -------------------- Koach Dispatch API Auth (JWT) --------------------
            bool koachJwtAcquired = false;
            string? koachAuthError = null;

            // Base URL should be "https://apicall.koachapp.com"
            var baseUrl = config["Koach:ApiBaseUrl"] ?? "https://apicall.koachapp.com";

            // Resolve AgentEmail from agents.json using AgentUid (required for Koach auth + trip calls).
            string agentEmail = "";
            if (!string.IsNullOrWhiteSpace(request.AgentUid) &&
                currentAgents.Agents.TryGetValue(request.AgentUid, out var agentCfgForAuth) &&
                agentCfgForAuth != null)
            {
                agentEmail = agentCfgForAuth.AgentEmail;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(agentEmail))
                {
                    koachAuthError = "AgentEmail is missing for this AgentUid in agents.json.";
                }
                else
                {
                    // Attempt to authenticate and cache a JWT.
                    // We do NOT store or return the JWT here; KoachApiService caches it internally.
                    await koachApiService.GetJwtAsync(
                        baseUrl: baseUrl,
                        tenant: companyCfg.Tenant,
                        username: agentEmail,
                        ct: ct
                    );

                    koachJwtAcquired = true;
                }
            }
            catch (Exception ex)
            {
                // Do NOT hard-fail the whole request if Koach auth fails.
                koachAuthError = ex.Message;
                koachJwtAcquired = false;
            }

            // -------------------- Voucher accounts (cached) --------------------
            // Cached per tenant for 30 minutes. Best-effort only.
            IReadOnlyList<GetVoucherAccounts.VoucherAccount> voucherAccounts = Array.Empty<GetVoucherAccounts.VoucherAccount>();

            try
            {
                if (koachJwtAcquired && !string.IsNullOrWhiteSpace(agentEmail))
                {
                    voucherAccounts = await voucherAccountsCache.GetAccountsAsync(
                        baseUrl: baseUrl,
                        tenant: companyCfg.Tenant,
                        username: agentEmail,
                        ct: ct
                    );
                }
            }
            catch
            {
                voucherAccounts = Array.Empty<GetVoucherAccounts.VoucherAccount>();
            }

            // -------------------- Reservation lookup (LIVE via Koach API) --------------------
            // MODULAR:
            // - GetLastReservationStatusByPhone lives in GetLastReservationStatusByPhone.cs
            // - GetReservationByRid lives in GetReservationByRid.cs
            ReservationLookupResult reservationLookup = new ReservationLookupResult
            {
                Found = false,
                Snapshot = null,
                Note = "Lookup not attempted."
            };

            // Debug objects (safe) to return for troubleshooting
            GetLastReservationStatusByPhone.KoachLastReservationStatusResult? lastTripByPhone = null;
            object? reservationByRidDebug = null;

            try
            {
                if (!koachJwtAcquired)
                {
                    reservationLookup = new ReservationLookupResult
                    {
                        Found = false,
                        Snapshot = null,
                        Note = $"Koach auth not available; reservation lookup skipped. AuthError={koachAuthError}"
                    };
                }
                else if (string.IsNullOrWhiteSpace(request.CallerPhone))
                {
                    reservationLookup = new ReservationLookupResult
                    {
                        Found = false,
                        Snapshot = null,
                        Note = "CallerPhone missing; reservation lookup skipped."
                    };
                }
                else if (string.IsNullOrWhiteSpace(agentEmail))
                {
                    reservationLookup = new ReservationLookupResult
                    {
                        Found = false,
                        Snapshot = null,
                        Note = "AgentEmail missing; reservation lookup skipped."
                    };
                }
                else
                {
                    // 1) Get last reservation status by phone.
                    lastTripByPhone = await koachTripClient.GetLastReservationStatusByPhoneAsync(
                        baseUrl: baseUrl,
                        tenant: companyCfg.Tenant,
                        username: agentEmail,
                        phone: request.CallerPhone!,
                        ct: ct
                    );

                    if (!lastTripByPhone.Success)
                    {
                        reservationLookup = new ReservationLookupResult
                        {
                            Found = false,
                            Snapshot = null,
                            Note = $"Koach API phone lookup failed: {lastTripByPhone.Error}"
                        };
                    }
                    else
                    {
                        // Snapshot from phone lookup (RID + basic fields).
                        var snapshot = new ReservationSnapshot
                        {
                            ReservationId = lastTripByPhone.ReservationId ?? "",
                            Status = lastTripByPhone.Status ?? "",
                            Pickup = lastTripByPhone.Pickup ?? "",
                            Dropoff = lastTripByPhone.Dropoff ?? "",
                            ScheduledTime = lastTripByPhone.ScheduledTime ?? ""
                        };

                        // 2) RID enrichment: If RID exists, fetch full reservation record.
                        if (!string.IsNullOrWhiteSpace(snapshot.ReservationId))
                        {
                            var rid = snapshot.ReservationId.Trim();

                            // Comment: Result type lives in GetReservationByRid.cs
                            GetReservationByRid.KoachReservationByRidResult ridResult =
                                await koachTripClient.GetReservationByRidAsync(
                                    baseUrl: baseUrl,
                                    tenant: companyCfg.Tenant,
                                    username: agentEmail,
                                    rid: rid,
                                    ct: ct
                                );

                            // Safe debug summary (no JWT/password)
                            reservationByRidDebug = new
                            {
                                success = ridResult.Success,
                                tenant = ridResult.TenantId,
                                reservationId = ridResult.ReservationId,
                                status = ridResult.Status,
                                pickup = ridResult.Pickup,
                                dropoff = ridResult.Dropoff,
                                scheduledTime = ridResult.ScheduledTime,
                                passengerName = ridResult.PassengerName,
                                vehicleNumber = ridResult.VehicleNumber,
                                driverName = ridResult.DriverName,
                                paymentType = ridResult.PaymentType,
                                error = ridResult.Error
                            };

                            if (ridResult.Success)
                            {
                                // Prefer RID details over phone lookup when present.
                                snapshot.Status = ridResult.Status ?? snapshot.Status;
                                snapshot.Pickup = ridResult.Pickup ?? snapshot.Pickup;
                                snapshot.Dropoff = ridResult.Dropoff ?? snapshot.Dropoff;
                                snapshot.ScheduledTime = ridResult.ScheduledTime ?? snapshot.ScheduledTime;

                                snapshot.PassengerName = ridResult.PassengerName ?? "";
                                snapshot.VehicleNumber = ridResult.VehicleNumber ?? "";
                                snapshot.DriverName = ridResult.DriverName ?? "";
                                snapshot.PaymentType = ridResult.PaymentType ?? "";

                                // Keep full raw reservation JSON, truncated for prompt safety.
                                snapshot.FullReservationRawJson = TruncateForPrompt(ridResult.RawJson, 2500);

                                reservationLookup = new ReservationLookupResult
                                {
                                    Found = true,
                                    Snapshot = snapshot,
                                    Note = "Live lookup via Koach API: GetLastReservationStatusByPhone + GetReservationByRid."
                                };
                            }
                            else
                            {
                                // Not fatal; still use phone lookup snapshot.
                                reservationLookup = new ReservationLookupResult
                                {
                                    Found = true,
                                    Snapshot = snapshot,
                                    Note = $"Phone lookup succeeded; RID lookup failed: {ridResult.Error}"
                                };
                            }
                        }
                        else
                        {
                            // Phone lookup succeeded but no RID returned.
                            reservationLookup = new ReservationLookupResult
                            {
                                Found = true,
                                Snapshot = snapshot,
                                Note = "Phone lookup succeeded but did not return a RID; full reservation lookup skipped."
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Hard fallback to "not found" but preserve diagnostic note.
                reservationLookup = new ReservationLookupResult
                {
                    Found = false,
                    Snapshot = null,
                    Note = $"Reservation lookup exception: {ex.Message}"
                };
            }

            // -------------------- Build prompt & call OpenAI --------------------
            var systemPrompt = BuildSystemPrompt(
                companyCfg,
                agent,
                request.CallerPhone,
                currentPrompts.Prompts,
                reservationLookup,
                voucherAccounts,
                isCallConnect);

            // Model selection (request override > config > default)
            var model = request.Model ?? config["OpenAI:Model"] ?? "gpt-4o-mini";

            // Responses API payload
            var payload = new
            {
                model,
                input = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = isCallConnect ? "Call connected. Begin the call." : userMessage }
                }
            };

            // Create OpenAI client via factory and apply auth header
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("https://api.openai.com/v1/responses", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return Results.Problem(
                    $"OpenAI API error: {(int)response.StatusCode} {response.ReasonPhrase}",
                    statusCode: (int)response.StatusCode,
                    extensions: new Dictionary<string, object?> { ["body"] = responseBody });
            }

            var extracted = ResponseHelper.ExtractResponseText(responseBody);

            // -------------------- Response --------------------
            return Results.Ok(new
            {
                response = extracted ?? responseBody,
                routing = new
                {
                    company,
                    extension_received = request.Extension,
                    caller_phone = request.CallerPhone,
                    agent_uid = request.AgentUid,
                    agent_alias = agent.Alias,
                    operator_number = agent.OperatorNumber,
                    reservation_snapshot = reservationLookup.Snapshot,
                    // DEBUG: confirms Koach API authentication worked (without leaking token)
                    koach_jwt_acquired = koachJwtAcquired,

                    // DEBUG: safe error message for troubleshooting (does not include password or JWT)
                    koach_auth_error = koachAuthError,

                    // DEBUG: phone lookup result (method-file DTO)
                    koach_last_trip_by_phone = lastTripByPhone,

                    // DEBUG: rid lookup summary (safe)
                    koach_reservation_by_rid = reservationByRidDebug,

                    // DEBUG: reservation lookup note
                    reservation_lookup_note = reservationLookup.Note
                }
            });
        })
        .WithName("PostAgentMessage");

        // -------------------- Run --------------------
        app.Run();
    }

    // -------------------- Request/Config Types --------------------

    // Swagger request payload DTO
    // Message is optional because the first event may be a call-connect event with no spoken text yet.
    public record AgentMessageRequest(string? Message, string? Model, string? Extension, string? AgentUid, string? CallerPhone);

    // Resolved agent identity used to fill greeting placeholders
    public record AgentIdentity(string Uid, string Alias, string OperatorNumber);

    // agents.json root object
    public sealed class AgentsFile
    {
        public Dictionary<string, AgentConfig> Agents { get; set; } = new();
    }

    // agents.json value object (per agent UID)
    public sealed class AgentConfig
    {
        public string Alias { get; set; } = "Operator";
        public string OperatorNumber { get; set; } = "000";

        // Dispatch UID (stored as string for flexibility)
        public string Duid { get; set; } = "0";

        // Used to authenticate to Koach API (with shared password from secrets/env).
        public string AgentEmail { get; set; } = "";
    }

    // companies.json root object
    public sealed class CompaniesFile
    {
        public Dictionary<string, CompanyConfig> Companies { get; set; } = new();
    }

    // companies.json value object (per extension)
    public sealed class CompanyConfig
    {
        public string CompanyName { get; set; } = "Unknown";
        public string GreetingCompanyName { get; set; } = "your company";
        public string ServiceArea { get; set; } = "";

        // Dispatch API tenant. Default is "koach"; DOB uses "DOB".
        public string Tenant { get; set; } = "koach";
    }

    // prompts.json root object
    public sealed class PromptsFile
    {
        public PromptsConfig Prompts { get; set; } = new();
    }

    // prompts.json value object (per prompts section)
    public sealed class PromptsConfig
    {
        // Used when the call first connects (no caller message yet)
        public string InitialCallTemplate { get; set; } = "";

        // Used after the caller has spoken (message is present)
        public string OngoingCallTemplate { get; set; } = "";
    }

    // -------------------- Reservation Lookup --------------------

    // Minimal reservation snapshot we can safely share with the model.
    public sealed class ReservationSnapshot
    {
        public string ReservationId { get; set; } = "";
        public string Status { get; set; } = "";
        public string Pickup { get; set; } = "";
        public string Dropoff { get; set; } = "";
        public string ScheduledTime { get; set; } = "";

        // Optional enrichment pulled from GetReservationByRid (best-effort).
        public string PassengerName { get; set; } = "";
        public string VehicleNumber { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string PaymentType { get; set; } = "";

        // Raw full reservation JSON (truncated before injecting into prompt).
        public string FullReservationRawJson { get; set; } = "";
    }

    // Result wrapper so we can explicitly represent "not found" vs "found".
    public sealed class ReservationLookupResult
    {
        public bool Found { get; set; }
        public ReservationSnapshot? Snapshot { get; set; }
        public string? Note { get; set; }
    }

    // -------------------- Routing + Guardrails + Greetings --------------------

    // Takes an extension/huntgroup like "4100" or "(4100)" and returns only digits.
    static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return "";
        return new string(ext.Where(char.IsDigit).ToArray());
    }

    // Looks up the company by extension in companies.json.
    static CompanyConfig? ResolveCompanyFromExtension(string? extension, CompaniesFile companiesFile)
    {
        var ext = NormalizeExtension(extension);

        if (string.IsNullOrWhiteSpace(ext))
            return null;

        if (companiesFile.Companies != null &&
            companiesFile.Companies.TryGetValue(ext, out var companyCfg))
        {
            return companyCfg;
        }

        return null;
    }

    // Looks up the agent by UID in agents.json.
    // If not found, returns Operator/000.
    static AgentIdentity ResolveAgent(string? agentUid, AgentsFile agentsFile)
    {
        var uid = (agentUid ?? "").Trim();

        if (string.IsNullOrWhiteSpace(uid))
            return new AgentIdentity("", "Operator", "000");

        if (agentsFile.Agents != null &&
            agentsFile.Agents.TryGetValue(uid, out var cfg) &&
            cfg != null)
        {
            return new AgentIdentity(uid, cfg.Alias ?? "Operator", cfg.OperatorNumber ?? "000");
        }

        return new AgentIdentity(uid, "Operator", "000");
    }

    // Replaces greeting placeholders with the resolved agent values.
    static string ApplyAgentToGreeting(string greetingTemplate, AgentIdentity agent)
    {
        return greetingTemplate
            .Replace("{ALIAS}", agent.Alias)
            .Replace("{OPNUM}", agent.OperatorNumber);
    }

    /// <summary>
    /// Truncates large strings before injecting into prompts.
    /// This prevents giant JSON payloads from ballooning token usage.
    /// </summary>
    static string TruncateForPrompt(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (maxChars <= 0)
            return string.Empty;

        if (value.Length <= maxChars)
            return value;

        // Use a visible marker so it's obvious content was truncated.
        return value.Substring(0, maxChars) + "…(truncated)";
    }

    static string BuildSystemPrompt(
        CompanyConfig companyCfg,
        AgentIdentity agent,
        string? callerPhone,
        PromptsConfig prompts,
        ReservationLookupResult reservationLookup,
        IReadOnlyList<GetVoucherAccounts.VoucherAccount> voucherAccounts,
        bool isCallConnect)
    {
        var greetingTemplate =
            $"Thank you for calling {companyCfg.GreetingCompanyName}. My name is {{ALIAS}}, and my operator number is {{OPNUM}}. How may I assist you today?";

        var greeting = ApplyAgentToGreeting(greetingTemplate, agent);

        // Choose correct lifecycle template
        var baseRules = isCallConnect
            ? prompts.InitialCallTemplate
            : prompts.OngoingCallTemplate;

        // Safety fallback
        if (string.IsNullOrWhiteSpace(baseRules))
        {
            baseRules = "You are a professional taxi dispatch call taker. Be polite, concise, and professional. Ask ONE question at a time.";
        }

        // Only initial template contains {{GREETING}}, but Replace is safe either way.
        baseRules = baseRules.Replace("{{GREETING}}", greeting);

        // Provide caller phone context clearly.
        var callerContext = string.IsNullOrWhiteSpace(callerPhone)
            ? "SYSTEM DATA: CallerPhone is NOT available."
            : $"SYSTEM DATA: CallerPhone is available and is {callerPhone}.";

        var companyContext =
            $"You are answering for {companyCfg.CompanyName}. " +
            (string.IsNullOrWhiteSpace(companyCfg.ServiceArea)
                ? ""
                : $"Service area is {companyCfg.ServiceArea}. ");

        // Deterministic reservation lookup results.
        string reservationContext;

        if (reservationLookup.Found && reservationLookup.Snapshot != null)
        {
            reservationContext =
                "SYSTEM DATA: An active reservation WAS found for this caller. " +
                $"ReservationId={reservationLookup.Snapshot.ReservationId}; " +
                $"Status={reservationLookup.Snapshot.Status}; " +
                $"Pickup={reservationLookup.Snapshot.Pickup}; " +
                $"Dropoff={reservationLookup.Snapshot.Dropoff}; " +
                $"ScheduledTime={reservationLookup.Snapshot.ScheduledTime}; " +
                (string.IsNullOrWhiteSpace(reservationLookup.Snapshot.PassengerName) ? "" : $"PassengerName={reservationLookup.Snapshot.PassengerName}; ") +
                (string.IsNullOrWhiteSpace(reservationLookup.Snapshot.VehicleNumber) ? "" : $"VehicleNumber={reservationLookup.Snapshot.VehicleNumber}; ") +
                (string.IsNullOrWhiteSpace(reservationLookup.Snapshot.DriverName) ? "" : $"DriverName={reservationLookup.Snapshot.DriverName}; ") +
                (string.IsNullOrWhiteSpace(reservationLookup.Snapshot.PaymentType) ? "" : $"PaymentType={reservationLookup.Snapshot.PaymentType}; ");

            if (!string.IsNullOrWhiteSpace(reservationLookup.Snapshot.FullReservationRawJson))
            {
                reservationContext += $"FullReservationJson={reservationLookup.Snapshot.FullReservationRawJson}";
            }
        }
        else
        {
            reservationContext = "SYSTEM DATA: No active reservation was found for this caller (or lookup not available yet).";
        }

        // Voucher accounts (SYSTEM DATA)
        string voucherContext;

        if (voucherAccounts != null && voucherAccounts.Count > 0)
        {
            // Include first 50 to keep prompt small
            var limited = voucherAccounts.Take(50).ToList();

            voucherContext =
                "SYSTEM DATA: VoucherAccounts (Id/Abbreviation/Company): " +
                string.Join(" | ", limited.Select(v =>
                    $"Id={v.Id}, Abbreviation={v.Abbreviation}, Company={v.Company}"
                ));

            voucherContext = TruncateForPrompt(voucherContext, 2500);
        }
        else
        {
            voucherContext = "SYSTEM DATA: VoucherAccounts list is empty or not available.";
        }

        // Guardrail to prevent reservation hallucinations.
        var reservationGuardrail =
            "GUARDRAIL: You must treat the SYSTEM DATA above as the only source of truth for reservation existence/status. " +
            "If SYSTEM DATA says no active reservation was found, do NOT claim you found one.";

        return baseRules + " " +
               callerContext + " " +
               reservationContext + " " +
               voucherContext + " " +
               reservationGuardrail + " " +
               companyContext;
    }

    static IResult GuardrailUnknownCompany(AgentMessageRequest request)
    {
        var extShown = string.IsNullOrWhiteSpace(request.Extension) ? "missing" : request.Extension;

        return Results.Ok(new
        {
            response =
                "Thanks for calling. I want to make sure I connect you to the right company. " +
                "Are you calling for Silver Cab of PG, Diamond Cab of Baltimore City, DC VIP Cab, " +
                "Diamond Cab of Anne Arundel County, or Transport DC?",
            routing = new
            {
                company = "Unknown",
                extension_received = extShown,
                caller_phone = request.CallerPhone,
            }
        });
    }

    // -------------------- Response Parsing (Responses API) --------------------

    public static class ResponseHelper
    {
        public static string? ExtractResponseText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("output", out var output) ||
                    output.ValueKind != JsonValueKind.Array)
                    return null;

                var sb = new StringBuilder();

                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var contentItem in content.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("type", out var type) &&
                            type.GetString() == "output_text" &&
                            contentItem.TryGetProperty("text", out var text))
                        {
                            sb.Append(text.GetString());
                        }
                    }
                }

                var result = sb.ToString();
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch
            {
                return null;
            }
        }
    }
}
