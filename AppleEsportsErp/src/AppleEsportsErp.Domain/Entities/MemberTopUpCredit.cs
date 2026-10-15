namespace AppleEsportsErp.Domain.Entities;

/// <summary>
/// A member's wallet top-up given on credit - balance granted immediately, payment collected
/// later. Deliberately its own table rather than a CustomerCredit row: CustomerCredit requires
/// a BillId (a top-up isn't a bill), and clearing it creates a Payment row which also requires
/// a BillId. Reusing that table meant making both required columns nullable on a table that
/// has been through enough money bugs already tonight. This is additive only - the existing
/// credit system (bill credits) is untouched, in schema and in code.
/// </summary>
public class MemberTopUpCredit
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;

    public Guid OperatorId { get; set; }
    public Operator Operator { get; set; } = null!;

    public Guid MemberId { get; set; }
    public Member Member { get; set; } = null!;

    public decimal CreditAmount { get; set; }

    public string Status { get; set; } = "pending"; // "pending" or "cleared"

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }

    public Guid? ClearedByOperatorId { get; set; }
    public Operator? ClearedByOperator { get; set; }
}
