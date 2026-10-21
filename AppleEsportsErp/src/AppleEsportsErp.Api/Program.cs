using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Api.Hubs;
using AppleEsportsErp.Api.Middleware;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Infrastructure.Identity;
using AppleEsportsErp.Infrastructure.Services;
using Serilog;

// ═══════════════════════════════════════════════
//  AppleEsports ERP — .NET 8 Enterprise Backend
//  SOP Master Source of Truth compliance
// ═══════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Pinned to where the executable lives, not to whatever directory it happened to be
    // launched from. A Windows service starts in System32, so the default would send it
    // looking for appsettings.Production.json there — and it would die on start-up
    // complaining that nothing is configured, which is exactly how a branch install
    // fails in a way nobody can diagnose.
    ContentRootPath = AppContext.BaseDirectory,
});

// At a branch this runs as a Windows service so the shop is ready the moment the operator
// PC boots, with no console window for anyone to close by accident. Harmless elsewhere:
// outside a service host it is a no-op, so the same build still runs in Docker and from
// the command line unchanged.
builder.Host.UseWindowsService(options => options.ServiceName = "AppleEsportsApi");

// Enable DI validation on build
builder.Host.UseDefaultServiceProvider((context, options) => {
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// ── Serilog ──
//
// The path is absolute, and deliberately so. "logs/appleesports-.log" is relative to the
// process working directory, which for a Windows service is C:\Windows\System32 — so a
// branch quietly wrote its log there, where nobody would ever look for it and where it has
// no business being. Setting ContentRootPath does not help; Serilog resolves against the
// working directory, not the content root.
//
// In a container this is a path inside it, which is where the logs were expected anyway.
var logDirectory = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                   "Apple Esports", "logs")
    : Path.Combine(AppContext.BaseDirectory, "logs");

Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    // Every request logged six lines and every query its full SQL, which on the sessions
    // screen — polled continuously — came to roughly 3 MB a minute, or 4 GB a day on a till
    // that is meant to run unattended for months. The framework's own chatter is dropped;
    // anything the application logs itself still comes through at Information.
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [req:{RequestId}] [op:{OperatorId}] [br:{BranchId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDirectory, "appleesports-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        // A hard ceiling as well as a daily roll. The daily roll alone did not stop a single
        // day's file reaching 612 MB, and a till that fills its own disk stops the shop.
        fileSizeLimitBytes: 50L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [req:{RequestId}] [op:{OperatorId}] [br:{BranchId}] [sh:{ShiftId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

Log.Information("Logging to {LogDirectory}", logDirectory);

// ── Configuration sections ──
var jwtConfig = builder.Configuration.GetSection("Jwt");
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173" };

// ═══════════════════════════════════════════════
//  SERVICES — maps from server/src/index.js
// ═══════════════════════════════════════════════

// ── 1. EF Core / PostgreSQL (SOP §23) ──
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);

// Branches record their shifts, tills and credits for Head Office as they save them; Head
// Office records nothing, having nowhere to send it and no wish to mail itself the rows it
// has just received. Set here because a deployment's role is fixed for the life of the
// process - it is read from configuration once, at the only moment it can possibly change.
AppleEsportsErp.Infrastructure.Data.SyncCapture.IsEnabled =
    !AppleEsportsErp.Infrastructure.Configuration.DeploymentRole.IsHeadOffice(builder.Configuration);

// ── 2. JWT Authentication (SOP §21 + Q1: full claims embedded) ──
//
// Checked explicitly rather than dereferenced with "!". In Docker these arrive as
// environment variables from compose, but a branch install has neither — and the
// unchecked version died on start-up with "ArgumentNullException: Parameter 's'",
// which tells whoever is standing at the branch precisely nothing. The service would
// register, refuse to run, and look like a broken installer.
foreach (var required in new[] { "Secret", "RefreshSecret" })
{
    if (string.IsNullOrWhiteSpace(jwtConfig[required]))
    {
        var message =
            $"Jwt:{required} is not configured, so the API cannot start.\n" +
            "A branch install writes this into appsettings.Production.json during setup; " +
            "a Docker deployment passes it in as an environment variable. " +
            "If you are seeing this on a branch PC, re-run the installer.";

        Log.Fatal(message);
        Console.Error.WriteLine(message);
        return 1;
    }
}

var jwtKey = Encoding.UTF8.GetBytes(jwtConfig["Secret"]!);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(options =>
{
    options.UseSecurityTokenValidators = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
        ValidateIssuer = true,
        ValidIssuer = jwtConfig["Issuer"],
        ValidateAudience = true,
        ValidAudience = jwtConfig["Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.Name,
    };

    // Q2 Decision: SignalR JWT via query string for WebSocket connections
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }

            // Tokens live in an HttpOnly cookie so page scripts cannot read them, which
            // also means the browser sends no *Bearer* header. Fall back to the cookie.
            //
            // The test is specifically for a Bearer scheme, not merely for the presence of
            // an Authorization header. The dashboard sits behind an nginx Basic Auth gate,
            // and once the browser has authenticated to that realm it attaches
            // "Authorization: Basic ..." to every request on the origin — including these
            // API calls. Treating any Authorization header as "a token was supplied" means
            // the cookie is never read and the gate's own credential gets handed to the JWT
            // parser, which rejects it: login succeeds, every call afterwards is a 401.
            if (string.IsNullOrEmpty(context.Token))
            {
                var authHeader = context.Request.Headers.Authorization.ToString();
                var hasBearer = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

                if (!hasBearer)
                {
                    // Checked first, and used instead of accessToken when present: this is
                    // what carries an Admin Quick-Switch. It is a wholly separate cookie from
                    // accessToken/refreshToken so switching in and out never touches the
                    // operator's own session - see AuthController's admin-switch endpoints.
                    var switchToken = context.Request.Cookies["adminSwitchToken"];
                    var cookieToken = !string.IsNullOrEmpty(switchToken)
                        ? switchToken
                        : context.Request.Cookies["accessToken"];
                    if (!string.IsNullOrEmpty(cookieToken))
                        context.Token = cookieToken;
                }
            }

            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var revocationService = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationService>();
            var jti = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
            var userIdString = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            if (jti != null && Guid.TryParse(userIdString, out var userId))
            {
                var validFrom = context.SecurityToken.ValidFrom;
                var issueTime = validFrom == DateTime.MinValue ? DateTimeOffset.UtcNow : new DateTimeOffset(validFrom, TimeSpan.Zero);
                if (await revocationService.IsTokenRevokedAsync(jti, userId, issueTime))
                {
                    context.Fail("Token has been revoked.");
                }
            }
        }
    };
});

// ── 3. Authorization Policies (SOP §5 + §19) ──
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy =>
        policy.RequireClaim(ClaimTypes.Role, Roles.SuperAdmin));

    options.AddPolicy("AdminOrSuperAdmin", policy =>
        policy.RequireClaim(ClaimTypes.Role, Roles.SuperAdmin, Roles.Admin));

    options.AddPolicy("OperatorOrAdmin", policy =>
        policy.RequireClaim(ClaimTypes.Role, Roles.SuperAdmin, Roles.Admin, Roles.Operator, "Agent"));

    // Dashboard-specific policies
    foreach (var dashboard in new[] { Dashboards.BillingCounter, Dashboards.Sessions, Dashboards.Reservations,
        Dashboards.FoodOrders, Dashboards.CashRegister, Dashboards.CashDesk, Dashboards.Members,
        Dashboards.MenuEditor, Dashboards.MainDashboard, Dashboards.PcStatus, Dashboards.Eod, Dashboards.Settings,
        Dashboards.WalletSettings, Dashboards.MemberValueEdit, Dashboards.Reports })
    {
        options.AddPolicy($"Dashboard:{dashboard}", policy =>
            policy.Requirements.Add(new DashboardRequirement(dashboard)));
    }
});
builder.Services.AddSingleton<IAuthorizationHandler, DashboardAuthorizationHandler>();

// ── 4. CORS (maps from Helmet + CORS in Node.js) ──
builder.Services.AddCors(options =>
{
    options.AddPolicy("AppleEsportsCors", policy =>
    {
        policy
            .WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ── 5. Rate Limiting (maps from rateLimit.js) ──
//
// Why an operator saw "Rate limit exceeded" while simply starting a session.
//
// The limit is per IP address, which is the right idea and was being fed the wrong address.
// Head Office runs behind nginx, and nginx is what actually opens the connection to this
// process - so RemoteIpAddress was nginx's own container address on every single request.
// Every dashboard, every branch, every PC agent in the company shared one bucket. Worse, they
// shared it with the heartbeats: four branches beating every three seconds is 80 requests a
// minute of pure background chatter, before anybody touches a screen. Whoever happened to
// press a button when the shared bucket ran dry got the 429, which is why it looked random and
// why it landed on ordinary work like starting a session.
//
// Three things fix it, and all three are needed.
var rateLimitConfig = builder.Configuration.GetSection("RateLimiting");
builder.Services.AddRateLimiter(options =>
{
    var permitLimit = rateLimitConfig.GetValue("PermitLimit", 100);
    var window = TimeSpan.FromSeconds(rateLimitConfig.GetValue("WindowSeconds", 60));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // 1. Branch-to-Head-Office traffic is not rate limited at all. It is machine traffic on
        // a fixed schedule that cannot be told to slow down and must never be dropped: the
        // heartbeat is how Head Office knows a shop is alive, the sync inbox is how money
        // arrives, and a command result is a branch reporting what it just did. Throttling any
        // of those loses a fact rather than delaying a click. They are also the endpoints most
        // likely to trip a shared bucket, being the only ones that run all night.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/branch-status")
            || path.StartsWithSegments("/api/sync")
            || path.StartsWithSegments("/api/health"))
        {
            return RateLimitPartition.GetNoLimiter("machine-traffic");
        }

        // 2. A signed-in person gets their own bucket, keyed on who they are rather than where
        // they are connecting from. This is what stops one busy counter from throttling
        // another, and it is correct even when every branch really does share one public IP -
        // which, behind a single office connection, several of them do.
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var key = !string.IsNullOrEmpty(userId)
            ? $"user:{userId}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 5,
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429;

        // Tells the caller how long to wait instead of leaving them to guess. Without it a
        // dashboard that retries immediately just spends the next window's budget too.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            success = false,
            error = "Too many requests from this account in the last minute. Wait a moment and try again.",
            code = "RATE_LIMIT",
        }, cancellationToken: cancellationToken);
    };
});

// ── 6. SignalR (Q2: auto-negotiation, WebSocket primary) ──
var signalRBuilder = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    signalRBuilder.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("AppleEsportsSignalR");
    });
}

// ── 7. FluentValidation ──
builder.Services.AddValidatorsFromAssemblyContaining<AppleEsportsErp.Application.Validators.Auth.AdminLoginValidator>();

// ── 8. Application Services ──
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>().GetSection("Jwt");
    return new JwtTokenService(
        config["Secret"]!, config["RefreshSecret"]!,
        config["AccessExpiry"] ?? "15m", config["RefreshExpiry"] ?? "7d",
        config["Issuer"] ?? "AppleEsportsErp", config["Audience"] ?? "AppleEsportsErpClient"
    );
});
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IEmailService, EmailService>();
// Resolves this deployment's own public URL for links inside outgoing emails.
// Needed by AppUrlProvider, so an email link can fall back to the address the caller actually
// reached this server on instead of localhost.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentRequestUrl, AppleEsportsErp.Api.Services.CurrentRequestUrl>();
builder.Services.AddSingleton<IAppUrlProvider, AppUrlProvider>();

// How Head Office reaches out and drives a branch. Scoped, because it writes through the
// request's own DbContext and so joins that request's transaction.
builder.Services.AddScoped<AppleEsportsErp.Api.Services.IRemoteBranchControl,
                           AppleEsportsErp.Api.Services.RemoteBranchControl>();
builder.Services.AddScoped<IPcStatusService, PcStatusService>();
builder.Services.AddScoped<ITokenRevocationService, TokenRevocationService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IHubNotificationService, AppleEsportsErp.Api.Services.HubNotificationService>();

// Sprint 2 Services
builder.Services.AddScoped<IReservationService, ReservationService>();
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ICreditService, CreditService>();
builder.Services.AddScoped<IFoodOrderService, FoodOrderService>();
builder.Services.AddScoped<ICashRegisterService, CashRegisterService>();
builder.Services.AddScoped<ICashDeskService, CashDeskService>();
builder.Services.AddScoped<IShiftTakeoverService, ShiftTakeoverService>();
builder.Services.AddScoped<IEodService, EodService>();
builder.Services.AddScoped<IPcManagementService, PcManagementService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IReportsService, ReportsService>();
builder.Services.AddScoped<ISystemDesksService, AppleEsportsErp.Infrastructure.Services.SystemDesksService>();
builder.Services.AddScoped<IUnitOfWork, AppleEsportsErp.Infrastructure.Repositories.UnitOfWork>();
builder.Services.AddScoped<ISessionActivityService, SessionActivityService>();
builder.Services.AddScoped<IMaintenanceLogService, MaintenanceLogService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.ReservationBackgroundService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SharedStockWatcher>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.OpenSessionMonitorService>();

// Closes a trading day that has ended when nobody ticked "last shift of the day", so the report
// no longer depends on being remembered at 3am.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.TradingDayCloserService>();
// Puts a PC back to "Not Set Up" once its agent has gone quiet for good (uninstalled, retired),
// instead of it reading Idle/Free forever with no physical machine behind it.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.PcAgentWatchdogService>();
// Closes a session automatically once its PC has gone quiet for hours (PcAgentWatchdogService's
// own sweep deliberately skips a PC mid-session - this is that gap: nothing else ever ends a
// session whose PC just stopped reporting in without a proper lock/end).
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.StaleActiveSessionWatchdogService>();
// Re-queues a fresh sync attempt for any shift, cash register or pending credit that is still
// open right now but has no delivery attempt waiting in the outbox - the safety net for a
// capture that was missed for any reason. Branch-only; see the class remarks.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SyncReconciliationService>();
// The actual cross-check: fingerprints every watched row this branch has recently cared about
// and asks Head Office whether it still agrees, resending only whatever does not match. This is
// what actually guarantees the two databases stay the same, rather than just guaranteeing a
// delivery attempt happened once. Branch-only; see the class remarks.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SyncManifestReconcilerService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.FixedDurationSessionMonitorService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.DeferredBillingMonitorService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SessionActivityCleanupService>();
// Marks live sessions as still running, so a power cut can be told apart from play time.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SessionHeartbeatService>();
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SyncCourierService>();

// Re-attempts anything still sitting unapplied in the sync inbox - a shift that synced late
// otherwise leaves its cash register stuck forever, since the branch was already told
// "delivered" and never resends. Head Office only; see the class remarks.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.SyncInboxRetryService>();

// Tells Head Office which version this branch is running. Without it the Updates page cannot
// say what any branch is on, so an update could be pushed to four shops with no way of knowing
// whether any of them took it. Does nothing at Head Office, which has nobody to report to.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.BranchVersionReporterService>();

// Sends Head Office a picture of this shop every thirty seconds - who is on duty, which PCs are
// busy, what the drawer holds, how far behind sync is. State rather than history, so a missed
// beat costs nothing and there is no queue behind it. Does nothing at Head Office.
builder.Services.AddHostedService<AppleEsportsErp.Api.Services.BranchHeartbeatService>();

// Lets a fresh branch take Head Office's identifiers instead of inventing its own, which is
// what makes anything it later reports recognisable at Head Office.
builder.Services.AddHttpClient();
builder.Services.AddScoped<AppleEsportsErp.Api.Services.BranchAdoptionService>();

// One answer to "who is an admin", shared by every alert. Three separate places had
// their own answer and all three resolved to nobody on the live system.
builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();

builder.Services.AddScoped<IOfflineSyncService, OfflineSyncService>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();
builder.Services.AddScoped<IPricingProfileService, PricingProfileService>();
builder.Services.AddScoped<IVersionService, VersionService>();
builder.Services.AddScoped<IOutboxService, OutboxService>();
builder.Services.AddScoped<IEmailQueueService, EmailQueueService>();


// ── 9. Controllers + Swagger ──
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "AppleEsports ERP API", Version = "v2.0" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter JWT token",
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// ═══════════════════════════════════════════════
//  MIDDLEWARE PIPELINE
//  SOP order: Helmet → CORS → Auth → RBAC → Branch → Audit → Rate Limit
//  .NET mapping: ExceptionHandler → CORS → Auth → Authz → RateLimiter → Controllers
// ═══════════════════════════════════════════════

var app = builder.Build();

// ── Auto-migrate in Development (SOP §23: Database Architecture) ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        if (await db.Database.CanConnectAsync())
        {
            Log.Information("PostgreSQL connection verified ✓");
            await db.Database.MigrateAsync();
            Log.Information("Database migrations applied ✓");

            // The demo seed used to be called from here too, with only an IsDevelopment()
            // check and no IsHeadOffice() check at all - so a Development-mode BRANCH could
            // have run it, directly contradicting DbUpdater's own reasoning for why a branch
            // must never get this data (it invents identifiers Head Office never issued, which
            // is the exact fault Phase 2's adoption flow exists to prevent). Two call sites
            // enforcing two different rules for the same "should this seed run" question is how
            // a case gets missed - which is exactly what happened here. DbUpdater.UpdateSchema
            // is now the only place that decides, checking both IsHeadOffice() and
            // IsDevelopment() together.
            AppleEsportsErp.Api.DbUpdater.UpdateSchema(app);
            Log.Information("Database schema patches applied ✓");

            // Checked here, not only by the desktop app on launch, because the desktop app's own
            // attempt to do this needs elevation it does not normally have and has been silently
            // failing on every ordinary launch since the day it was written - see
            // AutoUpdateTaskGuard for the full reasoning. This service is always elevated and
            // always running, which the desktop app is neither.
            if (!AppleEsportsErp.Infrastructure.Configuration.DeploymentRole.IsHeadOffice(app.Configuration))
            {
                AppleEsportsErp.Api.AutoUpdateTaskGuard.EnsureRegistered(app.Logger);

                // Same reasoning, same fix shape: an already-running branch never gets the
                // installer's setup steps re-run, only new binaries - so a branch live before
                // this existed would stay unreachable from the LAN forever without this.
                AppleEsportsErp.Api.FirewallGuard.EnsureOpen(app.Logger);
            }
        }
        else
        {
            Log.Warning("PostgreSQL not available — skipping migration. Start Docker PostgreSQL to enable.");
        }
    }
    catch (Exception ex)
    {
        Log.Warning("Database migration skipped: {Message} | Inner: {Inner}", ex.Message, ex.InnerException?.Message);
    }
}

// ── Credit back time lost while we were down (power cut, restart, update) ──
// Must happen here, before app.Run() starts the hosted services: the fixed-duration
// monitor auto-stops sessions whose EndTime has passed, and after an outage that
// would close sessions which are only "expired" because the branch had no power.
// Branch-only, for the same reason the monitors are. Head Office's downtime is not the
// branches' downtime, so crediting every synced session for a Head Office restart would hand
// back time to customers who played through it perfectly happily, at four shops at once.
try
{
    if (!AppleEsportsErp.Infrastructure.Configuration.DeploymentRole.IsHeadOffice(app.Configuration))
    {
        var recoveryLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SessionDowntimeRecovery");
        await AppleEsportsErp.Api.Services.SessionDowntimeRecovery.RunAsync(app.Services, recoveryLogger);
    }
}
catch (Exception ex)
{
    // Never block start-up over this — worst case is that no time is credited back.
    Log.Warning("Session downtime recovery skipped: {Message}", ex.Message);
}

// ── 0. Who is actually calling ──
//
// Must be first, before anything reads an IP address. Head Office sits behind nginx, so every
// request arrives from nginx's own container address and every caller in the company looked
// like the same one machine. That is what made the rate limiter throttle the whole business at
// once, and it would equally have made the audit log record one address for every action ever
// taken.
//
// KnownNetworks/KnownProxies are cleared because the proxy is a Docker container on an address
// that changes when the stack is recreated, and an unlisted proxy is silently ignored - the
// header would be dropped and the symptom would come straight back with nothing to point at.
// Safe here only because this port is not reachable from outside; nginx is the only thing that
// can talk to it, so nothing else is in a position to forge the header.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { },
});

// ── 1. Global Exception Handler (maps from errorHandler.js) ──
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

// ── Development tools ──
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AppleEsports ERP API v2.0"));
}

// ── 2. CORS (maps from Helmet + CORS) ──
app.UseCors("AppleEsportsCors");

// ── 3. Authentication (maps from auth.js verifyToken) ──
app.UseAuthentication();

// ── Auto Migrate Database ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// ── 4. Authorization (maps from roles.js authorize/requireDashboardAccess) ──
app.UseAuthorization();

// ── 5. Rate Limiting (maps from rateLimit.js) ──
app.UseRateLimiter();

// ── 5b. Serve the dashboard itself, when it has been published alongside the API ──
// A branch install has no nginx: one Windows service answers both the API and the screens,
// which removes a whole moving part from every operator PC. In Docker the client container
// still serves the UI and wwwroot simply does not exist, so this stays inert there.
var dashboardRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");

// How the browser is allowed to keep the dashboard's files.
//
// This is what decides whether an update is actually visible after it installs. The build
// gives every file under /assets a name containing a hash of its contents, so index-Bh2pXVML.js
// can never change meaning - a different build produces a different name. Those are safe to
// keep for ever. index.html is the opposite: its name never changes and it is the thing that
// names which bundle to load. Cache that and the browser goes on loading last week's app from
// disk, no matter what was installed underneath it.
//
// That is precisely what happened on a real branch. 2.2.5 installed correctly, and the counter
// still showed 2.2.4 until somebody pressed Ctrl+Shift+R. No operator will ever do that; they
// would have reported updates as broken, and they would have been right to. Every release after
// this one would have hit the same wall.
//
// favicon, logo and icons sit at the root and are not hashed either, so they revalidate too.
// One conditional request each, answered 304 in a few bytes.
static void CacheDashboardFiles(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext ctx)
{
    var path = ctx.Context.Request.Path.Value ?? string.Empty;

    ctx.Context.Response.Headers.CacheControl =
        path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache, must-revalidate";
}

var dashboardFiles = new StaticFileOptions { OnPrepareResponse = CacheDashboardFiles };

if (Directory.Exists(dashboardRoot))
{
    app.UseDefaultFiles();
    app.UseStaticFiles(dashboardFiles);
    Log.Information("Serving the dashboard from {Root}", dashboardRoot);
}

// ── 6. Map Controllers (maps from routes/index.js registerRoutes) ──
app.MapControllers();

// ── 7. Map SignalR Hubs (maps from sockets/index.js registerSocketHandlers) ──
app.MapHub<SessionHub>("/hubs/sessions");
app.MapHub<BillingHub>("/hubs/billing");
app.MapHub<ReservationHub>("/hubs/reservations");
app.MapHub<PcStatusHub>("/hubs/pc-status");
app.MapHub<FoodOrderHub>("/hubs/food-orders");
app.MapHub<CashHub>("/hubs/cash");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapHub<PcOverlayHub>("/hubs/pc-overlay");

// The dashboard is a single-page app: refreshing on /app/sessions must return index.html
// rather than a 404, because that route only exists in the browser. Registered after the
// controllers and hubs so it can never shadow a real endpoint.
if (Directory.Exists(dashboardRoot))
{
    // The same no-cache rules, passed in explicitly: the fallback does not inherit the options
    // given to UseStaticFiles. Miss this and every deep link - /app/sessions, the page an
    // operator actually has open all day - still serves a cached index.html and still shows
    // the old app, while a plain visit to the root correctly shows the new one.
    app.MapFallbackToFile("index.html", dashboardFiles);
}

// ── Startup banner ──
app.Lifetime.ApplicationStarted.Register(() =>
{
    Log.Information("╔═══════════════════════════════════════════════╗");
    Log.Information("║   AppleEsports ERP — .NET 8 Enterprise Backend  ║");
    Log.Information("║   Environment: {Env}", app.Environment.EnvironmentName);
    Log.Information("║   Hubs: 7 SignalR endpoints mapped            ║");
    Log.Information("║   Controllers: 15 API controllers mapped      ║");
    Log.Information("╚═══════════════════════════════════════════════╝");
});

app.Run();

// Top-level statements need an explicit success code because the configuration check
// above returns 1 on failure.
return 0;
