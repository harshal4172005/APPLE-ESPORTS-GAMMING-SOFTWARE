namespace AppleEsportsErp.Domain.Entities;

/// <summary>
/// Links two or more branches so their food/snacks menu and stock count are the same number
/// everywhere, instead of each branch tracking its own — for shops that share one physical
/// pantry (e.g. two installs at the same premises split apart for LAN/network reasons) but
/// otherwise run fully independently (PCs, cash, shifts stay per-branch, untouched by this).
///
/// A branch not pointed at any FoodGroup (the default) behaves exactly as it always has.
/// </summary>
public class FoodGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The one branch that keeps selling from the shared fridge when Head Office cannot be
    /// reached (its internet, or the other branch's). Every other branch in the group stops
    /// selling shared items until it is back, so two counters never sell the same can blind.
    /// </summary>
    public Guid? OfflineSellerBranchId { get; set; }

    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}
