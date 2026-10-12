using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Settings;
using AppleEsportsErp.Application.DTOs.Sync;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>
/// Where a branch says what it is doing right now, and where Head Office reads it back.
///
/// The counterpart to the sync inbox, and deliberately nothing like it. The inbox carries
/// history and may never lose a record; this carries state, where only the newest matters and
/// a missed beat costs nothing. Trying to serve both with one mechanism is why Head Office
/// could show a branch's bills from three weeks ago and not know whether anybody was standing
/// at the counter.
/// </summary>
[ApiController]
[Route("api/branch-status")]
public class BranchHeartbeatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    private readonly IHubNotificationService _hubNotifier;
    private readonly ILogger<BranchHeartbeatController> _logger;

    /// <summary>
    /// How long a branch may be silent before Head Office should say so.
    ///
    /// One minute, which at a three-second beat is twenty missed in a row - far past a dropped
    /// packet or a moment of bad signal, and still quick enough that a counter PC someone has
    /// switched off shows as gone while you are still standing there.
    ///
    /// This has to move whenever the beat does. Left at the two minutes that suited a
    /// thirty-second beat, it would have taken forty missed beats to admit a branch had
    /// stopped - a screen quietly showing a dead shop as healthy, which is the one thing this
    /// whole mechanism exists to prevent.
    /// </summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a command may go unanswered before Head Office stops waiting on it.
    ///
    /// Needed because two things hold while a command is open: the branch is handed it again on
    /// every beat, and Head Office stops applying that PC's reported state so the instruction is
    /// not overwritten before it lands. Both are right for the few seconds a command normally
    /// takes. Neither is right for ever.
    ///
    /// A branch running a build that predates commands entirely never replies to one - it does
    /// not know how. Without a bound, that command is re-sent twenty times a minute for the
    /// life of the deployment, and the PC it names is frozen on the Head Office screen at
    /// whatever it happened to be showing, with the branch faithfully reporting the truth
    /// every three seconds and Head Office ignoring all of it. A stuck screen that cannot be
    /// explained is worse than an instruction that visibly did not happen.
    ///
    /// Five minutes is far longer than any branch that is listening needs - they answer within
    /// one beat - and short enough that nobody stares at a stale PC for long.
    /// </summary>
    public static readonly TimeSpan CommandGivenUpAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a <see cref="AppleEsportsErp.Api.Services.BranchCommands.SetMemberPassword"/>
    /// command specifically may go unanswered before Head Office gives up on it.
    ///
    /// Five minutes is right for a PC-facing command because something on Head Office's own
    /// screen is frozen waiting on it and an operator is standing there watching it happen live.
    /// Neither is true of a password reset: nothing on any screen depends on it, and the member
    /// who requested it may not try to log in again for hours or days. Giving it the same
    /// five-minute leash meant a branch that was simply a few releases behind - not broken,
    /// just not yet updated - silently and permanently lost the member's new password with no
    /// visible failure anywhere. The member saw a working "password reset successful" and then
    /// "invalid password" at the counter, and the only trace of why was this exact command
    /// sitting Failed in a table nobody had reason to open. Two real members hit this before
    /// anyone noticed.
    ///
    /// 48 hours instead - long enough to cover a branch that is genuinely offline or several
    /// updates behind, short enough that a truly abandoned branch does not accumulate these
    /// forever.
    /// </summary>
    public static readonly TimeSpan MemberPasswordCommandGivenUpAfter = TimeSpan.FromHours(48);

    private static TimeSpan GiveUpAfterFor(string commandType) =>
        commandType == AppleEsportsErp.Api.Services.BranchCommands.SetMemberPassword
            ? MemberPasswordCommandGivenUpAfter
            : CommandGivenUpAfter;

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Caps how often a single PC's PoweredOff transition gets its own audit row.
    ///
    /// A PC whose own power detection is unstable can report a change on every three-second
    /// beat forever - a real reported difference each time, not a bug in this method, but one
    /// the audit trail should not be forced to carry twenty times a minute. The dashboard's own
    /// PC.PoweredOff field is unaffected: it is written from every beat regardless, so it still
    /// tracks whatever the branch currently says. Only the extra audit row is throttled.
    /// </summary>
    private static readonly TimeSpan PoweredOffAuditThrottle = TimeSpan.FromMinutes(1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _recentPoweredOffAudit = new();

    public BranchHeartbeatController(
        AppDbContext db, IAuditService audit, IHubNotificationService hubNotifier, ILogger<BranchHeartbeatController> logger)
    {
        _db = db;
        _audit = audit;
        _hubNotifier = hubNotifier;
        _logger = logger;
    }

    /// <summary>
    /// Accepts a branch's picture of itself.
    ///
    /// Anonymous, like the rest of the branch-to-Head-Office endpoints: a counter PC has no
    /// person signed in at 4am and must still report. Nothing here can be used to read data
    /// or move money — it only overwrites one branch's own live figures with what that branch
    /// says about itself, which it is the authority on.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Receive([FromBody] BranchHeartbeatDto dto, CancellationToken ct)
    {
        if (dto is null || dto.BranchId == Guid.Empty)
            return BadRequest(ApiResponse<object>.Fail("Which branch?", "BRANCH_REQUIRED"));

        if (!await _db.Branches.AnyAsync(b => b.Id == dto.BranchId, ct))
            return NotFound(ApiResponse<object>.Fail("Head Office does not know that branch.", "UNKNOWN_BRANCH"));

        var now = DateTimeOffset.UtcNow;

        var beat = await _db.Set<BranchHeartbeat>().FirstOrDefaultAsync(h => h.BranchId == dto.BranchId, ct);
        if (beat is null)
        {
            beat = new BranchHeartbeat { BranchId = dto.BranchId };
            _db.Add(beat);
        }

        // Two counter PCs both calling themselves one branch, caught the only moment it is
        // cheap to catch: while they are taking it in turns to overwrite each other.
        //
        // Checked before LastSeenAt moves, because "was the previous beat recent?" is the
        // whole test. Machines swapping every thirty seconds are two machines running at once;
        // a name that changed after an hour of silence is simply a replaced PC, which is a
        // normal thing to do and must not raise an alarm.
        if (!string.IsNullOrWhiteSpace(dto.MachineName)
            && !string.IsNullOrWhiteSpace(beat.ReportedByMachine)
            && !string.Equals(beat.ReportedByMachine, dto.MachineName, StringComparison.OrdinalIgnoreCase)
            && now - beat.LastSeenAt < SilentAfter)
        {
            beat.ConflictingMachine = beat.ReportedByMachine;
            beat.ConflictingMachineSeenAt = now;

            _logger.LogWarning(
                "Two PCs are both reporting as branch {BranchId}: {MachineA} and {MachineB}. " +
                "Each keeps its own records and syncs them under this one branch, so their " +
                "takings will be merged and cannot be separated afterwards. One of them should " +
                "be stopped.",
                dto.BranchId, beat.ReportedByMachine, dto.MachineName);
        }

        beat.ReportedByMachine = dto.MachineName;

        // Cleared once nothing has disagreed for a full silent window, so a clash that was
        // dealt with stops being reported and the warning keeps meaning something.
        if (beat.ConflictingMachineSeenAt is { } seen && now - seen > SilentAfter)
        {
            beat.ConflictingMachine = null;
            beat.ConflictingMachineSeenAt = null;
        }

        beat.LastSeenAt = now;

        // Converted here, and this one line is why no heartbeat ever reached Head Office.
        //
        // The branch sends its own wall clock, offset and all - 22:44 +05:30. PostgreSQL's
        // "timestamp with time zone" does not store an offset; it stores an instant, and
        // Npgsql refuses anything that is not already UTC rather than silently guessing.
        // So every beat threw on save and came back 500. Twenty a minute, from every branch,
        // for hours, and nothing on either screen said a word - Head Office looked like a
        // shop that had simply gone quiet.
        //
        // Nothing is lost. ToUniversalTime keeps the instant exactly, and the instant is the
        // whole point: comparing it against Head Office's own clock is how a branch with the
        // wrong time gets noticed. The +05:30 carried no information anyway - every branch
        // in this business is in India.
        //
        // Done on the receiving side deliberately. Head Office cannot assume every branch is
        // running today's build, and one shop with an odd clock must not be able to knock
        // over the endpoint that every other shop reports through.
        beat.BranchLocalTime = dto.BranchLocalTime.ToUniversalTime();

        beat.Version = dto.Version;
        beat.OperatorsOnDuty = JsonSerializer.Serialize(dto.OperatorsOnDuty);
        beat.OperatorsOnDutyCount = dto.OperatorsOnDuty.Count;
        beat.ActiveSessions = dto.ActiveSessions;
        beat.PcsTotal = dto.Pcs.Count;

        // "Not idle" used to mean busy, which also counted every never-configured placeholder
        // row (State: awaitingsetup) and every machine down for repair (undermaintenance) as if
        // a customer were sitting there. A branch with 35 PC rows and only a handful actually
        // set up read as almost entirely full on this dashboard while its own Sessions page
        // correctly showed one active session - the two screens disagreed about the same shop.
        // Busy now means what the word says: a customer is actually using or about to use the
        // seat.
        var busyStates = new[] { "active", "reserved", "awaitingbilling" };
        beat.PcsBusy = dto.Pcs.Count(p => busyStates.Contains(p.State?.ToLowerInvariant()));
        beat.DrawerExpected = dto.DrawerExpected;
        beat.TakingsToday = dto.TakingsToday;
        beat.UndeliveredRecords = dto.UndeliveredRecords;

        await ApplyOperatorsOnDutyAsync(dto, ct);
        var changedPcIds = await ApplyPcStatesAsync(dto, ct);
        await QueueMissingPcsAsync(dto, ct);

        await _db.SaveChangesAsync(ct);

        // Broadcast after saving successfully, same rule everything else that touches a PC
        // follows - see ReservationBackgroundService. Without this, Head Office's own dashboard
        // never heard about a heartbeat-driven change at all: it had no live push wired to this
        // endpoint, only its own 20-second poll (or a manual refresh, once the data itself is
        // actually there to refresh into). A PC shut down at the counter, or simply started a
        // session, could sit showing the wrong colour at Head Office for up to 20 seconds even
        // after this fix made the data correct.
        foreach (var pcId in changedPcIds)
        {
            try { await _hubNotifier.BroadcastPcStatusChangeAsync(dto.BranchId, pcId); }
            catch { /* best effort - the next poll picks up the change regardless */ }
        }

        // The reply carries this branch's settings back down, which is the half of sync that
        // never existed. Null when the branch already has them, so almost every beat stays a
        // few hundred bytes.
        var config = await ConfigForBranchIfChangedAsync(dto.BranchId, dto.ConfigVersion, ct);

        // And anything Head Office has asked this branch to do since it last checked in -
        // the other half. See BranchCommand for why this rides down as an instruction to
        // carry out rather than a fact already written.
        var commands = await PendingCommandsAsync(dto.BranchId, ct);

        return Ok(ApiResponse<object>.Ok(new { received = true, at = now, config, commands }));
    }

    /// <summary>
    /// Everything still owed to this branch, marked Sent so it is not handed out again on the
    /// next beat before this one has even been tried.
    ///
    /// "Sent" is not "done". A branch that never confirms - crashed mid-command, or the reply
    /// never arrived - simply has the command reappear, because the only thing marking a
    /// command finished is the branch's own result coming back.
    /// </summary>
    private async Task<List<BranchCommandDto>> PendingCommandsAsync(Guid branchId, CancellationToken ct)
    {
        var open = await _db.Set<BranchCommand>()
            .Where(c => c.BranchId == branchId
                && (c.Status == BranchCommandStatus.Pending || c.Status == BranchCommandStatus.Sent))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (open.Count == 0) return new List<BranchCommandDto>();

        var now = DateTimeOffset.UtcNow;

        // Given up on, and said so. A branch that was going to answer has answered long before
        // this; one that has not is on a build too old to understand the command, and will
        // never answer however many times it is asked. Closing it stops the retry and releases
        // the PC back to reporting its own state - see CommandGivenUpAfter and, for the one
        // command type that gets much longer, GiveUpAfterFor.
        var abandoned = open.Where(c => now - c.CreatedAt > GiveUpAfterFor(c.CommandType)).ToList();
        foreach (var c in abandoned)
        {
            var giveUpAfter = GiveUpAfterFor(c.CommandType);
            c.Status = BranchCommandStatus.Failed;
            c.CompletedAt = now;
            c.ResultMessage =
                $"The branch did not pick this up within {giveUpAfter.TotalHours:0} hour(s). " +
                "It is most likely running a version that does not understand this instruction yet.";

            _logger.LogWarning(
                "Giving up on {CommandType} ({CommandId}) for branch {BranchId} - queued {Age:0} " +
                "minutes ago and never confirmed.",
                c.CommandType, c.Id, branchId, (now - c.CreatedAt).TotalMinutes);

            // The one outcome that has no branch to report it, because the branch is exactly
            // what never answered - so Head Office writes this closing entry itself, the only
            // case anywhere in this file where that happens. ResultMessage was just set above
            // and is the whole reason this row is worth reading - without passing it through,
            // the Audit Trail said only "failed" with no way to tell a stale build apart from
            // any other kind of failure.
            await LogCommandOutcomeAsync(c, succeeded: false, ct, c.ResultMessage);
        }

        var pending = open.Except(abandoned).ToList();

        foreach (var c in pending.Where(c => c.Status == BranchCommandStatus.Pending))
        {
            c.Status = BranchCommandStatus.Sent;
            c.SentAt = now;
        }

        await _db.SaveChangesAsync(ct);

        if (pending.Count == 0) return new List<BranchCommandDto>();

        return pending.Select(c => new BranchCommandDto
        {
            Id = c.Id,
            CommandType = c.CommandType,
            Payload = c.Payload,
        }).ToList();
    }

    /// <summary>
    /// Where the branch reports back what actually happened. The only place a command is ever
    /// allowed to close - Head Office asked, it does not get to decide the answer.
    ///
    /// Anonymous like the rest of the branch-facing endpoints, and scoped by looking the
    /// command up by both its id and its branch, so one shop can never close another's command.
    /// </summary>
    [HttpPost("commands/result")]
    [AllowAnonymous]
    public async Task<IActionResult> CommandResult([FromBody] BranchCommandResultDto dto, CancellationToken ct)
    {
        var command = await _db.Set<BranchCommand>().FirstOrDefaultAsync(c => c.Id == dto.CommandId, ct);
        if (command is null)
            return NotFound(ApiResponse<object>.Fail("Head Office has no such command.", "COMMAND_NOT_FOUND"));

        // Already closed - a result arriving twice from the same beat retry, or a very late one
        // for a command that has since timed out and been requeued under a fresh id.
        if (command.Status is BranchCommandStatus.Succeeded or BranchCommandStatus.Failed)
            return Ok(ApiResponse<object>.Ok(new { alreadyClosed = true }));

        // Not done, not genuinely failed either - the member this command is for simply has not
        // reached this branch's own database yet. This is the actual fix for "the member got a
        // success message but still cannot log in": the old code had no third option here, so a
        // branch reporting exactly this situation as Succeeded=true closed the command outright
        // - Head Office had already told the customer their reset worked, and nothing was left
        // to ever tell the branch to store it, even once the member arrived moments later.
        //
        // Left exactly as it is - Pending or Sent, whichever it already was - which is what
        // keeps it inside BranchHeartbeatController's own "open" query (Pending or Sent) for the
        // next heartbeat to pick straight back up, same as a genuine delivery failure already
        // does. GiveUpAfterFor's 48-hour window is still what closes this out for a member that
        // truly never arrives - this only stops the FIRST beat from mistaking "not yet" for "no".
        if (!dto.Succeeded && dto.Message == AppleEsportsErp.Api.Services.BranchCommands.MemberNotYetSyncedMessage)
        {
            command.ResultMessage = dto.Message;
            await _db.SaveChangesAsync(ct);
            return Ok(ApiResponse<object>.Ok(new { willRetry = true }));
        }

        command.Status = dto.Succeeded ? BranchCommandStatus.Succeeded : BranchCommandStatus.Failed;
        command.ResultMessage = dto.Message;
        command.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        // What "remote_command_issued" was always missing the other half of: not just that
        // somebody asked, but whether it worked. A failed remote command used to leave nothing
        // behind but a message on one screen for the moment it happened - gone the instant
        // somebody navigated away, with no way to look back the next morning and see that PC-7
        // never actually got the memo.
        await LogCommandOutcomeAsync(command, dto.Succeeded, ct, dto.Message);

        return Ok(ApiResponse<object>.Ok(new { received = true }));
    }

    /// <summary>
    /// Closes the loop that <see cref="RemoteBranchControl"/> opened when the command was
    /// queued: that entry said who asked and for what; this one says whether it actually
    /// happened. Both carry the same command id in TargetId, so the two can be found together.
    /// </summary>
    private async Task LogCommandOutcomeAsync(
        BranchCommand command, bool succeeded, CancellationToken ct, string? message = null)
    {
        var branchName = await _db.Branches.AsNoTracking()
            .Where(b => b.Id == command.BranchId).Select(b => b.Name).FirstOrDefaultAsync(ct);

        var requestedBy = await _db.Set<User>().AsNoTracking()
            .Where(u => u.Id == command.RequestedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        await _audit.LogAsync(new AuditEntry
        {
            UserId = command.RequestedByUserId,
            UserRole = "SuperAdmin",
            UserName = requestedBy ?? "Head Office",
            Action = AuditActions.RemoteCommandIssued,
            TargetType = "branch_command",
            TargetId = command.Id,
            Success = succeeded,
            BranchId = command.BranchId,
            BranchName = branchName,
            Details = new { commandType = command.CommandType, outcome = succeeded ? "succeeded" : "failed", message },
        });
    }

    /// <summary>
    /// This branch's operators and what they are allowed to see - but only if the branch does
    /// not already have exactly this.
    ///
    /// Scoped to the one branch, deliberately. Katargam's staff are no business of Adajan's
    /// counter PC, and sending the whole chain's operators to every shop would put every
    /// password hash in the company on four machines instead of one.
    /// </summary>
    private async Task<BranchConfigDto?> ConfigForBranchIfChangedAsync(
        Guid branchId, string? branchHasVersion, CancellationToken ct)
    {
        // This branch's own operators, plus every Global Admin from every OTHER branch too -
        // "reachable from any counter" was the entire point of being promoted, and it was
        // never actually true. A promoted operator's own branch got the change (they are one
        // of "this branch's own operators" to themselves); nobody else's branch ever received
        // a row for them at all, so Admin Quick-Switch at any other branch could only ever
        // list people who happened to already be native there. Confirmed live: Nazmin
        // (Citylight's own Global Admin) never appeared at Adajan or anywhere but home.
        var operators = await _db.Operators.AsNoTracking()
            .Where(o => o.BranchId == branchId || o.IsGlobalAdmin)
            .OrderBy(o => o.Id)   // stable order, or the fingerprint changes on its own
            .Select(o => new BranchOperatorConfigDto
            {
                Id = o.Id,
                FullName = o.FullName,
                Username = o.Username,
                Email = o.Email,
                PasswordHash = o.PasswordHash,
                MobileNumber = o.MobileNumber,
                AccessPin = o.AccessPin,
                IsGlobalAdmin = o.IsGlobalAdmin,
                DashboardPermissions = o.DashboardPermissions,
                IsBlocked = o.Status == OperatorStatus.Suspended || o.Status == OperatorStatus.Disabled,
            })
            .ToListAsync(ct);

        // Null unless this branch shares a food group with another - see Branch.FoodGroupId.
        var foodGroupId = await _db.Branches.AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.FoodGroupId)
            .FirstOrDefaultAsync(ct);

        // Catalog fields only - CurrentStock and SoldQty are the branch's own trading state
        // and never travel down, for the same reason a PC's busy/idle state does not.
        //
        // Scoped to this branch alone, unless it shares a food group - then every branch
        // sharing that group is included too, so an item created or edited by ANY of them ends
        // up on every member's menu, not just its own creator's. No further dedup is needed:
        // Head Office's own InventoryItems table already holds at most one row per Id (the
        // primary key), the same as any other branch's echo.
        var menuItems = await _db.Set<InventoryItem>().AsNoTracking()
            .Where(i => i.BranchId == branchId
                || (foodGroupId != null && i.Branch!.FoodGroupId == foodGroupId))
            .OrderBy(i => i.Id)
            .Select(i => new BranchMenuItemConfigDto
            {
                Id = i.Id,
                ItemName = i.ItemName,
                Category = i.Category,
                Price = i.Price,
                ImageUrl = i.ImageUrl,
                IsDisabled = i.Status == FoodAvailability.Disabled,
            })
            .ToListAsync(ct);

        // Every member, not just this branch's own - a member joins at one shop and is meant
        // to be able to spend their wallet at any of them, which only works if every branch
        // knows who they are and what they currently hold.
        var members = await _db.Members.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new BranchMemberConfigDto
            {
                Id = m.Id,
                FullName = m.FullName,
                MemberNumber = m.MemberNumber,
                MobileNumber = m.MobileNumber,
                Email = m.Email,
                Username = m.Username,
                HomeBranchId = m.HomeBranchId,
                PasswordHash = m.PasswordHash,
                PasswordChangedAt = m.PasswordChangedAt,
                GamingBalance = m.GamingBalance,
                FoodBalance = m.FoodBalance,
                BalanceAsOf = m.BalanceAsOf,
                IsBlocked = m.Status == MemberStatus.Suspended || m.Status == MemberStatus.Inactive,
            })
            .ToListAsync(ct);

        // This branch's own pricing profiles, each with whatever custom packages are on it.
        // Not scoped by food group or anything shared - pricing is per-branch, always.
        var pricingProfiles = await _db.Set<PricingProfile>().AsNoTracking()
            .Where(p => p.BranchId == branchId)
            .OrderBy(p => p.Id)
            .Select(p => new BranchPricingProfileConfigDto
            {
                Id = p.Id,
                Name = p.Name,
                BaseHourlyRate = p.BaseHourlyRate,
                BufferMinutes = p.BufferMinutes,
                IsActive = p.IsActive,
                RefreshRate = p.RefreshRate,
                SystemSpecs = p.SystemSpecs,
                Packages = p.Packages
                    .OrderBy(pkg => pkg.Id)
                    .Select(pkg => new BranchPricingPackageConfigDto
                    {
                        Id = pkg.Id,
                        Name = pkg.Name,
                        DurationMinutes = pkg.DurationMinutes,
                        Price = pkg.Price,
                        SortOrder = pkg.SortOrder,
                        IsActive = pkg.IsActive,
                    }).ToList(),
            })
            .ToListAsync(ct);

        // Every Admin-level Users-table account, not just this branch's - the same "reachable
        // from any counter" reasoning as the Global Admin operators above, and the actual fix
        // for Quick Admin Switch showing nobody: an Admin made at Head Office had never once
        // been sent to any branch at all, on any beat, ever - Users was simply never in this
        // list, so there was nothing stale to invalidate and nothing a cache header could have
        // fixed.
        var admins = await _db.Users.AsNoTracking()
            .Where(u => u.Role == Roles.Admin)
            .OrderBy(u => u.Id)
            .Select(u => new BranchAdminConfigDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                PasswordHash = u.PasswordHash,
                AccessPin = u.AccessPin,
                DashboardPermissions = u.DashboardPermissions,
                IsBlocked = u.Status == UserStatus.Suspended || u.Status == UserStatus.Disabled,
            })
            .ToListAsync(ct);

        var config = new BranchConfigDto
        {
            Operators = operators,
            MenuItems = menuItems,
            Members = members,
            PricingProfiles = pricingProfiles,
            Admins = admins,
        };
        config.Version = Fingerprint(config);

        return string.Equals(config.Version, branchHasVersion, StringComparison.Ordinal)
            ? null                 // the branch is already correct; say nothing
            : config;
    }

    /// <summary>
    /// A short, stable fingerprint of the configuration.
    ///
    /// Computed from the content itself rather than kept as a column somebody has to remember
    /// to bump. A version number maintained by hand is a version number that eventually lies,
    /// and a branch that trusts a stale one would sit on old permissions for ever with both
    /// sides insisting they agree.
    /// </summary>
    private static string Fingerprint(BranchConfigDto config)
    {
        var operatorsPart = string.Join('\n', config.Operators.Select(o => string.Join('',
            o.Id, o.FullName, o.Username, o.Email, o.PasswordHash,
            o.MobileNumber, o.AccessPin, o.IsGlobalAdmin, o.DashboardPermissions, o.IsBlocked)));

        var menuPart = string.Join('\n', config.MenuItems.Select(i => string.Join('',
            i.Id, i.ItemName, i.Category, i.Price, i.ImageUrl, i.IsDisabled)));
        // PasswordHash included on purpose - a password-only change (Super Admin sets one, or a
        // member resets from their phone) must move this fingerprint on its own, or this whole
        // fix does nothing the one time it matters: nothing else about the member changed, so
        // without it "did anything change?" answers no and the new hash never goes out.

        // HomeBranchId included on purpose, same reasoning as PasswordHash above - without it,
        // a branch that already has a stale (null) HomeBranchId locally keeps reporting the
        // same fingerprint it always has, since nothing else about that member changed either.
        // Head Office then answers "nothing new" forever, and the fix that backfills it
        // (ApplyOneMemberAsync) never runs because the config never even arrives. Confirmed
        // live: Adajan sat on this exact gap for 11+ hours straight across hundreds of beats.
        var membersPart = string.Join('\n', config.Members.Select(m => string.Join('',
            m.Id, m.FullName, m.MemberNumber, m.MobileNumber, m.Email, m.Username, m.HomeBranchId,
            m.PasswordHash, m.PasswordChangedAt, m.GamingBalance, m.FoodBalance, m.BalanceAsOf, m.IsBlocked)));

        var pricingPart = string.Join('\n', config.PricingProfiles.Select(p => string.Join("",
            p.Id, p.Name, p.BaseHourlyRate, p.BufferMinutes, p.IsActive, p.RefreshRate, p.SystemSpecs,
            string.Join('|', p.Packages.Select(pkg => string.Join(',',
                pkg.Id, pkg.Name, pkg.DurationMinutes, pkg.Price, pkg.SortOrder, pkg.IsActive))))));

        var adminsPart = string.Join('\n', config.Admins.Select(a => string.Join("",
            a.Id, a.FullName, a.Email, a.PasswordHash, a.AccessPin, a.DashboardPermissions, a.IsBlocked)));

        var canonical = string.Join("\n---\n", operatorsPart, menuPart, membersPart, pricingPart, adminsPart);

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>
    /// Marks the branch's operators on or off duty to match what the branch says.
    ///
    /// This is the fix for a super admin opening Settings and seeing "logged out" beside an
    /// operator who is at that moment running the shop. Nothing ever told Head Office
    /// otherwise — an operator's status was written only where they logged in, which is the
    /// branch, and it stayed there.
    ///
    /// Scoped strictly to this branch's own operators. A heartbeat from Adajan must never be
    /// able to sign somebody out at Katargam.
    /// </summary>
    private async Task ApplyOperatorsOnDutyAsync(BranchHeartbeatDto dto, CancellationToken ct)
    {
        var onDuty = dto.OperatorsOnDuty.Select(o => o.OperatorId).ToHashSet();

        var branchOperators = await _db.Operators
            .Where(o => o.BranchId == dto.BranchId)
            .ToListAsync(ct);

        foreach (var op in branchOperators)
        {
            var isOn = onDuty.Contains(op.Id);

            // Suspended and disabled accounts are left exactly as they are. Those are Head
            // Office's decisions about a person, not a branch's observation of a shift, and a
            // heartbeat must not quietly reinstate somebody an admin has just suspended.
            if (op.Status is OperatorStatus.Suspended or OperatorStatus.Disabled) continue;

            op.Status = isOn ? OperatorStatus.Active : OperatorStatus.LoggedOut;
            op.IsOnline = isOn;
        }
    }

    /// <summary>
    /// Makes Head Office's PC grid show what the counter's grid shows.
    ///
    /// Session events already move a PC between busy and free, but only for the PCs a session
    /// happens on. Everything else — a machine put into maintenance, one held for a
    /// reservation, one waiting for the customer to pay — never moved at all, which is how
    /// three of Adajan's PCs sat on "awaiting billing" from early August against sessions that
    /// no longer existed.
    /// </summary>
    private async Task<List<Guid>> ApplyPcStatesAsync(BranchHeartbeatDto dto, CancellationToken ct)
    {
        var changed = new List<Guid>();
        if (dto.Pcs.Count == 0) return changed;

        var ids = dto.Pcs.Select(p => p.PcId).ToList();
        var pcs = await _db.Pcs
            .Where(p => p.BranchId == dto.BranchId && ids.Contains(p.Id))
            .ToListAsync(ct);

        var awaitingOrders = await PcsAwaitingAnOrderAsync(dto.BranchId, ct);

        foreach (var pc in pcs)
        {
            // The authority rule, and the reason a remote instruction stops being undone.
            //
            // A command is in flight for anything from a few hundred milliseconds to the next
            // beat, and during that window the branch is still reporting the old truth - it has
            // not been told yet. Applying that report would overwrite what Head Office just
            // asked for with the state it asked to change, and the screen would flick back
            // within three seconds. Somebody presses "maintenance", watches it take, watches it
            // undo itself, and concludes the button is broken. It was not: it was being
            // outvoted by a stale heartbeat twenty times a minute.
            //
            // So while this branch owes an answer on a PC, Head Office stops writing that PC
            // from heartbeats. This is deliberately narrow - only the PCs actually named by an
            // open command, and only until the branch reports back, successfully or not. Every
            // other PC in the shop keeps updating as normal, and the moment the command closes
            // this one does too, carrying whatever the branch actually did.
            if (awaitingOrders.Contains(pc.Id))
            {
                _logger.LogWarning(
                    "PC {PcNumber} ({PcId}) on branch {BranchId} was NOT updated from this " +
                    "heartbeat - an open command is holding it. Head Office still shows " +
                    "{HeadOfficeState}/{HeadOfficeSession}.",
                    pc.PcNumber, pc.Id, dto.BranchId, pc.State, pc.CurrentSessionId);
                continue;
            }

            var reported = dto.Pcs.FirstOrDefault(p => p.PcId == pc.Id);
            if (reported is null)
            {
                // Diagnostic for a real incident: a PC Head Office knows about that this
                // specific heartbeat did not mention at all. First() used to throw here,
                // which - since ApplyPcStatesAsync is now individually try/caught in
                // Receive() - would have quietly aborted the whole loop for every PC still
                // left to process this beat, not just this one.
                _logger.LogWarning(
                    "PC {PcNumber} ({PcId}) on branch {BranchId} was not present in this " +
                    "heartbeat's PC list at all ({ReportedCount} PCs reported). Left unchanged.",
                    pc.PcNumber, pc.Id, dto.BranchId, dto.Pcs.Count);
                continue;
            }

            if (!Enum.TryParse<PcState>(reported.State.Replace("_", ""), ignoreCase: true, out var state))
            {
                _logger.LogWarning(
                    "PC {PcNumber} ({PcId}) on branch {BranchId} reported a state this build " +
                    "does not recognise: '{ReportedState}'. Left unchanged.",
                    pc.PcNumber, pc.Id, dto.BranchId, reported.State);
                continue;   // a state this Head Office build does not know; leave it alone
            }

            // Nothing is written for a PC that has not moved, and this is what lets branches
            // report every three seconds instead of every thirty.
            //
            // The timestamps are the whole point. State and CurrentSessionId are compared by
            // EF, which skips an UPDATE when neither differs - but stamping LastActiveAt and
            // UpdatedAt unconditionally defeated that and forced a write for every PC on every
            // beat. Sixteen idle machines at four branches, twenty beats a minute, is around
            // 1,300 pointless row updates a minute for a chain where nothing is happening.
            //
            // It also made UpdatedAt useless. "Last changed" that changes every three seconds
            // whether or not anything changed answers no question anybody would ask of it.
            //
            // SessionStartTime/EndTime compared too, not just the session id - a session gets
            // extended without ever changing which session it is, and an extension is exactly
            // the kind of change Head Office needs to actually see, or its screen would show a
            // customer's remaining time as whatever the original plan said, forever.
            if (pc.State == state
                && pc.CurrentSessionId == reported.CurrentSessionId
                && pc.CurrentSessionStartTime == reported.SessionStartTime
                && pc.CurrentSessionEndTime == reported.SessionEndTime
                && pc.CurrentSessionPackagePrice == reported.SessionPackagePrice
                && pc.CurrentSessionPlannedDurationMin == reported.SessionPlannedDurationMin
                && pc.PoweredOff == reported.PoweredOff)
                continue;

            _logger.LogInformation(
                "PC {PcNumber} ({PcId}) on branch {BranchId} moving {OldState}/{OldSession}/poweredOff={OldPoweredOff} -> " +
                "{NewState}/{NewSession}/poweredOff={NewPoweredOff} from heartbeat.",
                pc.PcNumber, pc.Id, dto.BranchId, pc.State, pc.CurrentSessionId, pc.PoweredOff,
                state, reported.CurrentSessionId, reported.PoweredOff);

            // Written on the Audit Trail specifically for PoweredOff, not for every routine
            // Idle/Active/Reserved churn a busy shop produces dozens of times an hour - this is
            // the one field that used to never arrive at all (see PcStateDto.PoweredOff), so it
            // is the one worth a visible row confirming Head Office actually got it. If the
            // branch's own "pc_shutdown" row exists but this one never shows up for the same PC
            // around the same time, that gap IS the problem - the heartbeat left the branch
            // reporting one thing and Head Office never heard it.
            //
            // The field assignment happens before the log call (not after) so a save triggered
            // by AuditService.LogAsync - which shares this DbContext - always commits the two
            // together. That alone was not the whole story: one AE-CTL machine's own power
            // detection is itself flapping true/false on every single beat, three seconds apart,
            // which is a real (if noisy) reported change each time, not a persistence bug. The
            // dashboard still needs the live value, but the audit trail does not need the same
            // flap recorded twenty times a minute forever - so this is throttled to at most once
            // per PC per minute, independent of whether the underlying value is actually stable.
            bool poweredOffChanged = pc.PoweredOff != reported.PoweredOff;

            pc.State = state;
            pc.CurrentSessionId = reported.CurrentSessionId;
            pc.CurrentSessionStartTime = reported.SessionStartTime;
            pc.CurrentSessionEndTime = reported.SessionEndTime;
            pc.CurrentSessionPackagePrice = reported.SessionPackagePrice;
            pc.CurrentSessionPlannedDurationMin = reported.SessionPlannedDurationMin;
            pc.PoweredOff = reported.PoweredOff;
            pc.LastActiveAt = DateTimeOffset.UtcNow;
            pc.UpdatedAt = DateTimeOffset.UtcNow;
            changed.Add(pc.Id);

            var alreadyLoggedRecently = _recentPoweredOffAudit.TryGetValue(pc.Id, out var lastLoggedAt)
                && DateTimeOffset.UtcNow - lastLoggedAt < PoweredOffAuditThrottle;

            if (poweredOffChanged && !alreadyLoggedRecently)
            {
                _recentPoweredOffAudit[pc.Id] = DateTimeOffset.UtcNow;
                await _audit.LogAsync(new AuditEntry
                {
                    UserRole = "System",
                    UserName = "System",
                    Action = "pc_powered_off_synced",
                    BranchId = dto.BranchId,
                    TargetType = "pc",
                    TargetId = pc.Id,
                    Details = new { pcNumber = pc.PcNumber, poweredOff = reported.PoweredOff },
                });
            }
        }

        return changed;
    }

    /// <summary>
    /// How long to leave a failed (or still-open) add-PC attempt alone before trying again for
    /// the same PC. Found live, the hard way: a branch running a build old enough not to
    /// understand CreatePcDto.Id creates its own row with a fresh id every time, which can
    /// never satisfy the "is Head Office's id in the reported list" check below - so without a
    /// cooldown this re-queued a doomed retry every single heartbeat, forever, three seconds
    /// apart, for as long as that branch stayed on the old build. Matches the pace of the other
    /// self-healing sweeps in this codebase rather than the three-second beat, on purpose: this
    /// is a safety net for something rare (a PC Head Office thinks exists that a branch has
    /// genuinely never heard of), not a routine sync path that needs to be fast.
    /// </summary>
    private static readonly TimeSpan MissingPcRetryEvery = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Heals a PC that exists at Head Office for this branch but that this branch's own
    /// heartbeat never mentions - meaning the branch's local database genuinely does not have
    /// it, not that this one beat happened to omit it (every PC is reported on every beat, see
    /// BranchHeartbeatDto.Pcs). Found live: a PS5 added from Head Office landed only in Head
    /// Office's own database and stayed invisible to Testing branch indefinitely, because
    /// nothing was ever watching for exactly this gap - the add command that should have
    /// created it there predates this reconciliation entirely and was simply never queued.
    ///
    /// Skips a PC that already has an add-PC command in flight or attempted recently (see
    /// MissingPcRetryEvery), so this does not hammer the branch with a fresh copy every three
    /// seconds while an attempt is either still working its way there or has already failed for
    /// a reason that resending the identical command will not fix on its own.
    /// </summary>
    private async Task QueueMissingPcsAsync(BranchHeartbeatDto dto, CancellationToken ct)
    {
        if (dto.Pcs.Count == 0) return;   // an empty report is a branch with no PCs at all, not one missing everything

        var reportedIds = dto.Pcs.Select(p => p.PcId).ToHashSet();

        var ourPcs = await _db.Pcs.AsNoTracking()
            .Where(p => p.BranchId == dto.BranchId && !p.IsDeleted)
            .ToListAsync(ct);

        var missing = ourPcs.Where(p => !reportedIds.Contains(p.Id)).ToList();
        if (missing.Count == 0) return;

        var recentCutoff = DateTimeOffset.UtcNow - MissingPcRetryEvery;
        var openAddCommands = await _db.Set<BranchCommand>().AsNoTracking()
            .Where(c => c.BranchId == dto.BranchId
                && c.CommandType == AppleEsportsErp.Api.Services.BranchCommands.AddPc
                && (c.Status == BranchCommandStatus.Pending
                    || c.Status == BranchCommandStatus.Sent
                    || c.CreatedAt > recentCutoff))
            .Select(c => c.Payload)
            .ToListAsync(ct);

        // Deserialized as the real DTO, not hand-parsed, specifically so this cannot go wrong on
        // casing again - JsonSerializer.Serialize(payload) wrote this with the C# property names
        // verbatim ("Id", not "id"), and a manual TryGetProperty("id", ...) lookup against that
        // is case-sensitive and simply never matches. It looked like a working de-dup guard and
        // was not: every retry queued a fresh one, confirmed live at one every three seconds for
        // several minutes straight before this was caught. Case-insensitive here so however any
        // payload in this codebase happens to be cased, this cannot silently stop matching again.
        var alreadyQueuedIds = new HashSet<Guid>();
        foreach (var payload in openAddCommands)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<CreatePcDto>(payload, CaseInsensitiveJson);
                if (parsed?.Id is { } id) alreadyQueuedIds.Add(id);
            }
            catch (JsonException) { /* an unreadable payload is a different problem; skip it here */ }
        }

        foreach (var pc in missing)
        {
            if (alreadyQueuedIds.Contains(pc.Id)) continue;

            var payload = new CreatePcDto
            {
                Id = pc.Id,
                PcNumber = pc.PcNumber,
                PcName = pc.PcName,
                BranchId = pc.BranchId,
                IpAddress = pc.IpAddress,
                Specs = pc.Specs,
                Zone = pc.Zone,
                HardwareNotes = pc.HardwareNotes,
                PricingProfileId = pc.PricingProfileId,
            };

            _db.Add(new BranchCommand
            {
                Id = Guid.NewGuid(),
                BranchId = dto.BranchId,
                CommandType = AppleEsportsErp.Api.Services.BranchCommands.AddPc,
                Payload = JsonSerializer.Serialize(payload),
                Status = BranchCommandStatus.Pending,
                RequestedByUserId = Guid.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            _logger.LogWarning(
                "PC {PcNumber} ({PcId}) exists at Head Office for branch {BranchId} but was " +
                "missing from its own heartbeat - queuing a fresh add so it reaches the branch.",
                pc.PcNumber, pc.Id, dto.BranchId);

            await _audit.LogAsync(new AuditEntry
            {
                UserRole = "System",
                UserName = "System",
                Action = "pc_resync_queued",
                BranchId = dto.BranchId,
                TargetType = "pc",
                TargetId = pc.Id,
                Details = new { pcNumber = pc.PcNumber, reason = "missing from the branch's own heartbeat" },
            });
        }
    }

    /// <summary>
    /// The PCs this branch still owes Head Office an answer about.
    ///
    /// Worked out from the open commands themselves rather than from a flag on the PC, so there
    /// is nothing to set, nothing to clear, and nothing to leak. A crash between queuing a
    /// command and the branch answering it cannot strand a PC in an un-updatable state: the
    /// command is either still open, in which case the PC genuinely is still awaiting an
    /// answer, or it is closed and this returns nothing.
    ///
    /// Both shapes of command are covered. set_pc_state names its PC outright. stop_session and
    /// start_session name a session or a PC, and a stop has to be traced through the session to
    /// the PC it is running on - that is the case that matters most, because a stop is precisely
    /// when the branch's report and Head Office's intent disagree.
    /// </summary>
    private async Task<HashSet<Guid>> PcsAwaitingAnOrderAsync(Guid branchId, CancellationToken ct)
    {
        // Bounded by the same give-up window that closes an unanswered command, so a PC can
        // never be held out of reporting indefinitely by an instruction nobody is coming for.
        var oldestWorthWaitingFor = DateTimeOffset.UtcNow - CommandGivenUpAfter;

        var open = await _db.Set<BranchCommand>().AsNoTracking()
            .Where(c => c.BranchId == branchId
                && c.CreatedAt > oldestWorthWaitingFor
                && (c.Status == BranchCommandStatus.Pending || c.Status == BranchCommandStatus.Sent))
            .Select(c => new { c.CommandType, c.Payload })
            .ToListAsync(ct);

        if (open.Count == 0) return new HashSet<Guid>();

        var pcIds = new HashSet<Guid>();
        var sessionIds = new List<Guid>();

        foreach (var command in open)
        {
            try
            {
                using var doc = JsonDocument.Parse(command.Payload);
                var root = doc.RootElement;

                if (root.TryGetProperty("pcId", out var pc) && pc.TryGetGuid(out var pcId))
                    pcIds.Add(pcId);

                // A transfer names two PCs, not one - the source (resolved below through the
                // session it is currently attached to) and the destination it is moving onto.
                // Both have to be held: the destination is exactly what a stray heartbeat would
                // otherwise report as still idle a moment before the branch's own transfer has
                // actually landed, undoing the move on Head Office's screen before it is even
                // confirmed.
                if (root.TryGetProperty("targetPcId", out var t) && t.TryGetGuid(out var targetPcId))
                    pcIds.Add(targetPcId);

                if (root.TryGetProperty("sessionId", out var s) && s.TryGetGuid(out var sessionId))
                    sessionIds.Add(sessionId);
            }
            catch (JsonException)
            {
                // A payload Head Office cannot read is a bug in whatever queued it, not a reason
                // to stop protecting every other PC in the shop. Skip it and carry on.
                _logger.LogWarning(
                    "A {CommandType} command for branch {BranchId} has an unreadable payload; " +
                    "its PC will keep taking heartbeat updates.", command.CommandType, branchId);
            }
        }

        if (sessionIds.Count > 0)
        {
            var fromSessions = await _db.Sessions.AsNoTracking()
                .Where(s => s.BranchId == branchId && sessionIds.Contains(s.Id))
                .Select(s => s.PcId)
                .ToListAsync(ct);

            foreach (var pcId in fromSessions) pcIds.Add(pcId);
        }

        return pcIds;
    }

    /// <summary>
    /// Every branch and whether it is still talking, for the super admin's overview.
    ///
    /// A branch that has never reported appears too, rather than being left out. "Never heard
    /// from" is the single most important thing this screen can say, and omitting the row
    /// would hide exactly the branch worth worrying about.
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> All(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var branches = await _db.Branches
            .Where(b => b.Status == BranchStatus.Active)
            .OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(ct);

        var beats = await _db.Set<BranchHeartbeat>().ToDictionaryAsync(h => h.BranchId, ct);

        var rows = branches.Select(b =>
        {
            beats.TryGetValue(b.Id, out var beat);
            var silent = beat is null || now - beat.LastSeenAt > SilentAfter;

            return new
            {
                branchId = b.Id,
                branchName = b.Name,
                reporting = !silent,
                lastSeenAt = beat?.LastSeenAt,
                secondsSinceLastSeen = beat is null ? (int?)null : (int)(now - beat.LastSeenAt).TotalSeconds,
                version = beat?.Version,
                reportedByMachine = beat?.ReportedByMachine,

                // Surfaced beside the branch's own figures rather than buried in a log,
                // because every number on this row is wrong while two machines are supplying
                // them and nobody would think to doubt them otherwise.
                conflictingMachine = beat?.ConflictingMachine,
                conflictingMachineSeenAt = beat?.ConflictingMachineSeenAt,
                operatorsOnDuty = beat is null
                    ? new List<OperatorOnDutyDto>()
                    : JsonSerializer.Deserialize<List<OperatorOnDutyDto>>(beat.OperatorsOnDuty ?? "[]")
                      ?? new List<OperatorOnDutyDto>(),
                activeSessions = beat?.ActiveSessions ?? 0,
                pcsBusy = beat?.PcsBusy ?? 0,
                pcsTotal = beat?.PcsTotal ?? 0,
                drawerExpected = beat?.DrawerExpected,
                takingsToday = beat?.TakingsToday ?? 0m,
                undeliveredRecords = beat?.UndeliveredRecords ?? 0,
            };
        });

        return Ok(ApiResponse<object>.Ok(rows));
    }
}
