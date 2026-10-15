using System;
using AppleEsportsErp.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace AppleEsportsErp.Application.DTOs.Credits;

public class CreditDto
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public Guid BillId { get; set; }
    public string CustomerName { get; set; }
    public string CustomerPhone { get; set; }
    public string PcNumber { get; set; }
    public decimal OriginalBillAmount { get; set; }
    public decimal AmountPaidInitially { get; set; }
    public decimal CreditAmount { get; set; }
    public string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}

public class ClearCreditDto
{
    [Required]
    public PaymentType PaymentType { get; set; }

    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }

    // For cash changes
    public decimal CashReceived { get; set; }
}

/// <summary>A member's wallet top-up given on credit - see MemberTopUpCredit's own docstring
/// for why this is a separate table, and so a separate DTO, from a bill credit.</summary>
public class MemberTopUpCreditDto
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public Guid MemberId { get; set; }
    public string MemberName { get; set; } = null!;
    public string MemberUsername { get; set; } = null!;
    public decimal CreditAmount { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}
