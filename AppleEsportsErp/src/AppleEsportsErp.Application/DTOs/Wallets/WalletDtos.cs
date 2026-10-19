using System.ComponentModel.DataAnnotations;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Application.DTOs.Wallets;

public class WalletTransactionDto
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public WalletAction Action { get; set; }
    public WalletType TargetWallet { get; set; }
    public decimal Amount { get; set; }
    public decimal BonusAmount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? PaymentType { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class TopUpWalletDto
{
    [Required]
    public decimal Amount { get; set; }

    [Required]
    public WalletType TargetWallet { get; set; }

    [Required]
    public string PaymentType { get; set; } = null!; // "Cash", "Online", "Split"

    // Only used when PaymentType == "Split" — must add up to Amount
    public decimal? CashAmount { get; set; }
    public decimal? OnlineAmount { get; set; }

    // Super Admin only — overrides the standard 10% Gaming top-up bonus rate for this transaction.
    // Ignored (and the standard rate applies) if the caller isn't Super Admin.
    public decimal? BonusPercentOverride { get; set; }

    public string? Reason { get; set; }

    // Sent by the old Extra Bonus screen, which recorded a bonus as a cash top-up. Now refused -
    // a bonus goes through GiveBonusDto instead, which never counts as money received.
    public bool IsBonusOnly { get; set; } = false;
}

/// <summary>A free bonus: added to the wallet, never counted as cash/online/EOD money.</summary>
public class GiveBonusDto
{
    [Required]
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    [Required]
    public WalletType TargetWallet { get; set; }

    public string? Reason { get; set; }
}

/// <summary>Super Admin (or an Admin explicitly granted the "wallet_settings" permission) editable top-up rules.</summary>
public class WalletTopUpRulesDto
{
    [Required]
    [Range(1, double.MaxValue)]
    public decimal MinGamingTopUp { get; set; }

    [Required]
    [Range(0, 1000)]
    public decimal DefaultBonusPercent { get; set; }
}

public class DeductWalletDto
{
    [Required]
    public decimal Amount { get; set; }

    [Required]
    public WalletType TargetWallet { get; set; }

    [Required]
    public string Reason { get; set; } = null!;

    public Guid? BillId { get; set; }
}
