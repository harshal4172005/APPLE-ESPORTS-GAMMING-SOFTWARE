using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Eod;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;

using Microsoft.EntityFrameworkCore;

namespace AppleEsportsErp.Api.Controllers;

[ApiController]
[Route("api/eod")]
[Authorize]
[BranchIsolation]
public class EodController : ControllerBase
{
    private readonly IEodService _eodService;
    private readonly IUnitOfWork _unitOfWork;

    public EodController(IEodService eodService, IUnitOfWork unitOfWork)
    {
        _eodService = eodService;
        _unitOfWork = unitOfWork;
    }

    [HttpGet("range-report")]
    public async Task<IActionResult> GetRangeReport(
        [FromQuery] DateTimeOffset? startDate, 
        [FromQuery] DateTimeOffset? endDate, 
        [FromQuery] Guid? branchId = null)
    {
        var targetBranchId = branchId ?? GetBranchId();
        
        var startUtc = (startDate ?? DateTimeOffset.UtcNow.AddDays(-30)).ToUniversalTime();
        var endUtc = (endDate ?? DateTimeOffset.UtcNow).ToUniversalTime();

        var bills = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Bill>()
            .Query()
            .Include(b => b.DiscountByAdmin)
            .Include(b => b.Operator)
            .Include(b => b.Session)
            .Include(b => b.Pc)
            .Where(b => b.BranchId == targetBranchId
                     && b.Status == AppleEsportsErp.Domain.Enums.BillStatus.Completed 
                     && b.CompletedAt >= startUtc 
                     && b.CompletedAt <= endUtc)
            .ToListAsync();

        // Bucketed by the midnight-to-midnight IST day, the same day boundary
        // /eod/preview uses - not the raw UTC calendar date, which puts a bill rung
        // up at 01:30 IST on the wrong day here and the right day everywhere else.
        var billsByBusinessDay = bills
            .Select(b => (Bill: b, BusinessDay: IndiaTime.BusinessDayOf(b.CompletedAt!.Value)))
            .ToList();

        var dailyReport = billsByBusinessDay
            .GroupBy(x => x.BusinessDay)
            .Select(g => new {
                Date = g.Key.ToString("yyyy-MM-dd"),
                GamingRevenue = g.Sum(x => x.Bill.Subtotal > 0 ? x.Bill.GamingAmount - (x.Bill.GamingAmount / x.Bill.Subtotal * x.Bill.DiscountAmount) : x.Bill.GamingAmount),
                FoodRevenue = g.Sum(x => x.Bill.Subtotal > 0 ? x.Bill.FoodAmount - (x.Bill.FoodAmount / x.Bill.Subtotal * x.Bill.DiscountAmount) : x.Bill.FoodAmount),
                DiscountAmount = g.Sum(x => x.Bill.DiscountAmount),
                TotalRevenue = g.Sum(x => x.Bill.TotalAmount)
            })
            .OrderBy(r => r.Date)
            .ToList();

        var monthlyReport = billsByBusinessDay
            .GroupBy(x => new { x.BusinessDay.Year, x.BusinessDay.Month })
            .Select(g => new {
                Month = $"{g.Key.Year}-{g.Key.Month:D2}",
                GamingRevenue = g.Sum(x => x.Bill.Subtotal > 0 ? x.Bill.GamingAmount - (x.Bill.GamingAmount / x.Bill.Subtotal * x.Bill.DiscountAmount) : x.Bill.GamingAmount),
                FoodRevenue = g.Sum(x => x.Bill.Subtotal > 0 ? x.Bill.FoodAmount - (x.Bill.FoodAmount / x.Bill.Subtotal * x.Bill.DiscountAmount) : x.Bill.FoodAmount),
                DiscountAmount = g.Sum(x => x.Bill.DiscountAmount),
                TotalRevenue = g.Sum(x => x.Bill.TotalAmount)
            })
            .OrderBy(r => r.Month)
            .ToList();

        var discountAudit = bills
            .Where(b => b.DiscountAmount > 0)
            .Select(b => new {
                BillId = b.BillNumber,
                Date = b.CompletedAt,
                Subtotal = b.Subtotal,
                DiscountAmount = b.DiscountAmount,
                DiscountType = b.DiscountType?.ToString(),
                DiscountValue = b.DiscountValue,
                DiscountReason = b.DiscountReason,
                GivenBy = b.DiscountByAdmin != null 
                    ? $"Super Admin ({b.DiscountByAdmin.FullName})" 
                    : (b.Operator != null ? $"Operator ({b.Operator.FullName})" : "Unknown")
            })
            .OrderByDescending(d => d.Date)
            .ToList();

        var billIds = bills.Select(b => b.Id).ToList();
        var billCredits = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.CustomerCredit>()
            .Query()
            .Where(c => billIds.Contains(c.BillId))
            .ToListAsync();

        var allBills = bills.Select(b => {
            var credit = billCredits.FirstOrDefault(c => c.BillId == b.Id);
            var actualPaid = b.CashAmount + b.OnlineAmount + b.WalletAmount;
            var isCredit = credit != null || actualPaid < b.TotalAmount || b.PaymentType?.ToString() == "Credit";
            
            return new {
                BillId = b.BillNumber,
                // The bill's real, internal id - BillId above is the human-readable number
                // (e.g. "BILL-20260917-XXXX"), which every payment-affecting action on this row
                // needs the actual id for. Correcting a payment method sends this straight to
                // /api/bills/{id:guid}/payment-method, whose route constraint refuses anything
                // that is not a real GUID - so sending the display number 404'd outright, with
                // no error the operator could act on. Confirmed live: every "Save Correction"
                // press failed this way, for every role, since this screen has existed - not
                // something the recent Operator-access change introduced.
                RealBillId = b.Id,
                Date = b.CompletedAt,
                Operator = b.Operator != null ? b.Operator.FullName : "Unknown",
                Customer = string.IsNullOrEmpty(b.CustomerName) ? "Walk-in" : b.CustomerName,
                GamingRevenue = b.GamingAmount,
                FoodRevenue = b.FoodAmount,
                Discount = b.DiscountAmount,
                TotalRevenue = b.TotalAmount,
                PaymentType = isCredit ? "Credit" : (b.PaymentType?.ToString() ?? "Unknown"),
                AmountPaidInitially = credit != null ? credit.AmountPaidInitially : actualPaid,
                CreditAmount = credit != null ? credit.CreditAmount : (isCredit ? Math.Max(0, b.TotalAmount - actualPaid) : 0),
                CreditStatus = isCredit ? "pending" : null,
                SessionNotes = b.Session?.Notes,
                SessionStartTime = b.Session != null ? b.Session.StartTime : (DateTimeOffset?)null,
                SessionEndTime = b.Session != null ? b.Session.EndTime : (DateTimeOffset?)null,
                SessionDurationMinutes = b.Session != null && b.Session.EndTime.HasValue 
                    ? (b.Session.EndTime.Value - b.Session.StartTime).TotalMinutes 
                    : 0,
                PcId = b.PcId,
                PcName = b.Pc != null ? b.Pc.PcNumber : "Walk-in"
            };
        })
        .OrderByDescending(b => b.Date)
        .ToList();

        // PS5 entries from the Cash Desk (bills numbered PS5-...): already inside AllBills and every
        // total above, like any bill. Listed again on their own, with how each was paid, for the
        // PS5 log - display only, never added to anything.
        var ps5Bills = bills.Where(b => b.BillNumber.StartsWith("PS5-")).ToList();
        var ps5BillIds = ps5Bills.Select(b => b.Id).ToList();
        var ps5Notes = ps5BillIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.BillItem>().Query().AsNoTracking()
                .Where(i => ps5BillIds.Contains(i.BillId))
                .GroupBy(i => i.BillId)
                .Select(g => new { g.Key, Name = g.Select(i => i.ItemName).FirstOrDefault() })
                .ToDictionaryAsync(x => x.Key, x => x.Name ?? "");
        var ps5Entries = ps5Bills.Select(b =>
        {
            var credit = billCredits.FirstOrDefault(c => c.BillId == b.Id);
            var note = ps5Notes.GetValueOrDefault(b.Id, "");
            return new
            {
                BillNumber = b.BillNumber,
                Date = b.CompletedAt,
                Customer = b.CustomerName ?? "Walk-in",
                Note = note.StartsWith("PS5 - ") ? note[6..] : "",
                PaymentType = credit != null ? (b.CashAmount + b.OnlineAmount > 0 ? "Split + Credit" : "Credit") : (b.PaymentType?.ToString() ?? "Unknown"),
                Cash = b.CashAmount,
                Online = b.OnlineAmount,
                Credit = credit?.CreditAmount ?? 0m,
                CreditStatus = credit?.Status,
                Discount = b.DiscountAmount,
                Total = b.TotalAmount,
                Operator = b.Operator != null ? b.Operator.FullName : "Unknown",
            };
        }).OrderByDescending(e => e.Date).ToList();

        var credits = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.CustomerCredit>()
            .Query()
            .Include(c => c.Bill)
            .Include(c => c.ClearedByOperator)
            .Where(c => c.BranchId == targetBranchId 
                     && ((c.CreatedAt >= startUtc && c.CreatedAt <= endUtc) || (c.ClearedAt >= startUtc && c.ClearedAt <= endUtc)))
            .ToListAsync();

        // How a settled credit was actually paid - cash, online, or split. CustomerCredit itself
        // never recorded this; ClearCreditAsync already writes it onto a Payment row for the
        // same bill, so it is read back from there rather than added as a new column. A credit
        // bill can hold two Payment rows (the partial amount taken up front, then the clearing
        // one) - the clearing payment is always the later of the two, since clearing can only
        // happen after the bill already exists.
        var creditBillIds = credits.Select(c => c.BillId).ToList();
        var clearingPayments = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Payment>()
            .Query()
            .Where(p => creditBillIds.Contains(p.BillId))
            .ToListAsync();

        var clearedPastCredits = credits
            .Where(c => c.Status != null && c.Status.ToLower() == "cleared" && c.ClearedAt >= startUtc && c.ClearedAt <= endUtc)
            .Select(c => {
                var clearingPayment = clearingPayments
                    .Where(p => p.BillId == c.BillId)
                    .OrderByDescending(p => p.CreatedAt)
                    .FirstOrDefault();

                return new {
                    BillId = $"SETTLED-{(c.Bill != null ? c.Bill.BillNumber : "CREDIT")}",
                    Date = c.ClearedAt,
                    Operator = c.ClearedByOperator != null ? c.ClearedByOperator.FullName : "Unknown",
                    Customer = string.IsNullOrEmpty(c.CustomerName) ? "Walk-in" : c.CustomerName,
                    GamingRevenue = 0m,
                    FoodRevenue = 0m,
                    Discount = 0m,
                    TotalRevenue = c.CreditAmount,
                    PaymentType = "CREDIT SETTLED",
                    AmountPaidInitially = c.AmountPaidInitially,
                    CreditAmount = c.CreditAmount,
                    CreditStatus = "cleared",
                    ClearedPaymentType = clearingPayment?.PaymentType.ToString(),
                    ClearedCashAmount = clearingPayment?.CashAmount ?? 0m,
                    ClearedOnlineAmount = clearingPayment?.OnlineAmount ?? 0m,
                    SessionNotes = "Credit clearance payment for past session",
                    SessionStartTime = c.Bill != null ? c.Bill.CreatedAt : c.CreatedAt,
                    SessionEndTime = (DateTimeOffset?)null,
                    SessionDurationMinutes = 0d,
                    PcId = (Guid?)null,
                    PcName = c.PcNumber ?? "N/A"
                };
            })
            .Cast<object>()
            .ToList();

        var walletTopUps = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.WalletTransaction>()
            .Query()
            .Include(t => t.Member)
            .Include(t => t.Operator)
            .Where(t => t.BranchId == targetBranchId
                     && t.Action == AppleEsportsErp.Domain.Enums.WalletAction.Recharge
                     && t.CreatedAt >= startUtc
                     && t.CreatedAt <= endUtc)
            .ToListAsync();

        var walletTopUpRows = walletTopUps
            .Select(t => new {
                BillId = $"TOPUP-{t.Id.ToString().Substring(0, 8).ToUpper()}",
                Date = t.CreatedAt,
                Operator = t.Operator != null ? t.Operator.FullName : "Unknown",
                Customer = t.Member != null ? t.Member.FullName : "Unknown",
                GamingRevenue = 0m,
                FoodRevenue = 0m,
                Discount = 0m,
                TotalRevenue = t.Amount,
                PaymentType = $"Wallet Top-Up ({t.PaymentType ?? "cash"})",
                AmountPaidInitially = t.Amount,
                CreditAmount = 0m,
                CreditStatus = (string?)null,
                SessionNotes = t.Reason ?? $"{t.TargetWallet} wallet top-up",
                // A top-up is an instant, not a session, so it has a time but no duration.
                // Both were left null, which rendered as "-" and made it impossible to tell
                // from the day's audit log when money had actually gone into a wallet —
                // the one thing you need when a customer disputes a top-up.
                SessionStartTime = (DateTimeOffset?)t.CreatedAt,
                SessionEndTime = (DateTimeOffset?)null,
                SessionDurationMinutes = 0d,
                PcId = (Guid?)null,
                PcName = "-"
            })
            .Cast<object>()
            .ToList();

        var allBillsList = allBills.Cast<object>().ToList();
        var combinedBills = allBillsList.Concat(clearedPastCredits).Concat(walletTopUpRows)
            .OrderByDescending(b => ((dynamic)b).Date)
            .ToList();

        var allCredits = credits.Select(c => new {
            CreditId = c.Id,
            CustomerName = c.CustomerName,
            CustomerPhone = c.CustomerPhone,
            PcNumber = c.PcNumber,
            OriginalBillAmount = c.OriginalBillAmount,
            AmountPaidInitially = c.AmountPaidInitially,
            CreditAmount = c.CreditAmount,
            Status = c.Status,
            CreatedAt = c.CreatedAt,
            ClearedAt = c.ClearedAt
        })
        .OrderByDescending(c => c.CreatedAt)
        .ToList();

        // Power cuts and lost connections for this trading day.
        //
        // Reported alongside the money because they explain it. An evening that looks thin
        // is a very different conversation once you can see the branch was dark for forty
        // minutes, and an operator should not have to remember and argue the point.
        var downtime = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.DowntimeEvent>()
            .Query()
            .Where(d => d.BranchId == targetBranchId && d.StartedAt >= startUtc && d.StartedAt <= endUtc)
            .OrderBy(d => d.StartedAt)
            .ToListAsync();

        var downtimeRows = downtime.Select(d => new
        {
            d.Id,
            Kind = d.Kind switch
            {
                AppleEsportsErp.Domain.Enums.DowntimeKind.PowerOrRestart => "Power cut / restart",
                AppleEsportsErp.Domain.Enums.DowntimeKind.AppFault => "App problem",
                _ => "Internet offline",
            },
            // Formatted in IST here rather than left to the browser, because this is also
            // what goes onto the printed report.
            From = IndiaTime.FormatTime(d.StartedAt),
            To = IndiaTime.FormatTime(d.EndedAt),
            Minutes = Math.Round(d.DurationSeconds / 60.0, 0),
            d.SessionsAffected,
            // A power cut stops play and customers get their time back; losing the link to
            // Head Office does not interrupt anybody's game. An app problem is neither claim -
            // it is whatever the operator could not do while it was stuck, not a verified outage
            // of play or of the sync link, so it gets its own honest, unassuming wording rather
            // than borrowing either existing claim.
            Impact = d.Kind switch
            {
                AppleEsportsErp.Domain.Enums.DowntimeKind.PowerOrRestart =>
                    "Play stopped — affected sessions had their time credited back",
                AppleEsportsErp.Domain.Enums.DowntimeKind.AppFault =>
                    "Reported by the operator as a software problem — not a power cut or lost internet",
                _ => "Play unaffected — only the link to Head Office was down",
            },
            d.Notes,
        }).ToList();

        // Every shift that touched this window, so the billing log below can be read shift by
        // shift instead of as one undifferentiated list for the whole trading day. A shift still
        // open at the moment this report runs has no LogoutTime yet - reported as "still on
        // shift" rather than left blank, so a row from the current shift has somewhere to land.
        var shiftsInRange = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Shift>()
            .Query()
            .Include(s => s.Operator)
            .Where(s => s.BranchId == targetBranchId && s.LoginTime <= endUtc &&
                        (s.LogoutTime == null || s.LogoutTime >= startUtc))
            .OrderBy(s => s.LoginTime)
            .Select(s => new
            {
                s.Id,
                OperatorName = s.Operator != null ? s.Operator.FullName : "Unknown",
                LoginTime = s.LoginTime,
                LogoutTime = s.LogoutTime,
            })
            .ToListAsync();

        return Ok(ApiResponse<object>.Ok(new {
            Daily = dailyReport,
            Monthly = monthlyReport,
            Discounts = discountAudit,
            AllBills = combinedBills,
            Ps5Entries = ps5Entries,
            AllCredits = allCredits,
            Downtime = downtimeRows,
            DowntimeTotalMinutes = Math.Round(downtime.Sum(d => d.DurationSeconds) / 60.0, 0),
            Shifts = shiftsInRange,
        }));
    }

    private Guid GetBranchId() => Guid.Parse(HttpContext.Items["BranchId"]!.ToString()!);

    [HttpGet("report")]
    [HttpGet("preview")]
    public async Task<IActionResult> GetPreview([FromQuery] string? date)
    {
        // A plain DateOnly, never a DateTimeOffset built from this string. The bug this
        // replaces: DateTimeOffset.Parse("2026-09-01") has no offset in the input, so .NET
        // silently assumes the CURRENT PROCESS's own local timezone - India Standard Time on
        // a branch's own Windows machine, UTC in Head Office's Linux container. Converting
        // that through .ToUniversalTime() then shifted the date itself, not just the clock
        // time: "2026-09-01" entered on a branch became 2026-08-31T18:30Z, and every "today"
        // downstream truncated that back down to 31 August - the report for the date typed
        // in was silently the previous day's, and only on a branch's own machine.
        DateOnly businessDay;

        if (string.IsNullOrWhiteSpace(date))
        {
            businessDay = IndiaTime.BusinessDayOf(DateTimeOffset.UtcNow);
        }
        else if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out businessDay))
        {
            return BadRequest(ApiResponse<object>.Fail("Invalid date format. Use YYYY-MM-DD."));
        }

        var result = await _eodService.GenerateEodReportAsync(GetBranchId(), businessDay);
        return Ok(ApiResponse<EodReportDto>.Ok(result));
    }

}

