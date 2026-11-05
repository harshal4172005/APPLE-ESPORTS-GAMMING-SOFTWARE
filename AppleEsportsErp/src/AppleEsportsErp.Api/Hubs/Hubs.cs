using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Hubs;

/// <summary>
/// Base hub with branch isolation and group management.
/// SOP §20: All dashboards require live synchronization.
/// Q2 Decision: Auto-negotiation (WebSocket primary, SSE + Long Polling fallback).
/// </summary>
[Authorize]
public abstract class BranchAwareHub : Hub
{
    protected ILogger Logger { get; }

    protected BranchAwareHub(ILogger logger) => Logger = logger;

    public override async Task OnConnectedAsync()
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        var branchId = Context.User?.FindFirstValue("branchId");
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = Context.User?.FindFirstValue(ClaimTypes.Name);

        // SOP §6.4: Operators and Admins join their branch group
        if ((role == Roles.Operator || role == Roles.Admin) && !string.IsNullOrEmpty(branchId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"branch:{branchId}");
            if (role == Roles.Operator)
            {
                AppleEsportsErp.Application.Services.OperatorPresenceTracker.OperatorConnected(branchId);
                var notificationService = Context.GetHttpContext()?.RequestServices.GetService<AppleEsportsErp.Application.Interfaces.IHubNotificationService>();
                if (notificationService != null)
                {
                    await notificationService.TriggerDashboardRefreshAsync();
                }
            }
        }

        // Super Admin and Admin join all-branches group
        if (role == Roles.SuperAdmin || role == Roles.Admin)
            await Groups.AddToGroupAsync(Context.ConnectionId, "admin:all");

        // User-specific group for targeted notifications
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");

        Logger.LogInformation("Hub connected: {User} ({Role}) [{Hub}] - ConnectionId: {ConnectionId} - Branch: {BranchId}", 
            userName, role, GetType().Name, Context.ConnectionId, branchId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userName = Context.User?.FindFirstValue(ClaimTypes.Name);
        if (exception != null)
        {
            Logger.LogWarning(exception, "Hub disconnected with error: {User} [{Hub}] - ConnectionId: {ConnectionId}", 
                userName, GetType().Name, Context.ConnectionId);
        }
        else
        {
            Logger.LogInformation("Hub disconnected gracefully: {User} [{Hub}] - ConnectionId: {ConnectionId}", 
                userName, GetType().Name, Context.ConnectionId);
        }

        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        var branchId = Context.User?.FindFirstValue("branchId");
        if (role == Roles.Operator && !string.IsNullOrEmpty(branchId))
        {
            AppleEsportsErp.Application.Services.OperatorPresenceTracker.OperatorDisconnected(branchId);
            var notificationService = Context.GetHttpContext()?.RequestServices.GetService<AppleEsportsErp.Application.Interfaces.IHubNotificationService>();
            if (notificationService != null)
            {
                await notificationService.TriggerDashboardRefreshAsync();
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>SOP §7: Session state sync — /hubs/sessions</summary>
public class SessionHub : BranchAwareHub
{
    public SessionHub(ILogger<SessionHub> logger) : base(logger) { }
}

/// <summary>SOP §9: Billing counter sync — /hubs/billing</summary>
public class BillingHub : BranchAwareHub
{
    public BillingHub(ILogger<BillingHub> logger) : base(logger) { }
}

/// <summary>SOP §8: Reservation state sync — /hubs/reservations</summary>
public class ReservationHub : BranchAwareHub
{
    public ReservationHub(ILogger<ReservationHub> logger) : base(logger) { }
}

/// <summary>SOP §17: PC state sync — /hubs/pc-status — Enhanced for Client Agent dual-connection</summary>
public class PcStatusHub : BranchAwareHub
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PcOverlayHub> _pcOverlayHub;
    private readonly IHubNotificationService _hubNotificationService;
    private readonly IAuditService _auditService;

    public PcStatusHub(
        ILogger<PcStatusHub> logger,
        IServiceScopeFactory scopeFactory,
        IHubContext<PcOverlayHub> pcOverlayHub,
        IHubNotificationService hubNotificationService,
        IAuditService auditService) : base(logger)
    {
        _scopeFactory = scopeFactory;
        _pcOverlayHub = pcOverlayHub;
        _hubNotificationService = hubNotificationService;
        _auditService = auditService;
    }

    /// <summary>Called by the Gaming PC Agent when it connects</summary>
    public async Task AgentConnected(string pcId, string connectionMode)
    {
        // Add agent to its own group so we can send targeted commands
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent:{pcId}");
        
        // Notify all Operator/Admin dashboards in this branch
        var branchId = Context.User?.FindFirstValue("branchId");
        if (!string.IsNullOrEmpty(branchId))
        {
            await Clients.Group($"branch:{branchId}").SendAsync("AgentStatusChanged", new
            {
                PcId = pcId,
                IsOnline = true,
                ConnectionMode = connectionMode,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        // Also notify SuperAdmin
        await Clients.Group("admin:all").SendAsync("AgentStatusChanged", new
        {
            PcId = pcId,
            IsOnline = true,
            ConnectionMode = connectionMode,
            Timestamp = DateTimeOffset.UtcNow
        });

        Logger.LogInformation("Agent connected: PC {PcId} in {Mode} mode", pcId, connectionMode);
    }

    /// <summary>Called by the Gaming PC Agent when it switches between LAN and Cloud</summary>
    public async Task AgentModeChanged(string pcId, string newMode)
    {
        var branchId = Context.User?.FindFirstValue("branchId");
        var payload = new
        {
            PcId = pcId,
            ConnectionMode = newMode,
            Timestamp = DateTimeOffset.UtcNow
        };

        if (!string.IsNullOrEmpty(branchId))
            await Clients.Group($"branch:{branchId}").SendAsync("AgentModeChanged", payload);

        await Clients.Group("admin:all").SendAsync("AgentModeChanged", payload);

        Logger.LogWarning("Agent mode changed: PC {PcId} -> {Mode}", pcId, newMode);
    }

    /// <summary>Called by Operator or Admin to unlock a Gaming PC</summary>
    public async Task SendUnlockCommand(string pcId, int durationMinutes, string? customerName)
    {
        await Clients.Group($"agent:{pcId}").SendAsync("UnlockSession", new
        {
            DurationMinutes = durationMinutes,
            CustomerName = customerName,
            Timestamp = DateTimeOffset.UtcNow
        });

        Logger.LogInformation("Unlock command sent to PC {PcId} for {Duration}min", pcId, durationMinutes);
    }

    /// <summary>Called by Operator or Admin to lock a Gaming PC</summary>
    public async Task SendLockCommand(string pcId)
    {
        await Clients.Group($"agent:{pcId}").SendAsync("LockSession", new
        {
            Timestamp = DateTimeOffset.UtcNow
        });

        Logger.LogInformation("Lock command sent to PC {PcId}", pcId);
    }

    /// <summary>
    /// Shuts one gaming PC down, at the request of the branch that owns it.
    ///
    /// The comment here used to read "Called by Admin" and that was the only thing enforcing it -
    /// there was no role check at all, just the class-level [Authorize] on BranchAwareHub, which
    /// asks whether you are logged in and nothing else. So every authenticated account in the
    /// system could shut machines down, including a gaming PC's own user_panel token: a customer
    /// seat could have switched off the row. Every other sensitive PC action goes through
    /// PcManagementController with an explicit role policy; this one was reachable directly over
    /// the socket and was missed.
    ///
    /// It also took a pcId and sent to agent:{pcId} without ever asking whose PC that was, so an
    /// operator at one branch could shut down another branch's machine by id.
    ///
    /// Also sent to PcOverlayHub's own pc:{pcId} group, which is where a real machine actually is.
    /// agent:{pcId} is who AppleEsportsErp.ClientAgent joins, and nothing puts a real gaming PC in
    /// that group today - ClientAgent is never launched by the installer and ships with a
    /// placeholder token, so it never connects (see PHASE3_PLAN.md). The WebView2 page every
    /// gaming PC actually runs joins pc:{pcId} on load (OverlaySocketContext.jsx) for its lock
    /// screen and session overlay, and is genuinely connected. Kept both rather than replacing one
    /// with the other, so a future working ClientAgent does not need this touched again.
    /// </summary>
    public async Task SendShutdownCommand(string pcId)
    {
        var branchId = RequireShutdownPermission();

        if (!await PcBelongsToBranchAsync(pcId, branchId))
            throw new HubException("That PC does not belong to this branch.");

        var pcGuid = Guid.Parse(pcId); // safe: PcBelongsToBranchAsync above already parsed this

        // Marks the PC powered-off in the database, not only over the wire - without this the
        // shutdown was a SignalR message nobody's screen remembered, and the tile never changed
        // colour no matter how many times it was sent. See Pc.PoweredOff for why this is its own
        // column rather than State.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == pcGuid);

            // AwaitingSetup means no real machine has ever claimed this PC - there is nothing to
            // shut down, and marking it PoweredOff would make an unclaimed PC indistinguishable
            // from a real one that was just switched off. See the same guard in
            // SendShutdownAllCommand.
            if (pc != null && pc.State != Domain.Enums.PcState.AwaitingSetup)
            {
                pc.PoweredOff = true;
                pc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        await Clients.Group($"agent:{pcId}").SendAsync("ForceShutdown", new
        {
            Timestamp = DateTimeOffset.UtcNow
        });

        await _pcOverlayHub.Clients.Group($"pc:{pcId}").SendAsync("ShutdownPc");

        // Same broadcast a session start/stop or a maintenance flag already triggers, so every
        // open dashboard sees the tile change colour immediately instead of waiting on the
        // 20-second safety poll.
        await _hubNotificationService.BroadcastPcStatusChangeAsync(branchId, pcGuid);

        var actorRole = Context.User?.FindFirstValue(ClaimTypes.Role);
        var actorId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = Context.User?.FindFirstValue(ClaimTypes.Name);
        var isOperatorActor = actorRole == Roles.Operator;

        // Every other PC action (add, update, maintenance, delete) writes to the Audit Trail -
        // this one only ever wrote to the technical log, which an operator or Head Office never
        // sees. A shutdown is exactly the kind of action that needs to be on the record.
        await _auditService.LogAsync(new AuditEntry
        {
            UserId = isOperatorActor ? null : (Guid.TryParse(actorId, out var uid) ? uid : null),
            OperatorId = isOperatorActor && Guid.TryParse(actorId, out var oid) ? oid : null,
            UserRole = actorRole,
            UserName = actorName,
            Action = "pc_shutdown",
            BranchId = branchId,
            TargetType = "pc",
            TargetId = pcGuid,
            Details = null
        });

        Logger.LogWarning(
            "Shutdown command sent to PC {PcId} by {Role} {User} of branch {BranchId}",
            pcId, actorRole, actorName, branchId);
    }

    /// <summary>
    /// Shuts down every gaming PC at this branch - the closing-time action.
    ///
    /// Deliberately takes no list of PCs from the caller. The branch is read from the caller's own
    /// token and the machines are looked up here, so this cannot be aimed at another shop however
    /// it is called, and an operator cannot be tricked into sending ids they did not choose.
    ///
    /// A PC with somebody still playing on it is skipped rather than switched off underneath them,
    /// and the count of those is reported back so whoever pressed it knows the room is not empty
    /// yet. Closing up is not a reason to cut a paying customer off mid-session; if that is really
    /// wanted, the session gets stopped and billed first and then this does the rest.
    /// </summary>
    public async Task<object> SendShutdownAllCommand()
    {
        var branchId = RequireShutdownPermission();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pcs = await db.Pcs.AsNoTracking()
            .Where(p => p.BranchId == branchId && !p.IsDeleted && p.IsActive)
            .Select(p => new { p.Id, p.State })
            .ToListAsync();

        var excluded = pcs.Where(p =>
            p.State == Domain.Enums.PcState.Active ||
            p.State == Domain.Enums.PcState.AwaitingBilling ||
            p.State == Domain.Enums.PcState.AwaitingSetup ||
            p.State == Domain.Enums.PcState.UnderMaintenance).ToList();

        var targets = pcs.Except(excluded).ToList();

        // Same reasoning as SendShutdownCommand: mark these powered-off in the database so their
        // tiles actually change colour, not only send the live command. Loaded separately from
        // the AsNoTracking query above because that one only projects Id/State.
        if (targets.Count > 0)
        {
            var targetIds = targets.Select(t => t.Id).ToList();
            var targetPcs = await db.Pcs.Where(p => targetIds.Contains(p.Id)).ToListAsync();
            foreach (var pc in targetPcs)
            {
                pc.PoweredOff = true;
                pc.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        var actorRole = Context.User?.FindFirstValue(ClaimTypes.Role);
        var actorId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorName = Context.User?.FindFirstValue(ClaimTypes.Name);
        var isOperatorActor = actorRole == Roles.Operator;

        foreach (var pc in targets)
        {
            await Clients.Group($"agent:{pc.Id}").SendAsync("ForceShutdown", new
            {
                Timestamp = DateTimeOffset.UtcNow
            });

            // See the comment on SendShutdownCommand above - this is the group a real machine is
            // actually in today.
            await _pcOverlayHub.Clients.Group($"pc:{pc.Id}").SendAsync("ShutdownPc");

            // Same live-dashboard broadcast SendShutdownCommand uses, sent per PC so every tile
            // updates immediately.
            await _hubNotificationService.BroadcastPcStatusChangeAsync(branchId, pc.Id);

            // One entry per PC, same action name SendShutdownCommand uses, so the Audit Trail
            // reads the same regardless of whether a PC was shut down on its own or as part of
            // closing the whole branch.
            await _auditService.LogAsync(new AuditEntry
            {
                UserId = isOperatorActor ? null : (Guid.TryParse(actorId, out var uid) ? uid : null),
                OperatorId = isOperatorActor && Guid.TryParse(actorId, out var oid) ? oid : null,
                UserRole = actorRole,
                UserName = actorName,
                Action = "pc_shutdown",
                BranchId = branchId,
                TargetType = "pc",
                TargetId = pc.Id,
                Details = null
            });
        }

        Logger.LogWarning(
            "Shut down all PCs at branch {BranchId}: {Sent} sent, {Skipped} skipped as busy, by {Role} {User}",
            branchId, targets.Count, excluded.Count, actorRole, actorName);

        return new { sent = targets.Count, skippedBusy = excluded.Count };
    }

    /// <summary>
    /// Who is allowed to switch a machine off, and which branch's machines they get.
    ///
    /// Operators, and Admins standing at a branch having used Quick-Switch. Both are people who
    /// can see the room; switching off a PC is a physical act and the person doing it should be
    /// able to look at the screen first. Head Office is deliberately excluded - it has no way of
    /// knowing whether somebody is sitting at that machine, and no reason to need this.
    ///
    /// The branch comes from the caller's own token, never from anything they send, which is what
    /// keeps one shop out of another's machines.
    /// </summary>
    private Guid RequireShutdownPermission()
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);

        if (role != Roles.Operator && role != Roles.Admin)
            throw new HubException("Only an operator or an admin at the branch can shut a PC down.");

        var branchClaim = Context.User?.FindFirstValue("branchId");
        if (!Guid.TryParse(branchClaim, out var branchId))
            throw new HubException("No branch on this account, so there are no PCs to shut down.");

        return branchId;
    }

    private async Task<bool> PcBelongsToBranchAsync(string pcId, Guid branchId)
    {
        if (!Guid.TryParse(pcId, out var id)) return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Pcs.AsNoTracking()
            .AnyAsync(p => p.Id == id && p.BranchId == branchId && !p.IsDeleted);
    }

    /// <summary>
    /// Heartbeat from agent to keep connection alive. Also the only place a gaming PC's agent
    /// version reaches the database - agentVersion is optional so an older agent talking to a
    /// newer server (which has not yet installed the update that adds this parameter) still
    /// heartbeats successfully, just without a version to report yet.
    ///
    /// Written only when it actually changed. A PC heartbeats every 10 seconds
    /// (DualConnectionService.HealthCheckIntervalSeconds) and its version changes maybe once a
    /// month - writing on every heartbeat regardless would be 35 PCs' worth of UPDATEs every
    /// 10 seconds for a value that is, almost always, identical to what is already stored.
    /// </summary>
    public async Task AgentHeartbeat(string pcId, string mode, string? agentVersion = null)
    {
        Logger.LogDebug("Heartbeat from PC {PcId} in {Mode} mode", pcId, mode);

        if (!string.IsNullOrWhiteSpace(agentVersion) && Guid.TryParse(pcId, out var id))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
            if (pc != null && pc.AgentVersion != agentVersion)
            {
                pc.AgentVersion = agentVersion;
                await db.SaveChangesAsync();
            }
        }
    }
}

/// <summary>SOP §12: Food order sync — /hubs/food-orders</summary>
public class FoodOrderHub : BranchAwareHub
{
    public FoodOrderHub(ILogger<FoodOrderHub> logger) : base(logger) { }
}

/// <summary>SOP §10/§11: Cash register/desk sync — /hubs/cash</summary>
public class CashHub : BranchAwareHub
{
    public CashHub(ILogger<CashHub> logger) : base(logger) { }
}

/// <summary>Cross-cutting alerts and notifications — /hubs/notifications</summary>
public class NotificationHub : BranchAwareHub
{
    public NotificationHub(ILogger<NotificationHub> logger) : base(logger) { }
}
