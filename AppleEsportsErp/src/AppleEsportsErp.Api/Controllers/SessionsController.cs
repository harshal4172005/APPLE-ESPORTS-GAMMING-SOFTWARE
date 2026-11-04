using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppleEsportsErp.Api.Extensions;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Sessions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AppleEsportsErp.Api.Controllers;

[ApiController]
[Route("api/sessions")]
[Authorize]
[BranchIsolation]
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly ISessionActivityService _activityService;
    private readonly AppleEsportsErp.Infrastructure.Data.AppDbContext _db;
    private readonly AppleEsportsErp.Api.Services.IRemoteBranchControl _remote;

    public SessionsController(
        ISessionService sessionService,
        ISessionActivityService activityService,
        AppleEsportsErp.Infrastructure.Data.AppDbContext db,
        AppleEsportsErp.Api.Services.IRemoteBranchControl remote)
    {
        _sessionService = sessionService;
        _activityService = activityService;
        _db = db;
        _remote = remote;
    }
    private Guid GetBranchId() => Guid.Parse(HttpContext.Items["BranchId"]!.ToString()!);

    private Guid CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    /// <summary>
    /// Turns "Head Office pressed Start/Stop" into an instruction for the branch that owns the
    /// PC, and reports it back as a success.
    ///
    /// This is what makes the buttons on the Head Office screen work at all. They used to be
    /// refused outright, with a message explaining that only the counter could do it - correct,
    /// and useless to an owner who wants to shut down a session at a shop that has gone home
    /// and left one running. Now the same button sends the same instruction to the shop, which
    /// does it properly.
    ///
    /// 202 rather than 200, deliberately: this has been accepted, not completed. The body still
    /// reports success so the existing screens behave normally, and the figures update on their
    /// own within a few seconds when the branch's own record of doing it arrives.
    /// </summary>
    private async Task<IActionResult> SendToBranchAsync(
        Guid branchId, string commandType, object payload, CancellationToken ct)
    {
        var receipt = await _remote.SendAsync(branchId, commandType, payload, CurrentUserId(), ct);

        return Accepted(ApiResponse<object>.Ok(new
        {
            queued = true,
            commandId = receipt.CommandId,
            branchIsReporting = receipt.BranchIsReporting,
            message = receipt.Message,
        }));
    }

    [HttpGet]
    public async Task<IActionResult> GetActiveSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _sessionService.GetActiveSessionsAsync(GetBranchId(), page, pageSize);
        return Ok(ApiResponse<PaginatedResult<SessionDto>>.Ok(result));
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartSession([FromBody] SessionStartDto dto, CancellationToken ct)
    {
        // From Head Office this becomes an instruction for the shop rather than a row written
        // here. A session invented at Head Office exists only at Head Office: the counter never
        // sees it, so nobody can bill it and the customer plays for nothing.
        if (_remote.MustTravel)
        {
            // Taken from the PC rather than from the request's branch header. The PC is the
            // thing being acted on and it belongs to exactly one shop, so this cannot send
            // "start PC-04" to a branch that has no PC-04 because a header was stale or absent.
            var pcBranchId = await _db.Pcs.AsNoTracking()
                .Where(p => p.Id == dto.PcId).Select(p => p.BranchId).FirstOrDefaultAsync(ct);

            if (pcBranchId == Guid.Empty)
                return NotFound(ApiResponse<object>.Fail("Head Office has no such PC.", "PC_NOT_FOUND"));

            return await SendToBranchAsync(pcBranchId, Services.BranchCommands.StartSession, new
            {
                pcId = dto.PcId,
                memberId = dto.MemberId,
                customerName = dto.CustomerName,
                durationMinutes = dto.DurationMinutes,
                packageName = dto.PackageName,
                expectedAmount = dto.ExpectedAmount,
                notes = dto.Notes,
            }, ct);
        }

        var result = await _sessionService.StartSessionAsync(GetBranchId(), (await this.GetOperatorIdAsync()), (await this.GetShiftIdAsync()), dto);
        // Remove any pending walk-in request for this PC now that a session has started
        PcOverlayHub.PendingWalkinRequests.TryRemove(dto.PcId.ToString(), out _);
        PcOverlayHub.PendingWalkinRequests.TryRemove(result.PcName, out _);
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    /// <summary>
    /// Sessions held after an outage, waiting for someone to say whether the customer is
    /// still in the building.
    ///
    /// Returned as its own list rather than mixed into the active sessions, because these
    /// need a decision. After a power cut an operator opens the dashboard to several PCs
    /// that look busy but whose clocks are stopped, and nothing else on screen would tell
    /// them that or what to do about it.
    /// </summary>
    [HttpGet("interrupted")]
    public async Task<IActionResult> GetInterruptedSessions()
    {
        var branchId = GetBranchId();
        var now = DateTimeOffset.UtcNow;

        var sessions = await _db.Sessions
            .AsNoTracking()
            .Include(s => s.Pc)
            .Include(s => s.Member)
            .Where(s => s.BranchId == branchId && s.State == SessionState.Interrupted)
            .OrderBy(s => s.InterruptedAt)
            .ToListAsync();

        var rows = sessions.Select(s =>
        {
            var playedMinutes = (int)AppleEsportsErp.Application.Services.SessionTimeCalculator
                .ElapsedMinutes(s.StartTime, s.PausedSeconds, s.InterruptedAt ?? now);

            return new
            {
                sessionId = s.Id,
                pcName = s.Pc?.PcNumber ?? "—",
                customerName = s.CustomerName ?? s.Member?.Username ?? "Walk-in",
                playedMinutes,
                // What they still have coming, which is the whole question the operator is
                // being asked. Null for pay-as-you-go, where there is no purchased block.
                remainingMinutes = s.PlannedDurationMin.HasValue
                    ? Math.Max(0, s.PlannedDurationMin.Value - playedMinutes)
                    : (int?)null,
                outageMinutes = (int)Math.Round(s.PausedSeconds / 60.0),
                heldSince = AppleEsportsErp.Application.Services.IndiaTime.FormatTime(s.InterruptedAt ?? now),
                // Flagged when the gap ran through closed hours — far more likely a session
                // nobody stopped than a power cut with a customer waiting.
                needsReview = s.NeedsTimeReview,
            };
        }).ToList();

        return Ok(ApiResponse<object>.Ok(rows));
    }

    /// <summary>
    /// Restarts a session that was put on hold by a power cut or restart, with the
    /// customer's unused paid time intact. The alternative — the customer went home —
    /// is the existing stop endpoint, which bills only the minutes actually played.
    /// </summary>
    [HttpPost("{id}/resume")]
    public async Task<IActionResult> ResumeSession(Guid id)
    {
        var result = await _sessionService.ResumeSessionAsync(
            GetBranchId(), await this.GetOperatorIdAsync(), id);
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> StopSession(Guid id, [FromBody] StopSessionDto? dto = null, CancellationToken ct = default)
    {
        // The one the owner actually asked for: stop a session at any branch, from here.
        // Sent down as an instruction, so the shop stops it, bills it into the right shift and
        // reports back - rather than Head Office marking its own copy stopped while the counter
        // carries on charging the customer.
        if (_remote.MustTravel)
        {
            var branchId = await _db.Sessions.AsNoTracking()
                .Where(s => s.Id == id).Select(s => s.BranchId).FirstOrDefaultAsync(ct);

            if (branchId == Guid.Empty)
                return NotFound(ApiResponse<object>.Fail("Head Office has no such session.", "SESSION_NOT_FOUND"));

            return await SendToBranchAsync(branchId, Services.BranchCommands.StopSession, new
            {
                sessionId = id,
                // Never true for a remote stop. Deferring marks the bill paid on the spot and
                // writes the balance off as a customer credit - a debt forgiven that nobody
                // decided to forgive. False gives exactly what the counter's own Stop gives.
                deferPayment = false,
                reason = "Stopped from Head Office",
            }, ct);
        }

        // Member wallet sessions are now deducted synchronously inside StopSessionAsync —
        // no separate wallet-approval overlay step for this path (that overlay flow
        // remains, unchanged, for other wallet-payable bills such as mid-session food orders).
        var result = await _sessionService.StopSessionAsync(GetBranchId(), (await this.GetOperatorIdAsync()), id, dto?.DeferPayment ?? false);
        AppleEsportsErp.Api.Hubs.PcOverlayHub.PendingWalkinRequests.TryRemove(result.PcId.ToString(), out _);
        AppleEsportsErp.Api.Hubs.PcOverlayHub.PendingWalkinRequests.TryRemove(result.PcName, out _);

        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    /// <summary>Packages this running session could switch to - price, time left, and why not.</summary>
    [HttpGet("{id:guid}/package-options")]
    public async Task<IActionResult> GetPackageOptions(Guid id)
    {
        var result = await _sessionService.GetPackageOptionsAsync(GetBranchId(), id);
        return Ok(ApiResponse<PackageOptionsDto>.Ok(result));
    }

    /// <summary>
    /// Change the running package (up or down) - from the operator's own panel, or when approving
    /// the customer's request from their gaming PC. Done at the branch, where the session lives.
    /// </summary>
    [HttpPost("{id:guid}/change-package")]
    public async Task<IActionResult> ChangePackage(Guid id, [FromBody] ChangePackageDto dto,
        [FromServices] Microsoft.AspNetCore.SignalR.IHubContext<AppleEsportsErp.Api.Hubs.PcOverlayHub> overlayHub)
    {
        if (_remote.MustTravel)
            return BadRequest(ApiResponse<object>.Fail("Change the package at the branch counter, where the session is running."));

        var result = await _sessionService.ChangePackageAsync(GetBranchId(), await this.GetOperatorIdAsync(), id, dto.PackageId);
        await overlayHub.Clients.Group($"pc:{result.PcId}").SendAsync("PackageChanged", new { sessionId = id });
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    [HttpPost("{id}/extend")]
    public async Task<IActionResult> ExtendSession(Guid id, [FromBody] SessionExtendDto dto)
    {
        var result = await _sessionService.ExtendSessionAsync(GetBranchId(), (await this.GetOperatorIdAsync()), id, dto);
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    [HttpPost("{id}/transfer")]
    public async Task<IActionResult> TransferSession(Guid id, [FromBody] SessionTransferDto dto, CancellationToken ct)
    {
        // Same shape as Start and Stop: from Head Office this becomes an instruction the
        // branch carries out on its own PCs, rather than a write against Head Office's own
        // disconnected copy that the branch is never told about.
        if (_remote.MustTravel)
        {
            var branchId = await _db.Sessions.AsNoTracking()
                .Where(s => s.Id == id).Select(s => s.BranchId).FirstOrDefaultAsync(ct);

            if (branchId == Guid.Empty)
                return NotFound(ApiResponse<object>.Fail("Head Office has no such session.", "SESSION_NOT_FOUND"));

            return await SendToBranchAsync(branchId, Services.BranchCommands.TransferSession, new
            {
                sessionId = id,
                targetPcId = dto.TargetPcId,
            }, ct);
        }

        var result = await _sessionService.TransferSessionAsync(GetBranchId(), (await this.GetOperatorIdAsync()), id, dto);
        return Ok(ApiResponse<SessionDto>.Ok(result));
    }

    [HttpGet("{id}/activities")]
    public async Task<IActionResult> GetSessionActivities(Guid id)
    {
        var activities = await _activityService.GetSessionActivitiesAsync(id);
        return Ok(ApiResponse<IEnumerable<SessionActivityDto>>.Ok(activities.Select(a => new SessionActivityDto
        {
            Id = a.Id,
            SessionId = a.SessionId,
            Action = a.Action,
            Description = a.Description,
            Amount = a.Amount,
            Timestamp = a.Timestamp
        })));
    }

    [HttpGet("activities/recent")]
    public async Task<IActionResult> GetRecentActivities([FromQuery] int limit = 100)
    {
        var activities = await _activityService.GetRecentActivitiesAsync(GetBranchId(), limit);
        return Ok(ApiResponse<IEnumerable<SessionActivityDto>>.Ok(activities.Select(a => new SessionActivityDto
        {
            Id = a.Id,
            SessionId = a.SessionId,
            Action = a.Action,
            Description = a.Description,
            Amount = a.Amount,
            Timestamp = a.Timestamp
        })));
    }
}


