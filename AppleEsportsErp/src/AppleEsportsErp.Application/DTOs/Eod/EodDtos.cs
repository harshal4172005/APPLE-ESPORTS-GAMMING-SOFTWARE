namespace AppleEsportsErp.Application.DTOs.Eod;

public class EodReportDto
{
    public Guid BranchId { get; set; }
    public DateTimeOffset ReportDate { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    
    public RevenueSummaryDto Revenue { get; set; } = new();
    public CashSummaryDto Cash { get; set; } = new();
    public PaymentMethodSummaryDto PaymentMethods { get; set; } = new();

    /// <summary>Whether what was billed agrees with what was taken. See EodReconciliationDto.</summary>
    public EodReconciliationDto Reconciliation { get; set; } = new();

    /// <summary>Missing-stock bills raised or paid today, whoever they charge.</summary>
    public List<AppleEsportsErp.Application.DTOs.Reports.StockShortageDto> StockShortages { get; set; } = new();

    public ShiftSummaryDto Shifts { get; set; } = new();
    public OperationalStatsDto Operations { get; set; } = new();
    public List<EodCreditLogDto> CreditLogs { get; set; } = new();
}

public class EodCreditLogDto
{
    public Guid CreditId { get; set; }
    public string CustomerName { get; set; } = null!;
    public string CustomerPhone { get; set; } = null!;
    public string PcNumber { get; set; } = null!;
    public decimal OriginalBillAmount { get; set; }
    public decimal AmountPaidInitially { get; set; }
    public decimal CreditAmount { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}

public class RevenueSummaryDto
{
    public decimal TotalGamingRevenue { get; set; }
    public decimal TotalFoodRevenue { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal NetRevenue { get; set; }

    /// <summary>
    /// Part of Food Revenue: paid today by operators for stock their counts found missing
    /// (StockShortageBilling) - the money for cans that left the shelf without a sale.
    /// </summary>
    public decimal StockShortageRecovered { get; set; }
}

public class CashSummaryDto
{
    public decimal TotalOpeningBalance { get; set; }
    public decimal TotalCashSales { get; set; }
    public decimal TotalCashInwards { get; set; }
    public decimal TotalPettyExpenses { get; set; }
    public decimal TotalOwnerWithdrawals { get; set; }
    public decimal ExpectedCashInDrawer { get; set; }

    /// <summary>
    /// What the drawer currently on the counter was counted at. **Null when nobody has counted
    /// it yet**, which is the normal state until a shift ends.
    ///
    /// Nullable rather than zero on purpose. Zero reads as "the drawer is empty" — the whole
    /// day's takings shown as gone — when what is true is "nobody has looked".
    /// </summary>
    public decimal? ActualPhysicalCashCounted { get; set; }

    /// <summary>
    /// <see cref="ActualPhysicalCashCounted"/> minus <see cref="ExpectedCashInDrawer"/>, so the
    /// three figures always agree with each other. Null while the drawer is uncounted, because
    /// the difference is then unknown, and "no difference" on an uncounted drawer is the most
    /// dangerous thing this screen could say.
    /// </summary>
    public decimal? TotalDiscrepancy { get; set; }

    /// <summary>
    /// Differences already found and closed earlier in the same trading day — a shift handover
    /// where the drawer was counted short, or over.
    ///
    /// Kept as its own figure rather than folded into <see cref="TotalDiscrepancy"/>. That money
    /// went missing on an earlier shift, and the drawer in use now started from what was actually
    /// counted, so it is not itself short by this amount. Without the line the column does not
    /// add up: opening plus takings comes to more than the drawer is expected to hold, with
    /// nothing on screen explaining the gap.
    /// </summary>
    public decimal DifferencesFoundEarlier { get; set; }

    /// <summary>Cash set aside for the owner when the last shift of the day closed - see
    /// CashRegister.CoverAmount. Null until that happens.</summary>
    public decimal? CoverAmount { get; set; }
}

public class PaymentMethodSummaryDto
{
    /// <summary>Cash taken against bills. Does NOT include cash taken for wallet top-ups.</summary>
    public decimal TotalCash { get; set; }

    /// <summary>Online taken against bills. Does NOT include online wallet top-ups.</summary>
    public decimal TotalOnline { get; set; }

    /// <summary>
    /// Bill value settled out of members' wallet balances.
    ///
    /// This is <b>not money arriving today</b> and must never be added to a total of takings.
    /// The rupees were collected when the wallet was topped up - possibly weeks ago - and this
    /// is them being spent. Adding both the top-up and the later deduction counts the same
    /// rupee twice, which is exactly what the payment summary was doing.
    /// </summary>
    public decimal TotalWalletDeductions { get; set; }

    /// <summary>
    /// The <c>Amount</c> recorded against today's top-ups.
    ///
    /// <b>Do not treat this as money taken.</b> It is not reliably the customer's payment: on
    /// live data some rows carry payment-plus-bonus here (Rs 550 recorded against Rs 500 of
    /// notes and a Rs 50 promotional bonus) while others carry the payment alone. Summing it
    /// therefore counts free promotional credit as though it were cash through the door - Rs 100
    /// of it on one branch on one day.
    ///
    /// Kept because it is what the row says and hiding it would only move the confusion. The
    /// figures to trust are the two below, which are the amounts explicitly recorded against a
    /// payment method and never include bonus.
    /// </summary>
    public decimal TotalWalletTopUps { get; set; }

    /// <summary>Free credit given away in top-up bonuses today. A cost, not income.</summary>
    public decimal TotalWalletBonusGiven { get; set; }

    /// <summary>
    /// The part of today's top-ups actually handed over as notes.
    ///
    /// Split out because the summary was adding <see cref="TotalWalletTopUps"/> - every method -
    /// straight into the cash line. A Rs 500 top-up paid by UPI then showed as Rs 500 of cash in
    /// the drawer, and the operator counting that drawer came up Rs 500 short against a figure
    /// nobody had ever handed them.
    /// </summary>
    public decimal TotalWalletTopUpsCash { get; set; }

    /// <summary>The part of today's top-ups paid by UPI or card.</summary>
    public decimal TotalWalletTopUpsOnline { get; set; }

    /// <summary>
    /// Every rupee that actually entered the business today: bill cash and online, plus wallet
    /// top-ups by either method.
    ///
    /// This is the one figure to put next to the word "Total". It was previously assembled in
    /// the display component out of four other figures and got it wrong in three separate ways -
    /// it added wallet deductions (money collected on an earlier day, counted a second time),
    /// it put every top-up into the cash line regardless of how it was paid, and through
    /// <see cref="TotalWalletTopUps"/> it counted promotional bonus as takings.
    ///
    /// Built from the per-method amounts precisely because those are the only ones that mean
    /// "this much was handed over, by this method".
    /// </summary>
    public decimal TotalCollected { get; set; }
}

/// <summary>
/// Does what was billed agree with what was taken, and if not, by how much.
///
/// The payment summary showed two columns side by side - revenue on the left, payment methods
/// on the right - under a single total, and there was no arithmetic connecting them. They can
/// legitimately differ, by discounts, by credit given to a customer who left owing money, and
/// by credit collected today for a bill from an earlier day. None of those appeared, so the two
/// halves of the screen simply disagreed and the reader had no way to tell whether that was
/// normal or a fault.
///
/// Every line below is one of those legitimate reasons, in order, ending in a difference that
/// should be zero. When it is not zero it is shown rather than hidden: an unexplained gap is
/// the single most useful thing this screen can report, and burying it is how a real shortfall
/// goes unnoticed for a week.
/// </summary>
public class EodReconciliationDto
{
    /// <summary>Gaming plus food, before any discount, across every completed bill today.</summary>
    public decimal GrossBilled { get; set; }

    public decimal Discounts { get; set; }

    /// <summary>Bills a customer left today without paying. Earned, not collected.</summary>
    public decimal CreditGivenToday { get; set; }

    /// <summary>Old debts settled today. Collected today, earned on an earlier day.</summary>
    public decimal CreditClearedToday { get; set; }

    /// <summary>What today's trading should have produced in payments: gross, less discounts, less new credit, plus old credit collected.</summary>
    public decimal ShouldHaveBeenCollected { get; set; }

    /// <summary>What was actually taken against bills: cash, online and wallet deductions.</summary>
    public decimal ActuallySettled { get; set; }

    /// <summary>
    /// <see cref="ActuallySettled"/> minus <see cref="ShouldHaveBeenCollected"/>. Zero on a day
    /// that adds up. Anything else is worth looking at before the day is finalised.
    /// </summary>
    public decimal Difference { get; set; }
}

public class ShiftSummaryDto
{
    public int TotalShifts { get; set; }
    public List<ShiftDetailDto> ShiftDetails { get; set; } = new();
}

public class ShiftDetailDto
{
    public Guid ShiftId { get; set; }
    public Guid OperatorId { get; set; }
    public string OperatorName { get; set; } = null!;
    public decimal TotalSales { get; set; }
    public decimal CashDiscrepancy { get; set; }
}

public class OperationalStatsDto
{
    public int TotalSessions { get; set; }
    public int TotalReservations { get; set; }
    public int TotalFoodOrders { get; set; }
    public int NewMembersRegistered { get; set; }
}
