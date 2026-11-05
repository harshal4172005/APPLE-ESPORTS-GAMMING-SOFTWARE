using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP §10: Physical Cash Drawer Management</summary>
public class CashRegister
{
    public Guid Id { get; set; }

    /// <summary>The shift that opened the drawer. Kept for accountability, not for scoping.</summary>
    public Guid ShiftId { get; set; }

    /// <summary>
    /// The trading day (06:00-06:00 IST) this drawer belongs to.
    ///
    /// One drawer per day, not one per login. There is a single physical cash box on the
    /// counter; tying it to a shift meant an operator who logged in three times opened three
    /// registers and was asked to count the same money three times, producing three sets of
    /// figures that reconciled against nothing. Whoever is on duty adds to the same drawer,
    /// and it is counted once at the end of the day.
    /// </summary>
    public DateOnly BusinessDay { get; set; }

    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal TotalCashSales { get; set; }
    public decimal TotalSplitCash { get; set; }
    public decimal ExpectedDrawerCash { get; set; }
    public decimal? PhysicalCashCounted { get; set; }
    public decimal? CashDifference { get; set; }
    public string? MismatchReason { get; set; }

    /// <summary>
    /// Who physically counted the money in <see cref="PhysicalCashCounted"/>.
    ///
    /// Usually the same person as <see cref="OperatorId"/>, who opened the drawer. Not always:
    /// when a shift is abandoned the next operator counts the drawer on the way in, and that
    /// count is theirs. Null means nobody counted it - a drawer closed administratively or by
    /// the automatic day close, where writing in a figure would invent a count that never
    /// happened.
    /// </summary>
    public Guid? CountedByOperatorId { get; set; }

    /// <summary>
    /// Only ever set on the register that closed on a "last shift of the day" tick. What the
    /// owner withdrew from the counted drawer before it was handed back to the till - one figure,
    /// not tracked separately from an "owner withdrawal" elsewhere, per the owner's own account
    /// of what this money is.
    /// </summary>
    public decimal? CoverAmount { get; set; }

    /// <summary>
    /// Only ever set alongside <see cref="CoverAmount"/>. What was left in the drawer for
    /// tomorrow's first shift to open with - normally the branch's DefaultOpeningFloat, but
    /// recorded here as whatever was actually left, since that is the figure tomorrow's count
    /// gets checked against, not the branch's setting at the time.
    /// </summary>
    public decimal? NextDayOpeningBalance { get; set; }

    /// <summary>Why PhysicalCashCounted didn't split cleanly into CoverAmount + NextDayOpeningBalance,
    /// when it didn't. Null when it added up exactly.</summary>
    public string? NextDayFloatReason { get; set; }

    public CashRegisterStatus Status { get; set; } = CashRegisterStatus.Open;
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool SuperAdminAcknowledged { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }

    // Navigation
    public Shift Shift { get; set; } = null!;
    public Branch Branch { get; set; } = null!;
    public Operator Operator { get; set; } = null!;
    public ICollection<CashTransaction> CashTransactions { get; set; } = new List<CashTransaction>();
    public ICollection<DenominationCount> DenominationCounts { get; set; } = new List<DenominationCount>();
}
