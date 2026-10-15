using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Credits;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Api.Extensions;

namespace AppleEsportsErp.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[BranchIsolation]
public class CreditsController : ControllerBase
{
    private readonly ICreditService _creditService;

    public CreditsController(ICreditService creditService)
    {
        _creditService = creditService;
    }

    private Guid GetBranchId() => Guid.Parse(HttpContext.Items["BranchId"]!.ToString()!);

    /// <summary>
    /// Money customers still owe this branch.
    ///
    /// There used to be a debugging leftover here that wrote any failure to
    /// c:\Users\harsh\Desktop\credit_error.txt - one developer's own desktop, on one machine.
    /// On every other PC in the world that folder does not exist, so the write threw
    /// DirectoryNotFoundException from inside the catch block: the real error was destroyed
    /// and replaced with a meaningless one about a missing path.
    ///
    /// That is the worst possible place for a hardcoded path. It only ever ran when something
    /// had already gone wrong, and it guaranteed nobody could find out what. Removed
    /// entirely - unhandled exceptions already go to the branch's own log through the global
    /// handler, which is where somebody would actually look.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCredits([FromQuery] string status = "pending", [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _creditService.GetCreditsAsync(GetBranchId(), status, page, pageSize);
        return Ok(ApiResponse<PaginatedResult<CreditDto>>.Ok(result));
    }

    [HttpPost("{id}/clear")]
    public async Task<IActionResult> ClearCredit(Guid id, [FromBody] ClearCreditDto dto)
    {
        var result = await _creditService.ClearCreditAsync(
            GetBranchId(),
            await this.GetOperatorIdAsync(),
            await this.GetShiftIdAsync(),
            id,
            dto
        );
        return Ok(ApiResponse<CreditDto>.Ok(result));
    }

    /// <summary>A member's wallet top-up given on credit - separate from bill credits above,
    /// see MemberTopUpCredit's own docstring for why.</summary>
    [HttpGet("member-topups")]
    public async Task<IActionResult> GetMemberTopUpCredits([FromQuery] string status = "pending", [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _creditService.GetMemberTopUpCreditsAsync(GetBranchId(), status, page, pageSize);
        return Ok(ApiResponse<PaginatedResult<MemberTopUpCreditDto>>.Ok(result));
    }

    [HttpPost("member-topups/{id}/clear")]
    public async Task<IActionResult> ClearMemberTopUpCredit(Guid id, [FromBody] ClearCreditDto dto)
    {
        var result = await _creditService.ClearMemberTopUpCreditAsync(
            GetBranchId(),
            await this.GetOperatorIdAsync(),
            await this.GetShiftIdAsync(),
            id,
            dto
        );
        return Ok(ApiResponse<MemberTopUpCreditDto>.Ok(result));
    }
}
