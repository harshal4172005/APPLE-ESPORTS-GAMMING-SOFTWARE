namespace AppleEsportsErp.Application.DTOs.Sync;

/// <summary>
/// What Head Office tells a branch about itself, in the reply to a heartbeat.
///
/// Everything in this system flowed one way - upward. A branch reported its sessions, its
/// takings, its state, and Head Office listened. Nothing came back. So a super admin could
/// tick "End of Day" for an operator, watch the server save it, and the counter would never
/// hear of it. Every setting on the server was decoration.
///
/// It travels in the heartbeat reply rather than over anything new. A branch is already
/// speaking to Head Office every three seconds and already reading the answer, so a change
/// lands within three seconds of being made, over a connection that exists, with no second
/// mechanism to keep working.
///
/// Sent only when something actually changed. The branch reports the <see cref="Version"/> it
/// currently holds and Head Office replies with nothing at all when it matches - so the
/// ordinary case stays a few hundred bytes, and the full list only crosses the wire on the
/// rare beat after somebody edits a permission.
///
/// This is deliberately configuration and never operational state. Who exists and what they
/// are allowed to see belongs to Head Office. Who is on shift right now belongs to the branch,
/// which is the only place that can know it.
/// </summary>
public class BranchConfigDto
{
    /// <summary>
    /// Fingerprint of everything below. The branch sends back the one it holds; equal means
    /// nothing needs sending. Derived from the contents, so it changes when they do and no
    /// separate bookkeeping can fall out of step with it.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    public List<BranchOperatorConfigDto> Operators { get; set; } = new();

    /// <summary>
    /// The food menu as Head Office defines it for this branch.
    ///
    /// This is the fix for a super admin adding an item at Head Office and it never appearing
    /// at the counter: the Menu Editor is branch-scoped storage, so an item "added for Adajan"
    /// while working at Head Office was written into Head Office's own copy of Adajan's table
    /// and nowhere else - the physical Adajan counter runs an entirely separate database that
    /// was never told.
    /// </summary>
    public List<BranchMenuItemConfigDto> MenuItems { get; set; } = new();

    /// <summary>
    /// Every member and their current wallet balance, as Head Office holds it.
    ///
    /// Members are deliberately not branch-scoped - someone joins at Adajan and should be able
    /// to spend their wallet at Katargam - which means every branch legitimately needs to know
    /// about every member, not just its own. Sent whole rather than as a delta because a
    /// branch's local copy can be arbitrarily stale after time offline, and reconciling a
    /// balance from a partial history invites exactly the kind of drift this exists to remove.
    /// </summary>
    public List<BranchMemberConfigDto> Members { get; set; } = new();

    /// <summary>
    /// Every pricing profile this branch has, plus any custom fixed-duration packages on it.
    ///
    /// A branch that runs the full local install (its own database, not just a thin agent) has
    /// its own separate copy of PricingProfiles from Head Office's - created once at adoption
    /// and never touched again. A profile edited, or a package added, at Head Office's own
    /// dashboard was invisible at the counter for exactly the same reason the menu editor was:
    /// two different databases, only one of which anybody was actually looking at.
    /// </summary>
    public List<BranchPricingProfileConfigDto> PricingProfiles { get; set; } = new();

    /// <summary>
    /// Every Admin-level Users-table account, Head Office's own PIN and all.
    ///
    /// An Admin created at Head Office never reached any branch at all - not a caching problem,
    /// nothing to invalidate, because nothing had ever built the pipe in the first place. Quick
    /// Admin Switch reads a branch's own LOCAL Users table (AuthService.GetAvailableAdminsForSwitchAsync),
    /// and Users was never one of the things a heartbeat carried down - only Operator promoted
    /// with IsGlobalAdmin ever made that trip, which is a different, already-working path. An
    /// Admin made the "right way" at Head Office looked, from the counter, exactly like it did
    /// not exist, forever, no matter how long anyone waited or how many times they refreshed.
    ///
    /// Not scoped to one branch, the same reasoning as Members: an Admin is meant to be reachable
    /// from any counter's Quick Admin Switch, not just one, so every branch needs the whole list.
    /// </summary>
    public List<BranchAdminConfigDto> Admins { get; set; } = new();
}

/// <summary>
/// One operator as Head Office defines them.
///
/// Enough to create the person at a branch that has never heard of them, which closes the
/// other half of the same gap: an operator added at Head Office could not log in at the shop
/// they had just been hired for.
/// </summary>
public class BranchOperatorConfigDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The stored hash, never a password - the same value the branch would have written itself
    /// had the operator been created there. Without it a new operator exists at the shop and
    /// cannot sign in, which is worse than not existing.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string? MobileNumber { get; set; }
    public string? AccessPin { get; set; }
    public bool IsGlobalAdmin { get; set; }

    /// <summary>The permission map, verbatim. This is the thing that was never arriving.</summary>
    public string DashboardPermissions { get; set; } = string.Empty;

    /// <summary>
    /// Whether Head Office has barred this person - suspended or disabled.
    ///
    /// Sent as a plain yes or no rather than as a status, because the status column carries two
    /// unrelated ideas: an administrator's decision about a person, and whether they happen to
    /// be standing at a counter. The first travels down; the second is the branch's alone and
    /// must never be overwritten from here, or a heartbeat would sign out the operator who is
    /// mid-shift.
    /// </summary>
    public bool IsBlocked { get; set; }
}

/// <summary>
/// One food item's catalog details - what it is called, what it costs, whether it is for sale.
///
/// Deliberately not the whole InventoryItem. CurrentStock and SoldQty are the branch's own
/// trading state, exactly like a PC's busy/idle - they change constantly at the counter and
/// must never be overwritten by a heartbeat reply, or a shop that just sold its last plate of
/// fries would have Head Office silently restock it on the next beat.
/// </summary>
public class BranchMenuItemConfigDto
{
    public Guid Id { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Withdrawn from sale by Head Office, as opposed to Out of Stock - which is the branch's
    /// own call and is deliberately not carried here, for the same reason CurrentStock is not.
    /// </summary>
    public bool IsDisabled { get; set; }
}

/// <summary>One member and the wallet balance Head Office currently holds for them.</summary>
public class BranchMemberConfigDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string MemberNumber { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Username { get; set; }

    /// <summary>
    /// Without this, a branch a member merely visits (not their own) never learns which branch
    /// is "home" for them - so when that member resets their password there, the step that
    /// tells Head Office the new token exists (ShareMemberResetTokenAsync) finds HomeBranchId
    /// null on the local copy and silently gives up, no error anywhere. The email still goes
    /// out fine (that part never touches this field), so the link just always fails with
    /// "Invalid or expired reset token" - confirmed live for a member resetting at Adajan whose
    /// home branch is Citylight-144Hz.
    /// </summary>
    public Guid? HomeBranchId { get; set; }

    /// <summary>
    /// Without this, a member's password only ever reached the one branch a targeted
    /// set_member_password command happened to be addressed to - their own HomeBranchId, and
    /// only when set via a phone reset; a password set directly by Super Admin at Head Office
    /// reached nowhere at all, ever. Everything else about a member (name, balance, phone)
    /// already rides down on this same beat to every branch, on the stated reasoning that a
    /// member who joined at one shop is meant to be able to use their account at any of them -
    /// the password is the one field that was quietly excluded from that promise.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>When this password was actually last changed - see Member.PasswordChangedAt.
    /// Without it, whichever side's push happened to land last would win regardless of which
    /// password was actually newer.</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public decimal GamingBalance { get; set; }
    public decimal FoodBalance { get; set; }

    /// <summary>
    /// When this balance was last known correct. Sent so a branch can tell whether its own,
    /// more recent, local activity for this member is actually newer than what Head Office is
    /// handing back - and if so, keep its own figure rather than being overwritten by a beat
    /// that has not caught up yet.
    /// </summary>
    public DateTimeOffset? BalanceAsOf { get; set; }

    public bool IsBlocked { get; set; }
}

/// <summary>
/// One pricing profile as Head Office defines it, with its custom packages (if any).
///
/// BaseHourlyRate/BufferMinutes/RefreshRate/SystemSpecs plus whichever fixed-duration packages
/// are attached - everything a branch's own PC-plans screen needs, deliberately excluding
/// nothing operational: a profile carries no trading state of its own to protect, unlike a
/// PC or an inventory item.
/// </summary>
public class BranchPricingProfileConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal BaseHourlyRate { get; set; }
    public int BufferMinutes { get; set; }
    public bool IsActive { get; set; }
    public string? RefreshRate { get; set; }
    public string? SystemSpecs { get; set; }

    public List<BranchPricingPackageConfigDto> Packages { get; set; } = new();
}

/// <summary>One fixed-duration/fixed-price package on a pricing profile - see PricingPackage.</summary>
public class BranchPricingPackageConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// One Admin-level Users-table account as Head Office defines them.
///
/// Enough to create the person locally at a branch that has never heard of them and let them
/// straight into Quick Admin Switch there, the same closing-the-gap reasoning as
/// BranchOperatorConfigDto - an Admin made at Head Office could not be switched into at any
/// counter, ever, ever, which is exactly what "created the right way" was supposed to prevent.
/// </summary>
public class BranchAdminConfigDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>The stored hash, never a password - see BranchOperatorConfigDto.PasswordHash.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string? AccessPin { get; set; }
    public string? DashboardPermissions { get; set; }

    /// <summary>Suspended/disabled at Head Office - see BranchOperatorConfigDto.IsBlocked.</summary>
    public bool IsBlocked { get; set; }
}
