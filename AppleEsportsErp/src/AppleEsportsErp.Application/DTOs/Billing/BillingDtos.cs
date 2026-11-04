using System.ComponentModel.DataAnnotations;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Application.DTOs.Billing;

public class BillDto
{
    public Guid Id { get; set; }
    public string BillNumber { get; set; } = null!;
    public Guid? SessionId { get; set; }
    public Guid? PcId { get; set; }
    public string? PcNumber { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public Guid? ShiftId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? MemberId { get; set; }

    public decimal GamingAmount { get; set; }
    public decimal FoodAmount { get; set; }
    public decimal Subtotal { get; set; }

    public DiscountType? DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountReason { get; set; }

    public decimal TotalAmount { get; set; }

    public BillStatus Status { get; set; }
    public bool IsDeferred { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SessionEndTime { get; set; }

    public List<BillItemDto> Items { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
}

public class BillItemDto
{
    public Guid Id { get; set; }
    public string ItemType { get; set; } = null!;
    public string ItemName { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
}

public class PaymentDto
{
    public Guid Id { get; set; }
    public PaymentType PaymentType { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }
    public decimal WalletAmount { get; set; }
    public decimal CashReceived { get; set; }
    public decimal ChangeReturned { get; set; }
    public decimal ActualCashCollected { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ApplyDiscountDto
{
    [Required]
    public DiscountType DiscountType { get; set; }
    
    [Required]
    [Range(0, 1000000)]
    public decimal DiscountValue { get; set; }
    
    [Required]
    public string Reason { get; set; } = null!;
}

public class ProcessPaymentDto
{
    [Required]
    public PaymentType PaymentType { get; set; }
    
    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }
    public decimal WalletAmount { get; set; }
    public decimal CreditAmount { get; set; }
    
    // Specifically for cash payments
    public decimal CashReceived { get; set; }

    public Guid? MemberId { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
}

/// <summary>
/// Corrects how an already-completed bill was paid — e.g. it was marked Online, the bank
/// later declined the transaction, and the customer paid Cash on the spot instead. Wallet is
/// deliberately not an option here: a wallet payment was apportioned across a member's Gaming
/// and Food balances in a split that isn't retained per-payment, so reversing or re-applying
/// it correctly isn't possible from this DTO alone — moving money into or out of a member's
/// wallet stays a job for a fresh payment/refund, not this correction.
/// </summary>
public class EditPaymentMethodDto
{
    [Required]
    public PaymentType NewPaymentType { get; set; }

    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }

    [Required]
    [MinLength(1)]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// A PS5 customer's play, entered by the operator at the Cash Desk. Becomes an ordinary bill
/// (no PC, no session) numbered PS5-..., paid like any other: cash, online, split or credit.
/// </summary>
public class CreatePs5BillDto
{
    [Range(1, 100000)]
    public decimal Amount { get; set; }

    [Required, MaxLength(100)]
    public string CustomerName { get; set; } = null!;

    [MaxLength(20)]
    public string? CustomerPhone { get; set; }

    [MaxLength(120)]
    public string? Note { get; set; }
}
