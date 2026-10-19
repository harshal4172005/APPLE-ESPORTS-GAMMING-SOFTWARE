using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.DTOs.Eod;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

public class EodService : IEodService
{
    private readonly IUnitOfWork _unitOfWork;

    public EodService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<EodReportDto> GenerateEodReportAsync(Guid branchId, DateOnly businessDay)
    {
        // The window everything is actually counted over: midnight to midnight IST, the
        // calendar day. This screen used to read midnight-to-midnight UTC, which is 05:30
        // IST - so late-night takings landed on the wrong day here and the right day
        // everywhere else, and the two screens disagreed about the same money.
        var (dayStart, dayEnd) = IndiaTime.BusinessDayRange(businessDay);

        // Fetch Bills
        var bills = await _unitOfWork.Repository<Bill>().Query()
            .Where(b => b.BranchId == branchId && b.Status == BillStatus.Completed && b.CompletedAt >= dayStart && b.CompletedAt < dayEnd)
            .ToListAsync();
        var completedBills = bills;

        // Fetch Payments
        var payments = await _unitOfWork.Repository<Payment>().Query()
            .Where(p => p.BranchId == branchId && p.CreatedAt >= dayStart && p.CreatedAt < dayEnd)
            .ToListAsync();

        // Fetch Registers
        // Two, and only two, things belong to today: a register actually opened today (the
        // ordinary case), and a register opened before today that is STILL OPEN right now - a
        // branch genuinely trading straight through midnight with nothing closed yet. Anything
        // opened before today that has SINCE closed belongs entirely to the day(s) it was
        // actually open for, never to today, no matter how early this morning it happened to
        // close - it was already handed over and counted before today's business began, and
        // that count is what closes out THAT register's own day's report already, not a preview
        // of this one.
        //
        // The bug this replaces: a shift that closed at, say, 00:20 last night still touched
        // today's ClosedAt >= dayStart window, so it kept appearing in today's report too -
        // showing yesterday's already-settled opening balance and already-explained shortfall as
        // if today had somehow already started, on a morning nobody had opened anything yet. The
        // owner's own words for what this must do instead: "it should be 0 everywhere because
        // today is new day, opening balance is not yet open."
        //
        // BusinessDay alone used to be enough, because the register itself used to be force-
        // rolled into a fresh one at midnight (see AuthService.CloseFinishedTradingDaysAsync's
        // own history). That rollover was removed on the owner's explicit instruction after it
        // produced duplicate register rows on a branch that was genuinely still trading - so a
        // register spanning midnight is now a normal, expected thing, which is exactly the one
        // case (still open, never closed) this still has to find under both days it touches.
        var registers = await _unitOfWork.Repository<CashRegister>().Query()
            .Include(r => r.Operator)
            .Include(r => r.CashTransactions)
            .Where(r => r.BranchId == branchId
                && (
                    (r.OpenedAt >= dayStart && r.OpenedAt < dayEnd)
                    || (r.OpenedAt < dayStart && r.Status == CashRegisterStatus.Open)
                ))
            .OrderBy(r => r.OpenedAt)
            .ToListAsync();

        var walletTxs = await _unitOfWork.Repository<WalletTransaction>().Query()
            .Where(w => w.BranchId == branchId && w.CreatedAt >= dayStart && w.CreatedAt < dayEnd)
            .ToListAsync();

        var report = new EodReportDto
        {
            BranchId = branchId,
            ReportDate = dayStart,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        // Fetch Credits (for deduction from revenue)
        var credits = await _unitOfWork.Repository<CustomerCredit>().Query()
            .Where(c => c.BranchId == branchId && ((c.CreatedAt >= dayStart && c.CreatedAt < dayEnd) || (c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)))
            .ToListAsync();

        // As at the END of this day, never as at today: a credit given here and cleared on a
        // later day used to drop out of this day's figure the moment it was cleared, so a closed
        // day's Net Revenue changed afterwards (18 Oct Citylight: 5,020 at close, 5,160 a day
        // later). And an earlier day's credit cleared today is money in today - counted here,
        // once, on the day it was paid (owner's rule). Given-and-cleared the same day is
        // neither: the bill already carries it.
        bool UnpaidAtDayEnd(DateTimeOffset createdAt, DateTimeOffset? clearedAt) =>
            createdAt < dayEnd && (clearedAt is null || clearedAt >= dayEnd);

        var pendingCredits = credits
            .Where(c => c.CreatedAt >= dayStart && UnpaidAtDayEnd(c.CreatedAt, c.ClearedAt))
            .Sum(c => c.CreditAmount);
        var earlierCreditsClearedToday = credits
            .Where(c => c.CreatedAt < dayStart && c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)
            .Sum(c => c.CreditAmount);

        // Member top-ups given on credit - a separate table from CustomerCredit above (see
        // MemberTopUpCredit's own docstring), so deliberately NOT folded into pendingCredits:
        // that figure reduces Net Revenue, and a top-up credit was never gaming/food revenue to
        // begin with - it is a wallet balance grant, not a bill. Only shown in CreditLogs/
        // CreditClearedToday below, which the EOD screen already treats as additional context
        // rather than inputs to Net Revenue.
        var memberTopUpCredits = await _unitOfWork.Repository<MemberTopUpCredit>().Query()
            .Where(c => c.BranchId == branchId && ((c.CreatedAt >= dayStart && c.CreatedAt < dayEnd) || (c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)))
            .Include(c => c.Member)
            .ToListAsync();

        // Money actually collected today by clearing a member top-up credit - a cleared bill
        // credit's Payment flows into Cash/Online automatically; this is the same thing for a
        // top-up credit, which has no Payment row to piggyback on (see MemberTopUpCredit.cs).
        var clearedMemberTopUpCreditsToday = memberTopUpCredits
            .Where(c => c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)
            .ToList();
        var clearedMemberTopUpCashToday = clearedMemberTopUpCreditsToday.Sum(c => c.ClearedCashAmount);
        var clearedMemberTopUpOnlineToday = clearedMemberTopUpCreditsToday.Sum(c => c.ClearedOnlineAmount);

        // Revenue (Gaming / Food)
        report.Revenue.TotalGamingRevenue = completedBills.Sum(b => b.GamingAmount);
        report.Revenue.TotalFoodRevenue = completedBills.Sum(b => b.FoodAmount);
        report.Revenue.TotalDiscounts = completedBills.Sum(b => b.DiscountAmount);
        report.Revenue.NetRevenue = completedBills.Sum(b => b.TotalAmount) - pendingCredits + earlierCreditsClearedToday;

        // Payment Methods
        report.PaymentMethods.TotalCash = payments.Sum(p => p.CashAmount);
        report.PaymentMethods.TotalOnline = payments.Sum(p => p.OnlineAmount);
        report.PaymentMethods.TotalWalletDeductions = payments.Sum(p => p.WalletAmount);

        var topUps = walletTxs.Where(w => w.Action == WalletAction.Recharge).ToList();
        report.PaymentMethods.TotalWalletTopUps = topUps.Sum(w => w.Amount);

        // Split by how the top-up was actually paid, because the summary screen was adding the
        // whole figure into its cash line - so a UPI top-up inflated the cash the operator was
        // expected to be holding, and they came up short by exactly the amount nobody had
        // handed them.
        report.PaymentMethods.TotalWalletTopUpsCash = topUps.Sum(w => w.CashAmount) + clearedMemberTopUpCashToday;
        report.PaymentMethods.TotalWalletTopUpsOnline = topUps.Sum(w => w.OnlineAmount) + clearedMemberTopUpOnlineToday;

        // What the shop gave away in promotional bonus. Never money in; the owner is buying
        // loyalty with it, and it appeared nowhere at all before.
        report.PaymentMethods.TotalWalletBonusGiven = topUps.Sum(w => w.BonusAmount);

        // Every rupee that genuinely arrived today.
        //
        // Wallet deductions are deliberately absent. They are members spending balance that was
        // collected when they topped up, which may have been weeks ago - and if it was today,
        // it is already in the top-up figure. The screen was adding both, so a Rs 500 top-up
        // followed by Rs 90 of play reported Rs 590 of takings on a day Rs 500 came in.
        report.PaymentMethods.TotalCollected =
            report.PaymentMethods.TotalCash
            + report.PaymentMethods.TotalOnline
            + report.PaymentMethods.TotalWalletTopUpsCash
            + report.PaymentMethods.TotalWalletTopUpsOnline;

        // ── Does what was billed agree with what was taken ────────────────────────
        //
        // The two halves of the payment summary were shown side by side under one total with
        // no arithmetic joining them, so they simply disagreed whenever a discount was given or
        // a customer left owing money, and nothing said whether that was normal.
        var creditGivenToday = credits
            .Where(c => c.CreatedAt >= dayStart && c.CreatedAt < dayEnd)
            .Sum(c => c.CreditAmount);

        var creditClearedToday = credits
            .Where(c => c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)
            .Sum(c => c.CreditAmount);

        report.Reconciliation.GrossBilled =
            report.Revenue.TotalGamingRevenue + report.Revenue.TotalFoodRevenue;
        report.Reconciliation.Discounts = report.Revenue.TotalDiscounts;
        report.Reconciliation.CreditGivenToday = creditGivenToday;
        report.Reconciliation.CreditClearedToday = creditClearedToday;

        report.Reconciliation.ShouldHaveBeenCollected =
            report.Reconciliation.GrossBilled
            - report.Reconciliation.Discounts
            - creditGivenToday
            + creditClearedToday;

        // Added after ShouldHaveBeenCollected above, which must stay bill-only - a member
        // top-up credit was never billed, so it has no place in "should have been collected
        // against gross billed". The screen's own Credit Cleared row (EodPaymentSummaryBar.jsx)
        // reads this field directly though, and the owner's instruction was for a top-up credit
        // to count there once cleared, same as a bill credit does.
        report.Reconciliation.CreditClearedToday += memberTopUpCredits
            .Where(c => c.ClearedAt >= dayStart && c.ClearedAt < dayEnd)
            .Sum(c => c.CreditAmount);

        // Only what settled bills - top-ups are not bill payments and belong on the other side.
        report.Reconciliation.ActuallySettled =
            report.PaymentMethods.TotalCash
            + report.PaymentMethods.TotalOnline
            + report.PaymentMethods.TotalWalletDeductions;

        report.Reconciliation.Difference =
            report.Reconciliation.ActuallySettled - report.Reconciliation.ShouldHaveBeenCollected;

        // ── Cash Summary ──────────────────────────────────────────────────────────
        //
        // A branch has ONE physical drawer. Every figure here describes that one drawer over
        // the day, so nothing may be summed across shifts: shift 2 opens with the cash shift 1
        // left behind, and adding both totals counts the same notes twice. Summing them is
        // what produced an expected drawer of Rs 52,210 against a real one holding Rs 6,760.
        var firstRegister = registers.FirstOrDefault();
        var lastRegister = registers.LastOrDefault();

        // The money the drawer started the day with - the first shift's opening float, not the
        // sum of every shift's opening.
        //
        // Not simply firstRegister.OpeningBalance any more. That register can now be one still
        // spanning midnight from yesterday (see the query above), and its OpeningBalance is
        // whatever the drawer held when IT opened - yesterday, or earlier - not what it held
        // the moment today actually began. Reconstructed instead as: what it opened with, plus
        // every cash movement against it that happened before today started. A branch has one
        // physical drawer at a time, so "before today started" is unambiguous even without a
        // direct link from a payment to the register it was collected into.
        if (firstRegister is null)
        {
            report.Cash.TotalOpeningBalance = 0m;
        }
        else if (firstRegister.OpenedAt >= dayStart)
        {
            report.Cash.TotalOpeningBalance = firstRegister.OpeningBalance;
        }
        else
        {
            var cashSalesBeforeToday = await _unitOfWork.Repository<Payment>().Query()
                .Where(p => p.BranchId == branchId
                    && p.CreatedAt >= firstRegister.OpenedAt && p.CreatedAt < dayStart)
                .SumAsync(p => p.CashAmount);

            var topUpCashBeforeToday = await _unitOfWork.Repository<WalletTransaction>().Query()
                .Where(w => w.BranchId == branchId && w.Action == WalletAction.Recharge
                    && w.CreatedAt >= firstRegister.OpenedAt && w.CreatedAt < dayStart)
                .SumAsync(w => w.CashAmount);

            var movementBeforeToday = firstRegister.CashTransactions
                .Where(t => t.CreatedAt < dayStart)
                .Sum(t => t.TransactionType switch
                {
                    "petty_expense" or "withdrawal" => -Math.Abs(t.CashAmount),
                    _ => t.CashAmount,
                });

            report.Cash.TotalOpeningBalance =
                firstRegister.OpeningBalance + cashSalesBeforeToday + topUpCashBeforeToday + movementBeforeToday;
        }

        // Only the part of each top-up that was actually paid in notes. TotalWalletTopUps is
        // every top-up whatever the method, which is right for revenue and wrong for a drawer:
        // adding a UPI top-up to the cash line inflates the cash the operator is expected to
        // have, and they show short by exactly the amount nobody ever handed them.
        var cashFromTopUps = walletTxs
            .Where(w => w.Action == WalletAction.Recharge)
            .Sum(w => w.CashAmount) + clearedMemberTopUpCashToday;

        // From payments themselves, filtered to today, rather than a register's own running
        // TotalCashSales column - that column is not split by day, so on a register spanning
        // midnight it would carry yesterday's sales into today's report too. Payments already
        // has to be filtered by its own CreatedAt for the Payment Methods section above; reusing
        // that same, already-correct figure here is what keeps this section unable to disagree
        // with it.
        report.Cash.TotalCashSales = report.PaymentMethods.TotalCash + cashFromTopUps;

        // Filtered to today for the same reason - a spanning register's CashTransactions include
        // rows from yesterday too, which must not be counted again in today's report.
        var allCashTxs = registers.SelectMany(r => r.CashTransactions)
            .Where(t => t.CreatedAt >= dayStart && t.CreatedAt < dayEnd)
            .ToList();
        report.Cash.TotalCashInwards = allCashTxs.Where(t => t.TransactionType == "inward").Sum(t => t.CashAmount);
        report.Cash.TotalPettyExpenses = allCashTxs.Where(t => t.TransactionType == "petty_expense").Sum(t => Math.Abs(t.CashAmount));

        // Cash taken out of the drawer and handed to the owner. This leaves the branch, so it
        // must come off what the drawer is expected to hold - otherwise every handover looks
        // like the operator is short by exactly the amount they correctly sent up.
        report.Cash.TotalOwnerWithdrawals = allCashTxs.Where(t => t.TransactionType == "withdrawal").Sum(t => Math.Abs(t.CashAmount));

        // The drawer as it stands, what it was counted at, and the difference between the two -
        // all three read off the SAME register.
        //
        // That is the fix, and it matters because a trading day can now hold several drawers in
        // sequence: a shift handover closes one with a count and opens the next from what was
        // counted. This used to take "expected" from the drawer in use and "counted" from
        // whichever earlier drawer had last been counted, which printed three figures that could
        // not all be true at once - expected Rs 5,000, counted Rs 5,000, short Rs 240 - and left
        // an operator no way to tell which of them to believe.
        //
        // ExpectedDrawerCash is maintained on the register by every cash movement as it happens,
        // so the latest register carries the running figure. Re-deriving it is where double
        // counting creeps in.
        report.Cash.ExpectedCashInDrawer = lastRegister?.ExpectedDrawerCash ?? 0m;
        report.Cash.ActualPhysicalCashCounted = lastRegister?.PhysicalCashCounted;
        report.Cash.TotalDiscrepancy = lastRegister?.PhysicalCashCounted is null
            ? null
            : lastRegister.PhysicalCashCounted - lastRegister.ExpectedDrawerCash;
        report.Cash.CoverAmount = lastRegister?.CoverAmount;

        // Money that went missing, or turned up spare, on a drawer already closed today - a
        // handover counted short. Reported separately from the figure above because it belongs to
        // an earlier shift: the drawer in use started from what was physically counted, so it is
        // not short by this as well, and adding the two together would charge the same shortfall
        // to two shifts.
        //
        // It also closes the column's arithmetic. Opening plus takings less expenses is what the
        // drawer would hold had nothing gone astray; subtract what did, and the expected figure
        // beneath it follows. Without this line the two simply disagree by the missing amount and
        // the screen offers no account of why.
        report.Cash.DifferencesFoundEarlier = registers
            .Where(r => r.Id != lastRegister?.Id && r.CashDifference.HasValue)
            .Sum(r => r.CashDifference!.Value);

        // Shifts Summary
        report.Shifts.TotalShifts = registers.Count;
        foreach (var reg in registers)
        {
            report.Shifts.ShiftDetails.Add(new ShiftDetailDto
            {
                ShiftId = reg.ShiftId,
                OperatorId = reg.OperatorId,
                OperatorName = reg.Operator?.FullName ?? "Unknown",
                TotalSales = reg.TotalCashSales,
                CashDiscrepancy = reg.CashDifference ?? 0
            });
        }

        // Operational Stats
        report.Operations.TotalSessions = await _unitOfWork.Repository<Session>().Query().CountAsync(s => s.BranchId == branchId && s.StartTime >= dayStart && s.StartTime < dayEnd);
        report.Operations.TotalReservations = await _unitOfWork.Repository<Reservation>().Query().CountAsync(r => r.BranchId == branchId && r.CreatedAt >= dayStart && r.CreatedAt < dayEnd);
        report.Operations.TotalFoodOrders = await _unitOfWork.Repository<FoodOrder>().Query().CountAsync(o => o.BranchId == branchId && o.CreatedAt >= dayStart && o.CreatedAt < dayEnd);
        report.Operations.NewMembersRegistered = await _unitOfWork.Repository<Member>().Query().CountAsync(m => m.HomeBranchId == branchId && m.CreatedAt >= dayStart && m.CreatedAt < dayEnd);

        // Credit Logs (reuse credits fetched earlier)
        report.CreditLogs = credits.Select(c => new EodCreditLogDto
        {
            CreditId = c.Id,
            CustomerName = c.CustomerName,
            CustomerPhone = c.CustomerPhone,
            PcNumber = c.PcNumber,
            OriginalBillAmount = c.OriginalBillAmount,
            AmountPaidInitially = c.AmountPaidInitially,
            CreditAmount = c.CreditAmount,
            // As at this day's end, same rule as pendingCredits - the screen's Credit box sums
            // these, and a closed day must not change when a credit is cleared later.
            Status = UnpaidAtDayEnd(c.CreatedAt, c.ClearedAt) ? "pending" : "cleared",
            CreatedAt = c.CreatedAt,
            ClearedAt = c.ClearedAt < dayEnd ? c.ClearedAt : null
        }).ToList();

        // Same list, so the screen's own Credit / Credit Cleared rows (which sum straight from
        // CreditLogs, see EodPaymentSummaryBar.jsx) count a cleared top-up credit without
        // needing their own separate read. "Member Top-Up" stands in for PC Number - there is
        // no bill or PC behind this one, see MemberTopUpCredit's own docstring.
        report.CreditLogs.AddRange(memberTopUpCredits.Select(c => new EodCreditLogDto
        {
            CreditId = c.Id,
            CustomerName = c.Member.FullName,
            CustomerPhone = c.Member.MobileNumber,
            PcNumber = "Member Top-Up",
            OriginalBillAmount = c.CreditAmount,
            AmountPaidInitially = 0,
            CreditAmount = c.CreditAmount,
            Status = UnpaidAtDayEnd(c.CreatedAt, c.ClearedAt) ? "pending" : "cleared",
            CreatedAt = c.CreatedAt,
            ClearedAt = c.ClearedAt < dayEnd ? c.ClearedAt : null
        }));

        return report;
    }
}
