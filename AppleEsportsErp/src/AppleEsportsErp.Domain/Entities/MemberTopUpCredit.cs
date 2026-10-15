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

    // How the cleared amount was actually collected - set only when Status becomes "cleared".
    // EodService adds these into the day's Cash/Online totals the same way a cleared bill
    // credit's own Payment row already does, so the drawer and Overall End Total pick it up too.
    public decimal ClearedCashAmount { get; set; }
    public decimal ClearedOnlineAmount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }

    public Guid? ClearedByOperatorId { get; set; }
    public Operator? ClearedByOperator { get; set; }
}
