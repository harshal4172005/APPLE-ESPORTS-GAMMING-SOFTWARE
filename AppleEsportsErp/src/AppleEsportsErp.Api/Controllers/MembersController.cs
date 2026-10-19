using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Api.Extensions;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Members;
using AppleEsportsErp.Application.Interfaces;
using System.Security.Claims;

namespace AppleEsportsErp.Api.Controllers;

[ApiController]
[Route("api/members")]
[Authorize]
[BranchIsolation]
public class MembersController : ControllerBase
{
    private readonly IMemberService _memberService;
    private readonly IWalletService _walletService;
    private readonly AppleEsportsErp.Infrastructure.Data.AppDbContext _db;
    private readonly AppleEsportsErp.Api.Services.IRemoteBranchControl _remote;

    public MembersController(
        IMemberService memberService,
        IWalletService walletService,
        AppleEsportsErp.Infrastructure.Data.AppDbContext db,
        AppleEsportsErp.Api.Services.IRemoteBranchControl remote)
    {
        _memberService = memberService;
        _walletService = walletService;
        _db = db;
        _remote = remote;
    }

    private Guid GetBranchId()
    {
        var val = HttpContext.Items["BranchId"]?.ToString();
        return string.IsNullOrEmpty(val) ? Guid.Empty : Guid.Parse(val);
    }

    /// <summary>Same reasoning as BillingController.SendToBranchAsync: turns a Head Office
    /// instruction into a queued command the branch itself carries out.</summary>
    private async Task<IActionResult> SendToBranchAsync(
        Guid branchId, string commandType, object payload, Guid requestedByUserId, CancellationToken ct)
    {
        var receipt = await _remote.SendAsync(branchId, commandType, payload, requestedByUserId, ct);

        return Accepted(ApiResponse<object>.Ok(new
        {
            queued = true,
            commandId = receipt.CommandId,
            branchIsReporting = receipt.BranchIsReporting,
            message = receipt.Message,
        }));
    }

    [HttpGet]
    public async Task<IActionResult> GetMembers([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] bool includeDeleted = false)
    {
        var result = await _memberService.GetMembersAsync(GetBranchId(), search, page, pageSize, includeDeleted);
        return Ok(ApiResponse<PaginatedResult<MemberDto>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMemberById(Guid id)
    {
        var result = await _memberService.GetMemberByIdAsync(id);
        return Ok(ApiResponse<MemberDto>.Ok(result));
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> GetMemberHistory(Guid id, [FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate)
    {
        var result = await _memberService.GetMemberHistoryAsync(id, fromDate, toDate);
        return Ok(ApiResponse<List<MemberHistoryEntryDto>>.Ok(result));
    }

    [HttpGet("phone/{mobileNumber}")]
    public async Task<IActionResult> GetMemberByMobile(string mobileNumber)
    {
        var result = await _memberService.GetMemberByMobileAsync(mobileNumber);
        return Ok(ApiResponse<MemberDto>.Ok(result));
    }

    [HttpPost]
    public async Task<IActionResult> RegisterMember([FromBody] RegisterMemberDto dto)
    {
        var result = await _memberService.RegisterMemberAsync(GetBranchId(), (await this.GetOperatorIdAsync()), dto);
        return Ok(ApiResponse<MemberDto>.Ok(result));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateMember(Guid id, [FromBody] UpdateMemberDto dto)
    {
        // A debug Console.WriteLine used to print every member's name and login details here on
        // every edit, straight into the branch's service log where they sat in plain text.
        var result = await _memberService.UpdateMemberAsync(GetBranchId(), (await this.GetOperatorIdAsync()), id, dto);
        return Ok(ApiResponse<MemberDto>.Ok(result));
    }

    /// <summary>
    /// Removes a member. Super Admin only.
    ///
    /// It was open to any signed-in operator, which put the single most destructive action in
    /// the system on the same screen as everyday counter work. A member carries a wallet
    /// balance, a credit history and a share of every End of Day they have ever appeared in, so
    /// removing one is not like clearing a typo - and a counter is a busy place where the wrong
    /// row gets clicked. There is also no branch check on the way in, so an operator at one shop
    /// could remove a member who belongs to another.
    ///
    /// Nothing legitimate is lost by restricting it. Suspending a member is the operator-level
    /// action for somebody who should not be playing, it is reversible, and it keeps the money
    /// trail intact.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> DeleteMember(Guid id)
    {
        await _memberService.DeleteMemberAsync(GetBranchId(), (await this.GetOperatorIdAsync()), id);
        return Ok(ApiResponse<object>.Ok(null));
    }

    /// <summary>Super Admin only: directly override any wallet balance / lifetime stat on a member's profile.</summary>
    [HttpPut("{id:guid}/admin-edit")]
    [Authorize(Policy = "Dashboard:member_value_edit")]
    public async Task<IActionResult> AdminEditValues(Guid id, [FromBody] AdminEditMemberValuesDto dto, CancellationToken ct)
    {
        var adminId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // A balance written into Head Office's own copy of this member is invisible to the
        // gaming PC at the counter, which only ever checks the branch's own row - so this has
        // to travel there and be applied by the branch itself, the same as a discount or a
        // payment. See RemoteBranchControl's BranchCommands.AdminEditMemberValues.
        if (_remote.MustTravel)
        {
            var homeBranchId = await _db.Set<AppleEsportsErp.Domain.Entities.Member>().AsNoTracking()
                .Where(m => m.Id == id).Select(m => m.HomeBranchId).FirstOrDefaultAsync(ct);

            if (homeBranchId is null || homeBranchId == Guid.Empty)
                return NotFound(ApiResponse<object>.Fail(
                    "Head Office does not know which branch this member belongs to, so this edit " +
                    "has nowhere to be sent.", "MEMBER_HOME_BRANCH_UNKNOWN"));

            return await SendToBranchAsync(homeBranchId.Value, AppleEsportsErp.Api.Services.BranchCommands.AdminEditMemberValues, new
            {
                memberId = id,
                dto,
                adminId,
                adminName = User.FindFirstValue(ClaimTypes.Name),
            }, adminId, ct);
        }

        var result = await _memberService.AdminEditValuesAsync(GetBranchId(), adminId, id, dto);
        return Ok(ApiResponse<MemberDto>.Ok(result));
    }

    /// <summary>Free bonus into a member's wallet - never counted as money received. Super
    /// Admin always; an Admin only with the member_extra_bonus permission (same check as
    /// BillingController.ApplyDiscount, since the Dashboard policy lets every Admin through).</summary>
    [HttpPost("{id:guid}/bonus")]
    [Idempotent]
    public async Task<IActionResult> GiveBonus(
        Guid id, [FromBody] AppleEsportsErp.Application.DTOs.Wallets.GiveBonusDto dto, CancellationToken ct)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        var allowed = role == Roles.SuperAdmin;
        if (!allowed && role == Roles.Admin && User.FindFirstValue("dashboardPermissions") is { Length: > 0 } perms)
        {
            try
            {
                allowed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(perms)
                    ?.GetValueOrDefault("member_extra_bonus") == true;
            }
            catch { }
        }
        if (!allowed) return Forbid();

        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var actorName = User.FindFirstValue(ClaimTypes.Name) ?? role!;

        if (_remote.MustTravel)
        {
            var homeBranchId = await _db.Set<AppleEsportsErp.Domain.Entities.Member>().AsNoTracking()
                .Where(m => m.Id == id).Select(m => m.HomeBranchId).FirstOrDefaultAsync(ct);

            if (homeBranchId is null || homeBranchId == Guid.Empty)
                return NotFound(ApiResponse<object>.Fail(
                    "Head Office does not know which branch this member belongs to, so the bonus " +
                    "has nowhere to be sent.", "MEMBER_HOME_BRANCH_UNKNOWN"));

            return await SendToBranchAsync(homeBranchId.Value, AppleEsportsErp.Api.Services.BranchCommands.GiveMemberBonus,
                new { memberId = id, dto, actorId, actorName }, actorId, ct);
        }

        var result = await _walletService.GiveBonusAsync(GetBranchId(), id, dto, actorId, actorName);
        return Ok(ApiResponse<AppleEsportsErp.Application.DTOs.Wallets.WalletTransactionDto>.Ok(result));
    }

    /// <summary>Member self-login — POST /api/members/login (no auth required)</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> MemberLogin([FromBody] MemberLoginDto dto)
    {
        var result = await _memberService.LoginMemberAsync(dto);
        return Ok(ApiResponse<MemberLoginResponseDto>.Ok(result));
    }
}

