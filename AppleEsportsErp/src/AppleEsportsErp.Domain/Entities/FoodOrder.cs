using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP §12: Food Orders Dashboard</summary>
public class FoodOrder
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = null!;
    public Guid? SessionId { get; set; }
    public Guid? PcId { get; set; }
    public Guid? BillId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? OperatorId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? MemberId { get; set; }
    public decimal TotalAmount { get; set; }
    public string? PaymentType { get; set; } // cash, online, split, wallet, session_bill
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? CancelledReason { get; set; }
    public DateTimeOffset OrderTime { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Stock was taken off when the order was placed (so two people can never both order the
    /// last one). Delivery then must not take it again; cancelling gives it back. False only
    /// for orders placed before this existed, which still take their stock on delivery.
    /// </summary>
    public bool StockTaken { get; set; }

    // Navigation
    public Session? Session { get; set; }
    public Pc? Pc { get; set; }
    public Bill? Bill { get; set; }
    public Branch Branch { get; set; } = null!;
    public Operator? Operator { get; set; }
    public Member? Member { get; set; }
    public ICollection<FoodOrderItem> Items { get; set; } = new List<FoodOrderItem>();
}
