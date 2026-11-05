using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP §9: Billing Counter — Gaming + Food MUST remain separated</summary>
public class Bill
{
    public Guid Id { get; set; }
    public string BillNumber { get; set; } = null!;
    public Guid? SessionId { get; set; }
    public Guid? PcId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public Guid? ShiftId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? MemberId { get; set; }

    // SOP: Gaming and food MUST remain separated
    public decimal GamingAmount { get; set; }
    public decimal FoodAmount { get; set; }
    public decimal Subtotal { get; set; }

    // SOP §9.6: Discount (Super Admin only)
    public DiscountType? DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public decimal DiscountAmount { get; set; }
    public Guid? DiscountBy { get; set; }
    public string? DiscountReason { get; set; }

    public decimal TotalAmount { get; set; }

    // Payment details
    public PaymentType? PaymentType { get; set; }
    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }
    public decimal WalletAmount { get; set; }

    // SOP §9.5: Change return tracking
    public decimal CashReceived { get; set; }
    public decimal ChangeReturned { get; set; }
    public decimal ActualCashCollected { get; set; }

    public BillStatus Status { get; set; } = BillStatus.Pending;
    public bool IsDeferred { get; set; } = false;

    /// <summary>
    /// Set on a bill the system raised itself because a shift stock count came up short: the
    /// operator responsible pays for the missing items (StockShortageBilling). Shown as its own
    /// line in EOD, and its items cannot be removed by an operator.
    /// </summary>
    public Guid? ShortageOperatorId { get; set; }

    /// <summary>
    /// How long a PS5 customer played, in minutes (90 = 1 hr 30 min). Its own value, never buried
    /// in the note, so it can be read, added up and sorted (owner, 5 Nov 2026). Null on every other
    /// kind of bill and on PS5 entries made before this existed - those show "-" and are left out
    /// of the hours total rather than guessed at. Information only: it never changes an amount.
    /// </summary>
    public int? PlayMinutes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Session? Session { get; set; }
    public Pc? Pc { get; set; }
    public Branch Branch { get; set; } = null!;
    public Operator Operator { get; set; } = null!;
    public Shift? Shift { get; set; }
    public Member? Member { get; set; }
    public User? DiscountByAdmin { get; set; }
    public ICollection<BillItem> Items { get; set; } = new List<BillItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
