using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Sessions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using AppleEsportsErp.Api.Controllers; // To access BillingController.PendingApprovals
using AppleEsportsErp.Api.Extensions;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>
/// Unauthenticated public endpoints used by the user panel (kiosk, walk-in, member portal, PC overlay).
/// No JWT required — branch isolation is handled by explicit branchId parameters.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController : ControllerBase
{
    /// <summary>
    /// Sessions that have already had their low-balance alert sent, so the overlay's
    /// once-per-second tick can't spam the member's inbox or the operator's alert feed.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> LowBalanceAlertsSent = new();

    private readonly AppDbContext _db;
    private readonly AppleEsportsErp.Api.Services.IRemoteBranchControl _remote;

    public PublicController(AppDbContext db, AppleEsportsErp.Api.Services.IRemoteBranchControl remote)
    {
        _db = db;
        _remote = remote;
    }

    /// <summary>List all active branches — shown in walk-in & member branch selection screens.</summary>
    [HttpGet("branches")]
    public async Task<IActionResult> GetBranches()
    {
        var branches = await _db.Branches
            .Where(b => b.Status == BranchStatus.Active)
            .OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name, b.Address })
            .ToListAsync();

        return Ok(ApiResponse<object>.Ok(branches));
    }

    /// <summary>List idle PCs for a branch — shown in member portal PC selection screen.</summary>
    [HttpGet("branches/{branchId:guid}/pcs/idle")]
    public async Task<IActionResult> GetIdlePcs(Guid branchId)
    {
        var pcs = await _db.Pcs
            .Where(p => p.BranchId == branchId && p.State == PcState.Idle)
            .OrderBy(p => p.PcNumber)
            .Select(p => new { p.Id, Name = p.PcName ?? p.PcNumber })
            .ToListAsync();

        return Ok(ApiResponse<object>.Ok(pcs));
    }

    /// <summary>List all PCs for a branch — used by setup screen.</summary>
    [HttpGet("branches/{branchId:guid}/pcs")]
    public async Task<IActionResult> GetPcsForBranch(Guid branchId)
    {
        var pcs = await _db.Pcs
            .Where(p => p.BranchId == branchId)
            .OrderBy(p => p.PcNumber)
            .Select(p => new { p.Id, p.PcNumber, p.PcName, Name = p.PcName ?? p.PcNumber })
            .ToListAsync();

        return Ok(ApiResponse<object>.Ok(pcs));
    }

    /// <summary>
    /// Whether any PC at this branch is currently in an active session - used by the branch's
    /// own silent auto-update task (apply-update.ps1) to decide whether to install right now or
    /// wait for the next 15-minute pass.
    ///
    /// Anonymous on purpose: that script runs as SYSTEM, with no operator logged in and nothing
    /// to authenticate with. It mirrors exactly what BranchBusySignal.jsx already asks the
    /// dashboard's own /api/pcs for - same question, asked by a caller with no session cookie.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/busy")]
    public async Task<IActionResult> IsBranchBusy(Guid branchId)
    {
        var busy = await _db.Pcs.AnyAsync(p => p.BranchId == branchId && p.State == PcState.Active);
        return Ok(ApiResponse<object>.Ok(new { busy }));
    }

    /// <summary>
    /// Get the active session for a PC — used by the overlay on startup to load real session data.
    /// Accepts either a PC UUID or a PC name string (e.g. "PC-07").
    /// </summary>
    [HttpGet("session/pc/{pcIdentifier}")]
    public async Task<IActionResult> GetSessionByPc(string pcIdentifier)
    {
        // Try UUID first, fall back to name lookup
        Domain.Entities.Pc? pc = null;
        if (Guid.TryParse(pcIdentifier, out var pcGuid))
        {
            pc = await _db.Pcs.Include(p => p.PricingProfile).FirstOrDefaultAsync(p => p.Id == pcGuid);
        }
        else
        {
            pc = await _db.Pcs.Include(p => p.PricingProfile).FirstOrDefaultAsync(p => p.PcNumber == pcIdentifier || p.PcName == pcIdentifier);
        }

        if (pc == null)
            return Ok(new { success = true, data = (object?)null });

        var session = await _db.Sessions
            .Include(s => s.FoodOrders).ThenInclude(fo => fo.Items)
            .Where(s => s.PcId == pc.Id && s.State == SessionState.Active)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (session == null)
            return Ok(new { success = true, data = (object?)null });

        var foodCharges = session.FoodOrders.Sum(fo => fo.TotalAmount);
        // Downtime already credited back is excluded, so the countdown a customer sees on
        // the PC overlay matches what they are actually billed for after a power cut.
        var elapsedMinutes = (double)AppleEsportsErp.Application.Services.SessionTimeCalculator.ElapsedMinutes(
            session.StartTime, session.PausedSeconds, DateTimeOffset.UtcNow);
        var remainingSeconds = session.PlannedDurationMin.HasValue
            ? Math.Max(0, (session.PlannedDurationMin.Value - elapsedMinutes) * 60)
            : null as double?;

        var globalConfig = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == "global_system_rules");
        decimal defaultBaseRate = 0m; // No fabricated fallback — PC must have a Pricing Profile assigned
        Dictionary<string, decimal> hzPricing = new();
        if (globalConfig != null && !string.IsNullOrEmpty(globalConfig.ConfigValue))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(globalConfig.ConfigValue);
                if (doc.RootElement.TryGetProperty("pricing", out var pricing))
                {
                    if (pricing.TryGetProperty("baseRate", out var br))
                        defaultBaseRate = br.GetDecimal();
                        
                    if (pricing.TryGetProperty("hzPricing", out var hzObj))
                    {
                        foreach (var prop in hzObj.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                                hzPricing[prop.Name] = prop.Value.GetDecimal();
                        }
                    }
                }
            }
            catch { /* fallback */ }
        }

        decimal ratePerHour = defaultBaseRate;
        if (!string.IsNullOrEmpty(pc.MonitorHz) && hzPricing.TryGetValue(pc.MonitorHz, out var hrRate))
        {
            ratePerHour = hrRate;
        }

        int bufferMinutes = AppleEsportsErp.Application.Services.SessionPricingCalculator.DefaultBufferMinutes;
        if (pc.PricingProfile != null)
        {
            // The PC's assigned Branch-Wise Pricing Profile always wins over the global fallback,
            // matching the rate used everywhere else (session start, billing, operator PC card).
            ratePerHour = pc.PricingProfile.BaseHourlyRate;
            bufferMinutes = pc.PricingProfile.BufferMinutes;
        }

        // Package-aware - see SessionPricingCalculator.CalculateLiveGamingAmount. This session's
        // own committed package (if any) wins, the same as everywhere else that shows a live
        // amount; activePackages below is only ever a list of what's available, never what this
        // particular session is actually running under.
        decimal liveGamingCharges = AppleEsportsErp.Application.Services.SessionPricingCalculator.CalculateLiveGamingAmount(
            session.PackagePrice, session.PlannedDurationMin, ratePerHour, bufferMinutes, (decimal)elapsedMinutes);

        decimal? walletBalance = null;
        decimal? gamingBalance = null;
        decimal? foodBalance = null;
        if (session.MemberId.HasValue)
        {
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == session.MemberId.Value);
            if (member != null)
            {
                walletBalance = member.GamingBalance + member.FoodBalance;
                gamingBalance = member.GamingBalance;
                foodBalance = member.FoodBalance;
            }
        }

        var result = new
        {
            sessionId = session.Id.ToString(),
            pcId = pc.Id.ToString(),
            pcName = pc.PcName ?? pc.PcNumber,
            monitorHz = pc.MonitorHz,
            customerName = session.CustomerName ?? "Guest",
            sessionStart = session.StartTime,
            // Played time as the server bills it (paused/downtime excluded), so the PC screen
            // counts on from this instead of from StartTime on its own clock - a PC with a wrong
            // clock, or a session that was paused, used to show hours nobody played.
            elapsedSeconds = (int)(elapsedMinutes * 60),
            remainingTime = remainingSeconds.HasValue ? (int)remainingSeconds.Value : (int?)null,
            plannedDurationMin = session.PlannedDurationMin,
            ratePerHour = ratePerHour,
            bufferMinutes = bufferMinutes,
            gamingCharges = liveGamingCharges,
            foodCharges,
            foodItems = session.FoodOrders
                .SelectMany(fo => fo.Items)
                .Select(i => new { i.ItemName, i.Quantity, i.UnitPrice })
                .ToList(),
            totalBill = liveGamingCharges + foodCharges,
            sessionStatus = session.State.ToString().ToLowerInvariant(),
            memberId = session.MemberId?.ToString(),
            walletBalance = walletBalance,
            gamingBalance = gamingBalance,
            foodBalance = foodBalance
        };

        return Ok(ApiResponse<object>.Ok(result));
    }
    [HttpGet("pcs/{pcIdentifier}")]
    public async Task<IActionResult> GetPc(string pcIdentifier)
    {
        Domain.Entities.Pc? pc = null;
        if (Guid.TryParse(pcIdentifier, out var pcGuid))
        {
            pc = await _db.Pcs.Include(p => p.Branch).Include(p => p.PricingProfile).FirstOrDefaultAsync(p => p.Id == pcGuid);
        }
        else
        {
            pc = await _db.Pcs.Include(p => p.Branch).Include(p => p.PricingProfile).FirstOrDefaultAsync(p => p.PcNumber == pcIdentifier || p.PcName == pcIdentifier);
        }

        if (pc == null)
            return Ok(new { success = false, error = "PC not found" });

        // State changes the moment an operator flags/clears maintenance - a customer-facing
        // screen still showing yesterday's answer here is a real, visible mistake, not a
        // cosmetic one. Same reasoning as the plans endpoint's own no-cache headers.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        decimal rate = pc.PricingProfile?.BaseHourlyRate ?? 0m;

        return Ok(ApiResponse<object>.Ok(new { id = pc.Id, name = pc.PcName ?? pc.PcNumber, branchId = pc.BranchId, branchName = pc.Branch?.Name, monitorHz = pc.MonitorHz, ratePerHour = rate, state = pc.State.ToString() }));
    }

    [HttpPost("pcs/{pcId:guid}/hz")]
    public async Task<IActionResult> SetPcMonitorHz(Guid pcId, [FromBody] SetMonitorHzDto dto)
    {
        var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId);
        if (pc == null)
            return Ok(new { success = false, error = "PC not found" });

        pc.MonitorHz = dto.MonitorHz;
        pc.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new { success = true }));
    }

    /// <summary>
    /// The one place a gaming PC's own AppleEsports.exe reports what version it is actually
    /// running - separate from AgentVersion, which is the screen-lock agent's own, independent
    /// version. Called by MainForm.cs every 30 seconds (piggybacked on its own update-check
    /// loop) so "N of M gaming PCs up to date" on the Updates page reflects the program a
    /// customer actually plays through, not a different one on the same machine.
    /// </summary>
    [HttpPost("pcs/{pcId:guid}/app-version")]
    public async Task<IActionResult> ReportPcAppVersion(Guid pcId, [FromBody] ReportPcAppVersionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Version))
            return Ok(new { success = false, error = "No version given" });

        var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId);
        if (pc == null)
            return Ok(new { success = false, error = "PC not found" });

        // Also proof the PC is switched on - this runs every 30 s while AppleEsports.exe is up -
        // so it clears the "shut down" flag set when it powered off (see ReportPcPoweredOff).
        if (pc.AppVersion != dto.Version || pc.PoweredOff)
        {
            pc.AppVersion = dto.Version;
            pc.PoweredOff = false;
            pc.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(ApiResponse<object>.Ok(new { success = true }));
    }

    /// <summary>
    /// The gaming PC was switched off while a session was running - shut down without Logout,
    /// or found on its next boot to have lost power. Ends that session at the moment the PC
    /// went off, exactly as Logout would (member: gaming/food wallets, any shortfall left to
    /// collect; walk-in: bill to the counter), so nobody is charged for time after the PC was
    /// off and the tile stops showing a running session on a dark machine.
    ///
    /// Keyed on the session id the PC itself was showing, not just the PC: it can only ever end
    /// that one session, never a newer one started after the PC came back.
    /// </summary>
    [HttpPost("pcs/{pcId:guid}/powered-off")]
    public async Task<IActionResult> ReportPcPoweredOff(
        Guid pcId, [FromBody] PcPoweredOffDto dto,
        [FromServices] ISessionService sessionService,
        [FromServices] AppleEsportsErp.Application.Interfaces.IHubNotificationService hubNotifier)
    {
        // Sessions live at the branch - Head Office never ends one itself.
        if (_remote.MustTravel)
            return Ok(new { success = false, error = "Sessions are ended at the branch, not Head Office." });

        var session = await _db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == dto.SessionId && s.PcId == pcId);
        if (session == null)
            return Ok(new { success = false, error = "No such session on this PC." });

        if (session.State is not (SessionState.Active or SessionState.Interrupted))
            return Ok(ApiResponse<object>.Ok(new { alreadyEnded = true }));

        var now = DateTimeOffset.UtcNow;
        var endAt = dto.At is { } at && at <= now && at >= session.StartTime ? at : now;

        var sysUsername = $"system_admin_{session.BranchId:N}";
        var sysOp = await _db.Operators.AsNoTracking()
            .FirstOrDefaultAsync(o => o.BranchId == session.BranchId && o.Username == sysUsername);
        if (sysOp == null)
            return Ok(new { success = false, error = "System operator not found." });

        HttpContext.Items["BranchId"] = session.BranchId.ToString();
        var result = await sessionService.StopSessionAsync(session.BranchId, sysOp.Id, session.Id, deferPayment: false, asOfOverride: endAt);

        var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId);
        if (pc != null)
        {
            pc.PoweredOff = true;
            pc.UpdatedAt = now;
            await _db.SaveChangesAsync();
            await hubNotifier.BroadcastPcStatusChangeAsync(session.BranchId, pc.Id);
        }

        return Ok(ApiResponse<object>.Ok(new { ended = true, endedAt = endAt, sessionResult = result }));
    }

    [HttpGet("pcs/{pcId}/plans")]
    public async Task<IActionResult> GetPcPlans(string pcId)
    {
        // Pricing changes at any moment - a stale plan list showing an old price on the operator
        // console or the customer-facing PC screen is a money mistake, not a cosmetic one.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        Domain.Entities.Pc? pc = null;
        if (Guid.TryParse(pcId, out var pcGuid))
        {
            pc = await _db.Pcs.Include(p => p.Branch).Include(p => p.PricingProfile).ThenInclude(pp => pp!.Packages).FirstOrDefaultAsync(p => p.Id == pcGuid);
        }
        else
        {
            pc = await _db.Pcs.Include(p => p.Branch).Include(p => p.PricingProfile).ThenInclude(pp => pp!.Packages).FirstOrDefaultAsync(p => p.PcNumber == pcId || p.PcName == pcId);
        }

        if (pc == null)
            return Ok(new { success = false, error = "PC not found" });

        decimal ratePerHour = pc.PricingProfile?.BaseHourlyRate ?? 0m;
        string planName = string.IsNullOrEmpty(pc.MonitorHz) ? "Standard" : $"{pc.MonitorHz}Hz Tier";

        var plans = BuildPlansForProfile(pc.PricingProfile, planName);

        return Ok(ApiResponse<object>.Ok(plans));
    }

    /// <summary>Custom packages replace the auto-generated 1/2/3-hour multiples entirely when a
    /// profile has any active ones configured; otherwise falls back to the multiples exactly as
    /// before, so every branch without custom packages is unaffected.</summary>
    private static List<object> BuildPlansForProfile(Domain.Entities.PricingProfile? profile, string planName, string tier = "", string? tierLabel = null)
    {
        decimal ratePerHour = profile?.BaseHourlyRate ?? 0m;
        var activePackages = profile?.Packages?
            .Where(pkg => pkg.IsActive)
            .OrderBy(pkg => pkg.SortOrder)
            .ThenBy(pkg => pkg.DurationMinutes)
            .ToList() ?? new List<Domain.Entities.PricingPackage>();

        var plans = new List<object>();

        if (activePackages.Any())
        {
            foreach (var pkg in activePackages)
                plans.Add(new { id = pkg.Id, name = $"{pkg.Name} ({planName})", duration = pkg.DurationMinutes, price = pkg.Price, tier, tierLabel = tierLabel ?? planName, isPostpaid = false });
        }
        else
        {
            plans.Add(new { id = Guid.NewGuid(), name = $"1 Hour ({planName})", duration = 60, price = ratePerHour, tier, tierLabel = tierLabel ?? planName, isPostpaid = false });
            plans.Add(new { id = Guid.NewGuid(), name = $"2 Hours ({planName})", duration = 120, price = ratePerHour * 2, tier, tierLabel = tierLabel ?? planName, isPostpaid = false });
            plans.Add(new { id = Guid.NewGuid(), name = $"3 Hours ({planName})", duration = 180, price = ratePerHour * 3, tier, tierLabel = tierLabel ?? planName, isPostpaid = false });
        }

        plans.Add(new { id = Guid.NewGuid(), name = $"Postpaid ({planName})", duration = 0, price = 0m, tier, tierLabel = tierLabel ?? planName, isPostpaid = true });

        return plans;
    }

    [HttpGet("branches/{branchId}/plans")]
    public async Task<IActionResult> GetBranchPlans(Guid branchId)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        var branch = await _db.Branches
            .Include(b => b.Pcs)
                .ThenInclude(p => p.PricingProfile)
                    .ThenInclude(pp => pp!.Packages)
            .FirstOrDefaultAsync(b => b.Id == branchId);

        if (branch == null)
            return Ok(new { success = false, error = "Branch not found" });

        var uniqueTiers = branch.Pcs
            .Where(p => p.IsActive && !p.IsDeleted)
            .Select(p => string.IsNullOrWhiteSpace(p.MonitorHz) ? "Standard" : p.MonitorHz)
            .Distinct()
            .ToList();

        if (!uniqueTiers.Any())
        {
            uniqueTiers.Add("Standard");
        }

        var plans = new List<object>();

        foreach (var tier in uniqueTiers)
        {
            var pcWithTier = branch.Pcs.FirstOrDefault(p => (string.IsNullOrWhiteSpace(p.MonitorHz) ? "Standard" : p.MonitorHz) == tier);
            string planNameTier = tier == "Standard" ? "Standard Tier" : $"{tier}Hz Tier";
            string monitorHzVal = tier == "Standard" ? "" : tier;

            plans.AddRange(BuildPlansForProfile(pcWithTier?.PricingProfile, planNameTier, monitorHzVal, planNameTier));
        }

        return Ok(ApiResponse<object>.Ok(plans));
    }

    /// <summary>Returns all currently pending walk-in requests. Operator polls this as a SignalR fallback.</summary>
    [HttpGet("walkin-pending")]
    [Authorize]
    public IActionResult GetPendingWalkinRequests()
    {
        var pending = PcOverlayHub.PendingWalkinRequests.Values.ToList();
        
        // Isolate by BranchId if present in Request Headers
        if (Request.Headers.TryGetValue("X-Branch-Id", out var branchIdVal) && Guid.TryParse(branchIdVal.ToString(), out Guid branchId))
        {
            var branchIdStr = branchId.ToString();
            pending = pending.Where(p => string.IsNullOrEmpty(p.BranchId) || p.BranchId.Equals(branchIdStr, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        
        return Ok(new { success = true, data = pending });
    }

    [HttpPost("pcs/{pcId}/decline-walkin")]
    [Authorize]
    public async Task<IActionResult> DeclineWalkinRequest(string pcId, [FromServices] IHubContext<PcOverlayHub> overlayHub)
    {
        PcOverlayHub.PendingWalkinRequests.TryRemove(pcId, out _);
        await overlayHub.Clients.Group($"pc:{pcId}").SendAsync("WalkinRequestDeclined", new { reason = "Operator declined the request" });
        return Ok(ApiResponse<object>.Ok(null));
    }

    [HttpPost("sessions/member-start")]
    [Authorize] // Requires valid MemberToken
    public async Task<IActionResult> StartMemberSession(
        [FromBody] SessionStartDto dto,
        [FromServices] ISessionService sessionService,
        [FromServices] IHubContext<PcStatusHub> pcStatusHub,
        [FromServices] IHubContext<SessionHub> sessionHub)
    {
        // Get the MemberId from the JWT Token
        var memberIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(memberIdStr) || !Guid.TryParse(memberIdStr, out var memberId))
            return Unauthorized(new { success = false, error = "Invalid Member token." });

        dto.MemberId = memberId;

        // Retrieve PC to find out which Branch this session belongs to
        var pc = await _db.Pcs.Include(p => p.Branch).Include(p => p.PricingProfile).FirstOrDefaultAsync(p => p.Id == dto.PcId);
        if (pc == null)
            return BadRequest(new { success = false, error = "PC not found." });

        var branchId = pc.BranchId;

        // BranchIsolationAttribute deliberately skips Members ("not branch-scoped" - a member
        // isn't assigned to one branch the way an operator is), so it never populates this.
        // But GetOperatorIdAsync/GetShiftIdAsync below both require it to resolve a Member's
        // request to a branch-specific System Operator, and throw if it's missing - which it
        // always was here, crashing every member-started session. This endpoint is the one
        // place that already knows which branch a Member's request belongs to (the PC they
        // picked), so it fills in what the attribute left blank.
        HttpContext.Items["BranchId"] = branchId.ToString();

        // A member session bills straight out of the Gaming wallet — refuse to start one on an
        // empty wallet. Checked up front so it also covers the reservation auto-start path
        // below, which builds its session directly instead of going through StartSessionAsync.
        var startingMember = await _db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId);
        if (startingMember == null)
            return BadRequest(new { success = false, error = "Member not found." });

        if (startingMember.GamingBalance < MemberWalletRules.MinimumGamingBalanceToStart)
        {
            return BadRequest(new
            {
                success = false,
                error = $"Your Gaming wallet balance is ₹{startingMember.GamingBalance:0.00}. Please top up your Gaming wallet at the counter to start a session.",
                code = "INSUFFICIENT_GAMING_BALANCE",
                gamingBalance = startingMember.GamingBalance
            });
        }

        // ── AUTO-START FROM RESERVATION (SOP §22) ──
        // If a pending reservation exists for this (memberId, pcId) within the time window,
        // auto-start the session from it — no operator approval needed.
        var now = DateTimeOffset.UtcNow;
        var pendingReservation = await _db.Reservations
            .Where(r => r.MemberId == memberId
                     && r.PcId == dto.PcId
                     && r.State == ReservationState.Pending
                     && r.ReservationTime.AddMinutes(-30) <= now
                     && r.ReservationTime.AddMinutes(r.GracePeriodMin) >= now)
            .OrderBy(r => Math.Abs((r.ReservationTime - now).TotalMinutes))
            .FirstOrDefaultAsync();

        // A member's phone reaches Head Office's public address wherever they are sitting, not
        // the branch's own local server - so hit unconditionally, this whole endpoint used to
        // write a session, a bill and a PC's state straight into whichever database physically
        // received the request. At Head Office that is Head Office's own copy: the branch this
        // member is actually sitting in front of never learns the session exists, cannot bill
        // it, and cannot stop it - "No such session exists at this branch" is not a bug in the
        // stop path, it is this endpoint telling the truth about what it never told the branch.
        // Routed the same way an operator's own start already is (SessionsController.StartSession).
        if (_remote.MustTravel)
        {
            if (pendingReservation != null)
            {
                var reservationReceipt = await _remote.SendAsync(
                    branchId, Services.BranchCommands.StartReservation,
                    new { reservationId = pendingReservation.Id }, memberId, HttpContext.RequestAborted);

                return Ok(ApiResponse<object>.Ok(new
                {
                    queued = true,
                    commandId = reservationReceipt.CommandId,
                    branchIsReporting = reservationReceipt.BranchIsReporting,
                    message = reservationReceipt.Message,
                    fromReservation = true,
                }));
            }

            var startReceipt = await _remote.SendAsync(
                branchId, Services.BranchCommands.StartSession,
                new
                {
                    pcId = dto.PcId,
                    memberId = dto.MemberId,
                    customerName = dto.CustomerName,
                    durationMinutes = dto.DurationMinutes,
                    packageName = dto.PackageName,
                    expectedAmount = dto.ExpectedAmount,
                    notes = dto.Notes,
                }, memberId, HttpContext.RequestAborted);

            return Ok(ApiResponse<object>.Ok(new
            {
                queued = true,
                commandId = startReceipt.CommandId,
                branchIsReporting = startReceipt.BranchIsReporting,
                message = startReceipt.Message,
            }));
        }

        // Same system-admin operator/shift ControllerExtensions already resolves correctly
        // for a Member-role request - deterministic id (so it lands on the exact same row
        // Head Office independently creates, instead of two different ids sharing only a
        // username and colliding on sync forever) and no CashRegister auto-opened (a phantom
        // register with ₹0 and no one ever asking for it). Both branches below need it -
        // resolved once here, after the early-return above, not twice.
        var sysOpId = await this.GetOperatorIdAsync();
        var activeShiftId = await this.GetShiftIdAsync();

        if (pendingReservation != null)
        {
            // Build session from reservation
            var durationMin = pendingReservation.DurationMin ?? 60;
            var ratePerHour = pc.PricingProfile?.BaseHourlyRate ?? 0m;
            var expectedAmount = pendingReservation.DurationMin.HasValue ? (durationMin / 60m) * ratePerHour : 0m; // 0 for member-only (postpaid)

            var session = new Session
            {
                Id = Guid.NewGuid(), PcId = pc.Id, BranchId = branchId,
                OperatorId = sysOpId, ShiftId = activeShiftId,
                CustomerName = pendingReservation.CustomerName, MemberId = memberId,
                StartTime = now, EndTime = pendingReservation.DurationMin.HasValue ? now.AddMinutes(durationMin) : (DateTimeOffset?)null,
                PlannedDurationMin = pendingReservation.DurationMin,
                TotalAmount = expectedAmount, GamingAmount = expectedAmount,
                GamingType = "Member Reservation Auto-Start",
                State = SessionState.Active, Notes = pendingReservation.Notes,
                CreatedAt = now, UpdatedAt = now
            };
            _db.Sessions.Add(session);

            var totalDue = Math.Max(0m, expectedAmount - pendingReservation.AdvanceDeposit);
            var bill = new Bill
            {
                Id = Guid.NewGuid(),
                BillNumber = $"BILL-{now:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
                SessionId = session.Id, PcId = pc.Id, BranchId = branchId,
                OperatorId = sysOpId, ShiftId = activeShiftId,
                CustomerName = pendingReservation.CustomerName, MemberId = memberId,
                GamingAmount = expectedAmount, FoodAmount = 0, Subtotal = expectedAmount, TotalAmount = totalDue,
                Status = BillStatus.Pending, CreatedAt = now, UpdatedAt = now,
                DiscountReason = pendingReservation.AdvanceDeposit > 0 ? $"Advance deposit ₹{pendingReservation.AdvanceDeposit} applied" : null
            };
            _db.Bills.Add(bill);

            pendingReservation.State = ReservationState.Completed;
            pendingReservation.StartedAt = now;
            _db.Reservations.Update(pendingReservation);

            pc.State = PcState.Active;
            pc.CurrentSessionId = session.Id;
            pc.CurrentReservationId = pendingReservation.Id;
            _db.Pcs.Update(pc);

            await _db.SaveChangesAsync();

            // Broadcast updates
            await pcStatusHub.Clients.Group($"branch:{branchId}").SendAsync("PcStatusChanged", new { pcId = pc.Id, state = "Active" });
            await sessionHub.Clients.Group($"branch:{branchId}").SendAsync("SessionUpdated", new { sessionId = session.Id });

            return Ok(ApiResponse<object>.Ok(new { sessionId = session.Id, pcId = pc.Id, pcName = pc.PcName ?? pc.PcNumber, startTime = session.StartTime, fromReservation = true }));
        }
        // ── END AUTO-START FROM RESERVATION ──

        var result = await sessionService.StartSessionAsync(branchId, sysOpId, activeShiftId, dto);
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    [HttpGet("sessions/active")]
    [Authorize]
    public async Task<IActionResult> GetActiveMemberSession()
    {
        var memberIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(memberIdStr) || !Guid.TryParse(memberIdStr, out var memberId))
            return Unauthorized(new { success = false, error = "Invalid Member token." });

        var session = await _db.Sessions
            .Include(s => s.Pc)
            .FirstOrDefaultAsync(s => s.MemberId == memberId && s.State == AppleEsportsErp.Domain.Enums.SessionState.Active);

        if (session == null)
            return Ok(new { success = true, data = (object)null });

        return Ok(new { success = true, data = new { sessionId = session.Id, pcId = session.PcId, pcName = session.Pc?.PcName ?? session.Pc?.PcNumber, startTime = session.StartTime } });
    }

    [HttpPost("sessions/{sessionId:guid}/member-checkout")]
    [Authorize] // Requires valid MemberToken
    public async Task<IActionResult> MemberCheckout(
        Guid sessionId,
        [FromServices] ISessionService sessionService,
        [FromServices] IBillingService billingService,
        [FromServices] IHubContext<PcStatusHub> pcStatusHub,
        [FromServices] IHubContext<BillingHub> billingHub,
        [FromServices] IHubContext<SessionHub> sessionHub,
        [FromServices] IHubContext<PcOverlayHub> overlayHub)
    {
        var memberIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(memberIdStr) || !Guid.TryParse(memberIdStr, out var memberId))
            return Unauthorized(new { success = false, error = "Invalid Member token." });

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null || session.MemberId != memberId)
            return BadRequest(new { success = false, error = "Session not found or not owned by you." });

        var branchId = session.BranchId;
        var sysUsername = $"system_admin_{branchId:N}";
        var sysOp = await _db.Operators.FirstOrDefaultAsync(o => o.BranchId == branchId && o.Username == sysUsername);
        if (sysOp == null) return BadRequest(new { success = false, error = "System operator not found." });

        var activeShift = await _db.Shifts.FirstOrDefaultAsync(s => s.BranchId == branchId && s.Status == ShiftStatus.Active && s.OperatorId == sysOp.Id);
        var shiftId = activeShift?.Id ?? Guid.Empty;

        // 1. Stop the session to finalize bill
        var sessionResult = await sessionService.StopSessionAsync(branchId, sysOp.Id, sessionId, false);

        // 2. Fetch the bill to get the total amount
        var bill = await billingService.GetBillAsync(branchId, sessionResult.BillId);
        if (bill != null && bill.Status != AppleEsportsErp.Domain.Enums.BillStatus.Completed && bill.TotalAmount > 0)
        {
            // 3. Process wallet payment automatically
            var paymentDto = new AppleEsportsErp.Application.DTOs.Billing.ProcessPaymentDto
            {
                PaymentType = AppleEsportsErp.Domain.Enums.PaymentType.Wallet,
                CashAmount = 0,
                OnlineAmount = 0,
                WalletAmount = bill.TotalAmount,
                CashReceived = 0,
                MemberId = memberId
            };

            try
            {
                var paidBill = await billingService.ProcessPaymentAsync(branchId, sysOp.Id, shiftId, bill.Id, paymentDto);
                
                // SignalR updates for Admin dashboards
                await billingHub.Clients.Group($"branch:{branchId}").SendAsync("WalletApprovalApproved", new { billId = bill.Id });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = "Wallet payment failed: " + ex.Message });
            }
        }

        // Broadcast PC Idle status to operator dashboard
        var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == sessionResult.PcId);
        if (pc != null)
        {
            var hubNotifier = HttpContext.RequestServices.GetService<AppleEsportsErp.Application.Interfaces.IHubNotificationService>();
            if (hubNotifier != null)
            {
                await hubNotifier.BroadcastPcStatusChangeAsync(branchId, pc.Id);
            }
        }

        // Notify overlay so it goes to idle screen
        await overlayHub.Clients.Group($"pc:{sessionResult.PcId}").SendAsync("PcStatusChanged", new { State = "Idle" });

        return Ok(new { success = true, data = sessionResult });
    }

    /// <summary>
    /// The signed-in member's current profile and wallet balances. Used by the overlay to
    /// re-check the Gaming balance after an operator top-up, without a re-login.
    /// </summary>
    [HttpGet("members/me")]
    [Authorize] // Requires valid MemberToken
    public async Task<IActionResult> GetMyMemberProfile()
    {
        var memberIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(memberIdStr) || !Guid.TryParse(memberIdStr, out var memberId))
            return Unauthorized(new { success = false, error = "Invalid Member token." });

        var member = await _db.Members.AsNoTracking()
            .Where(m => m.Id == memberId)
            .Select(m => new
            {
                id = m.Id,
                fullName = m.FullName,
                username = m.Username,
                email = m.Email,
                mobileNumber = m.MobileNumber,
                gamingBalance = m.GamingBalance,
                foodBalance = m.FoodBalance,
                homeBranchId = m.HomeBranchId
            })
            .FirstOrDefaultAsync();

        if (member == null)
            return NotFound(new { success = false, error = "Member not found." });

        return Ok(ApiResponse<object>.Ok(member));
    }

    /// <summary>
    /// Raised by the overlay the first time a running member session's remaining Gaming balance
    /// drops past the reminder threshold. Emails the member and alerts the branch's operators.
    /// Fires at most once per session — the overlay ticks every second, so the guard matters.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/low-balance-alert")]
    [Authorize] // Requires valid MemberToken
    public async Task<IActionResult> LowBalanceAlert(
        Guid sessionId,
        [FromBody] LowBalanceAlertRequest req,
        [FromServices] IEmailService emailService,
        [FromServices] IHubContext<NotificationHub> notificationHub,
        [FromServices] ILogger<PublicController> logger)
    {
        var memberIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(memberIdStr) || !Guid.TryParse(memberIdStr, out var memberId))
            return Unauthorized(new { success = false, error = "Invalid Member token." });

        var session = await _db.Sessions.Include(s => s.Pc)
            .FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null || session.MemberId != memberId)
            return BadRequest(new { success = false, error = "Session not found or not owned by you." });

        // Only ever notify once per session, no matter how many ticks report the threshold.
        if (!LowBalanceAlertsSent.TryAdd(sessionId, DateTimeOffset.UtcNow))
            return Ok(new { success = true, alreadySent = true });

        var member = await _db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId);
        if (member == null)
            return BadRequest(new { success = false, error = "Member not found." });

        var pcName = session.Pc?.PcName ?? session.Pc?.PcNumber ?? "your PC";
        var remaining = req.RemainingBalance;
        var minutesLeft = req.MinutesRemaining;

        // Alert the operators on duty so they can walk over and offer a top-up.
        try
        {
            await notificationHub.Clients.Group($"branch:{session.BranchId}").SendAsync("Alert", new
            {
                Type = "MemberLowBalance",
                pcId = session.PcId,
                pcName,
                branchId = session.BranchId,
                memberId,
                memberName = member.FullName,
                remainingBalance = remaining,
                minutesRemaining = minutesLeft,
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                message = $"{member.FullName} on {pcName} has ₹{remaining:0.00} Gaming balance left (~{minutesLeft} min)."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push low-balance operator alert for session {SessionId}", sessionId);
        }

        // Email the member. Never let a mail failure break the running session.
        if (!string.IsNullOrWhiteSpace(member.Email))
        {
            try
            {
                var body = $@"
                <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
                    <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                        <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #f59e0b;'>
                            <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                                <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                            </h1>
                        </div>
                        <div style='padding: 40px 30px; text-align: left;'>
                            <h2 style='margin-top: 0; color: #f59e0b; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Low Gaming Wallet Balance</h2>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Hi <strong>{member.FullName}</strong>,</p>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Your session on <strong>{pcName}</strong> is running low on Gaming wallet balance.</p>
                            <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                                <p style='margin: 10px 0; color: #d1d5db;'>Remaining balance: <strong style='color: #f59e0b;'>₹{remaining:0.00}</strong></p>
                                <p style='margin: 10px 0; color: #d1d5db;'>Approximate play time left: <strong style='color: #ffffff;'>{minutesLeft} minute(s)</strong></p>
                            </div>
                            <p style='font-size: 15px; color: #d1d5db; line-height: 1.6; margin-top: 25px;'>Please top up at the counter to keep playing. Your session will stop automatically once the balance runs out.</p>
                        </div>
                        <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                            <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated notification from Apple Esports ERP.</p>
                        </div>
                    </div>
                </div>";

                await emailService.SendEmailAsync(member.Email, "Apple Esports - Low Gaming Wallet Balance", body);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send low-balance email to member {MemberId}", memberId);
            }
        }

        return Ok(new { success = true });
    }

    [HttpPost("bills/{billId:guid}/approve-wallet")]
    public async Task<IActionResult> ApproveWalletPayment(Guid billId, [FromBody] ApproveWalletRequest req, [FromServices] IBillingService billingService, [FromServices] IHubContext<BillingHub> billingHub)
    {
        if (!BillingController.PendingApprovals.TryGetValue(req.ApprovalToken, out var pending) || pending.BillId != billId)
            return BadRequest(new { success = false, error = "Invalid or expired approval token." });

        try
        {
            var paymentDto = new AppleEsportsErp.Application.DTOs.Billing.ProcessPaymentDto
            {
                PaymentType = AppleEsportsErp.Domain.Enums.PaymentType.Wallet,
                CashAmount = 0,
                OnlineAmount = 0,
                WalletAmount = pending.Amount,
                CashReceived = 0,
                MemberId = null // ProcessPayment links member automatically if needed, but it should already be linked
            };

            var bill = await billingService.ProcessPaymentAsync(pending.BranchId, pending.OperatorId, pending.ShiftId, billId, paymentDto);
            BillingController.PendingApprovals.TryRemove(req.ApprovalToken, out _);
            
            // Notify operator dashboard that it was approved
            await billingHub.Clients.Group($"branch:{pending.BranchId}").SendAsync("WalletApprovalApproved", new { billId });
            
            // Broadcast PC Idle status using standard hub notification service
            if (bill.PcId.HasValue)
            {
                var hubNotifier = HttpContext.RequestServices.GetService<AppleEsportsErp.Application.Interfaces.IHubNotificationService>();
                if (hubNotifier != null)
                {
                    await hubNotifier.BroadcastPcStatusChangeAsync(pending.BranchId, bill.PcId.Value);
                }

                var overlayHub = HttpContext.RequestServices.GetService<Microsoft.AspNetCore.SignalR.IHubContext<AppleEsportsErp.Api.Hubs.PcOverlayHub>>();
                if (overlayHub != null)
                {
                    await overlayHub.Clients.Group($"pc:{bill.PcId.Value}").SendAsync("PcStatusChanged", new { State = "Idle" });
                }
            }
            
            return Ok(new { success = true, data = bill });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("bills/{billId:guid}/decline-wallet")]
    public async Task<IActionResult> DeclineWalletPayment(Guid billId, [FromBody] ApproveWalletRequest req, [FromServices] IHubContext<BillingHub> billingHub)
    {
        if (!BillingController.PendingApprovals.TryGetValue(req.ApprovalToken, out var pending) || pending.BillId != billId)
            return BadRequest(new { success = false, error = "Invalid or expired approval token." });

        BillingController.PendingApprovals.TryRemove(req.ApprovalToken, out _);

        // Notify operator dashboard that it was declined
        await billingHub.Clients.Group($"branch:{pending.BranchId}").SendAsync("WalletApprovalDeclined", new { billId, reason = "Member declined the payment." });
        
        return Ok(new { success = true });
    }

    [HttpGet("branches/{branchId:guid}/menu")]
    public async Task<IActionResult> GetBranchMenu(Guid branchId)
    {
        var items = await _db.InventoryItems
            .Where(i => i.BranchId == branchId && i.Status != FoodAvailability.Disabled)
            .OrderBy(i => i.Category)
            .ThenBy(i => i.ItemName)
            .Select(i => new {
                id = i.Id,
                name = i.ItemName,
                price = i.Price,
                category = i.Category,
                inStock = i.Status != FoodAvailability.OutOfStock && i.CurrentStock > 0,
                imageUrl = i.ImageUrl
            })
            .ToListAsync();

        return Ok(ApiResponse<object>.Ok(items));
    }
}

public class ApproveWalletRequest
{
    public Guid ApprovalToken { get; set; }
}

public class LowBalanceAlertRequest
{
    public decimal RemainingBalance { get; set; }
    public int MinutesRemaining { get; set; }
}

public class SetMonitorHzDto
{
    public string? MonitorHz { get; set; }
}

public class ReportPcAppVersionDto
{
    public string? Version { get; set; }
}

/// <summary>Sent by a gaming PC's AppleEsports.exe as Windows shuts it down, or on its next boot
/// for a session it was showing when it lost power. At is when the PC actually went off.</summary>
public class PcPoweredOffDto
{
    public Guid SessionId { get; set; }
    public DateTimeOffset? At { get; set; }
}
