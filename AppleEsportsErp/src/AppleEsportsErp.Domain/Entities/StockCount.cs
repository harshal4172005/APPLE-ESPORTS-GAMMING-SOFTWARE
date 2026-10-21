namespace AppleEsportsErp.Domain.Entities;

/// <summary>
/// One shift's stock count, whole: who counted, when, and for every item what the system said
/// against what was actually on the shelf - including the items that matched, so "counted and
/// right" can be told apart from "never counted". Shown under the note breakdown in the Cash
/// Register history and synced to Head Office for Super Admin.
///
/// No foreign keys on purpose: it arrives at Head Office by sync and must never be refused for
/// a register or operator that has not landed there yet.
/// </summary>
public class StockCount
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid? CashRegisterId { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? OperatorId { get; set; }
    public string OperatorName { get; set; } = null!;

    /// <summary>"opening" (shift start) or "closing" (shift end).</summary>
    public string Kind { get; set; } = null!;

    public DateTimeOffset CountedAt { get; set; }

    /// <summary>The bill this count raised for missing items, if any (StockShortageBilling).</summary>
    public Guid? ShortageBillId { get; set; }

    /// <summary>JSON: [{ inventoryId, itemName, system, counted }].</summary>
    public string Lines { get; set; } = "[]";
}
