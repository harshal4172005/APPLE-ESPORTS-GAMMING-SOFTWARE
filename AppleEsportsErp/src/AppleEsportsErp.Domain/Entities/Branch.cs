using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP §16: Branch Configuration</summary>
public class Branch
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public TimeOnly OpeningTime { get; set; } = new(10, 0);
    public TimeOnly ClosingTime { get; set; } = new(2, 0);
    public BranchStatus Status { get; set; } = BranchStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    
    public string? ConfiguredReservationDurations { get; set; }

    /// <summary>The fixed float every shift opens the cash drawer with, unless the previous
    /// shift's close set a different amount for the next one. Same ₹1,000 for every branch by
    /// default; Admin/Super Admin can change it per branch in Settings.</summary>
    public decimal DefaultOpeningFloat { get; set; } = 1000m;

    /// <summary>
    /// Super Admin's per-branch switch: when on, stock a shift count finds missing is billed to
    /// the operator responsible (StockShortageBilling) and the shift-end screen counts stock
    /// and settles that bill before the register is locked. Off (the default) is exactly the
    /// behaviour from before this existed. Set at Head Office and sent to the branch as a
    /// command (BranchCommands.SetChargeMissingStock), so the branch keeps it with no internet.
    /// </summary>
    public bool ChargeMissingStock { get; set; }

    /// <summary>Null (the default) means this branch's food/snacks are fully independent.
    /// Set to share a menu and stock count with every other branch pointing at the same
    /// FoodGroup — see FoodGroup.cs.</summary>
    public Guid? FoodGroupId { get; set; }
    public FoodGroup? FoodGroup { get; set; }

    // Navigation
    public ICollection<Operator> Operators { get; set; } = new List<Operator>();
    public ICollection<Pc> Pcs { get; set; } = new List<Pc>();
    public ICollection<Shift> Shifts { get; set; } = new List<Shift>();
    public ICollection<Session> Sessions { get; set; } = new List<Session>();
    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
    public ICollection<Bill> Bills { get; set; } = new List<Bill>();
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
    public ICollection<FoodOrder> FoodOrders { get; set; } = new List<FoodOrder>();
}
