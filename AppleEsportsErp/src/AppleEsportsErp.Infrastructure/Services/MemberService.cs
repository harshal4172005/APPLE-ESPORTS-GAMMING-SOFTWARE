using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BCryptNet = BCrypt.Net.BCrypt;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Members;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Identity;

namespace AppleEsportsErp.Infrastructure.Services;

public class MemberService : IMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;
    private readonly JwtTokenService _jwt;
    private readonly IEmailService _emailService;
    private readonly IAppUrlProvider _appUrls;

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IOutboxService _outbox;
    private readonly IConfiguration _configuration;
    private readonly IHubNotificationService _hubNotifications;
    private readonly ILogger<MemberService> _logger;

    public MemberService(IUnitOfWork unitOfWork, IAuditService auditService, JwtTokenService jwt, IEmailService emailService, IAppUrlProvider appUrls, IOutboxService outbox, IConfiguration configuration, IHubNotificationService hubNotifications, ILogger<MemberService> logger)
    {
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _jwt = jwt;
        _emailService = emailService;
        _appUrls = appUrls;
        _configuration = configuration;
        _hubNotifications = hubNotifications;
        _logger = logger;
    }

    private static bool IsLocked(DateTimeOffset? lockedUntil) => lockedUntil.HasValue && lockedUntil.Value > DateTimeOffset.UtcNow;

    /// <summary>
    /// BCrypt.Verify throws instead of returning false when the stored value is not a valid
    /// bcrypt hash, rather than treating it as "does not match" - which took the whole login
    /// endpoint down with an unhandled 500 for every candidate sharing that identifier, not
    /// just the one with the bad hash. A malformed hash is still a login that must fail, just
    /// not by crashing.
    /// </summary>
    private static bool PasswordMatches(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return BCryptNet.Verify(password, hash); }
        catch { return false; }
    }

    private static string LockoutMessage(DateTimeOffset lockedUntil)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTimeOffset.UtcNow).TotalMinutes));
        return $"Too many failed attempts. Try again in {minutes} minute(s), or use 'Forgot password' to reset it now.";
    }

    /// <summary>Never throws — a reset email that fails to send must not surface as a login-endpoint 500.</summary>
    private async Task SendLockoutResetEmailAsync(string? email, string targetName, string token)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        try
        {
            var resetLink = _appUrls.BuildResetPasswordLink(email, token);
            var htmlBody = PasswordResetEmailTemplate.ComposeForLockout(targetName, resetLink);
            await _emailService.SendEmailAsync(email, PasswordResetEmailTemplate.Subject, htmlBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemberService] Failed to send lockout reset email to {email}: {ex.Message}");
        }
    }

    public async Task<PaginatedResult<MemberDto>> GetMembersAsync(Guid branchId, string? search, int page = 1, int pageSize = 50, bool includeDeleted = false)
    {
        var query = _unitOfWork.Repository<Member>().Query()
            .Include(m => m.HomeBranch)
            .AsQueryable();

        if (!includeDeleted)
            query = query.Where(m => m.Status != MemberStatus.Suspended);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(m => 
                m.MobileNumber.Contains(s) || 
                m.FullName.ToLower().Contains(s) || 
                m.MemberNumber.ToLower().Contains(s) ||
                (m.Username != null && m.Username.ToLower().Contains(s)));
        }

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(m => m.JoinDate)
                               .Skip((page - 1) * pageSize)
                               .Take(pageSize)
                               .ToListAsync();

        var dtos = items.Select(MapToDto).ToList();
        return new PaginatedResult<MemberDto>(dtos, total, page, pageSize);
    }

    public async Task<MemberDto> GetMemberByIdAsync(Guid id)
    {
        var member = await _unitOfWork.Repository<Member>().Query()
            .Include(m => m.HomeBranch)
            .FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new NotFoundException("Member not found.");

        return MapToDto(member);
    }

    public async Task<MemberDto> GetMemberByMobileAsync(string mobileNumber)
    {
        var member = await _unitOfWork.Repository<Member>().Query()
            .Include(m => m.HomeBranch)
            .FirstOrDefaultAsync(m => m.MobileNumber == mobileNumber)
            ?? throw new NotFoundException($"Member with mobile {mobileNumber} not found.");

        return MapToDto(member);
    }

    public async Task<MemberDto> RegisterMemberAsync(Guid branchId, Guid operatorId, RegisterMemberDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Email))
            dto.Email = dto.Email.Trim().ToLowerInvariant();

        // Full Name must be unique among ACTIVE members — phone/email can be shared freely
        // (e.g. a sibling with no phone of their own uses a family member's phone + email).
        // Deleted (Suspended) members free up their name for reuse by a new member.
        var normalizedName = dto.FullName.Trim().ToLowerInvariant();
        var nameTaken = await _unitOfWork.Repository<Member>().Query()
            .AnyAsync(m => m.Status != MemberStatus.Suspended && m.FullName.ToLower() == normalizedName);

        if (nameTaken)
            throw new AppException($"A member named '{dto.FullName}' already exists. If this is meant to be a different person, use a different name.");

        // Username uniqueness check — deleted (Suspended) members had their Username cleared
        // on delete (see DeleteMemberAsync), so this only ever matches active members anyway.
        if (!string.IsNullOrWhiteSpace(dto.Username))
        {
            var usernameTaken = await _unitOfWork.Repository<Member>().Query()
                .AnyAsync(m => m.Status != MemberStatus.Suspended && m.Username == dto.Username.Trim().ToLowerInvariant());
            if (usernameTaken)
                throw new AppException($"Username '{dto.Username}' is already taken.");
        }

        // Member number: MEM-YYMM-<branch>-XXXX
        //
        // The count is of THIS branch's own members, because a branch cannot ask the others
        // how many they have - it has to keep working with the internet unplugged. So every
        // branch counts from one, and without the branch tag Adajan's second member and
        // Citylight's second member are both "MEM-2608-0002".
        //
        // Head Office requires that number to be unique across the whole chain, so the second
        // one to arrive was rejected outright: "duplicate key value violates unique constraint
        // IX_members_MemberNumber". Every wallet top-up for that person was then refused in
        // turn, because as far as Head Office was concerned they had never been created. It
        // looked exactly like one shop's PC being broken; it was simply the second shop to
        // register its Nth member, and it would have happened to any branch on any machine.
        //
        // The tag comes from the branch's own id rather than its name. Two branches can easily
        // be named similarly enough to collide on their first few letters - "Adajan" and
        // "Adajan 2" - and that would quietly bring this whole failure back with no way to
        // notice until money went missing again.
        var count = await _unitOfWork.Repository<Member>().Query().CountAsync() + 1;
        var branchTag = branchId.ToString("N")[..4].ToUpperInvariant();
        var memberNum = $"MEM-{DateTime.UtcNow:yyMM}-{branchTag}-{count:D4}";

        var member = new Member
        {
            // Set here, not left to the database, because the outbox event below is written
            // before this member is ever saved - so anything reading the id now reads an empty
            // one.
            //
            // That is exactly what went wrong. Head Office was told a member had been created
            // with id 00000000-0000-0000-0000-000000000000, dated year 0001. It rejected that,
            // and then rejected every wallet top-up naming the member's real id, because as far
            // as Head Office was concerned that person had never been created - Rs 2,200 of
            // real top-ups stranded at one branch. It looked like two PCs behaving differently
            // when the only difference was which one created a new member.
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,

            MemberNumber = memberNum,
            FullName = dto.FullName,
            MobileNumber = dto.MobileNumber,
            Email = dto.Email,
            Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim().ToLowerInvariant(),
            PasswordHash = string.IsNullOrWhiteSpace(dto.Password) ? null : BCryptNet.HashPassword(dto.Password),
            PasswordChangedAt = string.IsNullOrWhiteSpace(dto.Password) ? null : DateTimeOffset.UtcNow,
            Status = MemberStatus.Active,
            HomeBranchId = branchId,
            JoinDate = DateTimeOffset.UtcNow,
            CreatedBy = operatorId,
            GamingBalance = 0,
            FoodBalance = 0,
            GamingPoints = 0,
            FoodPoints = 0,
            TotalPoints = 0
        };

        await _unitOfWork.Repository<Member>().AddAsync(member);

        // Head Office needs to know this person exists before it can make sense of anything
        // they do. Without it, a wallet top-up arrives naming a member the server has never
        // heard of - which is exactly what happened on the first offline test: Rs 1,000 topped
        // up at a branch, delivered to Head Office, and belonging to nobody up there.
        //
        // Recorded here rather than at the wallet, because the member has to exist first. The
        // outbox preserves order, so a member created and topped up while offline arrives in
        // the order it happened.
        await _outbox.RecordEventAsync(branchId, "Member", member.Id, "member.created", new
        {
            memberId = member.Id,
            memberNumber = member.MemberNumber,
            fullName = member.FullName,
            mobileNumber = member.MobileNumber,
            email = member.Email,
            username = member.Username,
            // Without this, a member's very first password - set right here at registration -
            // never reached Head Office at all: Head Office created its own copy of this person
            // with no password, forever, and every OTHER branch that later learned about this
            // member (members are shared across all branches) inherited that same blank
            // password from Head Office. The member could only ever log in at the one branch
            // where they signed up.
            passwordHash = member.PasswordHash,
            passwordChangedAt = member.PasswordChangedAt,
            createdAt = member.CreatedAt,
            createdBy = operatorId,
        });

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = AuditActions.MemberCreate,
            BranchId = branchId,
            TargetType = "member",
            TargetId = member.Id,
            Details = new { MemberNumber = member.MemberNumber, FullName = dto.FullName }
        });

        await _unitOfWork.CommitTransactionAsync();

        // Send Email Notification
        var branchName = "Unknown Branch";
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch != null) branchName = branch.Name;

        string emailBody = $@"
        <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
            <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #dc2626;'>
                    <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                        <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                    </h1>
                </div>
                <div style='padding: 40px 30px; text-align: left;'>
                    <h2 style='margin-top: 0; color: #3b82f6; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>New Member Joined</h2>
                    <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>A new member has officially joined the Apple Esports system.</p>
                    
                    <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Name:</span> <strong style='color: #ffffff;'>{member.FullName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Mobile:</span> <strong style='color: #ffffff;'>{member.MobileNumber}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Member ID:</span> <strong style='color: #ffffff;'>{member.MemberNumber}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Branch:</span> <strong style='color: #ffffff;'>{branchName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Time:</span> <strong style='color: #ffffff;'>{member.JoinDate.ToString("MMM dd, yyyy HH:mm")}</strong></p>
                    </div>
                </div>
                <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                    <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated notification from Apple Esports ERP.</p>
                    <p style='margin: 5px 0 0 0; color: #4b5563; font-size: 11px;'>© {DateTime.UtcNow.Year} Apple Esports. All rights reserved.</p>
                </div>
            </div>
        </div>";

        await SendNotificationAsync($"New Member Joined: {member.FullName} (ID: {member.MemberNumber})", emailBody);

        return MapToDto(member);
    }

    public async Task<MemberDto> UpdateMemberAsync(Guid branchId, Guid operatorId, Guid id, UpdateMemberDto dto)
    {
        var member = await _unitOfWork.Repository<Member>().GetByIdAsync(id)
            ?? throw new NotFoundException("Member not found.");

        if (!string.IsNullOrWhiteSpace(dto.Email))
            dto.Email = dto.Email.Trim().ToLowerInvariant();

        // Same rule as registration: Full Name must be unique among active members.
        var normalizedName = dto.FullName.Trim().ToLowerInvariant();
        var nameTaken = await _unitOfWork.Repository<Member>().Query()
            .AnyAsync(m => m.Id != id && m.Status != MemberStatus.Suspended && m.FullName.ToLower() == normalizedName);

        if (nameTaken)
            throw new AppException($"A member named '{dto.FullName}' already exists. If this is meant to be a different person, use a different name.");

        member.FullName = dto.FullName;
        member.MobileNumber = dto.MobileNumber;
        member.Email = dto.Email;
        member.UpdatedAt = DateTimeOffset.UtcNow;

        // Update username if provided
        if (dto.DisableLogin == true)
        {
            member.Username = null;
            member.SetPassword(null, DateTimeOffset.UtcNow);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(dto.Username))
            {
                var newUsername = dto.Username.Trim().ToLowerInvariant();
                var usernameTaken = await _unitOfWork.Repository<Member>().Query()
                    .AnyAsync(m => m.Status != MemberStatus.Suspended && m.Username == newUsername && m.Id != id);
                if (usernameTaken)
                    throw new AppException($"Username '{dto.Username}' is already taken.");

                member.Username = newUsername;
            }

            // Update password if provided
            if (!string.IsNullOrWhiteSpace(dto.Password))
                member.SetPassword(BCryptNet.HashPassword(dto.Password), DateTimeOffset.UtcNow);
        }

        _unitOfWork.Repository<Member>().Update(member);

        // Head Office's own copy of this member never moved when this changed - UpdateMemberAsync
        // never emitted anything, and no "member.updated" event type existed anywhere in the sync
        // system at all. A walk-in registration with no/placeholder email, corrected here minutes
        // or days later (an extremely common real workflow), left Head Office holding the ORIGINAL
        // stale email forever. That mismatch is exactly why a member's first password-setup email -
        // sent to the branch's current, correct address - later failed "invalid or expired reset
        // token": CompletePasswordResetAsync matches on Email AND Token together, the token was
        // right, and Head Office's stale Email never matched what was actually emailed to them.
        await _outbox.RecordEventAsync(branchId, "Member", member.Id, "member.updated", new
        {
            memberId = member.Id,
            fullName = member.FullName,
            mobileNumber = member.MobileNumber,
            email = member.Email,
            username = member.Username,
            // Same gap as member.created's own note: an operator resetting a member's password
            // at the counter - the normal, in-person way "forgot password" actually gets handled
            // - saved correctly here and then never told Head Office at all. Head Office kept
            // holding whatever password it last knew, and would go on to push THAT one back down
            // to every branch on the next heartbeat, silently undoing the reset that was just done.
            passwordHash = member.PasswordHash,
            passwordChangedAt = member.PasswordChangedAt,
            updatedAt = member.UpdatedAt,
            updatedBy = operatorId,
        });

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = "member_update",
            BranchId = branchId,
            TargetType = "member",
            TargetId = member.Id,
            Details = new { MemberNumber = member.MemberNumber }
        });

        await _unitOfWork.CommitTransactionAsync();

        return MapToDto(member);
    }

    public async Task DeleteMemberAsync(Guid branchId, Guid operatorId, Guid id)
    {
        var member = await _unitOfWork.Repository<Member>().GetByIdAsync(id)
            ?? throw new NotFoundException("Member not found.");

        // Soft delete: set status to Suspended
        member.Status = MemberStatus.Suspended;
        member.UpdatedAt = DateTimeOffset.UtcNow;
        // Free up the Username for reuse immediately (there's a real DB-level unique constraint
        // on Username, not just the app-level check) — a deleted member also shouldn't retain login access.
        member.Username = null;
        member.PasswordHash = null;
        member.ResetToken = null;
        member.ResetTokenExpiry = null;

        _unitOfWork.Repository<Member>().Update(member);

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = "member_delete",
            BranchId = branchId,
            TargetType = "member",
            TargetId = member.Id,
            Details = new { MemberNumber = member.MemberNumber, FullName = member.FullName }
        });

        await _unitOfWork.CommitTransactionAsync();

        // Send Email Notification
        string emailBody = $@"
        <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
            <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #dc2626;'>
                    <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                        <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                    </h1>
                </div>
                <div style='padding: 40px 30px; text-align: left;'>
                    <h2 style='margin-top: 0; color: #ef4444; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Member Deleted</h2>
                    <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>A member account has been removed from the system.</p>
                    
                    <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Name:</span> <strong style='color: #ffffff;'>{member.FullName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Mobile:</span> <strong style='color: #ffffff;'>{member.MobileNumber}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Member ID:</span> <strong style='color: #ffffff;'>{member.MemberNumber}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Time:</span> <strong style='color: #ffffff;'>{DateTimeOffset.UtcNow.ToString("MMM dd, yyyy HH:mm")}</strong></p>
                    </div>
                </div>
                <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                    <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated security notification from Apple Esports ERP.</p>
                    <p style='margin: 5px 0 0 0; color: #4b5563; font-size: 11px;'>© {DateTime.UtcNow.Year} Apple Esports. All rights reserved.</p>
                </div>
            </div>
        </div>";

        await SendNotificationAsync($"Member Suspended/Deleted: {member.FullName} (ID: {member.MemberNumber})", emailBody);
    }

    /// <summary>Super Admin only: direct override of any value on a member's profile.
    /// Gaming/Food balance changes also create a "Correction" wallet transaction for an audit trail;
    /// every other field just changes directly, with a single audit log entry summarizing the edit.
    ///
    /// <paramref name="remoteAdminName"/> is set only when this is being applied here on behalf
    /// of a Head Office admin (via a branch command), never for a genuinely local edit. WalletTransaction.AdminId
    /// is foreign-keyed to THIS branch's own local `users` table, and a Head Office admin's id
    /// has never been synced down to any branch - Users accounts are Head Office's own, unlike
    /// Operators/Members/menu items. Writing a Head Office id into AdminId therefore failed
    /// with a foreign key violation on every single remote balance edit, unconditionally. When
    /// <paramref name="remoteAdminName"/> is supplied, the actor goes into the non-FK-constrained
    /// RemoteAdminId/RemoteAdminName columns instead, and AdminId is left null.</summary>
    public async Task<MemberDto> AdminEditValuesAsync(
        Guid branchId, Guid adminId, Guid id, AdminEditMemberValuesDto dto, string? remoteAdminName = null)
    {
        // Head Office's own copy of a member is not what the gaming PC at the counter checks -
        // the branch's own row is. Writing here would show the new number on Head Office's
        // screen and change nothing an operator can actually see, exactly the trap
        // RemoteBranchControl's own class comment describes. MembersController routes this to
        // the branch instead whenever it is called from Head Office; this only guards against
        // some other, future caller reaching this method directly and re-creating that trap.
        if (_configuration.IsHeadOffice())
            throw new AppException(
                "A member's balance has to be edited at the branch itself, not written here at " +
                "Head Office - the branch's own copy is what the counter actually reads, and a " +
                "change written only here would be invisible to it.");

        var member = await _unitOfWork.Repository<Member>().GetByIdAsync(id)
            ?? throw new NotFoundException("Member not found.");

        var changes = new Dictionary<string, object>();

        async Task ApplyBalanceChangeAsync(WalletType wallet, decimal? newValue)
        {
            if (!newValue.HasValue) return;
            var before = wallet == WalletType.Gaming ? member.GamingBalance : member.FoodBalance;
            if (newValue.Value == before) return;

            if (wallet == WalletType.Gaming) member.GamingBalance = newValue.Value;
            else member.FoodBalance = newValue.Value;

            changes[$"{wallet}Balance"] = new { before, after = newValue.Value };

            await _unitOfWork.Repository<WalletTransaction>().AddAsync(new WalletTransaction
            {
                MemberId = id,
                BranchId = branchId,
                AdminId = remoteAdminName is null ? adminId : (Guid?)null,
                RemoteAdminId = remoteAdminName is null ? (Guid?)null : adminId,
                RemoteAdminName = remoteAdminName,
                Action = WalletAction.Correction,
                TargetWallet = wallet,
                Amount = newValue.Value - before,
                BalanceBefore = before,
                BalanceAfter = newValue.Value,
                PaymentType = "Admin Edit",
                CashAmount = 0,
                OnlineAmount = 0,
                BonusAmount = 0,
                Reason = dto.Reason ?? "Super Admin direct balance edit",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await ApplyBalanceChangeAsync(WalletType.Gaming, dto.GamingBalance);
        await ApplyBalanceChangeAsync(WalletType.Food, dto.FoodBalance);

        void ApplyFieldChange<T>(string name, T? newValue, Action<T> setter, T currentValue) where T : struct
        {
            if (!newValue.HasValue || EqualityComparer<T>.Default.Equals(newValue.Value, currentValue)) return;
            changes[name] = new { before = currentValue, after = newValue.Value };
            setter(newValue.Value);
        }

        ApplyFieldChange("TotalGamingTopUps", dto.TotalGamingTopUps, v => member.TotalGamingTopUps = v, member.TotalGamingTopUps);
        ApplyFieldChange("TotalGamingBonusEarned", dto.TotalGamingBonusEarned, v => member.TotalGamingBonusEarned = v, member.TotalGamingBonusEarned);
        ApplyFieldChange("TotalGamingSpend", dto.TotalGamingSpend, v => member.TotalGamingSpend = v, member.TotalGamingSpend);
        ApplyFieldChange("TotalFoodSpend", dto.TotalFoodSpend, v => member.TotalFoodSpend = v, member.TotalFoodSpend);
        ApplyFieldChange("GamingPoints", dto.GamingPoints, v => member.GamingPoints = v, member.GamingPoints);
        ApplyFieldChange("FoodPoints", dto.FoodPoints, v => member.FoodPoints = v, member.FoodPoints);
        ApplyFieldChange("TotalPoints", dto.TotalPoints, v => member.TotalPoints = v, member.TotalPoints);

        if (changes.Count == 0)
            return MapToDto(member);

        member.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<Member>().Update(member);

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = adminId,
            UserRole = "SuperAdmin",
            UserName = remoteAdminName ?? "System",
            Action = "admin_member_value_edit",
            BranchId = branchId,
            TargetType = "member",
            TargetId = member.Id,
            Details = new { MemberNumber = member.MemberNumber, Changes = changes, Reason = dto.Reason }
        });

        // This method only ever runs on a branch's own database (Head Office refuses it above)
        // - it never once told Head Office's own mirrored copy of this member what the new
        // balance is. A Super Admin editing a balance saw it change on the operator's screen
        // (the branch, correctly, applied it) but their own Head Office dashboard kept showing
        // the OLD number until something unrelated happened to resync the member. Same gap for
        // a genuinely local branch-admin edit, not just a remote one - neither path ever synced.
        await _outbox.RecordEventAsync(branchId, "Member", member.Id, "member.balance_adjusted", new
        {
            memberId = member.Id,
            gamingBalance = member.GamingBalance,
            foodBalance = member.FoodBalance,
            totalGamingTopUps = member.TotalGamingTopUps,
            totalGamingBonusEarned = member.TotalGamingBonusEarned,
            totalGamingSpend = member.TotalGamingSpend,
            totalFoodSpend = member.TotalFoodSpend,
            gamingPoints = member.GamingPoints,
            foodPoints = member.FoodPoints,
            totalPoints = member.TotalPoints,
            updatedAt = member.UpdatedAt,
        });

        await _unitOfWork.CommitTransactionAsync();

        // The operator PC (session start, wallet desk) only ever refetched on its own next
        // action before this - a balance changed here, whether by a local branch admin or
        // relayed from Head Office, was invisible until then.
        try { await _hubNotifications.BroadcastMemberBalanceUpdateAsync(branchId, member.Id); }
        catch { /* best effort - the balance change itself already succeeded */ }

        return MapToDto(member);
    }

    private async Task SendNotificationAsync(string subject, string body)
    {
        try 
        {
            var superAdmins = await _unitOfWork.Repository<Operator>().Query()
                .Where(o => o.IsGlobalAdmin && o.Status == OperatorStatus.Active)
                .ToListAsync();

            foreach (var admin in superAdmins)
            {
                if (!string.IsNullOrWhiteSpace(admin.Email))
                {
                    await _emailService.SendEmailAsync(admin.Email, subject, body);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemberService] Failed to send email notification: {ex.Message}");
        }
    }

    public async Task<MemberLoginResponseDto> LoginMemberAsync(MemberLoginDto dto)
    {
        var identifier = dto.Identifier.Trim().ToLowerInvariant();

        // Login is by Username or Email + password only (no phone number — phone/email can
        // now be shared between household members, so the password is what disambiguates
        // which exact account this is).
        var candidates = await _unitOfWork.Repository<Member>().Query()
            .Where(m => (m.Username != null && m.Username.ToLower() == identifier) ||
                        (m.Email != null && m.Email.ToLower() == identifier))
            .ToListAsync();

        var member = candidates.FirstOrDefault(m => PasswordMatches(dto.Password, m.PasswordHash));

        if (member != null && IsLocked(member.LockedUntil))
            throw new AuthorizationException(LockoutMessage(member.LockedUntil!.Value), "ACCOUNT_LOCKED");

        if (member == null)
        {
            // Identifier can be shared across members (e.g. household email) — since we can't
            // tell which account the attempt was aimed at, count it against every candidate it
            // could match rather than letting an attacker pick off a shared identifier for free.
            foreach (var candidate in candidates)
            {
                if (IsLocked(candidate.LockedUntil)) continue;

                candidate.FailedAttempts++;
                if (candidate.FailedAttempts >= MaxFailedAttempts)
                {
                    candidate.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                    var resetToken = Guid.NewGuid().ToString("N");
                    candidate.ResetToken = resetToken;
                    candidate.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(1);

                    // The reset link always points at Head Office (AppUrlProvider), but this
                    // token was only ever written into the branch's own local row - Member
                    // updates were never part of ordinary sync. Without this, Head Office has
                    // no way to recognise the token and every lockout email's link failed with
                    // "Invalid or expired reset token" on a token that was never actually spent
                    // or expired - it just never arrived. Same gap WalletService's own welcome
                    // email already closes for a first top-up; this is the equivalent for a
                    // lockout.
                    if (candidate.HomeBranchId is { } homeBranchId)
                    {
                        try
                        {
                            await _outbox.RecordEventAsync(homeBranchId, "Member", candidate.Id, "member.reset_requested", new
                            {
                                memberId = candidate.Id,
                                email = candidate.Email,
                                resetToken,
                                resetTokenExpiry = candidate.ResetTokenExpiry,
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Could not queue the lockout reset token for member {MemberId} to Head Office.", candidate.Id);
                        }
                    }

                    await SendLockoutResetEmailAsync(candidate.Email, candidate.FullName, resetToken);
                    await _auditService.LogAsync(new AuditEntry
                    {
                        UserRole = "Member",
                        UserName = candidate.FullName,
                        Action = AuditActions.AccountLocked,
                        Success = false,
                        TargetType = "member",
                        TargetId = candidate.Id,
                        BranchId = candidate.HomeBranchId,
                        Details = new { reason = "5 failed password attempts", lockedUntil = candidate.LockedUntil },
                    });
                }

                // One row per candidate this identifier could have meant, each tagged with that
                // candidate's own branch - without this, every member login failure was written
                // with no branch at all (see the AccountLocked entry above, which already did
                // this correctly) and SyncCapture silently drops anything it cannot file against
                // a branch. Confirmed live: in the entire audit history, operator login failures
                // showed up by the hundreds and member ones showed up exactly once - not because
                // members rarely get their password wrong, but because this row never left
                // whichever branch wrote it.
                await _auditService.LogAsync(new AuditEntry
                {
                    UserRole = "Member",
                    UserName = candidate.FullName,
                    Action = AuditActions.FailedLogin,
                    Success = false,
                    TargetType = "member",
                    TargetId = candidate.Id,
                    BranchId = candidate.HomeBranchId,
                    Details = new { reason = "Invalid password", identifier },
                });
            }
            await _unitOfWork.SaveChangesAsync();

            // No candidate at all - the identifier itself does not match anything. Nothing to
            // tag with a branch, since no member account was even found to be ambiguous about.
            if (candidates.Count == 0)
            {
                await _auditService.LogAsync(new AuditEntry
                {
                    UserRole = "Member",
                    UserName = identifier,
                    Action = AuditActions.FailedLogin,
                    Success = false,
                    Details = new { reason = "Invalid username or password" },
                });
            }

            throw new AuthenticationException("Invalid username or password.", "INVALID_CREDENTIALS");
        }

        if (member.Status != MemberStatus.Active)
            throw new AuthorizationException("Member account is inactive.", "ACCOUNT_INACTIVE");

        member.FailedAttempts = 0;
        member.LockedUntil = null;
        await _unitOfWork.SaveChangesAsync();

        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = member.Id.ToString(),
            [ClaimTypes.Role] = "Member",
            [ClaimTypes.Name] = member.FullName,
            ["memberNumber"] = member.MemberNumber,
        };

        var token = _jwt.GenerateAccessToken(claims);

        return new MemberLoginResponseDto
        {
            MemberId = member.Id,
            MemberNumber = member.MemberNumber,
            FullName = member.FullName,
            GamingBalance = member.GamingBalance,
            FoodBalance = member.FoodBalance,
            Token = token,
        };
    }

    private static MemberDto MapToDto(Member m)
    {
        return new MemberDto
        {
            Id = m.Id,
            MemberNumber = m.MemberNumber,
            FullName = m.FullName,
            MobileNumber = m.MobileNumber,
            Email = m.Email,
            Username = m.Username,
            HasPassword = !string.IsNullOrEmpty(m.PasswordHash),
            Status = m.Status,
            GamingBalance = m.GamingBalance,
            FoodBalance = m.FoodBalance,
            TotalGamingTopUps = m.TotalGamingTopUps,
            TotalGamingBonusEarned = m.TotalGamingBonusEarned,
            TotalGamingSpend = m.TotalGamingSpend,
            TotalFoodSpend = m.TotalFoodSpend,
            GamingPoints = m.GamingPoints,
            FoodPoints = m.FoodPoints,
            TotalPoints = m.TotalPoints,
            JoinDate = m.JoinDate,
            LastVisit = m.LastVisit,
            HomeBranchName = m.HomeBranch?.Name
        };
    }

    public async Task<List<MemberHistoryEntryDto>> GetMemberHistoryAsync(Guid memberId, DateOnly? fromDate, DateOnly? toDate)
    {
        DateTimeOffset? rangeStart = fromDate.HasValue
            ? AppleEsportsErp.Application.Services.IndiaTime.BusinessDayRange(fromDate.Value).Start
            : null;
        DateTimeOffset? rangeEnd = toDate.HasValue
            ? AppleEsportsErp.Application.Services.IndiaTime.BusinessDayRange(toDate.Value).End
            : null;

        var sessionsQuery = _unitOfWork.Repository<Session>().Query()
            .Where(s => s.MemberId == memberId);
        if (rangeStart.HasValue) sessionsQuery = sessionsQuery.Where(s => s.StartTime >= rangeStart.Value);
        if (rangeEnd.HasValue) sessionsQuery = sessionsQuery.Where(s => s.StartTime < rangeEnd.Value);

        var sessions = await sessionsQuery
            .Include(s => s.Pc)
            .Include(s => s.Branch)
            .ToListAsync();

        var walletQuery = _unitOfWork.Repository<WalletTransaction>().Query()
            .Where(w => w.MemberId == memberId);
        if (rangeStart.HasValue) walletQuery = walletQuery.Where(w => w.CreatedAt >= rangeStart.Value);
        if (rangeEnd.HasValue) walletQuery = walletQuery.Where(w => w.CreatedAt < rangeEnd.Value);

        var walletTxs = await walletQuery
            .Include(w => w.Branch)
            .ToListAsync();

        var entries = new List<MemberHistoryEntryDto>();

        entries.AddRange(sessions.Select(s => new MemberHistoryEntryDto
        {
            Id = s.Id,
            Type = "Session",
            Timestamp = s.StartTime,
            BranchId = s.BranchId,
            BranchName = s.Branch?.Name ?? "",
            PcName = s.Pc?.PcName ?? s.Pc?.PcNumber,
            DurationMinutes = s.ActualDurationMin ?? s.PlannedDurationMin,
            Amount = s.TotalAmount,
            Description = $"Gaming session - {s.GamingType}"
        }));

        entries.AddRange(walletTxs.Select(w => new MemberHistoryEntryDto
        {
            Id = w.Id,
            Type = w.Action == WalletAction.Recharge ? "WalletTopUp" : "WalletDeduction",
            Timestamp = w.CreatedAt,
            BranchId = w.BranchId,
            BranchName = w.Branch?.Name ?? "",
            PcName = null,
            DurationMinutes = null,
            Amount = w.Amount,
            Description = $"{w.Action} - {w.TargetWallet}" + (string.IsNullOrEmpty(w.Reason) ? "" : $" ({w.Reason})")
        }));

        return entries.OrderByDescending(e => e.Timestamp).ToList();
    }
}
