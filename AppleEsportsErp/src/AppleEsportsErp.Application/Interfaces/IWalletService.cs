using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Wallets;

namespace AppleEsportsErp.Application.Interfaces;

public interface IWalletService
{
    Task<WalletTransactionDto> TopUpWalletAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid memberId, TopUpWalletDto dto, bool isSuperAdmin);
    Task<WalletTransactionDto> DeductWalletAsync(Guid branchId, Guid operatorId, Guid? shiftId, Guid memberId, DeductWalletDto dto, bool commit = true);
    Task<PaginatedResult<WalletTransactionDto>> GetWalletHistoryAsync(Guid memberId, int page = 1, int pageSize = 50);
    Task<WalletTransactionDto> GiveBonusAsync(Guid branchId, Guid memberId, GiveBonusDto dto, Guid actorId, string actorName, Guid? transactionId = null);
}
