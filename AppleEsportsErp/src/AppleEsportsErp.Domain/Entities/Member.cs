using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP §14: Members Dashboard with wallet and loyalty points</summary>
public class Member
{
    public Guid Id { get; set; }
    public string MemberNumber { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string MobileNumber { get; set; } = null!;
    public string? Email { get; set; }
    public string? Username { get; set; }       // nullable — set when operator assigns login
    public string? PasswordHash { get; set; }   // BCrypt hash

    /// <summary>
    /// When this password was actually last changed - same reasoning as BalanceAsOf just below,
    /// applied to the field it was missing on. A password can change at a branch (registration,
    /// or an operator resetting it at the counter) or at Head Office (a phone reset, or a Super
    /// Admin setting one directly), and without a timestamp neither side can tell whose value is
    /// actually newer - whichever sync happened to run last would silently win, overwriting a
    /// brand new correct password with an old one. Compared the same way BalanceAsOf is: only
    /// overwrite local with incoming when incoming is later.
    /// </summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Whether an incoming password (with this changed-at) should replace this
    /// member's current one - only when it's actually newer, so a late or redelivered sync
    /// can never undo a password that has already changed again since. An incoming value with
    /// no timestamp never beats a local one that has one: Head Office still holds 15 legacy
    /// hashes with no time, and treating "no time" as newer made every heartbeat put that old
    /// password back over a reset the operator had just done at the counter.</summary>
    public bool IsPasswordNewer(DateTimeOffset? incomingChangedAt) =>
        PasswordChangedAt is not { } local || (incomingChangedAt is { } incoming && incoming > local);

    /// <summary>A new password also lifts any lockout - a member locked out for typing the
    /// old one would otherwise be told "account locked" for up to 15 minutes after a reset
    /// that worked.</summary>
    public void SetPassword(string? hash, DateTimeOffset? changedAt)
    {
        PasswordHash = hash;
        PasswordChangedAt = changedAt;
        FailedAttempts = 0;
        LockedUntil = null;
    }

    // Password Reset fields
    public string? ResetToken { get; set; }
    public DateTimeOffset? ResetTokenExpiry { get; set; }

    // Brute-force lockout
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    public MemberStatus Status { get; set; } = MemberStatus.Active;

    // SOP §14.1: Wallet System - Separated per SOP §11.1
    public decimal GamingBalance { get; set; }
    public decimal FoodBalance { get; set; }

    /// <summary>
    /// When this balance was last known to be correct - the moment of the top-up or spend that
    /// produced it, not when the row happened to be saved.
    ///
    /// A member is shared across every branch on purpose: they join at one shop and should be
    /// able to spend at any of them. That only works if whichever branch or Head Office is
    /// holding the freshest figure can prove it is the freshest, rather than the two simply
    /// overwriting each other - two branches touching the same wallet minutes apart must not be
    /// able to undo one another depending on which sync happened to land last.
    /// </summary>
    public DateTimeOffset? BalanceAsOf { get; set; }

    // Lifetime running counters (never decrease on spend) — for the "how much of my balance
    // is real money vs bonus" breakdown shown in the Members UI. GamingBalance itself is the
    // one spendable pool and always shrinks on spend, same as before.
    public decimal TotalGamingTopUps { get; set; }
    public decimal TotalGamingBonusEarned { get; set; }

    // SOP §15: Loyalty Point System — gaming/food separated
    public int GamingPoints { get; set; }
    public int FoodPoints { get; set; }
    public int TotalPoints { get; set; }

    // Spending tracking — separated per SOP
    public decimal TotalGamingSpend { get; set; }
    public decimal TotalFoodSpend { get; set; }

    public Guid? HomeBranchId { get; set; }
    public DateTimeOffset JoinDate { get; set; }
    public DateTimeOffset? LastVisit { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Branch? HomeBranch { get; set; }
    public ICollection<WalletTransaction> WalletTransactions { get; set; } = new List<WalletTransaction>();
    public ICollection<LoyaltyPoint> LoyaltyPoints { get; set; } = new List<LoyaltyPoint>();
}
