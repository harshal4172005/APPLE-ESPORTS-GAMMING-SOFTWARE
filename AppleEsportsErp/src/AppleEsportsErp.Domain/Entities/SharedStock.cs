namespace AppleEsportsErp.Domain.Entities;

/// <summary>
/// The one stock count of a shared fridge, kept at Head Office. Branches in a food group sell
/// from one physical shelf, so there is exactly one number for each item and both counters ask
/// Head Office to move it (see SharedStockController). A branch's own InventoryItem.CurrentStock
/// is only a mirror of this.
/// </summary>
public class SharedStockCount
{
    public Guid FoodGroupId { get; set; }
    public Guid InventoryItemId { get; set; }
    public int Count { get; set; }

    /// <summary>Rises on every change, so a branch can ask "what changed since N".</summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Every request a branch made against a shared count, keyed by the branch's own request id -
/// a retry of the same request returns the stored answer instead of moving the count twice.
/// </summary>
public class SharedStockRequest
{
    public Guid Id { get; set; }
    public Guid FoodGroupId { get; set; }
    public Guid InventoryItemId { get; set; }
    public Guid BranchId { get; set; }
    public string Op { get; set; } = null!;      // take, add, set
    public int Quantity { get; set; }
    public int? ResultCount { get; set; }
    public string? RefusedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A stock change made at a branch while Head Office could not be reached, waiting to be sent.
/// Branch-local only; deleted once Head Office has applied it.
/// </summary>
public class SharedStockPending
{
    public Guid Id { get; set; }
    public Guid InventoryItemId { get; set; }
    public string Op { get; set; } = null!;
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
