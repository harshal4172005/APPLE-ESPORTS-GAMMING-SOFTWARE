using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Credits;

namespace AppleEsportsErp.Application.Interfaces;

public interface ICreditService
{
    Task<PaginatedResult<CreditDto>> GetCreditsAsync(Guid branchId, string status = "pending", int page = 1, int pageSize = 50);
    Task<CreditDto> ClearCreditAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid creditId, ClearCreditDto dto);

    Task<PaginatedResult<MemberTopUpCreditDto>> GetMemberTopUpCreditsAsync(Guid branchId, string status = "pending", int page = 1, int pageSize = 50);
    Task<MemberTopUpCreditDto> ClearMemberTopUpCreditAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid creditId, ClearCreditDto dto);
}
