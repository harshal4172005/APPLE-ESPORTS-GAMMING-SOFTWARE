using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Cash;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

public class CashDeskService : ICashDeskService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;
    private readonly IHubNotificationService _hubNotification;
    private readonly IAdminNotifier _notifier;
    private readonly IAuthService _authService;

    public CashDeskService(
        IUnitOfWork unitOfWork,
        IAuditService auditService,
        IHubNotificationService hubNotification,
        IAdminNotifier notifier,
        IAuthService authService)
    {
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _hubNotification = hubNotification;
        _notifier = notifier;
        _authService = authService;
    }

    public async Task StartVerificationAsync(Guid branchId, Guid operatorId, Guid shiftId)
    {
        // THIS branch's one Open register - by BranchId alone, no longer by ShiftId. ShiftId
        // scoping was the fix for a double-click race that could lock the wrong one of two
        // Open registers - now structurally impossible, since the unique index on
        // cash_register guarantees at most one non-closed row per branch (see
        // CashRegisterService.GetActiveRegisterAsync's own note for the full reasoning).
        //
        // ShiftId scoping had become actively harmful, the same way it had on the read side: a
        // register opened under one shift, still sitting Open, no longer matched once that
        // operator's shift id changed underneath it (a re-login after a shift properly closed
        // but its register didn't) - "Start Verification" on a drawer that was genuinely,
        // visibly open would answer 404, "No open cash register found to verify."
        var register = await _unitOfWork.Repository<CashRegister>().Query()
            .FirstOrDefaultAsync(r => r.BranchId == branchId && r.Status == CashRegisterStatus.Open)
            ?? throw new NotFoundException("No open cash register found to verify.");

        // Only where Super Admin has switched on charging for missing stock: the bill for it is
        // settled (paid, or put on credit) before the drawer is counted, so its cash is in the
        // count. Off, this check does not exist.
        var chargeMissing = await _unitOfWork.Repository<Branch>().Query()
            .Where(b => b.Id == branchId).Select(b => b.ChargeMissingStock).FirstOrDefaultAsync();
        if (chargeMissing && await _unitOfWork.Repository<Bill>().Query().AnyAsync(b =>
                b.BranchId == branchId && b.ShortageOperatorId == register.OperatorId && b.Status == BillStatus.Pending))
            throw new AppException("Pay the missing stock bill first (cash, online, split or credit), then lock the register.");

        register.Status = CashRegisterStatus.Verifying;
        _unitOfWork.Repository<CashRegister>().Update(register);
        
        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = "cash_desk_verification_started",
            BranchId = branchId,
            TargetType = "cash_register",
            TargetId = register.Id,
            Details = new { ExpectedDrawerCash = register.ExpectedDrawerCash }
        });

        await _unitOfWork.CommitTransactionAsync();
        await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, register.Id);
    }

    public async Task<DenominationCountDto> SubmitDenominationsAsync(Guid branchId, Guid operatorId, Guid shiftId, SubmitDenominationDto dto)
    {
        // Same reasoning as StartVerificationAsync above: THIS branch's one Verifying register,
        // by BranchId alone - no longer by ShiftId, now that at most one non-closed register can
        // exist per branch at all. This is very likely the real cause of counts going missing
        // from Cash Register History: if the operator's shift id changed between Start
        // Verification and submitting the count (a re-login in between is enough), this used to
        // throw "No verifying cash register found" on a register that was genuinely, visibly
        // locked and waiting - pushing the operator toward some other close path (a takeover
        // handover, say) that records only a lump total and never a denomination breakdown at all.
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var register = await _unitOfWork.Repository<CashRegister>().Query()
                .FirstOrDefaultAsync(r => r.BranchId == branchId && r.Status == CashRegisterStatus.Verifying)
                ?? throw new NotFoundException("No verifying cash register found. Must start verification first.");

            decimal countedTotal = 
                (dto.Notes2000 * 2000) + 
                (dto.Notes500 * 500) + 
                (dto.Notes200 * 200) + 
                (dto.Notes100 * 100) + 
                (dto.Notes50 * 50) + 
                (dto.Notes20 * 20) + 
                (dto.Notes10 * 10) + 
                (dto.Coins5 * 5) + 
                (dto.Coins2 * 2) + 
                (dto.Coins1 * 1);

            var difference = countedTotal - register.ExpectedDrawerCash;
            var isVerified = difference == 0;

            if (!isVerified && string.IsNullOrEmpty(dto.MismatchReason))
                throw new AppException("Mismatch reason is required when drawer cash does not match expected total.");

            var countRecord = new DenominationCount
            {
                CashRegisterId = register.Id,
                ShiftId = shiftId,
                BranchId = branchId,
                OperatorId = operatorId,
                Notes2000 = dto.Notes2000,
                Notes500 = dto.Notes500,
                Notes200 = dto.Notes200,
                Notes100 = dto.Notes100,
                Notes50 = dto.Notes50,
                Notes20 = dto.Notes20,
                Notes10 = dto.Notes10,
                Coins5 = dto.Coins5,
                Coins2 = dto.Coins2,
                Coins1 = dto.Coins1,
                CountedTotal = countedTotal,
                ExpectedTotal = register.ExpectedDrawerCash,
                Difference = difference,
                IsVerified = isVerified,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _unitOfWork.Repository<DenominationCount>().AddAsync(countRecord);

            // Update Register Status
            register.PhysicalCashCounted = countedTotal;
            register.CashDifference = difference;
            register.CountedByOperatorId = operatorId;
            register.MismatchReason = dto.MismatchReason;
            register.Status = CashRegisterStatus.Verified;
            register.VerifiedAt = DateTimeOffset.UtcNow;
            
            _unitOfWork.Repository<CashRegister>().Update(register);

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = "cash_desk_verified",
                BranchId = branchId,
                TargetType = "cash_register",
                TargetId = register.Id,
                Details = new { Expected = register.ExpectedDrawerCash, Actual = countedTotal, Difference = difference }
            });

            await _unitOfWork.CommitTransactionAsync();
            await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, register.Id);

            // After the commit, and only when the money is actually wrong. The count is the one
            // moment a shortfall is visible while anyone still remembers the evening; left to the
            // end-of-day figures it becomes an unexplained number nobody can account for.
            if (!isVerified)
                await NotifyOwnerOfCashDifferenceAsync(branchId, operatorId, register, countedTotal, difference, dto.MismatchReason);

            return MapToDto(countRecord);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public async Task CloseRegisterAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid cashRegisterId, CloseCashRegisterDto dto)
    {
        var register = await _unitOfWork.Repository<CashRegister>().Query()
            .FirstOrDefaultAsync(r => r.Id == cashRegisterId && r.BranchId == branchId)
            ?? throw new NotFoundException("Cash register not found.");

        if (register.Status != CashRegisterStatus.Verified)
            throw new AppException("Cash register must be verified before it can be closed.");

        // Only the register that closes the trading day carries a cover amount and a next-day
        // float - every earlier shift in the day just hands the drawer to whoever is next, with
        // nothing to record beyond the count already taken in SubmitDenominationsAsync.
        if (dto.ClosesTradingDay)
        {
            if (dto.CoverAmount is null || dto.NextDayOpeningBalance is null)
                throw new AppException("Cover amount and next-day float are both required to close the day.");

            var remainder = (register.PhysicalCashCounted ?? 0) - dto.CoverAmount.Value - dto.NextDayOpeningBalance.Value;
            if (remainder != 0 && string.IsNullOrWhiteSpace(dto.Reason))
                throw new AppException(
                    $"The counted cash doesn't split evenly into the cover amount and next-day float " +
                    $"(₹{remainder:0.00} {(remainder > 0 ? "left over" : "short")}). Please explain why.");

            register.CoverAmount = dto.CoverAmount;
            register.NextDayOpeningBalance = dto.NextDayOpeningBalance;
            register.NextDayFloatReason = remainder != 0 ? dto.Reason : null;
        }

        register.Status = CashRegisterStatus.Closed;
        register.ClosedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<CashRegister>().Update(register);

        // Ending a shift and closing its register are the same real event - the operator who
        // just finished counting out is done, whether they're handing over to someone else or
        // closing the shop for the night. This used to depend on a SEPARATE, later request
        // (the client's own logout call) to mark the shift itself as over, and that second
        // request had no guarantee of ever arriving: if the PC lost power, lost wifi, or was
        // simply switched off the moment this screen said "closed", the register stayed
        // correctly closed forever while the shift underneath it stayed Active forever too -
        // confirmed live, twice, the same night (Adajan-240Hz and Citylight-144Hz, 1-2 Oct
        // 2026) - which is also why both operators kept being asked to explain an ever-growing
        // "shift gap" every time they logged back in: the system was telling the truth, their
        // shift genuinely never closed. Closing it here, in the same request that closes the
        // register, means there is no gap between the two for anything to interrupt.
        //
        // Calls AuthService's own LogoutAsync rather than repeating its shift/operator-closing
        // steps - the same method the client's own logout request calls a moment later, which
        // is why that later call is safe to still fire: it is wrapped in .catch(() => {}) client
        // side and was already built to tolerate running against a session that is no longer
        // there. Reusing it also means a "last shift of the day" close now reliably triggers the
        // end-of-day summary email even if that later client request never arrives - previously
        // the email depended on the exact same fragile second request this whole fix exists to
        // stop relying on.
        await _authService.LogoutAsync(operatorId, Roles.Operator, shiftId, dto.ClosesTradingDay);

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = "cash_register_closed",
            BranchId = branchId,
            TargetType = "cash_register",
            TargetId = register.Id,
            Details = dto.ClosesTradingDay
                ? new { ClosesTradingDay = true, register.CoverAmount, register.NextDayOpeningBalance, register.NextDayFloatReason }
                : null
        });

        await _unitOfWork.CommitTransactionAsync();
        await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, register.Id);
    }

    /// <summary>
    /// Tells the owner the drawer did not match, with both figures and the operator's reason.
    ///
    /// Short by more than it should be, or over — both are sent. Extra cash in a till is not good
    /// news: it usually means a sale went unrecorded, which is the same hole in the takings seen
    /// from the other side.
    /// </summary>
    private async Task NotifyOwnerOfCashDifferenceAsync(
        Guid branchId, Guid operatorId, CashRegister register,
        decimal countedTotal, decimal difference, string? reason)
    {
        try
        {
            var branch = await _unitOfWork.Repository<Branch>().Query()
                .FirstOrDefaultAsync(b => b.Id == branchId);
            var op = await _unitOfWork.Repository<Operator>().Query()
                .FirstOrDefaultAsync(o => o.Id == operatorId);

            var isShort = difference < 0;
            var amount = Math.Abs(difference);

            var rows = new List<(string Label, string Value)>
            {
                ("Branch", branch?.Name ?? "Unknown branch"),
                ("Counted by", op?.FullName ?? op?.Username ?? "Unknown operator"),
                ("", ""),
                ("Should have been in the drawer", $"Rs {register.ExpectedDrawerCash:N2}"),
                ("Actually counted", $"Rs {countedTotal:N2}"),
                (isShort ? "Missing" : "Extra", $"Rs {amount:N2}"),
                ("", ""),
                ("Reason given", string.IsNullOrWhiteSpace(reason) ? "None given" : reason.Trim()),
                ("Counted at", IndiaTime.Now.ToString("dd MMM yyyy, h:mm tt")),
            };

            var body = AdminEmailTemplate.Compose(
                heading: isShort ? "Cash is missing from the drawer" : "There is extra cash in the drawer",
                accent: isShort ? AdminEmailTemplate.Red : AdminEmailTemplate.Amber,
                summary: isShort
                    ? "The cash counted at the end of this shift is less than the system expected. " +
                      "The operator's reason is below."
                    : "The cash counted at the end of this shift is more than the system expected. " +
                      "This usually means a sale was not put through the system.",
                rows: rows,
                headline: $"Rs {amount:N2} {(isShort ? "short" : "over")}",
                footnote: "The shift was allowed to finish - the shop is not held up by this. " +
                          "The figures above are recorded against that shift.");

            await _notifier.NotifyAsync(
                $"{(isShort ? "Cash short" : "Cash over")} by Rs {amount:N0} at {branch?.Name ?? "a branch"}",
                body);
        }
        catch (Exception ex)
        {
            // Never allowed to undo a completed count. The money has been counted and recorded;
            // failing the whole operation because the mail did would lose the count itself.
            System.Diagnostics.Debug.WriteLine($"Could not send the cash difference email: {ex.Message}");
        }
    }

    public async Task CancelVerificationAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid cashRegisterId)
    {
        var register = await _unitOfWork.Repository<CashRegister>().Query()
            .FirstOrDefaultAsync(r => r.Id == cashRegisterId && r.BranchId == branchId)
            ?? throw new NotFoundException("Cash register not found.");

        // Already unlocked — nothing to cancel, treat as a no-op success.
        if (register.Status == CashRegisterStatus.Open)
            return;

        if (register.Status != CashRegisterStatus.Verifying)
            throw new AppException("Only a register that is currently locked for verification can be cancelled.");

        register.Status = CashRegisterStatus.Open;
        _unitOfWork.Repository<CashRegister>().Update(register);

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = "cash_register_verification_cancelled",
            BranchId = branchId,
            TargetType = "cash_register",
            TargetId = register.Id,
            Details = null
        });

        await _unitOfWork.CommitTransactionAsync();
        await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, register.Id);
    }

    public async Task ReopenLastDayCloseAsync(Guid branchId, Guid adminUserId, string adminName)
    {
        var register = await _unitOfWork.Repository<CashRegister>().Query()
            .Where(r => r.BranchId == branchId)
            .OrderByDescending(r => r.OpenedAt)
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException("No cash register found for this branch.");

        if (register.Status != CashRegisterStatus.Closed)
            throw new AppException("The drawer is already open - there is nothing to undo.");

        // CoverAmount is only ever set on a register closed via "last shift of the day" - see
        // CloseRegisterAsync. A register closed as a normal handover has nothing to undo here;
        // the next shift already opened over it the ordinary way.
        if (register.CoverAmount is null)
            throw new AppException("The last close wasn't a day close, so there's nothing to undo.");

        register.Status = CashRegisterStatus.Open;
        register.ClosedAt = null;
        register.CoverAmount = null;
        register.NextDayOpeningBalance = null;
        register.NextDayFloatReason = null;
        _unitOfWork.Repository<CashRegister>().Update(register);

        var shift = await _unitOfWork.Repository<Shift>().Query()
            .FirstOrDefaultAsync(s => s.Id == register.ShiftId);
        if (shift != null && shift.ClosedTradingDay)
        {
            shift.ClosedTradingDay = false;
            shift.Status = ShiftStatus.Active;
            shift.LogoutTime = null;
            _unitOfWork.Repository<Shift>().Update(shift);
        }

        await _auditService.LogAsync(new AuditEntry
        {
            UserId = adminUserId,
            UserRole = "Admin",
            UserName = adminName,
            Action = "cash_register_day_close_undone",
            BranchId = branchId,
            TargetType = "cash_register",
            TargetId = register.Id,
            Details = null
        });

        await _unitOfWork.CommitTransactionAsync();
        await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, register.Id);

        try
        {
            var branch = await _unitOfWork.Repository<Branch>().Query().FirstOrDefaultAsync(b => b.Id == branchId);
            var body = AdminEmailTemplate.Compose(
                heading: "A day close was undone",
                accent: AdminEmailTemplate.Amber,
                summary: $"{adminName} undid the \"last shift of the day\" close at {branch?.Name ?? "a branch"}. " +
                          "The drawer is open again and trading continues as normal.",
                rows: new List<(string, string)>
                {
                    ("Branch", branch?.Name ?? "Unknown branch"),
                    ("Undone by", adminName),
                    ("Undone at", IndiaTime.Now.ToString("dd MMM yyyy, h:mm tt")),
                },
                headline: "Day reopened",
                footnote: "The operator can carry on trading on the same drawer as before.");

            await _notifier.NotifyAsync($"Day close undone at {branch?.Name ?? "a branch"}", body);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not send the day-reopen email: {ex.Message}");
        }
    }

    private static DenominationCountDto MapToDto(DenominationCount d)
    {
        return new DenominationCountDto
        {
            Id = d.Id,
            CashRegisterId = d.CashRegisterId,
            Notes2000 = d.Notes2000,
            Notes500 = d.Notes500,
            Notes200 = d.Notes200,
            Notes100 = d.Notes100,
            Notes50 = d.Notes50,
            Notes20 = d.Notes20,
            Notes10 = d.Notes10,
            Coins5 = d.Coins5,
            Coins2 = d.Coins2,
            Coins1 = d.Coins1,
            CountedTotal = d.CountedTotal,
            ExpectedTotal = d.ExpectedTotal,
            Difference = d.Difference,
            IsVerified = d.IsVerified,
            CreatedAt = d.CreatedAt
        };
    }
}
