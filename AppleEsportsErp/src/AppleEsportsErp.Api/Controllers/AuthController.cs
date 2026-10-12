using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppleEsportsErp.Api.Extensions;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.DTOs.Auth;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Infrastructure.Configuration;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>
/// Authentication controller — maps from auth.routes.js + auth.controller.js.
/// SOP §6: Login System (Admin + Operator flows)
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>SOP §6.2: Super Admin Login — POST /api/auth/admin/login</summary>
    [HttpPost("admin/login")]
    [AllowAnonymous]
    public async Task<IActionResult> AdminLogin([FromBody] AdminLoginDto dto)
    {
        var result = await _authService.LoginAdminAsync(dto);
        SetAuthCookies(result.AccessToken, result.RefreshToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    /// <summary>SOP §6.3: Operator Login — POST /api/auth/operator/login</summary>
    [HttpPost("operator/login")]
    [AllowAnonymous]
    public async Task<IActionResult> OperatorLogin([FromBody] OperatorLoginDto dto)
    {
        var result = await _authService.LoginOperatorAsync(dto);
        SetAuthCookies(result.AccessToken, result.RefreshToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    /// <summary>Member Login — POST /api/auth/member/login</summary>
    [HttpPost("member/login")]
    [AllowAnonymous]
    public async Task<IActionResult> MemberLogin([FromBody] MemberLoginDto dto)
    {
        var result = await _authService.LoginMemberAsync(dto);
        SetAuthCookies(result.AccessToken, result.RefreshToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    /// <summary>SOP §10: Logout — POST /api/auth/logout</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutDto? dto = null)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role)!;
        var shiftIdClaim = User.FindFirstValue("shiftId");
        var shiftId = string.IsNullOrEmpty(shiftIdClaim) ? (Guid?)null : Guid.Parse(shiftIdClaim);

        await _authService.LogoutAsync(userId, role, shiftId, dto?.ClosesTradingDay ?? false);

        // The client cannot clear an HttpOnly cookie itself, so without this the browser
        // keeps presenting a valid token and the next visit silently logs straight back in.
        ClearAuthCookies();

        return Ok(ApiResponse.Ok());
    }

    /// <summary>
    /// Clears this browser's cookies with no other side effect — POST /api/auth/session/clear.
    ///
    /// For exactly the case <c>/auth/logout</c> is wrong for: a login portal (Admin or Super
    /// Admin) discarding a stale session it found on mount, because that role does not belong
    /// on this portal. It used to call <c>/auth/logout</c> for this, which for an Operator
    /// means <see cref="IAuthService.LogoutAsync"/> - closing their active shift with no drawer
    /// count, no EOD, and no warning, just from visiting the wrong login page. Nobody asked to
    /// end a shift; they asked to look at a different login screen. This clears the cookie the
    /// page cannot touch itself and nothing else - nobody's shift, online status, or anything
    /// server-side changes.
    /// </summary>
    [HttpPost("session/clear")]
    [Authorize]
    public IActionResult ClearSession()
    {
        ClearAuthCookies();
        ClearAdminSwitchCookie();
        return Ok(ApiResponse.Ok());
    }

    /// <summary>Refresh token — POST /api/auth/refresh</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenDto? dto = null)
    {
        // The browser client keeps its refresh token in an HttpOnly cookie and so cannot put
        // it in the body — it posts an empty object. Non-browser callers may still send one.
        var refreshToken = !string.IsNullOrWhiteSpace(dto?.RefreshToken)
            ? dto!.RefreshToken
            : Request.Cookies["refreshToken"];

        if (string.IsNullOrWhiteSpace(refreshToken))
            return Unauthorized(ApiResponse.Fail("No refresh token supplied.", "NO_REFRESH_TOKEN"));

        var result = await _authService.RefreshAccessTokenAsync(refreshToken);
        SetAuthCookies(result.AccessToken);
        return Ok(ApiResponse<TokenResponseDto>.Ok(result));
    }

    /// <summary>SOP §19: Get current user — GET /api/auth/me</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role)!;

        var result = await _authService.GetCurrentUserAsync(userId, role);
        return Ok(ApiResponse<UserProfileDto>.Ok(result));
    }

    /// <summary>SOP §6.3 Step 2: Get branches — GET /api/auth/branches</summary>
    [HttpGet("branches")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBranches()
    {
        var branches = await _authService.GetActiveBranchesAsync();
        return Ok(ApiResponse<IEnumerable<BranchListItemDto>>.Ok(branches));
    }

    /// <summary>SOP §6.3 Step 3: Get operators — GET /api/auth/operators/{branchId}</summary>
    [HttpGet("operators/{branchId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBranchOperators(Guid branchId)
    {
        var operators = await _authService.GetBranchOperatorsAsync(branchId);
        return Ok(ApiResponse<IEnumerable<OperatorListItemDto>>.Ok(operators));
    }

    /// <summary>SOP §11: Force logout — POST /api/auth/force-logout/{id}</summary>
    [HttpPost("force-logout/{id:guid}")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> ForceLogout(Guid id)
    {
        var adminId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.ForceLogoutAsync(adminId, id);
        return Ok(ApiResponse<ForceLogoutResponseDto>.Ok(result));
    }

    [HttpGet("check-setup")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckSetupStatus()
    {
        var result = await _authService.CheckSetupStatusAsync();
        return Ok(ApiResponse<CheckSetupResponseDto>.Ok(result));
    }

    [HttpPost("setup-master")]
    [AllowAnonymous]
    public async Task<IActionResult> SetupMasterAccount([FromBody] SetupMasterDto dto)
    {
        var result = await _authService.SetupMasterAccountAsync(dto);
        SetAuthCookies(result.AccessToken, result.RefreshToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    [HttpPost("setup-operator")]
    [AllowAnonymous]
    public async Task<IActionResult> SetupOperatorAccount([FromBody] SetupOperatorDto dto)
    {
        var result = await _authService.SetupOperatorAccountAsync(dto);
        SetAuthCookies(result.AccessToken, result.RefreshToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    /// <summary>
    /// A member's reset link always points at Head Office (AppUrlProvider - that is the only
    /// address that means anything in an inbox), but a member requesting one does it from
    /// whichever branch's own PC they happen to be sitting at. Minting the token locally and
    /// then telling Head Office about it (ShareMemberResetTokenAsync) meant the token's very
    /// existence depended on that branch's own sync reaching Head Office - which this whole
    /// investigation found broken in two separate, compounding ways in one evening (a field
    /// missing from what gets synced down, then missing from the fingerprint that decides
    /// whether to sync at all). Both are now fixed, but the dependency itself is still fragile:
    /// any future branch-sync gap breaks password resets again, silently, the same way.
    ///
    /// For a member specifically, this branch now hands the request straight to Head Office
    /// instead of minting anything locally - Head Office is the only place the link ever gets
    /// checked against anyway, so there is nothing to mint here and nothing to keep in sync.
    /// Operators and admins are untouched: they reset at the counter they are standing in front
    /// of, not from any branch.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        var proxied = dto.AccountType == "member" && !_configuration.IsHeadOffice()
            && await TryProxyToHeadOfficeAsync("forgot-password", dto);

        if (!proxied)
            await _authService.InitiatePasswordResetAsync(dto.Email, dto.AccountType);

        return Ok(ApiResponse<object>.Ok(new { message = "If that email exists, a reset link has been sent." }));
    }

    /// <summary>
    /// Best-effort only: a branch with no internet right now must not block a member who is
    /// standing right there asking for a reset. Falls back to minting locally (the pre-existing
    /// behaviour) on any failure - worse than reaching Head Office directly, but never worse
    /// than before this existed.
    /// </summary>
    private async Task<bool> TryProxyToHeadOfficeAsync(string action, object dto)
    {
        var headOffice = _configuration["Sync:HeadOfficeUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(headOffice)) return false;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var response = await client.PostAsync(
                $"{headOffice}/api/auth/{action}",
                new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach Head Office to proxy a member {Action} request - minting locally instead.", action);
            return false;
        }
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        await _authService.CompletePasswordResetAsync(dto);
        return Ok(ApiResponse<object>.Ok(new { message = "Password reset successfully." }));
    }

    [HttpPost("change-credentials")]
    [Authorize(Roles = "super_admin,admin")]
    public async Task<IActionResult> ChangeCredentials([FromBody] ChangeCredentialsDto dto)
    {
        await _authService.ChangeCredentialsAsync(dto.UserId, dto);
        return Ok(ApiResponse<object>.Ok(new { message = "Credentials updated successfully." }));
    }

    /// <summary>Verify admin password — POST /api/auth/verify-admin</summary>
    [HttpPost("verify-admin")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyAdmin([FromBody] VerifyAdminDto dto)
    {
        var isValid = await _authService.VerifyAdminPasswordAsync(dto.Password);
        if (!isValid)
        {
            return BadRequest(ApiResponse.Fail("Invalid admin password", "INVALID_ADMIN_PASSWORD"));
        }
        return Ok(ApiResponse.Ok());
    }

    /// <summary>
    /// Emergency Offline JWT Generation — POST /api/auth/emergency-token.
    /// Returns a 30-day signed JWT embedding the caller's role, branchId, and dashboard permissions.
    /// The client stores this in IndexedDB behind a 4-digit PIN for offline operation.
    /// </summary>
    [HttpPost("emergency-token")]
    [Authorize(Policy = "OperatorOrAdmin")]
    public async Task<IActionResult> GenerateEmergencyToken()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role)!;
        var branchId = User.FindFirstValue("branchId");
        var dashboardPermissions = User.FindFirstValue("dashboardPermissions");

        var token = await _authService.GenerateEmergencyTokenAsync(userId, role, branchId, dashboardPermissions);
        return Ok(new { token });
    }

    /// <summary>SOP §22: Admin Quick-Switch Available</summary>
    [HttpGet("admin-switch/available")]
    [Authorize(Roles = Roles.Operator)]
    public async Task<IActionResult> GetAvailableAdminsForSwitch()
    {
        // A PIN set up minutes ago must show up the next time this opens, not whenever the
        // embedded browser's disk cache happens to decide the old empty answer expired.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        var result = await _authService.GetAvailableAdminsForSwitchAsync();
        return Ok(ApiResponse<IEnumerable<AvailableAdminDto>>.Ok(result));
    }

    /// <summary>
    /// SOP §22: Admin Quick-Switch In.
    ///
    /// Writes the elevated identity into its own <c>adminSwitchToken</c> cookie, entirely
    /// separate from <c>accessToken</c>/<c>refreshToken</c>. Those two must never be touched
    /// here: they were previously overwritten with the switch's own access token and an
    /// unrefreshable "refresh token" (the same JWT signed for the access-token key, which
    /// the refresh endpoint validates against a *different* key and always rejects) - so the
    /// operator's real 7-day session was destroyed on switch-in and unrecoverable the moment
    /// anything triggered a refresh, which is what "the station just stops syncing" actually
    /// was. Leaving the operator's cookies alone means their real session is exactly as
    /// healthy after a switch as before one, whether this ends cleanly or not.
    /// </summary>
    [HttpPost("admin-switch/in")]
    [Authorize(Roles = Roles.Operator)]
    public async Task<IActionResult> AdminSwitchIn([FromBody] AdminSwitchInDto dto)
    {
        // Require that the request is coming from an authenticated Operator
        var shiftIdClaim = User.FindFirstValue("shiftId");
        if (string.IsNullOrEmpty(shiftIdClaim))
            return Unauthorized(ApiResponse.Fail("Must be inside an active shift."));

        dto.ShiftId = Guid.Parse(shiftIdClaim);
        var result = await _authService.AdminSwitchInAsync(dto);
        SetAdminSwitchCookie(result.AccessToken);
        return Ok(ApiResponse<LoginResponseDto>.Ok(result));
    }

    /// <summary>
    /// SOP §22: Admin Quick-Switch Out. Deletes the <c>adminSwitchToken</c> cookie - nothing
    /// else to do, since switch-in never touched the operator's own cookies. The operator's
    /// session resumes on the very next request with no re-login and no gap.
    /// </summary>
    [HttpPost("admin-switch/out")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> AdminSwitchOut()
    {
        var adminId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var shiftIdClaim = User.FindFirstValue("shiftId");

        // Ensure this is actually a switched-in token
        var isSwitchedAdmin = User.FindFirstValue("isSwitchedAdmin");
        if (isSwitchedAdmin != "true")
            return BadRequest(ApiResponse.Fail("Token is not an admin switch token."));

        if (!string.IsNullOrEmpty(shiftIdClaim))
        {
            await _authService.AdminSwitchOutAsync(adminId, Guid.Parse(shiftIdClaim));
        }

        ClearAdminSwitchCookie();
        return Ok(ApiResponse.Ok());
    }

    /// <summary>
    /// Unconditionally deletes a stale <c>adminSwitchToken</c> cookie, with no authentication
    /// required. Exists for exactly one case: the switch token itself expired (it is
    /// deliberately short-lived, 2 hours) while still switched in, so the station can no
    /// longer authenticate a normal <c>admin-switch/out</c> call to clear it. The operator's
    /// own <c>accessToken</c>/<c>refreshToken</c> cookies were never touched by the switch, so
    /// once this clears the stale cookie the very next request resumes as the operator with
    /// no re-login - this endpoint deletes a cookie and nothing else, so there is nothing here
    /// that requires proving who is asking.
    /// </summary>
    [HttpPost("admin-switch/clear-cookie")]
    [AllowAnonymous]
    public IActionResult ClearAdminSwitchCookieEndpoint()
    {
        ClearAdminSwitchCookie();
        return Ok(ApiResponse.Ok());
    }

    /// <summary>
    /// An Admin confirms their own PIN before switching into another branch's data. Access was
    /// never actually the gap - BranchIsolationAttribute already lets Admin reach any branch,
    /// same as Super Admin - this exists so the switch leaves an accountability record instead
    /// of happening silently. Super Admin is deliberately not required to call this: their
    /// free branch switching is pre-existing, unchanged behaviour, not a new gate.
    /// </summary>
    [HttpPost("branches/switch-confirm")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ConfirmBranchSwitch([FromBody] ConfirmBranchSwitchDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _authService.ConfirmBranchSwitchAsync(userId, dto.AccessPin, dto.BranchId);
        return Ok(ApiResponse.Ok());
    }

    private void ClearAuthCookies()
    {
        // Must match the attributes the cookies were written with, or the browser treats
        // them as different cookies and quietly keeps the originals.
        var expired = new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Expires = DateTimeOffset.UnixEpoch,
        };

        Response.Cookies.Append("accessToken", string.Empty, expired);
        Response.Cookies.Append("refreshToken", string.Empty, expired);
    }

    /// <summary>
    /// Deliberately short-lived (2h, vs. the operator's own 24h/7d pair) - a switch is a
    /// supervised, in-person action, not a login, so it should not quietly outlast the person
    /// who started it. Never carries a refresh token: nothing here is meant to renew itself.
    /// </summary>
    private void SetAdminSwitchCookie(string accessToken)
    {
        Response.Cookies.Append("adminSwitchToken", accessToken, new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddHours(2)
        });
    }

    private void ClearAdminSwitchCookie()
    {
        Response.Cookies.Append("adminSwitchToken", string.Empty, new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Expires = DateTimeOffset.UnixEpoch,
        });
    }

    private void SetAuthCookies(string accessToken, string? refreshToken = null)
    {
        var isSecure = Request.IsHttps;

        Response.Cookies.Append("accessToken", accessToken, new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            Secure = isSecure,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddHours(24)
        });

        if (!string.IsNullOrEmpty(refreshToken))
        {
            Response.Cookies.Append("refreshToken", refreshToken, new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = isSecure,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(7)
            });
        }
    }
}


