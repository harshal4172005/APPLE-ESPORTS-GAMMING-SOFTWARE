using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Sessions;
using AppleEsportsErp.Application.DTOs.Wallets;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Infrastructure.Services;

public class SessionService : ISessionService
{
    private readonly IUnitOfWork _uow;
    private readonly AppDbContext _db;
    private readonly IHubNotificationService _hubNotifier;
    private readonly IAuditService _audit;
    private readonly IPcStatusService _pcStatus;
    private readonly ILogger<SessionService> _logger;
    private readonly IWalletService _walletService;
    private readonly ISessionActivityService _activityService;
    private readonly IOutboxService _outbox;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// The last line of defence against Head Office writing a branch's trading directly.
    ///
    /// Head Office can now start and stop play at any shop - that was asked for and it is
    /// built. What it does not do is write the session itself. It sends the branch an
    /// instruction, the branch runs this same method locally, and the result syncs back. So by
    /// the time execution reaches here, it is always a branch doing its own work, and Head
    /// Office reaching this point means something has bypassed the command queue.
    ///
    /// Why that must still throw, when the feature exists: sync carries operations one way. A
    /// session written into Head Office's copy is written nowhere else. The counter never sees
    /// it, so no operator can stop or bill it, and a customer sits there playing for free.
    /// Exactly that happened on ADJ-PC-01 - Head Office showed an hour running and counting
    /// down while the counter showed PC-01 free. It also explains a Stop button that "does
    /// nothing": start on one screen, stop on the other, and there is nothing there to stop.
    ///
    /// The message names the queue, because anybody who hits this is one call away from the
    /// thing they actually wanted.
    /// </summary>
    /// <summary>
    /// Records that an attempt to touch a session did not work, before the exception is
    /// rethrown to whoever is actually waiting on it.
    ///
    /// Every one of these five methods already tells the person at the counter why it failed -
    /// a toast, a red banner, gone the moment they click past it. None of that was ever kept.
    /// "PC not idle", "insufficient wallet balance", a session somebody tried to start twice -
    /// all of it happened, all of it mattered to somebody at the time, and none of it could be
    /// looked back on the next morning. This is the same audit trail every successful session
    /// action already writes to; a failure was simply the one outcome it never recorded.
    ///
    /// Never allowed to throw itself - AuditService.LogAsync already swallows its own failures,
    /// and a session that failed to start must not fail a second time, differently, because the
    /// note-taking about the first failure went wrong.
    /// </summary>
    private async Task LogSessionFailureAsync(
        Guid branchId, Guid operatorId, string action, Guid? targetId, Exception ex)
    {
        await _audit.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = Roles.Operator,
            Action = action,
            TargetType = "session",
            TargetId = targetId,
            Success = false,
            BranchId = branchId,
            Details = new { error = ex.GetBaseException().Message },
        });
    }

    private void RefuseIfHeadOffice(string what)
    {
        if (!_configuration.IsHeadOffice()) return;

        throw new AppException(
            $"This session has to be {what} by the branch itself, not written here at Head Office - " +
            "a session written here is invisible to the counter, so nobody could bill it. " +
            "Send it as an instruction instead (api/branch-commands) and the branch will carry it " +
            "out within a few seconds and report back.",
            System.Net.HttpStatusCode.BadRequest,
            "BRANCH_ONLY_OPERATION");
    }

    public SessionService(
        IUnitOfWork uow,
        AppDbContext db,
        IHubNotificationService hubNotifier,
        IAuditService audit,
        IPcStatusService pcStatus,
        ILogger<SessionService> logger,
        IWalletService walletService,
        ISessionActivityService activityService,
        IOutboxService outbox,
        IConfiguration configuration)
    {
        _configuration = configuration;
        _uow = uow;
        _db = db;
        _hubNotifier = hubNotifier;
        _audit = audit;
        _pcStatus = pcStatus;
        _logger = logger;
        _walletService = walletService;
        _activityService = activityService;
        _outbox = outbox;
    }

    public async Task<PaginatedResult<SessionDto>> GetActiveSessionsAsync(Guid branchId, int page, int pageSize)
    {
        var items = await _db.Sessions
            .Include(s => s.Pc)
            .Include(s => s.Bills)
            .Where(s => s.BranchId == branchId && s.State == SessionState.Active)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        var uniqueItems = items
            .GroupBy(s => s.PcId)
            .Select(g => g
                .OrderByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.StartTime)
                .First())
            .OrderByDescending(s => s.StartTime)
            .ToList();

        var total = uniqueItems.Count;
        var pageItems = uniqueItems
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var dtos = pageItems.Select(s => new SessionDto
        {
            Id = s.Id,
            PcId = s.PcId,
            PcName = s.Pc?.PcNumber ?? "Unknown PC",
            BranchId = s.BranchId,
            OperatorId = s.OperatorId,
            ShiftId = s.ShiftId ?? Guid.Empty,
            CustomerName = s.CustomerName,
            MemberId = s.MemberId,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            DurationMinutes = s.ActualDurationMin ?? s.PlannedDurationMin ?? 0,
            ExpectedAmount = s.Bills.FirstOrDefault()?.TotalAmount ?? s.TotalAmount,
            PackageName = s.GamingType,
            Status = s.State,
            BillId = s.Bills.FirstOrDefault()?.Id ?? Guid.Empty
        }).ToList();

        return new PaginatedResult<SessionDto>(dtos, total, page, pageSize);
    }

    public async Task<SessionDto> StartSessionAsync(Guid branchId, Guid operatorId, Guid shiftId, SessionStartDto dto)
    {
        RefuseIfHeadOffice("started");

        await _uow.BeginTransactionAsync();

        try
        {
            var pc = await _db.Pcs.FindAsync(dto.PcId);
            if (pc == null || pc.BranchId != branchId)
                throw new NotFoundException("PC not found or does not belong to this branch", "PC_NOT_FOUND");

            if (pc.PricingProfileId == null)
                throw new AppException($"{pc.PcNumber} has no Pricing Profile assigned. Ask a Super Admin to assign one in Settings → Pricing Profiles before starting a session on this PC.", System.Net.HttpStatusCode.BadRequest, "NO_PRICING_PROFILE");

            var existingOpenSession = await _db.Sessions
                .AsNoTracking()
                .Where(s => s.BranchId == branchId
                    && s.PcId == pc.Id
                    // Interrupted counts as open: a session held after a power cut is still
                    // sitting on this PC, waiting for the operator to resume or stop it.
                    && (s.State == SessionState.Active
                        || s.State == SessionState.Interrupted
                        || s.State == SessionState.AwaitingBilling))
                .OrderByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();

            if (existingOpenSession != null)
            {
                throw new AppException(
                    $"Cannot start session. PC already has an open session ({existingOpenSession.State}).",
                    System.Net.HttpStatusCode.BadRequest,
                    "PC_ALREADY_HAS_SESSION");
            }

            if (pc.State != PcState.Idle)
            {
                if (pc.State == PcState.Reserved)
                {
                    throw new AppException("This PC is reserved. Manual session creation is blocked.", System.Net.HttpStatusCode.BadRequest, "PC_RESERVED");
                }
                else
                {
                    throw new AppException($"Cannot start session. PC is currently {pc.State}", System.Net.HttpStatusCode.BadRequest, "PC_NOT_IDLE");
                }
            }

            // A member session bills straight out of the Gaming wallet, so it can never be
            // started on an empty one — it would be auto-stopped by the wallet monitor on the
            // very first tick. Blocked here so it covers every entry point (operator start,
            // member overlay start, walk-in conversion) with one rule.
            if (dto.MemberId.HasValue)
            {
                var member = await _db.Members.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == dto.MemberId.Value)
                    ?? throw new NotFoundException("Member not found", "MEMBER_NOT_FOUND");

                if (member.GamingBalance < MemberWalletRules.MinimumGamingBalanceToStart)
                {
                    throw new AppException(
                        $"Cannot start session — {member.FullName}'s Gaming wallet balance is ₹{member.GamingBalance:0.00}. Please top up the Gaming wallet before starting a session.",
                        System.Net.HttpStatusCode.BadRequest,
                        "INSUFFICIENT_GAMING_BALANCE");
                }

                // One member, one PC at a time. Two live sessions would both draw down the
                // same Gaming wallet, so they would race each other and could overdraw it.
                // Deliberately not filtered by branch: the wallet is shared across all four,
                // so playing at Adajan and Citylight at once is the same problem.
                // Interrupted counts as occupied — that seat is being held for them.
                var sessionElsewhere = await _db.Sessions.AsNoTracking()
                    .Include(s => s.Pc)
                    .Include(s => s.Branch)
                    .Where(s => s.MemberId == dto.MemberId.Value
                        && (s.State == SessionState.Active || s.State == SessionState.Interrupted))
                    .FirstOrDefaultAsync();

                if (sessionElsewhere != null)
                {
                    var where = sessionElsewhere.Pc?.PcNumber ?? "another PC";
                    var atBranch = sessionElsewhere.BranchId == branchId
                        ? string.Empty
                        : $" at {sessionElsewhere.Branch?.Name ?? "another branch"}";

                    throw new AppException(
                        $"{member.FullName} already has a session running on {where}{atBranch}. " +
                        "Stop that session before starting a new one.",
                        System.Net.HttpStatusCode.BadRequest,
                        "MEMBER_ALREADY_IN_SESSION");
                }
            }

            var now = DateTimeOffset.UtcNow;

            // Same rule as ExtendSessionAsync, and the same reason: a fixed-duration plan has
            // one true price, the one on the branch's own PricingPackage for that exact
            // duration, and a client's own calculation of it is a preview, never the authority.
            // Only overrides when a plan actually exists for this exact duration - Pay-As-You-Go
            // (DurationMinutes 0) and any duration with no matching package keep trusting the
            // caller's figure, same as before, since there is nothing here to check it against.
            decimal expectedAmount = dto.ExpectedAmount;
            PricingPackage? matchingPackage = null;
            if (dto.DurationMinutes > 0)
            {
                matchingPackage = await _db.Set<PricingPackage>().AsNoTracking()
                    .Where(pkg => pkg.PricingProfileId == pc.PricingProfileId
                        && pkg.IsActive && pkg.DurationMinutes == (int)dto.DurationMinutes)
                    .OrderBy(pkg => pkg.SortOrder)
                    .FirstOrDefaultAsync();

                if (matchingPackage != null)
                    expectedAmount = matchingPackage.Price;
            }

            var session = new Session
            {
                Id = Guid.NewGuid(),
                PcId = pc.Id,
                BranchId = branchId,
                OperatorId = operatorId,
                ShiftId = shiftId == Guid.Empty ? null : shiftId,
                CustomerName = dto.CustomerName,
                MemberId = dto.MemberId,
                StartTime = now,
                EndTime = dto.DurationMinutes > 0 ? now.AddMinutes((double)dto.DurationMinutes) : null,
                PlannedDurationMin = dto.DurationMinutes > 0 ? (int)dto.DurationMinutes : null,
                TotalAmount = expectedAmount,
                GamingAmount = expectedAmount,
                // Only a real catalog package is a committed prepaid price Stop should honor
                // later — a plain duration with no matching package is just the client's guess,
                // same as before this field existed.
                PricingPackageId = matchingPackage?.Id,
                PackagePrice = matchingPackage?.Price,
                PackageMinutes = matchingPackage?.DurationMinutes,
                PackageFromMin = matchingPackage?.PackagePriceFromMin,
                GamingType = dto.PackageName,
                State = SessionState.Active,
                Notes = dto.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _uow.Repository<Session>().AddAsync(session);

            // Wait, does BillStatus have Pending? Let's assume it does. 
            // The domain entities might differ slightly, let me verify it or use a default.
            // Let's create the bill directly.
            var bill = new Bill
            {
                Id = Guid.NewGuid(),
                BillNumber = $"BILL-{now:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 4).ToUpper()}",
                SessionId = session.Id,
                PcId = pc.Id,
                BranchId = branchId,
                OperatorId = operatorId,
                ShiftId = shiftId == Guid.Empty ? null : shiftId,
                CustomerName = dto.CustomerName,
                MemberId = dto.MemberId,
                GamingAmount = expectedAmount,
                FoodAmount = 0,
                Subtotal = expectedAmount,
                TotalAmount = expectedAmount,
                Status = BillStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _uow.Repository<Bill>().AddAsync(bill);

            var billItem = new BillItem
            {
                Id = Guid.NewGuid(),
                BillId = bill.Id,
                ItemType = "gaming",
                ItemName = $"Base Session ({dto.DurationMinutes}m)",
                Quantity = 1,
                UnitPrice = expectedAmount,
                TotalPrice = expectedAmount,
                CreatedAt = now
            };
            
            await _uow.Repository<BillItem>().AddAsync(billItem);

            pc.State = PcState.Active;
            pc.CurrentSessionId = session.Id;
            _uow.Repository<Pc>().Update(pc);

            // Tell Head Office this happened. Written inside the same transaction as the
            // session itself, so the two commit together or not at all — a session that
            // exists at the branch but never reaches Head Office is exactly the "the
            // numbers don't add up across branches" problem this whole design avoids.
            await _outbox.RecordEventAsync(branchId, "Session", session.Id, "session.started", new
            {
                sessionId = session.Id,
                pcId = pc.Id,
                pcNumber = pc.PcNumber,
                operatorId,
                shiftId,
                memberId = dto.MemberId,
                customerName = dto.CustomerName,
                startTime = session.StartTime,
                plannedDurationMin = session.PlannedDurationMin,
                gamingType = session.GamingType,
                expectedAmount = dto.ExpectedAmount,
            });

            await _uow.CommitTransactionAsync();

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = Roles.Operator,
                UserName = "System",
                Action = AuditActions.SessionStart,
                BranchId = branchId,
                TargetType = "session",
                TargetId = session.Id,
                Details = new { PcNumber = pc.PcNumber, dto.DurationMinutes, dto.ExpectedAmount }
            });

            await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, pc.Id);
            await _hubNotifier.BroadcastSessionUpdateAsync(branchId, session.Id);
            await _hubNotifier.BroadcastBillingUpdateAsync(branchId, bill.Id);

            // Log session activity
            await _activityService.LogActivityAsync(
                session.Id, branchId,
                "session_started",
                $"Session started for {dto.CustomerName} on {pc.PcNumber} - Duration: {dto.DurationMinutes}m, Amount: ₹{dto.ExpectedAmount}",
                dto.ExpectedAmount);

            // Dispatch Unlock Command to the actual PC Agent - with the actual price the
            // customer is being charged (package or hourly), see the Session Pricing PRD's
            // issue 07. The customer's own PC had never shown any price before this at all.
            var agentPricingProfile = await _db.Set<PricingProfile>().AsNoTracking()
                .FirstOrDefaultAsync(pp => pp.Id == pc.PricingProfileId);
            await _hubNotifier.SendUnlockCommandToAgentAsync(
                pc.Id, (int)dto.DurationMinutes, dto.CustomerName,
                packagePrice: session.PackagePrice, plannedDurationMin: session.PackageMinutes ?? session.PlannedDurationMin,
                    packageFromMin: session.PackageFromMin,
                packageName: matchingPackage?.Name, ratePerHour: agentPricingProfile?.BaseHourlyRate ?? 0m,
                bufferMinutes: agentPricingProfile?.BufferMinutes ?? SessionPricingCalculator.DefaultBufferMinutes,
                sessionStartUtc: session.StartTime);

            return new SessionDto
            {
                Id = session.Id,
                PcId = pc.Id,
                PcName = pc.PcNumber,
                BranchId = branchId,
                OperatorId = operatorId,
                ShiftId = shiftId,
                CustomerName = dto.CustomerName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                DurationMinutes = dto.DurationMinutes,
                ExpectedAmount = dto.ExpectedAmount,
                PackageName = dto.PackageName,
                Status = session.State,
                BillId = bill.Id
            };
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            await LogSessionFailureAsync(branchId, operatorId, AuditActions.SessionStart, dto.PcId, ex);
            throw;
        }
    }

    public async Task<SessionDto> StopSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, bool deferPayment = false, DateTimeOffset? asOfOverride = null)
    {
        RefuseIfHeadOffice("stopped");

        await _uow.BeginTransactionAsync();
        try
        {
            var session = await _db.Sessions
                .Include(s => s.Pc)
                    .ThenInclude(p => p.PricingProfile)
                .Include(s => s.Bills)
                    .ThenInclude(b => b.Items)
                .Include(s => s.Member)
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.BranchId == branchId);

            if (session == null)
                throw new NotFoundException("Session not found", "SESSION_NOT_FOUND");

            // Interrupted is stoppable too: that is the "customer left during the power cut"
            // case, and they must still be billed for the minutes they actually played.
            if (session.State != SessionState.Active && session.State != SessionState.Interrupted)
                throw new AppException("Session is already ended.", System.Net.HttpStatusCode.BadRequest, "SESSION_ALREADY_ENDED");

            // See the interface doc on asOfOverride - only the stale-session watchdog ever
            // passes one.
            var now = asOfOverride ?? DateTimeOffset.UtcNow;

            // Fold any time spent on hold into the paused total before billing, so the
            // wait for an operator's decision is never charged to the customer.
            SettleInterruption(session, now);

            session.State = SessionState.Completed;
            session.UpdatedAt = now;
            session.EndTime = now;
            // Time the branch spent powered off is not play time — bill only what was actually used.
            session.ActualDurationMin = (int)SessionTimeCalculator.ElapsedMinutes(
                session.StartTime, session.PausedSeconds, now);
            
            var bill = session.Bills.FirstOrDefault();

            // 1. Rate comes solely from the PC's assigned Pricing Profile — Start blocks any
            // session on a PC without one, so this should always be set. ₹0 (never a fabricated
            // guess) is the honest fallback for legacy sessions that predate that enforcement.
            decimal ratePerHour = session.Pc?.PricingProfile?.BaseHourlyRate ?? SessionPricingCalculator.DefaultRatePerHour;

            // 2. Honor a genuine prepaid package (PackagePrice, set only from a real catalog
            // PricingPackage at Start/Extend - never from a client's own guess) within a grace
            // window equal to the branch's own buffer, in either direction. Ending a few minutes
            // early still pays the prepaid block in full, same as any prepaid deal; running a few
            // minutes over still honors it too, rather than re-pricing the whole session pro-rata
            // for landing just outside an exact minute. Only a genuine overrun - past the grace
            // window - adds pro-rata billing for the extra minutes on top of the package price,
            // instead of discarding the deal and rebilling everything from zero.
            //
            // A session with no PackagePrice (Pay-As-You-Go, or a plain duration with no matching
            // catalog package) was never a committed price to begin with, so it always bills the
            // honest elapsed rate - same as before this existed.
            //
            // The buffer's free-cancellation window comes first and wins outright, package or
            // not: stopping inside it has always meant Rs 0 (see the "Cancelled" labelling right
            // below), and a package must not override that free window into a full charge just
            // because a price was committed - nobody signed up to pay for a session they ended
            // before it ever really started.
            int bufferMinutes = session.Pc?.PricingProfile?.BufferMinutes ?? SessionPricingCalculator.DefaultBufferMinutes;

            // Safety net: a session with a real planned duration (a package WAS picked at
            // Start - GamingType and PlannedDurationMin both say so) must never fall through
            // to plain hourly billing just because PackagePrice itself came back empty. Found
            // live on Citylight-144Hz: two "1 hr" sessions (₹50 each per the branch's own
            // Settings) billed ₹80 instead, because PackagePrice was null on both by the time
            // Stop ran - Pay-As-You-Go hourly took over silently, with no sign anywhere that
            // the committed package price had been lost. Re-deriving it fresh here, the exact
            // same lookup Start itself uses, closes that gap regardless of how PackagePrice
            // went missing - Stop no longer has to trust that Start's write landed.
            var packagePrice = session.PackagePrice;
            if (!packagePrice.HasValue && session.PlannedDurationMin is > 0 && session.Pc?.PricingProfileId is { } profileId)
            {
                var found = await _db.Set<PricingPackage>().AsNoTracking()
                    .Where(pkg => pkg.PricingProfileId == profileId
                        && pkg.IsActive && pkg.DurationMinutes == session.PlannedDurationMin.Value)
                    .OrderBy(pkg => pkg.SortOrder)
                    .FirstOrDefaultAsync();

                // Found it - honor it exactly as if Start had captured it, so this session's
                // own record stops disagreeing with itself the next time anything reads it.
                if (found != null)
                {
                    packagePrice = session.PackagePrice = found.Price;
                    session.PackageMinutes = found.DurationMinutes;
                    session.PackageFromMin = found.PackagePriceFromMin;
                }
            }

            // Now the same shared call every live-amount screen uses too (see
            // CalculateLiveGamingAmount's own comment) - this was the one place that already
            // got the package-vs-hourly branching right; it now just calls the version of
            // itself every other screen calls, instead of keeping its own private copy of it.
            session.GamingAmount = SessionPricingCalculator.CalculateLiveGamingAmount(
                packagePrice, session.PackageMinutes ?? session.PlannedDurationMin,
                ratePerHour, bufferMinutes, session.ActualDurationMin!.Value, session.PackageFromMin);

            if (session.ActualDurationMin <= bufferMinutes)
            {
                if (!session.GamingType.Contains("Cancelled"))
                {
                    var suffix = $" (Cancelled - Under {bufferMinutes}m Buffer)";
                    // Defensive cap — GamingType is a bounded DB column; never let a long
                    // package name + suffix silently fail the whole Stop transaction again.
                    const int maxLen = 150;
                    session.GamingType = (session.GamingType + suffix).Length > maxLen
                        ? session.GamingType.Substring(0, Math.Max(0, maxLen - suffix.Length)) + suffix
                        : session.GamingType + suffix;
                }
            }

            // Food orders update bill.FoodAmount directly when delivered (FoodOrderService) —
            // session.FoodAmount is never touched, so it must NOT be used as the source of
            // truth here. Reading it (always 0) would silently erase any food already billed.
            decimal foodAmount = bill?.FoodAmount ?? session.FoodAmount;
            session.FoodAmount = foodAmount;
            session.TotalAmount = session.GamingAmount + foodAmount;

            if (bill != null)
            {
                // Preserve any discount already applied to the bill instead of wiping it out.
                var (displayGaming, displayFood, roundedTotal) = SessionPricingCalculator.ComputeRoundedBreakdown(
                    session.GamingAmount, foodAmount, bill.DiscountAmount);

                bill.GamingAmount = displayGaming;
                bill.FoodAmount = displayFood;
                bill.Subtotal = displayGaming + displayFood;
                bill.TotalAmount = roundedTotal;

                var gamingItem = bill.Items.FirstOrDefault(i => i.ItemType == "gaming");
                if (gamingItem != null)
                {
                    gamingItem.ItemName = session.PlannedDurationMin == null ? $"Open Session ({session.ActualDurationMin}m)" : $"{session.GamingType}";
                    gamingItem.TotalPrice = displayGaming;
                    gamingItem.UnitPrice = displayGaming;

                    if (session.PackageMinutes is { } packageMinutes && packagePrice is { } price)
                        SplitPackageBillLines(bill, gamingItem, session, packageMinutes, price, displayGaming, bufferMinutes, now);
                }
            }

            // A ₹0 bill (free buffer, or a fully-discounted session) has nothing to collect —
            // auto-close it so the operator isn't forced to process a ₹0 "payment" and the PC
            // frees up immediately, matching the whole point of a free grace period.
            if (bill != null && bill.TotalAmount == 0 && bill.Status != BillStatus.Completed)
            {
                bill.Status = BillStatus.Completed;
            }

            var pc = session.Pc!;

            // 2. Member sessions are paid straight out of the wallet at stop time — no
            // separate manual "approve wallet payment" step for this path (that overlay
            // flow remains for other wallet-payable bills, e.g. mid-session food orders).
            decimal walletDeducted = 0m;
            decimal walletShortfall = 0m;

            if (session.MemberId != null && session.Member != null && bill != null
                && bill.Status != BillStatus.Completed && bill.TotalAmount > 0)
            {
                var member = session.Member;
                decimal gamingOwed = bill.GamingAmount;
                decimal foodOwed = bill.FoodAmount;

                decimal gamingDeduct = Math.Min(member.GamingBalance, gamingOwed);
                decimal foodDeduct = Math.Min(member.FoodBalance, foodOwed);

                if (gamingDeduct > 0)
                {
                    await _walletService.DeductWalletAsync(branchId, operatorId, session.ShiftId, member.Id, new DeductWalletDto
                    {
                        TargetWallet = WalletType.Gaming,
                        Amount = gamingDeduct,
                        Reason = $"Session Stop ({pc.PcNumber})",
                        BillId = bill.Id
                    }, commit: false);
                }

                if (foodDeduct > 0)
                {
                    await _walletService.DeductWalletAsync(branchId, operatorId, session.ShiftId, member.Id, new DeductWalletDto
                    {
                        TargetWallet = WalletType.Food,
                        Amount = foodDeduct,
                        Reason = $"Session Stop ({pc.PcNumber})",
                        BillId = bill.Id
                    }, commit: false);
                }

                walletDeducted = gamingDeduct + foodDeduct;
                walletShortfall = (gamingOwed - gamingDeduct) + (foodOwed - foodDeduct);

                // Bill is settled from the wallet's point of view either way — fully paid,
                // or partially paid with the rest tracked as a CustomerCredit for later collection.
                bill.IsDeferred = walletShortfall > 0;
                bill.Status = BillStatus.Completed;
            }

            var hasUnpaidBill = session.Bills.Any(b => b.Status != BillStatus.Completed);

            if (!hasUnpaidBill || deferPayment)
            {
                // Paid (member wallet) or operator deferred — free the PC immediately
                pc.State = PcState.Idle;
            }
            else
            {
                // Customer at counter waiting to pay
                pc.State = PcState.AwaitingBilling;
            }

            // Mark bill as deferred so payment is backdated to session date
            if (deferPayment && bill != null && bill.Status != BillStatus.Completed)
            {
                bill.IsDeferred = true;
                bill.Status = BillStatus.Completed; // It leaves the billing counter
                
                var customerCredit = new CustomerCredit
                {
                    BranchId = branchId,
                    OperatorId = operatorId,
                    BillId = bill.Id,
                    CustomerName = session.CustomerName ?? session.Member?.Username ?? "Walk-in",
                    CustomerPhone = session.Member?.MobileNumber ?? "N/A",
                    PcNumber = pc.PcNumber ?? "N/A",
                    OriginalBillAmount = bill.TotalAmount,
                    AmountPaidInitially = 0,
                    CreditAmount = bill.TotalAmount,
                    Status = "pending",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _uow.Repository<CustomerCredit>().AddAsync(customerCredit);
            }
            else if (walletShortfall > 0 && bill != null)
            {
                // Wallet didn't fully cover the bill — record only the unpaid remainder for
                // later collection, not the whole bill amount (part was already collected
                // from the wallet just now).
                var walletShortfallCredit = new CustomerCredit
                {
                    BranchId = branchId,
                    OperatorId = operatorId,
                    BillId = bill.Id,
                    CustomerName = session.CustomerName ?? session.Member?.Username ?? "Member",
                    CustomerPhone = session.Member?.MobileNumber ?? "N/A",
                    PcNumber = pc.PcNumber ?? "N/A",
                    OriginalBillAmount = bill.TotalAmount,
                    AmountPaidInitially = walletDeducted,
                    CreditAmount = walletShortfall,
                    Status = "pending",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _uow.Repository<CustomerCredit>().AddAsync(walletShortfallCredit);
            }

            pc.CurrentSessionId = null;
            
            _uow.Repository<Session>().Update(session);
            if (bill != null) _uow.Repository<Bill>().Update(bill);
            _uow.Repository<Pc>().Update(pc);

            // The money event. Carries the billed duration and the paused seconds behind it,
            // so Head Office can see not just what was charged but why it differs from the
            // wall clock when a power cut ate part of the session.
            await _outbox.RecordEventAsync(branchId, "Session", session.Id, "session.stopped", new
            {
                sessionId = session.Id,
                pcId = pc.Id,
                pcNumber = pc.PcNumber,
                operatorId,
                memberId = session.MemberId,
                customerName = session.CustomerName,
                startTime = session.StartTime,
                endTime = session.EndTime,
                billedMinutes = session.ActualDurationMin,
                pausedSeconds = session.PausedSeconds,
                gamingAmount = session.GamingAmount,
                foodAmount = session.FoodAmount,
                totalAmount = session.TotalAmount,
                billId = bill?.Id,
                deferred = deferPayment,
            });

            await _uow.CommitTransactionAsync();

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = Roles.Operator,
                UserName = "System",
                Action = AuditActions.SessionStop,
                BranchId = branchId,
                TargetType = "session",
                TargetId = session.Id,
                Details = new { PcNumber = pc.PcNumber, session.TotalAmount }
            });

            await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, pc.Id);
            await _hubNotifier.BroadcastSessionUpdateAsync(branchId, session.Id);
            if (session.Bills.Any())
            {
                await _hubNotifier.BroadcastBillingUpdateAsync(branchId, session.Bills.First().Id);
            }
            
            // Dispatch Lock Command to the actual PC Agent
            await _hubNotifier.SendLockCommandToAgentAsync(pc.Id);

            // Log session activity
            var status = deferPayment ? "deferred" : (bill?.TotalAmount == 0 ? "free" : "billing");
            await _activityService.LogActivityAsync(
                session.Id, branchId,
                "session_stopped",
                $"Session stopped - Duration: {session.ActualDurationMin}m, Gaming: ₹{session.GamingAmount}, Total: ₹{session.TotalAmount}, Status: {status}",
                session.TotalAmount);

            return new SessionDto
            {
                Id = session.Id,
                PcId = pc.Id,
                PcName = pc.PcNumber,
                BranchId = branchId,
                OperatorId = session.OperatorId,
                ShiftId = session.ShiftId ?? Guid.Empty,
                CustomerName = session.CustomerName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                DurationMinutes = session.ActualDurationMin ?? 0,
                ExpectedAmount = session.TotalAmount,
                PackageName = session.GamingType,
                Status = session.State,
                BillId = session.Bills.FirstOrDefault()?.Id ?? Guid.Empty,
                MemberId = session.MemberId,
                WalletDeductedAmount = walletDeducted > 0 ? walletDeducted : (decimal?)null,
                WalletShortfallAmount = walletShortfall > 0 ? walletShortfall : (decimal?)null
            };
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            await LogSessionFailureAsync(branchId, operatorId, AuditActions.SessionStop, sessionId, ex);
            throw;
        }
    }

    /// <summary>
    /// Folds time spent on hold after an outage into the session's paused total, and pushes
    /// a fixed-duration finish time out by the same amount. Called before billing a session
    /// and before resuming one, so the gap between the power returning and an operator
    /// acting on it is never charged to the customer. Safe to call on any session.
    /// </summary>
    private static void SettleInterruption(Session session, DateTimeOffset now)
    {
        if (session.InterruptedAt is not { } interruptedAt)
            return;

        var heldSeconds = (int)Math.Max(0, (now - interruptedAt).TotalSeconds);
        session.PausedSeconds += heldSeconds;

        if (session.EndTime is { } endTime)
            session.EndTime = endTime.AddSeconds(heldSeconds);

        session.InterruptedAt = null;
    }

    public async Task<SessionDto> ResumeSessionAsync(Guid branchId, Guid operatorId, Guid sessionId)
    {
        await _uow.BeginTransactionAsync();
        try
        {
            var session = await _db.Sessions
                .Include(s => s.Pc)
                .Include(s => s.Bills)
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.BranchId == branchId);

            if (session == null)
                throw new NotFoundException("Session not found", "SESSION_NOT_FOUND");

            if (session.State != SessionState.Interrupted)
                throw new AppException(
                    "Only a session interrupted by an outage can be resumed.",
                    System.Net.HttpStatusCode.BadRequest, "SESSION_NOT_INTERRUPTED");

            var now = DateTimeOffset.UtcNow;

            // Credit the hold, then start the clock again from this moment.
            SettleInterruption(session, now);

            session.State = SessionState.Active;
            session.NeedsTimeReview = false;
            session.LastHeartbeatAt = now;
            session.UpdatedAt = now;

            await _db.SaveChangesAsync();
            await _uow.CommitTransactionAsync();

            var pc = session.Pc;

            // Unlock the PC again for whatever time the customer has left, not the
            // original purchase — they already used part of it before the outage.
            if (pc != null)
            {
                var elapsed = SessionTimeCalculator.ElapsedMinutes(session.StartTime, session.PausedSeconds, now);
                var remaining = session.PlannedDurationMin.HasValue
                    ? Math.Max(0, session.PlannedDurationMin.Value - (int)elapsed)
                    : 0;   // open/pay-as-you-go session — no countdown to hand the agent

                var resumePricingProfile = await _db.Set<PricingProfile>().AsNoTracking()
                    .FirstOrDefaultAsync(pp => pp.Id == pc.PricingProfileId);
                await _hubNotifier.SendUnlockCommandToAgentAsync(
                    pc.Id, remaining, session.CustomerName ?? "Guest",
                    packagePrice: session.PackagePrice, plannedDurationMin: session.PackageMinutes ?? session.PlannedDurationMin,
                    packageFromMin: session.PackageFromMin,
                    ratePerHour: resumePricingProfile?.BaseHourlyRate ?? 0m,
                    bufferMinutes: resumePricingProfile?.BufferMinutes ?? SessionPricingCalculator.DefaultBufferMinutes,
                    sessionStartUtc: session.StartTime);
                await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, pc.Id);
                await _hubNotifier.BroadcastSessionUpdateAsync(branchId, session.Id);
            }

            await _activityService.LogActivityAsync(
                session.Id, branchId,
                "session_resumed",
                $"Session resumed after outage - {session.PausedSeconds / 60}m of downtime credited back",
                null);

            return new SessionDto
            {
                Id = session.Id,
                PcId = session.PcId,
                PcName = pc?.PcNumber ?? string.Empty,
                BranchId = branchId,
                OperatorId = session.OperatorId,
                ShiftId = session.ShiftId ?? Guid.Empty,
                CustomerName = session.CustomerName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                DurationMinutes = session.PlannedDurationMin ?? 0,
                ExpectedAmount = session.TotalAmount,
                PackageName = session.GamingType,
                Status = session.State,
                BillId = session.Bills.FirstOrDefault()?.Id ?? Guid.Empty
            };
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            await LogSessionFailureAsync(branchId, operatorId, AuditActions.SessionResume, sessionId, ex);
            throw;
        }
    }

    /// <summary>
    /// A package session's bill as the customer should read it: the package at its own price,
    /// then "Extra time 48m" for what was played past it - not "4 hr + Extension - 60m Rs 290"
    /// next to a separate "Extension - 60m Rs 60" that the total never included. Stopped
    /// before the package price applies, one line says it was charged by the hour.
    /// </summary>
    private void SplitPackageBillLines(Bill bill, BillItem gamingItem, Session session,
        int packageMinutes, decimal packagePrice, decimal displayGaming, int bufferMinutes, DateTimeOffset now)
    {
        // The extension lines were what was promised when it was added; the real figure is below.
        foreach (var extension in bill.Items.Where(i => i.ItemType == "gaming" && i != gamingItem).ToList())
        {
            bill.Items.Remove(extension);
            _uow.Repository<BillItem>().Remove(extension);
        }

        var played = session.ActualDurationMin ?? 0;
        if (played <= bufferMinutes) return;   // cancelled - keeps its "(Cancelled ...)" line

        var packageName = session.GamingType.Split(" + ")[0];
        if (played < (session.PackageFromMin ?? packageMinutes - bufferMinutes))
        {
            gamingItem.ItemName = $"{packageName} - played {played / 60}h {played % 60}m, hourly rate";
            return;
        }

        // Rounding (and a discount) live in displayGaming; the package line never shows more than it.
        var packageLine = Math.Min(packagePrice, displayGaming);
        gamingItem.ItemName = packageName;
        gamingItem.UnitPrice = gamingItem.TotalPrice = packageLine;

        var extra = displayGaming - packageLine;
        if (extra <= 0) return;
        var extraItem = new BillItem
        {
            Id = Guid.NewGuid(),
            BillId = bill.Id,
            ItemType = "gaming",
            ItemName = $"Extra time {Math.Max(0, played - packageMinutes)}m",
            Quantity = 1,
            UnitPrice = extra,
            TotalPrice = extra,
            CreatedAt = now
        };
        // Added explicitly: found only through bill.Items, a row with its Id already set is
        // taken for an existing one and saved as an UPDATE of nothing (409 on every extended stop).
        _db.Add(extraItem);
    }

    public async Task<SessionDto> ExtendSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, SessionExtendDto dto)
    {
        await _uow.BeginTransactionAsync();
        try
        {
            var session = await _db.Sessions
                .Include(s => s.Pc).ThenInclude(p => p!.PricingProfile).ThenInclude(pp => pp!.Packages)
                .Include(s => s.Bills)
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.BranchId == branchId);

            if (session == null)
                throw new NotFoundException("Session not found", "SESSION_NOT_FOUND");

            if (session.State != SessionState.Active)
                throw new AppException("Cannot extend inactive session.", System.Net.HttpStatusCode.BadRequest, "SESSION_NOT_ACTIVE");

            var now = DateTimeOffset.UtcNow;

            // Priced from the branch's own plans, never from whatever the operator's screen
            // calculated and sent. An exact-duration custom package wins outright - a 4-hour
            // extension on a PC whose 4-hour plan is Rs 180 must charge Rs 180, not Rs 200 from
            // (4 x hourly rate) - the same "plans, not raw multiplication" rule GetPcPlans
            // already applies to a session's own start. Only a duration with no matching
            // package (a plain "45 more minutes") falls back to the hourly pro-rata this always
            // used. See BuildPlansForProfile in PublicController for the same matching logic.
            var profile = session.Pc?.PricingProfile;
            var matchingPackage = profile?.Packages?
                .Where(pkg => pkg.IsActive && pkg.DurationMinutes == (int)dto.AdditionalMinutes)
                .OrderBy(pkg => pkg.SortOrder)
                .FirstOrDefault();

            var additionalAmount = matchingPackage?.Price
                ?? (dto.AdditionalMinutes / 60m) * (profile?.BaseHourlyRate ?? 0m);

            session.PlannedDurationMin = (session.PlannedDurationMin ?? 0) + (int)dto.AdditionalMinutes;
            if (session.EndTime.HasValue)
            {
                session.EndTime = session.EndTime.Value.AddMinutes((double)dto.AdditionalMinutes);
            }
            session.GamingAmount += additionalAmount;
            session.TotalAmount += additionalAmount;

            // Every extension - matched to a real package or the hourly pro-rata fallback - is a
            // charge already committed to the customer the moment it's added, same reasoning as
            // the original package price at Start. Stop honors this running total within a grace
            // window rather than forfeiting it for landing a few minutes off the combined plan.
            //
            // Sessions that remember their package (PackageMinutes) skip this: the package keeps
            // its own price and whatever is played past it is billed by the minute at Stop, so
            // getting up 20 minutes into a 1 hour extension pays for 20 minutes (owner, 25 Oct).
            if (session.PackageMinutes == null)
            {
                session.PricingPackageId ??= matchingPackage?.Id;
                session.PackagePrice = (session.PackagePrice ?? 0m) + additionalAmount;
            }
            
            var newGamingType = $"{session.GamingType} + {dto.PackageName}";
            if (newGamingType.Length > 150)
            {
                newGamingType = newGamingType.Substring(0, 147) + "...";
            }
            session.GamingType = newGamingType;
            
            session.UpdatedAt = now;

            var bill = session.Bills.FirstOrDefault();
            if (bill != null)
            {
                decimal previousGamingAmount = bill.GamingAmount;
                decimal newRawGamingAmount = previousGamingAmount + additionalAmount;

                var (displayGaming, displayFood, roundedTotal) = SessionPricingCalculator.ComputeRoundedBreakdown(
                    newRawGamingAmount, bill.FoodAmount, bill.DiscountAmount);

                bill.GamingAmount = displayGaming;
                bill.Subtotal = displayGaming + displayFood;
                bill.TotalAmount = roundedTotal;
                bill.UpdatedAt = now;
                _uow.Repository<Bill>().Update(bill);

                var extendItem = new BillItem
                {
                    Id = Guid.NewGuid(),
                    BillId = bill.Id,
                    ItemType = "gaming",
                    ItemName = dto.PackageName, // "Extension - 60m"
                    Quantity = 1,
                    // Absorbs the rounding diff so (original gaming item + this extension) still
                    // sums to the new displayGaming aggregate exactly.
                    UnitPrice = displayGaming - previousGamingAmount,
                    TotalPrice = displayGaming - previousGamingAmount,
                    CreatedAt = now
                };

                await _uow.Repository<BillItem>().AddAsync(extendItem);
            }

            _uow.Repository<Session>().Update(session);
            await _uow.CommitTransactionAsync();

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = Roles.Operator,
                UserName = "System",
                Action = AuditActions.SessionExtend,
                BranchId = branchId,
                TargetType = "session",
                TargetId = session.Id,
                Details = new { dto.AdditionalMinutes, AdditionalAmount = additionalAmount, PcNumber = session.Pc?.PcNumber }
            });

            // The customer's own PC never learned about an extension before this at all - no
            // agent push existed here whatsoever, so its countdown (and, now, its price) sat
            // frozen at whatever the session was started with until the next full unlock. This
            // re-unlocks with the new totals; LockScreen.UnlockPc is safe to call while already
            // unlocked (it just refreshes the remaining time and price, no visible flash).
            if (session.Pc != null)
            {
                var elapsedNow = SessionTimeCalculator.ElapsedMinutes(session.StartTime, session.PausedSeconds, now);
                var remainingNow = session.PlannedDurationMin.HasValue
                    ? Math.Max(0, session.PlannedDurationMin.Value - (int)elapsedNow)
                    : 0;
                await _hubNotifier.SendUnlockCommandToAgentAsync(
                    session.PcId, remainingNow, session.CustomerName,
                    packagePrice: session.PackagePrice, plannedDurationMin: session.PackageMinutes ?? session.PlannedDurationMin,
                    packageFromMin: session.PackageFromMin,
                    ratePerHour: session.Pc.PricingProfile?.BaseHourlyRate ?? 0m,
                    bufferMinutes: session.Pc.PricingProfile?.BufferMinutes ?? SessionPricingCalculator.DefaultBufferMinutes,
                    sessionStartUtc: session.StartTime);
            }

            await _hubNotifier.BroadcastSessionUpdateAsync(branchId, session.Id);
            await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, session.PcId);
            if (bill != null)
            {
                await _hubNotifier.BroadcastBillingUpdateAsync(branchId, bill.Id);
            }

            return new SessionDto
            {
                Id = session.Id,
                PcId = session.PcId,
                PcName = session.Pc?.PcNumber ?? "Unknown",
                BranchId = branchId,
                OperatorId = session.OperatorId,
                ShiftId = session.ShiftId ?? Guid.Empty,
                CustomerName = session.CustomerName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                DurationMinutes = session.PlannedDurationMin ?? 0,
                ExpectedAmount = session.TotalAmount,
                PackageName = session.GamingType,
                Status = session.State,
                BillId = bill?.Id ?? Guid.Empty
            };
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            await LogSessionFailureAsync(branchId, operatorId, AuditActions.SessionExtend, sessionId, ex);
            throw;
        }
    }

    public async Task<SessionDto> TransferSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, SessionTransferDto dto)
    {
        // The gap that let a transfer write itself into thin air. Start and Stop have refused
        // to run at Head Office since the very first version of this file; this method was
        // added later and never got the same line.
        //
        // What that gap does: dragging a session onto another PC while looking at Head Office's
        // own screen runs this method against Head Office's OWN database - which faithfully
        // sets the old PC idle and the new one active RIGHT THERE, so the screen shows the move
        // succeeding immediately. The real branch, on a different machine, was never told
        // anything. Its actual PC-1 keeps the customer playing exactly as before. Three seconds
        // later that branch's own heartbeat reports its real, unchanged truth, and Head Office's
        // "moved" PC states are overwritten straight back - but the session's own PcId, which
        // heartbeat does not touch, is left pointing at the new PC forever. The result is a
        // session Head Office believes is on PC-7 while every PC state screen correctly, and
        // confusingly, shows PC-1 still occupied - not a flicker, a permanent split between two
        // records that will never agree again on their own.
        RefuseIfHeadOffice("transferred");

        await _uow.BeginTransactionAsync();
        try
        {
            var session = await _db.Sessions
                .Include(s => s.Pc)
                .Include(s => s.Bills)
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.BranchId == branchId);

            if (session == null)
                throw new NotFoundException("Session not found", "SESSION_NOT_FOUND");

            if (session.State != SessionState.Active)
                throw new AppException("Only active sessions can be transferred.", System.Net.HttpStatusCode.BadRequest, "SESSION_NOT_ACTIVE");

            var targetPc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == dto.TargetPcId && p.BranchId == branchId);
            if (targetPc == null)
                throw new NotFoundException("Target PC not found", "PC_NOT_FOUND");

            if (targetPc.State != PcState.Idle)
                throw new AppException($"Target PC is currently {targetPc.State}", System.Net.HttpStatusCode.BadRequest, "PC_NOT_IDLE");

            var oldPc = session.Pc!;
            
            session.PcId = targetPc.Id;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            
            var transferLog = $"[Transfer: {oldPc.PcName ?? oldPc.PcNumber} -> {targetPc.PcName ?? targetPc.PcNumber} at {DateTimeOffset.Now:HH:mm}]";
            session.Notes = string.IsNullOrEmpty(session.Notes) ? transferLog : session.Notes + "\n" + transferLog;

            oldPc.State = PcState.Idle;
            oldPc.CurrentSessionId = null;
            targetPc.State = PcState.Active;
            targetPc.CurrentSessionId = session.Id;

            var bill = session.Bills.FirstOrDefault();
            if (bill != null)
            {
                bill.PcId = targetPc.Id;
                bill.UpdatedAt = DateTimeOffset.UtcNow;
                _uow.Repository<Bill>().Update(bill);
            }

            _uow.Repository<Session>().Update(session);
            _uow.Repository<Pc>().Update(oldPc);
            _uow.Repository<Pc>().Update(targetPc);
            
            await _uow.CommitTransactionAsync();

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = Roles.Operator,
                UserName = "System",
                Action = AuditActions.SessionTransfer,
                BranchId = branchId,
                TargetType = "session",
                TargetId = session.Id,
                Details = new { from = oldPc.PcNumber, to = targetPc.PcNumber }
            });

            await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, oldPc.Id);
            await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, targetPc.Id);
            await _hubNotifier.BroadcastSessionUpdateAsync(branchId, session.Id);

            if (bill != null)
            {
                await _hubNotifier.BroadcastBillingUpdateAsync(branchId, bill.Id);
            }

            return new SessionDto
            {
                Id = session.Id,
                PcId = session.PcId,
                PcName = targetPc.PcNumber,
                BranchId = branchId,
                OperatorId = session.OperatorId,
                ShiftId = session.ShiftId ?? Guid.Empty,
                CustomerName = session.CustomerName,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                DurationMinutes = session.PlannedDurationMin ?? 0,
                ExpectedAmount = session.TotalAmount,
                PackageName = session.GamingType,
                Status = session.State,
                BillId = bill?.Id ?? Guid.Empty
            };
        }
        catch (Exception ex)
        {
            await _uow.RollbackTransactionAsync();
            await LogSessionFailureAsync(branchId, operatorId, AuditActions.SessionTransfer, sessionId, ex);
            throw;
        }
    }
}
