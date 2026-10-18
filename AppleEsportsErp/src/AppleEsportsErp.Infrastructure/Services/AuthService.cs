using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Auth;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Infrastructure.Identity;
using BCryptNet = BCrypt.Net.BCrypt;

namespace AppleEsportsErp.Infrastructure.Services;

/// <summary>
/// Full AuthService implementation — 1:1 mapping from auth.service.js.
/// SOP §6: Login System (Admin + Operator flows)
/// SOP §21: Security Model (hashing, tokens, device tracking)
/// SOP §10: Shift Management (auto shift start on login)
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly IAuditService _audit;
    private readonly ILogger<AuthService> _logger;
    private readonly ITokenRevocationService _tokenRevocation;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly IOutboxService _outbox;

    /// <summary>
    /// Sends a member's freshly minted reset token up to Head Office.
    ///
    /// The link in their email now points at Head Office, because that is the only address that
    /// means anything in an inbox - see AppUrlProvider.ResetLinkBaseUrl. Head Office therefore
    /// has to be able to recognise the token when they arrive, and it cannot: the branch minted
    /// it into its own local database, and Member updates were never part of sync at all. Without
    /// this the link opens and is refused as invalid, which is a worse failure than the shop-LAN
    /// link it replaced, because it looks like it should have worked.
    ///
    /// Only the token and its expiry travel. Head Office already has the member itself from
    /// member.created; this is the one field it is missing, and it is spent the moment it is used.
    /// </summary>
    private async Task ShareMemberResetTokenAsync(Member member, string token, DateTimeOffset expiry)
    {
        if (member.HomeBranchId is not { } branchId) return;

        try
        {
            await _outbox.RecordEventAsync(branchId, "Member", member.Id, "member.reset_requested", new
            {
                memberId = member.Id,
                email = member.Email,
                resetToken = token,
                resetTokenExpiry = expiry,
            });
        }
        catch (Exception ex)
        {
            // Never let this take down a password reset. The customer still gets their mail; the
            // link fails at Head Office instead, which is visible and recoverable - unlike an
            // exception surfacing out of a login endpoint as a 500.
            _logger.LogError(ex, "Could not queue the reset token for member {MemberId} to Head Office.", member.Id);
        }
    }
    private readonly IAppUrlProvider _appUrls;
    private const int SALT_ROUNDS = 12;

    // Brute-force lockout — SOP-adjacent security hardening: 5 wrong passwords locks the
    // account for 15 minutes and auto-emails a reset link so a legitimate user isn't stuck
    // waiting out the clock.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IAdminNotifier _adminNotifier;
    private readonly IShiftTakeoverService _takeover;
    private readonly IHubNotificationService _hubNotifications;

    public AuthService(AppDbContext db, JwtTokenService jwt, IAuditService audit, ILogger<AuthService> logger, ITokenRevocationService tokenRevocation, IEmailService emailService, IConfiguration configuration, IAppUrlProvider appUrls, IAdminNotifier adminNotifier, IShiftTakeoverService takeover, IOutboxService outbox, IHubNotificationService hubNotifications)
    {
        _adminNotifier = adminNotifier;
        _takeover = takeover;
        _db = db;
        _jwt = jwt;
        _audit = audit;
        _logger = logger;
        _tokenRevocation = tokenRevocation;
        _emailService = emailService;
        _configuration = configuration;
        _outbox = outbox;
        _appUrls = appUrls;
        _hubNotifications = hubNotifications;
    }

    /// <summary>
    /// SOP §6.2: Super Admin Login — Email/Password → Validate credentials, permissions, device, account status.
    /// SOP: Super Admin session persists until logout/timeout/password reset/forced signout.
    /// Maps from: auth.service.js loginAdmin()
    /// </summary>
    public async Task<LoginResponseDto> LoginAdminAsync(AdminLoginDto dto)
    {
        // 1. Find admin user. Case-insensitive: Postgres `text` equality is case-sensitive, so
        // an admin stored as "Manager@Apple.com" could never log in typing "manager@apple.com"
        // - it fell straight through to "Invalid email/username or password" with no hint why.
        var email = dto.Email.Trim().ToLower();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
        if (user != null)
        {
            // 2. Check account status
            if (user.Status != UserStatus.Active)
                throw new AuthorizationException($"Account is {user.Status}. Contact system administrator.", "ACCOUNT_INACTIVE");

            // 2b. Brute-force lockout
            if (IsLocked(user.LockedUntil))
                throw new AuthorizationException(LockoutMessage(user.LockedUntil!.Value), "ACCOUNT_LOCKED");

            // 3. Verify password — SOP §21.1: Password Hashing = YES
            if (!BCryptNet.Verify(dto.Password, user.PasswordHash))
            {
                user.FailedAttempts++;
                if (user.FailedAttempts >= MaxFailedAttempts)
                {
                    user.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                    var resetToken = Guid.NewGuid().ToString("N");
                    user.ResetToken = resetToken;
                    user.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
                    await _db.SaveChangesAsync();
                    await SendPasswordResetEmailAsync(user.Email, user.FullName, resetToken, isLockout: true);
                    await _audit.LogAsync(new AuditEntry
                    {
                        UserId = user.Id,
                        UserRole = Roles.SuperAdmin,
                        UserName = user.FullName,
                        Action = AuditActions.AccountLocked,
                        Success = false,
                        TargetType = "user",
                        TargetId = user.Id,
                        Details = new { reason = "5 failed password attempts", lockedUntil = user.LockedUntil },
                    });
                }
                else
                {
                    await _db.SaveChangesAsync();
                }

                // Log failed attempt — SOP §22: Audit every critical action
                await _audit.LogAsync(new AuditEntry
                {
                    UserId = user.Id,
                    UserRole = Roles.SuperAdmin,
                    UserName = user.FullName,
                    Action = AuditActions.FailedLogin,
                    Success = false,
                    Details = new { reason = "Invalid password", deviceInfo = dto.DeviceInfo },
                });
                throw new AuthenticationException("Invalid email/username or password", "INVALID_CREDENTIALS");
            }

            // Successful login clears the lockout counter
            user.FailedAttempts = 0;
            user.LockedUntil = null;

            // 4. Generate tokens — Q1 Decision: full claims embedded in JWT
            var claims = new Dictionary<string, string>
            {
                [ClaimTypes.NameIdentifier] = user.Id.ToString(),
                [ClaimTypes.Role] = user.Role,
                [ClaimTypes.Name] = user.FullName,
            };

            if (user.Role == Roles.Admin && !string.IsNullOrEmpty(user.DashboardPermissions))
            {
                claims["dashboardPermissions"] = user.DashboardPermissions;
            }

            var accessToken = _jwt.GenerateAccessToken(claims);
            var refreshToken = _jwt.GenerateRefreshToken(claims);

            // 5. Update last login + device info — SOP §21.1: Device Tracking
            user.LastLogin = DateTimeOffset.UtcNow;
            user.DeviceInfo = dto.DeviceInfo != null ? JsonSerializer.Serialize(dto.DeviceInfo) : null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();

            // 6. Audit log — SOP §22
            await _audit.LogAsync(new AuditEntry
            {
                UserId = user.Id,
                UserRole = user.Role,
                UserName = user.FullName,
                Action = AuditActions.Login,
                Details = new { method = "email_password", deviceInfo = dto.DeviceInfo },
            });

            _logger.LogInformation("{Role} logged in: {Name}", user.Role, user.FullName);

            return new LoginResponseDto
            {
                User = new UserProfileDto
                {
                    Id = user.Id,
                    Email = user.Email,
                    FullName = user.FullName,
                    Role = user.Role,
                    DashboardPermissions = user.DashboardPermissions != null 
                        ? JsonSerializer.Deserialize<object>(user.DashboardPermissions) 
                        : null,
                    Status = user.Status.ToString().ToLowerInvariant(),
                    LastLogin = user.LastLogin,
                },
                AccessToken = accessToken,
                RefreshToken = refreshToken,
            };
        }

        // 1b. Fallback: check if it's an Operator promoted to Global Admin
        var op = await _db.Operators.FirstOrDefaultAsync(o => o.Username == dto.Email && o.IsGlobalAdmin);
        if (op != null)
        {
            if (op.Status != OperatorStatus.Active)
                throw new AuthorizationException($"Account is {op.Status}. Contact system administrator.", "ACCOUNT_INACTIVE");

            if (IsLocked(op.LockedUntil))
                throw new AuthorizationException(LockoutMessage(op.LockedUntil!.Value), "ACCOUNT_LOCKED");

            if (!BCryptNet.Verify(dto.Password, op.PasswordHash))
            {
                op.FailedAttempts++;
                if (op.FailedAttempts >= MaxFailedAttempts)
                {
                    op.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                    var resetToken = Guid.NewGuid().ToString("N");
                    op.ResetToken = resetToken;
                    op.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
                    await _db.SaveChangesAsync();
                    await SendPasswordResetEmailAsync(op.Email, op.FullName, resetToken, isLockout: true);
                    await _audit.LogAsync(new AuditEntry
                    {
                        OperatorId = op.Id,
                        UserRole = Roles.Admin,
                        UserName = op.FullName,
                        Action = AuditActions.AccountLocked,
                        Success = false,
                        TargetType = "operator",
                        TargetId = op.Id,
                        Details = new { reason = "5 failed password attempts", lockedUntil = op.LockedUntil },
                    });
                }
                else
                {
                    await _db.SaveChangesAsync();
                }

                await _audit.LogAsync(new AuditEntry
                {
                    OperatorId = op.Id,
                    UserRole = Roles.Admin,
                    UserName = op.FullName,
                    Action = AuditActions.FailedLogin,
                    Success = false,
                    Details = new { reason = "Invalid password", deviceInfo = dto.DeviceInfo },
                });
                throw new AuthenticationException("Invalid email/username or password", "INVALID_CREDENTIALS");
            }

            op.FailedAttempts = 0;
            op.LockedUntil = null;

            var claims = new Dictionary<string, string>
            {
                [ClaimTypes.NameIdentifier] = op.Id.ToString(),
                [ClaimTypes.Role] = Roles.Admin,
                [ClaimTypes.Name] = op.FullName,
            };

            if (!string.IsNullOrEmpty(op.DashboardPermissions))
            {
                claims["dashboardPermissions"] = op.DashboardPermissions;
            }

            var accessToken = _jwt.GenerateAccessToken(claims);
            var refreshToken = _jwt.GenerateRefreshToken(claims);

            op.LastLogin = DateTimeOffset.UtcNow;
            op.DeviceInfo = dto.DeviceInfo != null ? JsonSerializer.Serialize(dto.DeviceInfo) : null;
            op.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = op.Id,
                UserRole = Roles.Admin,
                UserName = op.FullName,
                Action = AuditActions.Login,
                Details = new { method = "username_password_admin_fallback", deviceInfo = dto.DeviceInfo },
            });

            _logger.LogInformation("{Role} logged in: {Name} (Operator Fallback)", Roles.Admin, op.FullName);

            return new LoginResponseDto
            {
                User = new UserProfileDto
                {
                    Id = op.Id,
                    Email = op.Username, // Map username to email field for frontend compatibility
                    FullName = op.FullName,
                    Role = Roles.Admin,
                    DashboardPermissions = op.DashboardPermissions != null 
                        ? JsonSerializer.Deserialize<object>(op.DashboardPermissions) 
                        : null,
                    Status = op.Status.ToString().ToLowerInvariant(),
                    LastLogin = op.LastLogin,
                },
                AccessToken = accessToken,
                RefreshToken = refreshToken,
            };
        }

        throw new AuthenticationException("Invalid email/username or password", "INVALID_CREDENTIALS");
    }

    /// <summary>
    /// SOP §6.3: Operator Login — Select Branch → Select Profile → Enter PIN → System starts shift.
    /// SOP: Operator CANNOT see other branch data (enforced via branch assignment check).
    /// Maps from: auth.service.js loginOperator()
    /// </summary>
    public async Task<LoginResponseDto> LoginOperatorAsync(OperatorLoginDto dto)
    {
        // 1. Verify branch exists and is active
        var branch = await _db.Branches.FirstOrDefaultAsync(b => b.Id == dto.BranchId);
        if (branch == null)
            throw new NotFoundException("Branch not found", "BRANCH_NOT_FOUND");
        if (branch.Status != BranchStatus.Active)
            throw new AuthorizationException("Branch is currently inactive", "BRANCH_INACTIVE");

        // 2. Find operator assigned to this branch
        var op = await _db.Operators.FirstOrDefaultAsync(
            o => o.Username == dto.Username && o.BranchId == dto.BranchId);
        if (op == null)
            throw new AuthenticationException("Invalid credentials or operator not assigned to this branch", "INVALID_CREDENTIALS");

        // 3. Check operator status — SOP §12: Operator Status Types
        if (op.Status == OperatorStatus.Suspended)
            throw new AuthorizationException("Operator account is suspended. Contact Super Admin.", "OPERATOR_SUSPENDED");
        if (op.Status == OperatorStatus.Disabled)
            throw new AuthorizationException("Operator account is disabled. Contact Super Admin.", "OPERATOR_DISABLED");

        // 3b. Brute-force lockout
        if (IsLocked(op.LockedUntil))
            throw new AuthorizationException(LockoutMessage(op.LockedUntil!.Value), "ACCOUNT_LOCKED");

        // 4. Verify password/PIN
        if (!BCryptNet.Verify(dto.Password, op.PasswordHash))
        {
            op.FailedAttempts++;
            if (op.FailedAttempts >= MaxFailedAttempts)
            {
                op.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                var resetToken = Guid.NewGuid().ToString("N");
                op.ResetToken = resetToken;
                op.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
                await _db.SaveChangesAsync();
                await SendPasswordResetEmailAsync(op.Email, op.FullName, resetToken, isLockout: true);
                await _audit.LogAsync(new AuditEntry
                {
                    OperatorId = op.Id,
                    UserRole = Roles.Operator,
                    UserName = op.FullName,
                    Action = AuditActions.AccountLocked,
                    Success = false,
                    BranchId = dto.BranchId,
                    BranchName = branch.Name,
                    TargetType = "operator",
                    TargetId = op.Id,
                    Details = new { reason = "5 failed password attempts", lockedUntil = op.LockedUntil },
                });
            }
            else
            {
                await _db.SaveChangesAsync();
            }

            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = op.Id,
                UserRole = Roles.Operator,
                UserName = op.FullName,
                Action = AuditActions.FailedLogin,
                Success = false,
                BranchId = dto.BranchId,
                BranchName = branch.Name,
                Details = new { reason = "Invalid password/PIN", deviceInfo = dto.DeviceInfo },
            });
            throw new AuthenticationException("Invalid password or PIN", "INVALID_CREDENTIALS");
        }

        op.FailedAttempts = 0;
        op.LockedUntil = null;

        // 5. Resume this operator's open shift if they already have one, rather than opening a
        //    second. A shift only ends by being closed properly, with the cash counted - so an
        //    operator whose PC lost power mid-shift comes back to a shift still marked Active.
        //    Starting a fresh one would leave the first open forever, with uncounted takings
        //    against it, and every login would add another. There is no counting your way out
        //    of that. Resuming also means the shop opens on time without ringing an admin.
        var shift = await _db.Shifts
            .Where(s => s.OperatorId == op.Id && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .FirstOrDefaultAsync();

        // 5b. Somebody else's shift, still open and untouched for long enough that nobody can be
        //     at that counter. It has to be closed and its drawer counted before this operator
        //     starts trading on top of it — otherwise a second shift opens alongside the first,
        //     the abandoned one dangles with uncounted takings, and the money in the drawer
        //     belongs to two shifts at once.
        var pendingTakeover = await _takeover.GetPendingAsync(dto.BranchId, op.Id);

        var resumedShift = shift is not null;
        var gapSinceLastSeen = TimeSpan.Zero;

        if (resumedShift)
        {
            // How long the shift was unattended, measured from the last thing that actually
            // happened on it rather than from login - a shift opened at 09:00 and abandoned at
            // 23:00 was not "14 hours idle".
            var lastActivity = await _db.Sessions
                .Where(s => s.ShiftId == shift!.Id)
                .MaxAsync(s => (DateTimeOffset?)s.UpdatedAt) ?? shift!.LoginTime;

            gapSinceLastSeen = DateTimeOffset.UtcNow - lastActivity;

            shift!.DeviceInfo = dto.DeviceInfo != null ? JsonSerializer.Serialize(dto.DeviceInfo) : shift.DeviceInfo;
            _logger.LogInformation(
                "Operator {Operator} resumed shift {ShiftId}, which had been unattended for {Gap}.",
                op.FullName, shift.Id, gapSinceLastSeen);
        }
        else if (pendingTakeover is null)
        {
            shift = new Shift
            {
                OperatorId = op.Id,
                BranchId = dto.BranchId,
                LoginTime = DateTimeOffset.UtcNow,
                DeviceInfo = dto.DeviceInfo != null ? JsonSerializer.Serialize(dto.DeviceInfo) : null,
                Status = ShiftStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.Shifts.Add(shift);
        }
        // A takeover is waiting, so no shift is opened here. The login succeeds and the operator
        // gets nothing to trade with: every shift-scoped endpoint refuses them until the drawer
        // in front of them has been counted, and the takeover itself is what issues the shift.
        //
        // A blocking screen alone would not do. It can be refreshed past, closed, or simply
        // never reach the browser, and the one thing that must not happen is a second shift
        // trading over an uncounted drawer. Withholding the shift makes the count unavoidable
        // rather than merely asked for.
        //
        // The exception is an operator resuming their OWN open shift, above: that shift already
        // exists and cannot be taken back. There the blocking screen is all there is.

        // 6. Update operator status to active
        op.Status = OperatorStatus.Active;
        op.LastLogin = DateTimeOffset.UtcNow;
        op.DeviceInfo = dto.DeviceInfo != null ? JsonSerializer.Serialize(dto.DeviceInfo) : null;
        op.IsOnline = true;
        op.UpdatedAt = DateTimeOffset.UtcNow;

        // Whether the drawer is actually open right now - see LoginResponseDto.HasOpenRegister
        // for why this can no longer just be inferred from ResumedShift.
        var hasOpenRegister = await _db.CashRegisters
            .AnyAsync(r => r.BranchId == dto.BranchId && r.Status == CashRegisterStatus.Open);

        await _db.SaveChangesAsync();

        // 7. Generate tokens with branch + permissions embedded — Q1 Decision
        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = op.Id.ToString(),
            [ClaimTypes.Role] = Roles.Operator,
            [ClaimTypes.Name] = op.FullName,
            ["branchId"] = op.BranchId.ToString(),
            ["dashboardPermissions"] = op.DashboardPermissions ?? "{}",
        };

        // No shift, no claim. The lookup that reads this claim falls back to the operator's
        // active shift in the database, so the token stays correct once the takeover issues one
        // and there is nothing to re-sign.
        if (shift is not null)
            claims["shiftId"] = shift.Id.ToString();

        var accessToken = _jwt.GenerateAccessToken(claims);
        var refreshToken = _jwt.GenerateRefreshToken(claims);

        // 8. Audit log — login
        await _audit.LogAsync(new AuditEntry
        {
            OperatorId = op.Id,
            UserRole = Roles.Operator,
            UserName = op.FullName,
            Action = AuditActions.Login,
            BranchId = dto.BranchId,
            BranchName = branch.Name,
            Details = new { shiftId = shift?.Id, loginTime = shift?.LoginTime, deviceInfo = dto.DeviceInfo },
        });

        // 9. Shift start audit — only when one actually started. Logging a shift start for a
        //    login that was held at the door would put a shift in the trail that never existed.
        if (shift is not null)
        {
            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = op.Id,
                UserRole = Roles.Operator,
                UserName = op.FullName,
                Action = AuditActions.ShiftStart,
                BranchId = dto.BranchId,
                BranchName = branch.Name,
                Details = new { shiftId = shift.Id },
            });
        }

        if (pendingTakeover is not null)
        {
            _logger.LogWarning(
                "Operator {Name} logged in at {Branch} to find {Other}'s shift still open after " +
                "{Minutes} minutes. No shift started until they have counted the drawer.",
                op.FullName, branch.Name, pendingTakeover.OutgoingOperatorName, pendingTakeover.UnattendedMinutes);
        }
        else
        {
            _logger.LogInformation("Operator logged in: {Name} @ {Branch}", op.FullName, branch.Name);
        }

        return new LoginResponseDto
        {
            User = new UserProfileDto
            {
                Id = op.Id,
                FullName = op.FullName,
                Username = op.Username,
                Role = Roles.Operator,
                BranchId = op.BranchId,
                BranchName = branch.Name,
                ShiftId = shift?.Id,
                DashboardPermissions = op.DashboardPermissions != null
                    ? JsonSerializer.Deserialize<object>(op.DashboardPermissions)
                    : null,
                Status = op.Status.ToString().ToLowerInvariant(),
                LastLogin = op.LastLogin,
                ActiveShift = shift is null
                    ? null
                    : new ActiveShiftDto { Id = shift.Id, LoginTime = shift.LoginTime },
            },
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ResumedShift = resumedShift,
            UnattendedMinutes = (int)gapSinceLastSeen.TotalMinutes,
            // Ten minutes, so a browser refresh or a quick re-login is not treated as an
            // incident, while a genuine outage or an overnight shutdown always is.
            NeedsGapExplanation = resumedShift && gapSinceLastSeen >= TimeSpan.FromMinutes(10),
            PendingTakeover = pendingTakeover,
            HasOpenRegister = hasOpenRegister,
        };
    }

    /// <summary>
    /// Member Login - Accessible via PC Client Overlay across any branch.
    /// Uses Username, MobileNumber or Email + Password.
    /// </summary>
    public async Task<LoginResponseDto> LoginMemberAsync(MemberLoginDto dto)
    {
        // Find member by Username, MobileNumber, or Email. Email compared case-insensitively,
        // same reason as LoginAdminAsync - Postgres text equality is case-sensitive and a
        // member typing their own email in a different case should not be told it's wrong.
        var identifier = dto.Identifier.Trim();
        var identifierLower = identifier.ToLower();
        var member = await _db.Members.FirstOrDefaultAsync(m =>
            (m.Username != null && m.Username == identifier) ||
            (m.MobileNumber != null && m.MobileNumber == identifier) ||
            (m.Email != null && m.Email.ToLower() == identifierLower));

        if (member == null)
            throw new AuthenticationException("Invalid credentials", "INVALID_CREDENTIALS");

        if (member.Status != MemberStatus.Active)
            throw new AuthorizationException($"Account is {member.Status}. Please contact front desk.", "ACCOUNT_INACTIVE");

        if (IsLocked(member.LockedUntil))
            throw new AuthorizationException(LockoutMessage(member.LockedUntil!.Value), "ACCOUNT_LOCKED");

        if (string.IsNullOrEmpty(member.PasswordHash) || !BCryptNet.Verify(dto.Password, member.PasswordHash))
        {
            member.FailedAttempts++;
            if (member.FailedAttempts >= MaxFailedAttempts)
            {
                member.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                var resetToken = Guid.NewGuid().ToString("N");
                member.ResetToken = resetToken;
                member.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
                await _db.SaveChangesAsync();
                await ShareMemberResetTokenAsync(member, resetToken, member.ResetTokenExpiry.Value);
                await SendPasswordResetEmailAsync(member.Email, member.FullName, resetToken, isLockout: true);
                await _audit.LogAsync(new AuditEntry
                {
                    UserRole = "Member",
                    UserName = member.FullName,
                    Action = AuditActions.AccountLocked,
                    Success = false,
                    TargetType = "member",
                    TargetId = member.Id,
                    BranchId = member.HomeBranchId,
                    Details = new { reason = "5 failed password attempts", lockedUntil = member.LockedUntil },
                });
            }
            else
            {
                await _db.SaveChangesAsync();
            }

            await _audit.LogAsync(new AuditEntry
            {
                UserRole = "Member",
                UserName = member.FullName,
                Action = AuditActions.FailedLogin,
                Success = false,
                TargetType = "member",
                TargetId = member.Id,
                BranchId = member.HomeBranchId,
                Details = new { reason = "Invalid password", deviceInfo = dto.DeviceInfo },
            });
            throw new AuthenticationException("Invalid credentials", "INVALID_CREDENTIALS");
        }

        member.FailedAttempts = 0;
        member.LockedUntil = null;

        // Generate JWT
        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = member.Id.ToString(),
            [ClaimTypes.Role] = "Member",
            [ClaimTypes.Name] = member.FullName,
        };

        var accessToken = _jwt.GenerateAccessToken(claims);
        var refreshToken = _jwt.GenerateRefreshToken(claims);

        member.LastVisit = DateTimeOffset.UtcNow;
        member.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(new AuditEntry
        {
            UserRole = "Member",
            UserName = member.FullName,
            Action = AuditActions.Login,
            Details = new { deviceInfo = dto.DeviceInfo },
        });

        _logger.LogInformation("Member logged in: {Name}", member.FullName);

        return new LoginResponseDto
        {
            User = new UserProfileDto
            {
                Id = member.Id,
                Username = member.Username,
                FullName = member.FullName,
                Role = "Member",
                Status = member.Status.ToString().ToLowerInvariant(),
                LastLogin = member.LastVisit,
            },
            AccessToken = accessToken,
            RefreshToken = refreshToken,
        };
    }

    /// <summary>
    /// SOP §10: Logout — closes shift for operators.
    /// SOP: System records logout time, shift summary, revenue, actions.
    /// Maps from: auth.service.js logout()
    /// </summary>
    /// <summary>
    /// The day's figures, emailed to the owner when the last shift closes.
    ///
    /// Counted over the midnight-to-midnight trading day rather than the shift, because that is
    /// how the money is counted everywhere else - a session that starts at 01:00 belongs to the
    /// calendar day already under way when it started, whoever happened to be on duty.
    ///
    /// Never throws. A summary that cannot be emailed must not stop an operator going home.
    /// </summary>
    /// <summary>
    /// Closes any trading day that is genuinely over and that nobody closed, and sends its report.
    /// A branch still trading past midnight is left entirely alone here, on the owner's explicit
    /// instruction - see the comment inside this method, where that decision actually happens,
    /// for why.
    ///
    /// The end-of-day report used to depend entirely on an operator ticking "last shift of the
    /// day". Ticking it wrongly costs one confusing email. Forgetting it used to leave the
    /// register open indefinitely too - which is where the thirty stale registers already
    /// cleared off the live system came from. Forgetting is much the likelier outcome, because
    /// it takes doing nothing.
    ///
    /// So the day no longer depends on being remembered. The tick still works, and closes the day
    /// early when an operator uses it; this is what happens when nobody does.
    ///
    /// Safe to call repeatedly: it only acts on registers still open from a day that has ended,
    /// and closing them is what stops it acting again.
    /// </summary>
    /// <summary>
    /// How long a branch must have gone without a session starting/stopping, a bill, or a
    /// cash movement before it counts as "quiet" for <see cref="IsBranchGenuinelyClosedForTheNightAsync"/>.
    ///
    /// There is no fixed opening/closing schedule to fall back on - branches trade for however
    /// long customers keep showing up, not to a timetable - so this window is the only safety
    /// margin against a genuine lull mid-session. Kept longer than the 45 minutes first used for
    /// exactly that reason: with no time-of-day floor underneath it, this alone has to be long
    /// enough that nobody mid-café would ever plausibly go quiet for its whole length.
    /// </summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(90);

    /// <summary>
    /// Whether a branch is actually done trading for the night, not merely quiet for a moment
    /// during a normal day - the real gate on force-closing anything still open from a
    /// calendar-stale trading day.
    ///
    /// Now that the trading day ends at midnight (moved from the old 06:00-06:00 boundary),
    /// midnight falls in the middle of real trading hours for any branch open past it - so
    /// "the business day has ended" is no longer a safe signal on its own. Both of the
    /// following must hold instead - purely activity-based, per the owner's own call: branches
    /// have no fixed closing time, so there is no schedule to check against:
    ///
    ///  1. No PC at the branch is <see cref="PcState.Active"/> or <see cref="PcState.AwaitingBilling"/>
    ///     - nobody is actually playing or waiting to be billed right now.
    ///  2. Nothing has actually happened recently - no session starting or stopping, no bill,
    ///     no cash movement - within <see cref="QuietWindow"/>. (1) alone is not enough: a
    ///     branch can be between customers for a few quiet minutes at any hour.
    /// </summary>
    private async Task<bool> IsBranchGenuinelyClosedForTheNightAsync(
        Guid branchId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 1. Nobody currently playing or waiting to be billed.
        var hasLivePc = await _db.Pcs.AnyAsync(
            p => p.BranchId == branchId
              && (p.State == PcState.Active || p.State == PcState.AwaitingBilling),
            cancellationToken);
        if (hasLivePc) return false;

        // 2. Nothing real has happened at the branch inside the quiet window.
        var quietSince = now - QuietWindow;

        var recentSessionActivity = await _db.Sessions.AnyAsync(
            s => s.BranchId == branchId && (s.StartTime >= quietSince || s.UpdatedAt >= quietSince),
            cancellationToken);
        if (recentSessionActivity) return false;

        var recentBill = await _db.Bills.AnyAsync(
            b => b.BranchId == branchId && (b.CreatedAt >= quietSince || b.UpdatedAt >= quietSince),
            cancellationToken);
        if (recentBill) return false;

        var recentCashTransaction = await _db.CashTransactions.AnyAsync(
            c => c.BranchId == branchId && c.CreatedAt >= quietSince,
            cancellationToken);
        if (recentCashTransaction) return false;

        return true;
    }

    public async Task<int> CloseFinishedTradingDaysAsync(CancellationToken cancellationToken = default)
    {
        var today = IndiaTime.BusinessDayOf(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        // Calendar-stale candidates only - strictly earlier than today's trading day. Being
        // calendar-stale is necessary but, since the boundary moved to midnight, no longer
        // sufficient: a branch trading past midnight has calendar-stale registers every single
        // night for as long as it keeps trading. IsBranchGenuinelyClosedForTheNightAsync below
        // is the real gate on whether any of these are actually force-closed this pass.
        var stale = await _db.CashRegisters
            .Where(r => r.BusinessDay < today && r.Status != CashRegisterStatus.Closed)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0) return 0;

        var closedDays = 0;

        foreach (var branchDay in stale.GroupBy(r => new { r.BranchId, r.BusinessDay }))
        {
            try
            {
                // Not yet genuinely closed for the night - still trading, so leave it alone
                // entirely and try again on the next pass. This used to call
                // RolloverOpenRegisterAsync here to keep EodService's day-bucketing correct
                // while trading continued past midnight - reverted on the owner's explicit
                // instruction after it produced duplicate register and shift rows on a branch
                // that was genuinely still trading (Citylight 144Hz, the night this was found):
                // this 15-minute sweep and something else touching the same register at close
                // to the same moment were never made to exclude each other, so both could see
                // "nothing open for today yet" and both create one. Nothing here now closes or
                // moves anything for an active shift - only the operator's own "last shift of
                // the day" button does, same as before this rollover existed at all. The
                // known cost: a branch trading past midnight keeps yesterday's BusinessDay on
                // its register until that button is pressed, which is exactly the bucketing
                // gap RolloverOpenRegisterAsync was written to close - accepted deliberately in
                // exchange for never touching a shift that is still actually in use.
                if (!await IsBranchGenuinelyClosedForTheNightAsync(branchDay.Key.BranchId, now, cancellationToken))
                {
                    continue;
                }

                foreach (var register in branchDay)
                {
                    register.Status = CashRegisterStatus.Closed;
                    register.ClosedAt = DateTimeOffset.UtcNow;

                    // Left uncounted on purpose where nobody counted it. Writing a figure in would
                    // invent a count that never happened; an empty one keeps the money visibly
                    // unreconciled, which is the truth and the thing worth following up.
                    if (register.PhysicalCashCounted == null)
                    {
                        register.MismatchReason = string.IsNullOrWhiteSpace(register.MismatchReason)
                            ? "Nobody counted this drawer or marked the last shift. The day was closed automatically once it was over."
                            : register.MismatchReason;
                    }
                }

                // Any shift still running from that finished day ends with it. Its end is the last
                // moment of ITS OWN trading day, not now - stamping it with the current time would
                // push the shift into today and file the whole report under the wrong date.
                var dayShifts = await _db.Shifts
                    .Where(s => s.BranchId == branchDay.Key.BranchId && s.Status == ShiftStatus.Active)
                    .ToListAsync(cancellationToken);

                Shift? closingShift = null;

                foreach (var shift in dayShifts)
                {
                    var (_, shiftDayEnd) = IndiaTime.BusinessDayRangeFor(shift.LoginTime);
                    if (IndiaTime.BusinessDayOf(shift.LoginTime) != branchDay.Key.BusinessDay) continue;

                    shift.LogoutTime = shiftDayEnd.AddSeconds(-1);
                    shift.Status = ShiftStatus.Completed;
                    shift.ClosedTradingDay = true;
                    closingShift = shift;

                    var op = await _db.Operators.FindAsync(new object[] { shift.OperatorId }, cancellationToken);
                    if (op != null && op.Status == OperatorStatus.Active)
                    {
                        op.Status = OperatorStatus.LoggedOut;
                        op.IsOnline = false;
                    }
                }

                // No shift was left open — the operators logged out properly and simply nobody
                // ticked the box. That is the common case, not the exception, so the report must
                // still go out. Any shift of this branch will do: it is only read for its branch,
                // and the day being reported on is passed in separately below.
                closingShift ??= await _db.Shifts
                    .Where(s => s.BranchId == branchDay.Key.BranchId && s.LogoutTime != null)
                    .OrderByDescending(s => s.LogoutTime)
                    .FirstOrDefaultAsync(cancellationToken);

                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogWarning(
                    "Closed trading day {Day} for branch {Branch} automatically: nobody marked the last shift. " +
                    "{Registers} register(s) closed, holding Rs {Amount} between them.",
                    branchDay.Key.BusinessDay, branchDay.Key.BranchId,
                    branchDay.Count(), branchDay.Sum(r => r.ExpectedDrawerCash));

                // Midday IST on the day being closed - unambiguously inside its midnight-to-
                // midnight window, whichever shift the report ends up attributed to. Passing this rather
                // than letting the day be inferred from a logout time is the whole fix: the first
                // version compared the two and sent nothing when they disagreed, which is exactly
                // the case that needs a report.
                var dayAnchor = new DateTimeOffset(
                    branchDay.Key.BusinessDay.Year, branchDay.Key.BusinessDay.Month, branchDay.Key.BusinessDay.Day,
                    12, 0, 0, TimeSpan.FromHours(5.5));

                if (closingShift != null)
                    await SendDaySummaryAsync(closingShift, closedAutomatically: true, anchor: dayAnchor);
                else
                    _logger.LogWarning(
                        "Closed trading day {Day} for branch {Branch} but sent no report: the branch has no shift on record.",
                        branchDay.Key.BusinessDay, branchDay.Key.BranchId);

                closedDays++;
            }
            catch (Exception ex)
            {
                // One branch failing must not leave the other three un-closed.
                _logger.LogError(ex, "Could not close trading day {Day} for branch {Branch}.",
                    branchDay.Key.BusinessDay, branchDay.Key.BranchId);
            }
        }

        return closedDays;
    }

    /// <param name="anchor">
    /// Any moment inside the trading day being reported on. Given explicitly when the day is
    /// closed automatically, because the shift it is attributed to may well have finished on a
    /// later day — an operator who logged out properly and simply never ticked the box. Inferring
    /// the day from that shift's logout time reports on the wrong day, and guarding against that
    /// by refusing to send means no report at all, which is what happened on the first run.
    /// </param>
    private async Task SendDaySummaryAsync(
        Shift shift, bool closedAutomatically = false, DateTimeOffset? anchor = null)
    {
        try
        {
            var reportOn = anchor ?? shift.LogoutTime ?? DateTimeOffset.UtcNow;
            var (dayStart, dayEnd) = IndiaTime.BusinessDayRangeFor(reportOn);
            var businessDay = IndiaTime.BusinessDayOf(reportOn);
            var branchName = await _db.Branches.Where(b => b.Id == shift.BranchId)
                .Select(b => b.Name).FirstOrDefaultAsync() ?? "Unknown branch";

            var payments = await _db.Payments.AsNoTracking()
                .Where(p => p.BranchId == shift.BranchId && p.CreatedAt >= dayStart && p.CreatedAt < dayEnd)
                .ToListAsync();

            var sessions = await _db.Sessions.AsNoTracking()
                .CountAsync(s => s.BranchId == shift.BranchId && s.StartTime >= dayStart && s.StartTime < dayEnd);

            var outages = await _db.DowntimeEvents.AsNoTracking()
                .Where(d => d.BranchId == shift.BranchId && d.StartedAt >= dayStart && d.StartedAt < dayEnd)
                .ToListAsync();

            var total = payments.Sum(p => p.TotalAmount);

            var rows = new List<(string, string)>
            {
                ("Branch", branchName),
                ("Day", $"{businessDay:dd MMM yyyy}"),
                ("Counted from", "midnight to midnight"),
                ("Shop closed at", closedAutomatically
                    ? "midnight - closed by the system, not by an operator"
                    : IndiaTime.Format(shift.LogoutTime ?? DateTimeOffset.UtcNow)),
                ("", ""),
                ("Total money taken", $"Rs {total:0.00}"),
                ("  Cash", $"Rs {payments.Sum(p => p.CashAmount):0.00}"),
                ("  Online or UPI", $"Rs {payments.Sum(p => p.OnlineAmount):0.00}"),
                ("  Paid from wallets", $"Rs {payments.Sum(p => p.WalletAmount):0.00}"),
                ("", ""),
                ("Customers who played", sessions.ToString()),
                ("Bills taken", payments.Count.ToString()),
                ("", ""),
                ("Problems today", outages.Count == 0
                    ? "None - the system ran all day"
                    : $"{outages.Count} interruption{(outages.Count == 1 ? "" : "s")}"),
            };

            foreach (var outage in outages)
            {
                var what = outage.Kind == DowntimeKind.InternetOffline
                    ? "Internet was down"
                    : "System was off";
                rows.Add(($"  {what}",
                    $"from {IndiaTime.FormatTime(outage.StartedAt)}, for {AdminEmailTemplate.Describe(TimeSpan.FromSeconds(outage.DurationSeconds))}"));
            }

            await _adminNotifier.NotifyAsync(
                $"{branchName} - money taken on {businessDay:dd MMM yyyy} - Rs {total:0.00}",
                AdminEmailTemplate.Compose(
                    $"How {branchName} did today",
                    AdminEmailTemplate.Green,
                    $"The shop has closed for the day. {branchName} took Rs {total:0.00} from {sessions} customer{(sessions == 1 ? "" : "s")} on {businessDay:dd MMM yyyy}."
                        + (closedAutomatically
                            ? " Nobody marked the last shift of the day, so the system closed the day itself once trading had genuinely stopped for the night."
                            : string.Empty),
                    rows,
                    headline: $"Rs {total:0.00}",
                    footnote: closedAutomatically
                        ? "The day runs from midnight to midnight, so late-night play before midnight still counts "
                          + "towards the day it started on. No operator ticked \"last shift of the day\", so this was "
                          + "put together automatically once the branch had gone quiet for the night. The figures are "
                          + "complete; the only thing missing is a counted drawer, if nobody counted it before leaving."
                        : "The day runs from midnight to midnight, so late-night play before midnight still counts towards the day it started on. You are getting this because the operator ticked \"last shift of the day\" when they finished."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the end-of-day summary for shift {ShiftId}.", shift.Id);
        }
    }

    public async Task LogoutAsync(Guid userId, string role, Guid? shiftId, bool closesTradingDay = false)
    {
        if (role == Roles.Operator && shiftId.HasValue)
        {
            // Close the operator's shift
            var shift = await _db.Shifts.FirstOrDefaultAsync(
                s => s.Id == shiftId.Value && s.OperatorId == userId && s.Status == ShiftStatus.Active);
            if (shift != null)
            {
                shift.LogoutTime = DateTimeOffset.UtcNow;
                shift.Status = ShiftStatus.Completed;
                shift.ClosedTradingDay = closesTradingDay;
            }

            // Update operator status
            var op = await _db.Operators.FindAsync(userId);
            if (op != null)
            {
                op.Status = OperatorStatus.LoggedOut;
                op.IsOnline = false;
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Operator shift ended: {UserId}, shift: {ShiftId}, closed the day: {ClosedDay}",
                userId, shiftId, closesTradingDay);

            // Sent after the save, so the figures in it are the ones on record. Only on the
            // last shift of the day: a summary after every shift would be partial, and with
            // four branches and several shifts each it would be ignored within a week.
            if (closesTradingDay && shift != null)
            {
                await SendDaySummaryAsync(shift);
            }
        }

        // Fetch user name for audit
        string userName = "Unknown";
        if (role == Roles.SuperAdmin)
        {
            var user = await _db.Users.FindAsync(userId);
            userName = user?.FullName ?? "Admin";
        }
        else
        {
            var op = await _db.Operators.FindAsync(userId);
            userName = op?.FullName ?? "Operator";
        }

        await _audit.LogAsync(new AuditEntry
        {
            UserId = role == Roles.SuperAdmin ? userId : null,
            OperatorId = role == Roles.Operator ? userId : null,
            UserRole = role,
            UserName = userName,
            Action = AuditActions.Logout,
            Details = new { shiftId },
        });

        // Revoke tokens globally for this user (hardens against stale token reuse)
        await _tokenRevocation.RevokeUserTokensAsync(userId, TimeSpan.FromDays(7));
    }

    /// <summary>
    /// SOP §11: Force Logout — Super Admin forcibly logs out an operator.
    /// Instantly revoke access, terminate session, block future login.
    /// Maps from: auth.service.js forceLogout()
    /// </summary>
    public async Task<ForceLogoutResponseDto> ForceLogoutAsync(Guid adminId, Guid operatorId)
    {
        var op = await _db.Operators
            .Include(o => o.Branch)
            .FirstOrDefaultAsync(o => o.Id == operatorId);
        if (op == null)
            throw new NotFoundException("Operator not found", "OPERATOR_NOT_FOUND");

        // Close ALL active shifts for this operator
        var activeShifts = await _db.Shifts
            .Where(s => s.OperatorId == operatorId && s.Status == ShiftStatus.Active)
            .ToListAsync();
        foreach (var shift in activeShifts)
        {
            shift.LogoutTime = DateTimeOffset.UtcNow;
            shift.Status = ShiftStatus.ForceClosed;
        }

        // A cash register must never outlive the shift that opened it. Closing the shift and
        // leaving the drawer open strands it: no shift owns it, so nobody can count it, and it
        // is still "open" so the next operator's End Shift picks up the previous operator's
        // takings as though they were their own. Their count then cannot balance, through no
        // fault of theirs.
        //
        // Closed as NOT counted, deliberately. Nobody counted this cash - the shift was ended
        // administratively, with no one at the drawer. Writing in a figure would invent a count
        // that never happened; leaving it empty keeps the money visibly unreconciled, which is
        // the truth and the thing somebody should follow up.
        //
        // Matched on the operator rather than only on the shifts closed just now, so a drawer
        // already stranded by an earlier force-logout is cleared up by the next one.
        var strandedRegisters = await _db.CashRegisters
            .Where(r => r.OperatorId == operatorId && r.Status == CashRegisterStatus.Open)
            .ToListAsync();

        foreach (var register in strandedRegisters)
        {
            register.Status = CashRegisterStatus.Closed;
            register.ClosedAt = DateTimeOffset.UtcNow;
            register.MismatchReason = string.IsNullOrWhiteSpace(register.MismatchReason)
                ? "Drawer was never counted - the shift was ended by an admin, with nobody at the till."
                : register.MismatchReason;
        }

        if (strandedRegisters.Count > 0)
        {
            _logger.LogWarning(
                "Force logout of {Operator} closed {Count} cash register(s) that were never counted, " +
                "holding Rs {Amount} between them.",
                op.FullName, strandedRegisters.Count, strandedRegisters.Sum(r => r.ExpectedDrawerCash));
        }

        // Set operator status to logged_out
        op.Status = OperatorStatus.LoggedOut;
        op.IsOnline = false;

        await _db.SaveChangesAsync();

        // Get admin name for audit
        var admin = await _db.Users.FindAsync(adminId);
        var adminName = admin?.FullName ?? "Admin";

        await _audit.LogAsync(new AuditEntry
        {
            UserId = adminId,
            UserRole = Roles.SuperAdmin,
            UserName = adminName,
            Action = AuditActions.ForcedLogout,
            TargetType = "operator",
            TargetId = operatorId,
            BranchId = op.BranchId,
            BranchName = op.Branch?.Name,
            Details = new { operatorName = op.FullName, reason = "Forced logout by Super Admin" },
        });

        _logger.LogWarning("FORCE LOGOUT: {Operator} by {Admin}", op.FullName, adminName);

        // Force revoke all existing tokens for this operator
        await _tokenRevocation.RevokeUserTokensAsync(operatorId, TimeSpan.FromDays(7));

        // The one-line gap between "Super Admin sees the shift closed" (their dashboard just
        // re-queries the DB) and "the operator sees it" - without this, the operator's own
        // screen kept showing the shift as open until their token happened to expire or they
        // reloaded the page. SocketContext.jsx has been listening for this event the whole
        // time; nothing on the server ever sent it.
        try
        {
            await _hubNotifications.SendForceLogoutAsync(operatorId, "Forced logout by Super Admin");
        }
        catch (Exception ex)
        {
            // The DB work above already committed - a dead hub must never turn a successful
            // force-logout into a failed one. The operator falls back to their token expiring.
            _logger.LogWarning(ex, "Could not push a live ForceLogout to operator {OperatorId}.", operatorId);
        }

        return new ForceLogoutResponseDto { Success = true, Operator = op.FullName };
    }

    /// <summary>
    /// Refresh access token — re-verify user is still active before issuing.
    /// Maps from: auth.service.js refreshAccessToken()
    /// </summary>
    public async Task<TokenResponseDto> RefreshAccessTokenAsync(string refreshToken)
    {
        var principal = _jwt.ValidateRefreshToken(refreshToken);
        if (principal == null)
            throw new AuthenticationException("Invalid refresh token", "REFRESH_INVALID");

        var id = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        var role = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value;

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(role))
            throw new AuthenticationException("Invalid refresh token", "REFRESH_INVALID");

        var userId = Guid.Parse(id);

        // Re-verify user still exists and is active
        if (role == Roles.SuperAdmin)
        {
            var active = await _db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active);
            if (!active) throw new AuthorizationException("Account is no longer active", "ACCOUNT_INACTIVE");
        }
        else if (role == Roles.Operator)
        {
            var active = await _db.Operators.AnyAsync(o => o.Id == userId && o.Status == OperatorStatus.Active);
            if (!active) throw new AuthorizationException("Account is no longer active", "ACCOUNT_INACTIVE");
        }

        // Filter out protocol claims (exp, iat, nbf, iss, aud, jti) to prevent duplicates and validation failures
        var protocolClaims = new System.Collections.Generic.HashSet<string>(new[] { 
            "exp", "iat", "nbf", "iss", "aud", "jti"
        });

        var claims = new Dictionary<string, string>();
        foreach (var claim in principal.Claims)
        {
            if (!protocolClaims.Contains(claim.Type) && !claims.ContainsKey(claim.Type))
                claims[claim.Type] = claim.Value;
        }

        return new TokenResponseDto { AccessToken = _jwt.GenerateAccessToken(claims) };
    }

    /// <summary>
    /// SOP §19: Get current user profile with permissions.
    /// Returns full dashboard permission map for frontend rendering.
    /// Maps from: auth.service.js getCurrentUser()
    /// </summary>
    public async Task<UserProfileDto> GetCurrentUserAsync(Guid userId, string role)
    {
        if (role == Roles.SuperAdmin || role == Roles.Admin)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null) throw new NotFoundException("User not found", "USER_NOT_FOUND");

            return new UserProfileDto
            {
                Id = user.Id,
                Email = user.Email,
                FullName = user.FullName,
                Role = user.Role,
                DashboardPermissions = user.DashboardPermissions != null 
                    ? JsonSerializer.Deserialize<object>(user.DashboardPermissions) 
                    : null,
                Status = user.Status.ToString().ToLowerInvariant(),
                LastLogin = user.LastLogin,
            };
        }

        if (role == Roles.Operator)
        {
            var op = await _db.Operators
                .Include(o => o.Branch)
                .FirstOrDefaultAsync(o => o.Id == userId);
            if (op == null) throw new NotFoundException("Operator not found", "OPERATOR_NOT_FOUND");

            // Get active shift
            var activeShift = await _db.Shifts
                .Where(s => s.OperatorId == userId && s.Status == ShiftStatus.Active)
                .OrderByDescending(s => s.LoginTime)
                .Select(s => new ActiveShiftDto { Id = s.Id, LoginTime = s.LoginTime })
                .FirstOrDefaultAsync();

            return new UserProfileDto
            {
                Id = op.Id,
                FullName = op.FullName,
                Username = op.Username,
                Role = Roles.Operator,
                BranchId = op.BranchId,
                BranchName = op.Branch?.Name,
                DashboardPermissions = op.DashboardPermissions != null
                    ? JsonSerializer.Deserialize<object>(op.DashboardPermissions)
                    : null,
                Status = op.Status.ToString().ToLowerInvariant(),
                LastLogin = op.LastLogin,
                // Also as ShiftId, which is what the dashboard reads. It was only ever set on
                // the login response, so a page refresh left the browser holding a user with no
                // shift on it — and after a takeover the shift is issued after login, so this is
                // the only way the new one reaches the client.
                ShiftId = activeShift?.Id,
                ActiveShift = activeShift,
            };
        }

        throw new AppException("Invalid role", System.Net.HttpStatusCode.BadRequest, "INVALID_ROLE");
    }

    /// <summary>SOP §6.3 Step 2: Get active branches for login screen</summary>
    public async Task<IEnumerable<BranchListItemDto>> GetActiveBranchesAsync()
    {
        return await _db.Branches
            .Where(b => b.Status == BranchStatus.Active)
            .OrderBy(b => b.Name)
            .Select(b => new BranchListItemDto
            {
                Id = b.Id,
                Name = b.Name,
                Address = b.Address,
                Status = b.Status.ToString().ToLowerInvariant(),
                OpeningTime = b.OpeningTime.ToString("HH:mm"),
                ClosingTime = b.ClosingTime.ToString("HH:mm"),
            })
            .ToListAsync();
    }

    /// <summary>SOP §6.3 Step 3: Get operators for a branch (for operator selection screen)</summary>
    public async Task<IEnumerable<OperatorListItemDto>> GetBranchOperatorsAsync(Guid branchId)
    {
        return await _db.Operators
            .Where(o => o.BranchId == branchId && o.Status != OperatorStatus.Disabled)
            .OrderBy(o => o.FullName)
            .Select(o => new OperatorListItemDto
            {
                Id = o.Id,
                FullName = o.FullName,
                Username = o.Username,
                Status = o.Status.ToString().ToLowerInvariant(),
            })
            .ToListAsync();
    }

    /// <summary>Verify if admin password is valid</summary>
    public async Task<bool> VerifyAdminPasswordAsync(string password)
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Role == Roles.SuperAdmin);
        if (admin == null) return false;
        return BCryptNet.Verify(password, admin.PasswordHash);
    }

    /// <summary>
    /// Generate a 30-day emergency offline JWT.
    /// The token is signed with the same access secret so the existing JWT middleware validates it.
    /// A "token_type" claim of "emergency_offline" allows the client to distinguish it from regular tokens.
    /// </summary>
    public async Task<string> GenerateEmergencyTokenAsync(Guid userId, string role, string? branchId, string? dashboardPermissions)
    {
        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = userId.ToString(),
            [ClaimTypes.Role] = role,
            ["token_type"] = "emergency_offline",
        };

        if (!string.IsNullOrEmpty(branchId))
            claims["branchId"] = branchId;

        if (!string.IsNullOrEmpty(dashboardPermissions))
            claims["dashboardPermissions"] = dashboardPermissions;

        // Resolve the user's display name for the audit log
        string userName = "Unknown";
        if (role == Roles.Operator)
        {
            var op = await _db.Operators.FindAsync(userId);
            if (op != null)
            {
                claims[ClaimTypes.Name] = op.FullName;
                userName = op.FullName;
            }
        }
        else
        {
            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                claims[ClaimTypes.Name] = user.FullName;
                userName = user.FullName;
            }
        }

        await _audit.LogAsync(new AuditEntry
        {
            UserId = role != Roles.Operator ? userId : null,
            OperatorId = role == Roles.Operator ? userId : null,
            UserRole = role,
            UserName = userName,
            Action = "emergency_token_generated",
            BranchId = !string.IsNullOrEmpty(branchId) && Guid.TryParse(branchId, out var bid) ? bid : null,
            Details = new { tokenType = "emergency_offline", expiryHours = 720 },
        });

        return _jwt.GenerateEmergencyToken(claims);
    }

    public async Task<CheckSetupResponseDto> CheckSetupStatusAsync()
    {
        var hasSuperAdmin = await _db.Users.AnyAsync(u => u.Role == Roles.SuperAdmin);
        var hasAdmin = await _db.Users.AnyAsync(u => u.Role == Roles.Admin);
        var hasOperator = await _db.Operators.AnyAsync();

        return new CheckSetupResponseDto
        {
            NeedsSuperAdminSetup = !hasSuperAdmin,
            NeedsAdminSetup = !hasAdmin,
            NeedsOperatorSetup = !hasOperator
        };
    }

    public async Task<LoginResponseDto> SetupMasterAccountAsync(SetupMasterDto dto)
    {
        var hasSuperAdmin = await _db.Users.AnyAsync(u => u.Role == Roles.SuperAdmin);
        if (hasSuperAdmin) throw new AuthorizationException("Master account already exists.", "SETUP_LOCKED");

        var adminHash = BCryptNet.HashPassword(dto.Password);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = dto.Email.Trim().ToLowerInvariant(),
            FullName = dto.FullName,
            Role = Roles.SuperAdmin,
            Status = UserStatus.Active,
            PasswordHash = adminHash,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return await LoginAdminAsync(new AdminLoginDto { Email = dto.Email, Password = dto.Password });
    }

    public async Task<LoginResponseDto> SetupOperatorAccountAsync(SetupOperatorDto dto)
    {
        var hasOperator = await _db.Operators.AnyAsync();
        if (hasOperator) throw new AuthorizationException("An operator already exists. Use the dashboard to create more.", "SETUP_LOCKED");

        var opHash = BCryptNet.HashPassword(dto.Password);
        var op = new Operator
        {
            Id = Guid.NewGuid(),
            FullName = dto.FullName,
            Username = dto.Username.Trim().ToLowerInvariant(),
            Email = dto.Email.Trim().ToLowerInvariant(),
            PasswordHash = opHash,
            BranchId = dto.BranchId,
            DashboardPermissions = "{}",
            Status = OperatorStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Operators.Add(op);
        await _db.SaveChangesAsync();

        return await LoginOperatorAsync(new OperatorLoginDto { BranchId = dto.BranchId, Username = dto.Username, Password = dto.Password });
    }

    private static bool IsLocked(DateTimeOffset? lockedUntil) => lockedUntil.HasValue && lockedUntil.Value > DateTimeOffset.UtcNow;

    private static string LockoutMessage(DateTimeOffset lockedUntil)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTimeOffset.UtcNow).TotalMinutes));
        return $"Too many failed attempts. Try again in {minutes} minute(s), or use 'Forgot password' to reset it now.";
    }

    /// <summary>Never throws — a reset email that fails to send must not surface as a login-endpoint 500.</summary>
    private async Task SendPasswordResetEmailAsync(string? email, string targetName, string token, bool isLockout = false)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        try
        {
            var resetLink = _appUrls.BuildResetPasswordLink(email, token);
            var htmlBody = isLockout
                ? PasswordResetEmailTemplate.ComposeForLockout(targetName, resetLink)
                : PasswordResetEmailTemplate.Compose(targetName, resetLink);
            await _emailService.SendEmailAsync(email, PasswordResetEmailTemplate.Subject, htmlBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the password-reset email to {Email}.", email);
        }
    }

    public async Task InitiatePasswordResetAsync(string email, string? accountType = null)
    {
        email = email.Trim().ToLowerInvariant();
        // Same email can belong to both a Member and a staff (User/Operator) account.
        // Scope the lookup to whichever screen the request came from so the reset never
        // lands on the wrong account type.
        // Compared case-insensitively on both sides - `email` above is already lowered, but a
        // stored address saved in mixed case (e.g. "John@Gmail.com") would otherwise never
        // match, and this method fails silent by design (see below), so the person requesting
        // a reset would see "check your email" and genuinely never receive one, with no error
        // anywhere to explain why.
        var user = accountType == "member" ? null : await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
        var op = accountType == "member" ? null : await _db.Operators.FirstOrDefaultAsync(o => o.Email.ToLower() == email);
        // Members can end up with duplicate rows sharing an email (e.g. abandoned re-registrations).
        // Prefer the active one so a reset never lands on a stale/suspended duplicate instead of
        // the account the person is actually trying to log into.
        var member = accountType == "staff" ? null : await _db.Members
            .Where(m => m.Email.ToLower() == email)
            .OrderByDescending(m => m.Status == MemberStatus.Active)
            .ThenByDescending(m => m.UpdatedAt)
            .FirstOrDefaultAsync();

        if (user == null && op == null && member == null) return; // Silent fail for security

        var token = Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);

        string targetName = "";
        if (user != null)
        {
            user.ResetToken = token;
            user.ResetTokenExpiry = expiry;
            targetName = user.FullName;
        }
        else if (op != null)
        {
            op.ResetToken = token;
            op.ResetTokenExpiry = expiry;
            targetName = op.FullName;
        }
        else if (member != null)
        {
            member.ResetToken = token;
            member.ResetTokenExpiry = expiry;
            targetName = member.FullName;
        }

        await _db.SaveChangesAsync();

        if (member != null) await ShareMemberResetTokenAsync(member, token, expiry);

        await SendPasswordResetEmailAsync(email, targetName, token);
    }

    public async Task<IEnumerable<AvailableAdminDto>> GetAvailableAdminsForSwitchAsync()
    {
        // Admin-level profiles only - a Users-table Admin, or an Operator promoted with
        // IsGlobalAdmin. Super Admin is deliberately excluded even if one happens to have a
        // PIN set: Super Admin is not a profile anyone quick-switches into from an operator
        // station, it is its own separate login. Listing it here would let an operator's PIN
        // guess reach the single most powerful account in the system from the counter.
        var activeAdmins = await _db.Users
            .Where(u => u.Role == Roles.Admin && u.Status == UserStatus.Active && !string.IsNullOrEmpty(u.AccessPin))
            .Select(u => new AvailableAdminDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Type = "Admin",
                PinLength = u.AccessPin!.Length
            })
            .ToListAsync();

        // Not gated on Active - LoggedOut is Operators' ordinary "not currently signed in
        // anywhere" state, not a block, and requiring Active defeated the entire point of
        // being promoted: a Global Admin is reached from a DIFFERENT branch's counter
        // precisely because they are not logged in there. Only Suspended/Disabled - genuine
        // access revocation - actually should hide someone from this list.
        var activeOps = await _db.Operators
            .Where(o => o.IsGlobalAdmin
                && o.Status != OperatorStatus.Suspended && o.Status != OperatorStatus.Disabled
                && !string.IsNullOrEmpty(o.AccessPin))
            .Select(o => new AvailableAdminDto
            {
                Id = o.Id,
                FullName = o.FullName,
                Type = "Operator",
                PinLength = o.AccessPin!.Length
            })
            .ToListAsync();

        return activeAdmins.Concat(activeOps).OrderBy(a => a.FullName);
    }

    public async Task<LoginResponseDto> AdminSwitchInAsync(AdminSwitchInDto dto)
    {
        Guid adminId;
        string adminFullName;
        string? adminDashboardPermissions;
        UserStatus adminStatus;

        // The role this person actually holds, carried through to the token below.
        string adminRole;

        // 1. Try to find an Admin in the Users table.
        //
        // Super Admin is deliberately excluded - Super Admin is Head Office, never a branch
        // counter, so there is no legitimate reason this operator-station elevation would ever
        // need to reach one. Excluded here too, not just from the list this picks from, so a
        // request built by hand with a Super Admin's id and PIN cannot reach it either.
        var adminUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == dto.AdminId && u.Role == Roles.Admin);
        if (adminUser != null)
        {
            if (adminUser.AccessPin != dto.AccessPin)
                throw new AuthenticationException("Invalid Admin PIN.", "INVALID_PIN");

            adminId = adminUser.Id;
            adminFullName = adminUser.FullName;
            adminDashboardPermissions = adminUser.DashboardPermissions;
            adminStatus = adminUser.Status;
            adminRole = adminUser.Role;
        }
        else
        {
            var adminOp = await _db.Operators.FirstOrDefaultAsync(o => o.Id == dto.AdminId && o.IsGlobalAdmin);
            if (adminOp != null)
            {
                if (adminOp.AccessPin != dto.AccessPin)
                    throw new AuthenticationException("Invalid Admin PIN.", "INVALID_PIN");

                adminId = adminOp.Id;
                adminFullName = adminOp.FullName;
                adminDashboardPermissions = adminOp.DashboardPermissions;
                // LoggedOut counts as eligible, same reasoning as the list this is picked
                // from: it is Operators' ordinary "not signed in anywhere right now" state,
                // and a Global Admin being reached from a station they are not logged into
                // is the entire point of this feature, not a reason to refuse it.
                adminStatus = adminOp.Status is OperatorStatus.Suspended or OperatorStatus.Disabled
                    ? UserStatus.Disabled
                    : UserStatus.Active;
                // A promoted operator really is an Admin, not a Super Admin.
                adminRole = Roles.Admin;
            }
            else
            {
                throw new AuthenticationException("Admin not found.", "INVALID_ADMIN");
            }
        }

        if (adminStatus != UserStatus.Active)
            throw new AuthorizationException("Admin account is inactive.", "ACCOUNT_INACTIVE");

        var shift = await _db.Shifts.Include(s => s.Operator).ThenInclude(o => o.Branch).FirstOrDefaultAsync(s => s.Id == dto.ShiftId);
        if (shift == null || shift.Status != ShiftStatus.Active)
            throw new AuthorizationException("Active operator shift not found for switch.", "INVALID_SHIFT");

        // Generate temporary token for the Admin Switch
        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = adminId.ToString(),
            [ClaimTypes.Role] = adminRole,
            [ClaimTypes.Name] = adminFullName,
            ["originalOperatorId"] = shift.OperatorId.ToString(),
            ["shiftId"] = shift.Id.ToString(),
            ["branchId"] = shift.BranchId.ToString(),
            ["isSwitchedAdmin"] = "true"
        };

        if (!string.IsNullOrEmpty(adminDashboardPermissions))
        {
            claims["dashboardPermissions"] = adminDashboardPermissions;
        }

        var accessToken = _jwt.GenerateAccessToken(claims);

        await _audit.LogAsync(new AuditEntry
        {
            UserId = adminId,
            UserRole = adminRole,
            UserName = adminFullName,
            OperatorId = shift.OperatorId, // associate with the operator
            Action = AuditActions.AdminSwitchIn,
            Details = new { shiftId = shift.Id, operatorName = shift.Operator.FullName, branchId = shift.BranchId }
        });

        return new LoginResponseDto
        {
            User = new UserProfileDto
            {
                Id = adminId,
                FullName = adminFullName,
                Email = adminFullName, // Set email to full name as fallback
                // Must match the token's role claim. The client decides what to render from
                // this; the server decides what to allow from the claim. Two different answers
                // is how you get a button that is offered and then refused.
                Role = adminRole,
                BranchId = shift.BranchId,
                BranchName = shift.Operator.Branch.Name,
                ShiftId = shift.Id,
                DashboardPermissions = adminDashboardPermissions != null ? JsonSerializer.Deserialize<object>(adminDashboardPermissions) : null,
                Status = adminStatus.ToString().ToLowerInvariant(),
                LastLogin = DateTimeOffset.UtcNow
            },
            AccessToken = accessToken,
            RefreshToken = accessToken // We don't refresh this, it's ephemeral
        };
    }

    public async Task AdminSwitchOutAsync(Guid adminId, Guid shiftId)
    {
        // Same split AdminSwitchInAsync itself has to bridge: a switched-in admin can be a
        // genuine Users-table Admin, or an operator promoted with IsGlobalAdmin (e.g.
        // "Ankur"/"Nazmin", managed from the Operators tab, not the Admins list). Checking
        // Users alone meant every switch-out by a promoted operator produced no audit row at
        // all - the switch-in was logged, the switch-out silently wasn't.
        var admin = await _db.Users.FindAsync(adminId);
        if (admin != null)
        {
            await _audit.LogAsync(new AuditEntry
            {
                UserId = admin.Id,
                UserRole = Roles.Admin,
                UserName = admin.FullName,
                Action = AuditActions.AdminSwitchOut,
                Details = new { shiftId }
            });
            return;
        }

        var adminOp = await _db.Operators.FindAsync(adminId);
        if (adminOp != null && adminOp.IsGlobalAdmin)
        {
            await _audit.LogAsync(new AuditEntry
            {
                UserId = adminOp.Id,
                UserRole = Roles.Admin,
                UserName = adminOp.FullName,
                Action = AuditActions.AdminSwitchOut,
                Details = new { shiftId }
            });
        }
    }

    public async Task ConfirmBranchSwitchAsync(Guid userId, string pin, Guid? branchId)
    {
        // Two separate tables can carry Admin-level access, the same split
        // AdminSwitchInAsync already has to bridge: a genuine Users-table Admin/Super
        // Admin, or an Operator promoted with IsGlobalAdmin (managed from the Operators
        // tab, not the Admins list - "Ankur" and "Nazmin" are this kind, not the other).
        // Checking Users alone meant a promoted operator's PIN was never going to match
        // anything and every branch switch they tried would fail outright, which is worse
        // than the silent-switch behaviour this endpoint exists to replace.
        Guid actorId; string actorRole; string actorName; string? storedPin;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId
            && (u.Role == Roles.Admin || u.Role == Roles.SuperAdmin));
        if (user is not null)
        {
            actorId = user.Id;
            actorRole = user.Role;
            actorName = user.FullName;
            storedPin = user.AccessPin;
        }
        else
        {
            var op = await _db.Operators.FirstOrDefaultAsync(o => o.Id == userId && o.IsGlobalAdmin);
            if (op is null)
                throw new AuthenticationException("Admin not found.", "INVALID_ADMIN");

            actorId = op.Id;
            actorRole = Roles.Admin;
            actorName = op.FullName;
            storedPin = op.AccessPin;
        }

        if (string.IsNullOrEmpty(storedPin) || storedPin != pin)
            throw new AuthenticationException("Invalid PIN.", "INVALID_PIN");

        string? branchName = branchId is { } bId
            ? await _db.Branches.AsNoTracking().Where(b => b.Id == bId).Select(b => b.Name).FirstOrDefaultAsync()
            : null;

        await _audit.LogAsync(new AuditEntry
        {
            UserId = actorId,
            UserRole = actorRole,
            UserName = actorName,
            Action = AuditActions.BranchSwitch,
            BranchId = branchId,
            BranchName = branchName,
            Details = new { switchedTo = branchName ?? "All Branches (Global)" },
        });
    }

    public async Task CompletePasswordResetAsync(ResetPasswordDto dto)
    {
        // No time cutoff on purpose - the link is one-time-use, not one-hour-use. A token is
        // valid until it is actually spent (the three ResetToken = null lines below, the moment
        // this method finishes) or superseded by a newer request for the same account
        // (InitiatePasswordResetAsync overwrites it), never by a clock. ResetTokenExpiry is
        // still stamped when a token is issued - WalletService reads it to avoid re-sending a
        // welcome email while one is already outstanding - but nothing here treats it as a
        // deadline any more.
        //
        // What removing that clock check took with it: ResetPasswordDto.Token has no [Required]
        // attribute, so a request that omits it (or sends it explicitly as null) arrives here as
        // dto.Token == null. ResetToken defaults to null on every account until a reset is
        // actually requested, so "ResetToken == dto.Token" alone would then match the first
        // account it found that had never requested one - in effect, unauthenticated access to
        // any untouched account. The expiry comparison used to block this by accident (EF
        // translates a null-vs-timestamp comparison to SQL's three-valued NULL, which a WHERE
        // clause never treats as a match) - this replaces that accident with the real guard.
        if (string.IsNullOrWhiteSpace(dto.Token))
            throw new AuthorizationException("Invalid or expired reset token.");

        // Matched on ResetToken ALONE, not "Email AND ResetToken" - the token is already the
        // unguessable, single-use credential here (a random 32-char hex string, minted only
        // when a reset was actually requested), so it is what should decide this, not an email
        // address that can go stale independently of it. It very much does: an operator can
        // correct a member's email at the branch (a routine, common edit) with nothing syncing
        // that change up to Head Office - see MemberService.UpdateMemberAsync's member.updated
        // fix - so Head Office's copy of Email can lag what was actually emailed to the member
        // for the reset link that carries this exact token. Requiring both meant a perfectly
        // valid, unspent, correctly-delivered token was rejected as "invalid or expired" purely
        // because of an unrelated sync lag on a field that was never the credential.
        //
        // dto.Email is still read and normalized below, kept only as a non-blocking sanity
        // signal (logged on mismatch) - never a second gate the token has to also clear.
        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.ResetToken == dto.Token);
        var op = await _db.Operators.FirstOrDefaultAsync(o => o.ResetToken == dto.Token);
        var member = await _db.Members.FirstOrDefaultAsync(m => m.ResetToken == dto.Token);

        var matchedEmail = (user?.Email ?? op?.Email ?? member?.Email)?.Trim().ToLowerInvariant();
        if (matchedEmail != null && matchedEmail != email)
        {
            _logger.LogInformation(
                "Password reset token matched an account whose stored email ({StoredEmail}) " +
                "differs from the one submitted ({SubmittedEmail}) - proceeding on the token, " +
                "which is the actual credential here.", matchedEmail, email);
        }

        if (user == null && op == null && member == null)
        {
            // Not necessarily a dead token - just a member Head Office does not have a row for
            // yet. See ApplyMemberResetTokenAsync's own docstring for why that method refuses to
            // invent one: a branch that has never told Head Office a member exists at all is
            // something to wait for, not something to guess at. But the token itself already
            // arrived here the moment it was minted (ShareMemberResetTokenAsync sends it
            // separately from - and before - the member ever needs to exist), so it does not
            // have to wait on a Member row to be checked against.
            if (_configuration.IsHeadOffice() && await TryCompleteResetForUnsyncedMemberAsync(email, dto.Token, dto.NewPassword))
                return;

            throw new AuthorizationException("Invalid or expired reset token.");
        }

        var newHash = BCryptNet.HashPassword(dto.NewPassword);
        var passwordChangedAt = DateTimeOffset.UtcNow;
        if (user != null)
        {
            user.PasswordHash = newHash;
            user.ResetToken = null;
            user.ResetTokenExpiry = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else if (op != null)
        {
            op.PasswordHash = newHash;
            op.ResetToken = null;
            op.ResetTokenExpiry = null;
            op.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else if (member != null)
        {
            member.SetPassword(newHash, passwordChangedAt);
            member.ResetToken = null;
            member.ResetTokenExpiry = null;
            member.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // A member's password set at Head Office has to reach the branch, or it has changed
        // nothing that matters. The copy a gaming PC checks when somebody logs in at the counter
        // is the branch's own, in the branch's own database on a machine in another city - so a
        // reset that only lands here leaves the member holding a password that works nowhere
        // they would actually use it.
        //
        // Queued as a command rather than written into the branch's rows, for the reason
        // RemoteBranchControl exists: Head Office writing to its synced copy looks right for
        // about three seconds and does nothing. This rides down on the branch's next heartbeat,
        // a few seconds away, and the branch stores it locally - which is also what keeps that
        // member able to log in with the shop's internet down.
        //
        // Only Head Office queues it. At a branch the member is right here and has just been
        // updated directly; a row written there would be collected by nobody, because a branch
        // polls Head Office and never itself.
        if (member is { HomeBranchId: { } homeBranchId } && _configuration.IsHeadOffice())
        {
            _db.Add(new BranchCommand
            {
                Id = Guid.NewGuid(),
                BranchId = homeBranchId,
                CommandType = "set_member_password",
                Payload = JsonSerializer.Serialize(new
                {
                    memberId = member.Id,
                    passwordHash = newHash,
                    passwordChangedAt,
                }),
                Status = BranchCommandStatus.Pending,
                RequestedByUserId = Guid.Empty,   // the member themselves, not a Head Office user
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _db.SaveChangesAsync();

        if (user != null)
        {
            await _audit.LogAsync(new AuditEntry
            {
                UserId = user.Id,
                UserRole = user.Role,
                UserName = user.FullName,
                Action = AuditActions.PasswordReset,
                TargetType = "user",
                TargetId = user.Id,
                Details = new { status = "success", resetAt = DateTimeOffset.UtcNow },
            });
        }
        else if (op != null)
        {
            await _audit.LogAsync(new AuditEntry
            {
                OperatorId = op.Id,
                UserRole = op.IsGlobalAdmin ? Roles.Admin : Roles.Operator,
                UserName = op.FullName,
                Action = AuditActions.PasswordReset,
                TargetType = "operator",
                TargetId = op.Id,
                Details = new { status = "success", resetAt = DateTimeOffset.UtcNow },
            });
        }
        else if (member != null)
        {
            await _audit.LogAsync(new AuditEntry
            {
                UserRole = "Member",
                UserName = member.FullName,
                Action = AuditActions.PasswordReset,
                TargetType = "member",
                TargetId = member.Id,
                Details = new { status = "success", resetAt = DateTimeOffset.UtcNow },
            });
        }
    }

    /// <summary>
    /// Completes a member's reset when Head Office has the token but not the member - the same
    /// gap ApplyMemberResetTokenAsync defers on and waits for a member.created that, for a member
    /// created before this branch's sync ever ran, may never arrive.
    ///
    /// A member's password only ever needs to reach one place: the branch's own row, which is
    /// what a gaming PC actually checks at login (see the comment on the ordinary member branch
    /// above). Head Office does not need a Member row of its own to queue that command - it only
    /// needs to know which branch and which member id, and both of those already arrived with the
    /// reset-token event itself, stored as-received in SyncInboxEntries. Matching this dormant
    /// event on email and token is exactly the same check the ordinary path makes against a real
    /// Member row; the only thing missing here is the row, not the proof of who asked.
    /// </summary>
    private async Task<bool> TryCompleteResetForUnsyncedMemberAsync(string email, string token, string newPassword)
    {
        var candidates = await _db.SyncInboxEntries
            .Where(e => e.EventType == "member.reset_requested" && !e.Applied)
            .ToListAsync();

        foreach (var entry in candidates)
        {
            using var doc = JsonDocument.Parse(entry.EventData);
            var root = doc.RootElement;
            var entryEmail = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var entryToken = root.TryGetProperty("resetToken", out var t) ? t.GetString() : null;

            // Token alone, same reasoning as the main lookup above: this entry's own "email"
            // field was captured at the moment the reset was requested and can just as easily
            // have gone stale if the branch corrected the member's email afterward, before Head
            // Office ever got a member.created row to check it against.
            if (entryToken != token)
                continue;

            _db.Add(new BranchCommand
            {
                Id = Guid.NewGuid(),
                BranchId = entry.BranchId,
                CommandType = "set_member_password",
                Payload = JsonSerializer.Serialize(new
                {
                    memberId = entry.AggregateId,
                    passwordHash = BCryptNet.HashPassword(newPassword),
                    passwordChangedAt = DateTimeOffset.UtcNow,
                }),
                Status = BranchCommandStatus.Pending,
                RequestedByUserId = Guid.Empty,   // the member themselves, not a Head Office user
                CreatedAt = DateTimeOffset.UtcNow,
            });

            // Spent, the same as ResetToken = null on a real row - this exact token cannot be
            // replayed, and it stops ApplyMemberResetTokenAsync's retry from still trying to
            // apply it if the member.created it was waiting on ever does show up afterward.
            entry.Applied = true;
            entry.ApplyError = null;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(new AuditEntry
            {
                UserRole = "Member",
                UserName = entryEmail ?? email,
                Action = AuditActions.PasswordReset,
                TargetType = "member",
                TargetId = entry.AggregateId,
                Details = new { status = "success", resetAt = DateTimeOffset.UtcNow, viaUnsyncedMember = true },
            });

            return true;
        }

        return false;
    }

    public async Task ChangeCredentialsAsync(Guid targetUserId, ChangeCredentialsDto dto)
    {
        var user = await _db.Users.FindAsync(targetUserId);
        if (user != null)
        {
            if (!string.IsNullOrEmpty(dto.NewEmail)) user.Email = dto.NewEmail.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(dto.NewPassword)) user.PasswordHash = BCryptNet.HashPassword(dto.NewPassword);
        }
        else
        {
            var op = await _db.Operators.FindAsync(targetUserId);
            if (op != null)
            {
                if (!string.IsNullOrEmpty(dto.NewEmail)) op.Email = dto.NewEmail.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(dto.NewUsername)) op.Username = dto.NewUsername.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(dto.NewPassword)) op.PasswordHash = BCryptNet.HashPassword(dto.NewPassword);
            }
            else
            {
                throw new NotFoundException("Target user or operator not found.");
            }
        }
        await _db.SaveChangesAsync();
    }
}
