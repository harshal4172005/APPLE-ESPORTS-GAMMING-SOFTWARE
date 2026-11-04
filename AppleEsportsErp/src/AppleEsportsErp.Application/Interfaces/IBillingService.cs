using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Billing;

namespace AppleEsportsErp.Application.Interfaces;

public interface IBillingService
{
    Task<PaginatedResult<BillDto>> GetActiveBillsAsync(Guid branchId, int page = 1, int pageSize = 50);
    Task<List<BillDto>> GetDeferredBillsAsync(Guid branchId);
    Task<BillDto> GetBillAsync(Guid branchId, Guid id);
    Task<BillDto> GetBillByNumberAsync(Guid branchId, string billNumber);
    /// <summary>A PS5 entry from the Cash Desk, as a pending gaming bill to be paid like any other.</summary>
    Task<BillDto> CreatePs5BillAsync(Guid branchId, Guid operatorId, Guid shiftId, string actorRole, CreatePs5BillDto dto);
    /// <summary>
    /// <paramref name="actorRole"/> is recorded on the audit entry. It used to be hardcoded to
    /// SuperAdmin regardless of who applied it, which made the audit trail unable to answer the
    /// one question a disputed discount is ever asked: who authorised this.
    /// </summary>
    Task<BillDto> ApplyDiscountAsync(Guid branchId, Guid actorId, string actorRole, Guid id, ApplyDiscountDto dto);
    Task<BillDto> ProcessPaymentAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid id, ProcessPaymentDto dto);

    /// <summary>Corrects only the payment method on an already-completed bill. See
    /// BillingService.EditPaymentMethodAsync for why Wallet is refused and how the cash delta
    /// is booked to today's register rather than rewriting the original payment day.</summary>
    Task<BillDto> EditPaymentMethodAsync(Guid branchId, Guid actorId, string actorRole, Guid id, EditPaymentMethodDto dto);
    Task<BillDto> RemoveBillItemAsync(Guid branchId, Guid operatorId, Guid billId, Guid billItemId);
}
