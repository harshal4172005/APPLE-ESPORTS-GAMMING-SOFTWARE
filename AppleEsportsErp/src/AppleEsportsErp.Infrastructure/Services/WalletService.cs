using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Wallets;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

public class WalletService : IWalletService
{
    // Fallback defaults, used only if Super Admin has never saved custom Wallet Settings.
    // Live values are editable via WalletSettingsController → SystemConfig["wallet_topup_rules"].
    private const decimal DefaultMinGamingTopUp = 500m;
    private const decimal DefaultBonusPercent = 10m;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly IAppUrlProvider _appUrls;
    private readonly ILogger<WalletService> _logger;

    private readonly IOutboxService _outbox;

    public WalletService(IUnitOfWork unitOfWork, IAuditService auditService, IEmailService emailService, IConfiguration configuration, IAppUrlProvider appUrls, ILogger<WalletService> logger, IOutboxService outbox, IAdminNotifier adminNotifier)
    {
        _adminNotifier = adminNotifier;
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _emailService = emailService;
        _configuration = configuration;
        _appUrls = appUrls;
        _logger = logger;
        _outbox = outbox;
    }

    private readonly IAdminNotifier _adminNotifier;

    /// <summary>
    /// Same as AuthService.ShareMemberResetTokenAsync, duplicated rather than shared because that
    /// one is private to AuthService - the token this member's setup link needs, sent up to Head
    /// Office so the link (which always points there) can be honoured. Never lets a queueing
    /// failure take down the top-up itself; the member still gets their receipt and the welcome
    /// email either way, and a lost sync here is recoverable, an unwound top-up is not.
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
            _logger.LogError(ex, "Could not queue the setup token for member {MemberId} to Head Office.", member.Id);
        }
    }

    private async Task<(decimal minGamingTopUp, decimal defaultBonusPercent)> GetTopUpRulesAsync()
    {
        var config = await _unitOfWork.Repository<SystemConfig>().Query()
            .FirstOrDefaultAsync(c => c.ConfigKey == SystemConfigKeys.WalletTopUpRules);

        if (config == null)
            return (DefaultMinGamingTopUp, DefaultBonusPercent);

        try
        {
            var rules = JsonSerializer.Deserialize<WalletTopUpRulesDto>(config.ConfigValue);
            return rules != null
                ? (rules.MinGamingTopUp, rules.DefaultBonusPercent)
                : (DefaultMinGamingTopUp, DefaultBonusPercent);
        }
        catch
        {
            return (DefaultMinGamingTopUp, DefaultBonusPercent);
        }
    }

    public async Task<WalletTransactionDto> TopUpWalletAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid memberId, TopUpWalletDto dto, bool isSuperAdmin)
    {
        if (dto.Amount <= 0)
            throw new AppException("Top-up amount must be greater than zero.");

        var (minGamingTopUp, defaultBonusPercent) = await GetTopUpRulesAsync();

        var isGaming = dto.TargetWallet == WalletType.Gaming;

        // Skip minimum validation for bonus-only transfers (Extra Bonus feature)
        if (isGaming && dto.Amount < minGamingTopUp && !dto.IsBonusOnly)
            throw new AppException($"Minimum Gaming wallet top-up is ₹{minGamingTopUp:0}.");

        var isSplit = dto.PaymentType.Equals("Split", StringComparison.OrdinalIgnoreCase);
        if (isSplit)
        {
            var splitTotal = (dto.CashAmount ?? 0) + (dto.OnlineAmount ?? 0);
            if (Math.Abs(splitTotal - dto.Amount) > 0.01m)
                throw new AppException("Split cash + online amounts must add up to the total top-up amount.");
        }

        // Balance granted now, nothing received yet - see MemberTopUpCredit's own docstring for
        // why this writes its own table instead of reusing CustomerCredit (which requires a
        // bill this top-up doesn't have).
        var isCredit = dto.PaymentType.Equals("Credit", StringComparison.OrdinalIgnoreCase);

        var member = await _unitOfWork.Repository<Member>().GetByIdAsync(memberId)
            ?? throw new NotFoundException("Member not found.");

        // Gaming top-ups earn a bonus (gaming-only, no equivalent for Food) — the admin-configured
        // default rate, unless Super Admin overrides it for this specific top-up.
        var bonusRate = (isSuperAdmin && dto.BonusPercentOverride.HasValue)
            ? dto.BonusPercentOverride.Value / 100m
            : defaultBonusPercent / 100m;
        var bonusAmount = isGaming ? Math.Round(dto.Amount * bonusRate, 2) : 0m;
        var totalCredit = dto.Amount + bonusAmount;

        var balanceBefore = isGaming ? member.GamingBalance : member.FoodBalance;

        if (isGaming)
        {
            member.GamingBalance += totalCredit;
            member.TotalGamingTopUps += dto.Amount;
            member.TotalGamingBonusEarned += bonusAmount;
        }
        else
        {
            member.FoodBalance += totalCredit;
        }

        var balanceAfter = isGaming ? member.GamingBalance : member.FoodBalance;

        _unitOfWork.Repository<Member>().Update(member);

        var walletTx = new WalletTransaction
        {
            MemberId = memberId,
            BranchId = branchId,
            OperatorId = operatorId,
            ShiftId = shiftId,
            Action = WalletAction.Recharge,
            TargetWallet = dto.TargetWallet,
            Amount = dto.Amount,
            BalanceBefore = balanceBefore,
            BalanceAfter = balanceAfter,
            PaymentType = dto.PaymentType,
            CashAmount = isSplit ? (dto.CashAmount ?? 0) : (dto.PaymentType.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? dto.Amount : 0),
            OnlineAmount = isSplit ? (dto.OnlineAmount ?? 0) : (dto.PaymentType.Equals("Online", StringComparison.OrdinalIgnoreCase) ? dto.Amount : 0),
            BonusAmount = bonusAmount,
            Reason = dto.Reason ?? "Manual Top-Up",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _unitOfWork.Repository<WalletTransaction>().AddAsync(walletTx);

        // If Cash, we must also update the Cash Register (SOP §10.3)
        if (walletTx.CashAmount > 0)
        {
            var activeRegister = await _unitOfWork.Repository<CashRegister>().Query()
                .FirstOrDefaultAsync(cr => cr.BranchId == branchId && cr.ShiftId == shiftId && cr.Status == CashRegisterStatus.Open);
                
            if (activeRegister == null)
                throw new AppException("Cannot process cash top-up: No active cash register found for this shift.");

            activeRegister.ExpectedDrawerCash += walletTx.CashAmount;
            _unitOfWork.Repository<CashRegister>().Update(activeRegister);

            // CRITICAL: CashAmount is the actual cash received from the member (₹1000).
            // Bonus (₹100) is internal wallet credit only, NEVER added to the cash drawer.
            var cashTx = new CashTransaction
            {
                CashRegisterId = activeRegister.Id,
                BranchId = branchId,
                OperatorId = operatorId,
                TransactionType = "wallet_recharge",
                CashAmount = walletTx.CashAmount,  // dto.Amount only, never includes bonus
                CashReceived = walletTx.CashAmount,
                ChangeReturned = 0,
                ActualCashCollected = walletTx.CashAmount,
                GamingAmount = 0,
                FoodAmount = 0,
                CustomerName = member.Username,
                BillId = null,  // Wallet top-ups are not tied to bills
                CreatedAt = DateTimeOffset.UtcNow
            };
            await _unitOfWork.Repository<CashTransaction>().AddAsync(cashTx);
        }

        // Balance is already granted above (member.GamingBalance/FoodBalance), same as any
        // other top-up - the member plays now. This just records what's still owed, on its own
        // table, the Credits dashboard reads it from.
        if (isCredit)
        {
            var topUpCredit = new MemberTopUpCredit
            {
                BranchId = branchId,
                OperatorId = operatorId,
                MemberId = member.Id,
                CreditAmount = dto.Amount,
                Status = "pending",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _unitOfWork.Repository<MemberTopUpCredit>().AddAsync(topUpCredit);

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = AuditActions.MemberTopUpCreditGiven,
                BranchId = branchId,
                TargetType = "member_topup_credit",
                TargetId = topUpCredit.Id,
                Details = new { MemberUsername = member.Username, Amount = dto.Amount }
            });
        }

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = AuditActions.WalletRecharge,
            BranchId = branchId,
            TargetType = "wallet",
            TargetId = member.Id,
            Details = new { Amount = dto.Amount, PaymentType = dto.PaymentType }
        });

        // Stamped on this branch's own copy the moment the money actually moved, so that if
        // Head Office later hands this member back down with an older figure - the sync had
        // not caught up yet - this branch can tell its own number is the newer one and refuse
        // to be overwritten by its own top-up arriving late.
        var occurredAt = DateTimeOffset.UtcNow;
        member.BalanceAsOf = occurredAt;
        _unitOfWork.Repository<Member>().Update(member);

        // A wallet is shared across all four branches, so Head Office must learn about a
        // top-up quickly — it is the only place that can spot the same balance being spent
        // in two towns at once. Sent with the resulting balance, not just the delta, so a
        // batch that arrives out of order can still be reconciled.
        await _outbox.RecordEventAsync(branchId, "Member", member.Id, "wallet.topped_up", new
        {
            memberId = member.Id,
            memberUsername = member.Username,
            walletTransactionId = walletTx.Id,
            operatorId,
            shiftId,
            cashAmount = walletTx.CashAmount,
            onlineAmount = walletTx.OnlineAmount,
            bonusAmount,
            totalCredit,
            paymentType = dto.PaymentType,
            gamingBalanceAfter = member.GamingBalance,
            foodBalanceAfter = member.FoodBalance,
            occurredAt,
        });

        await _unitOfWork.CommitTransactionAsync();

        await SendTopUpEmailsAsync(member, dto.Amount, bonusAmount, totalCredit);

        return MapToDto(walletTx);
    }

    public async Task<WalletTransactionDto> DeductWalletAsync(Guid branchId, Guid operatorId, Guid? shiftId, Guid memberId, DeductWalletDto dto, bool commit = true)
    {
        if (dto.Amount <= 0)
            throw new AppException("Deduction amount must be greater than zero.");

        var member = await _unitOfWork.Repository<Member>().GetByIdAsync(memberId)
            ?? throw new NotFoundException("Member not found.");

        var isGaming = dto.TargetWallet == WalletType.Gaming;
        var balanceBefore = isGaming ? member.GamingBalance : member.FoodBalance;

        if (balanceBefore < dto.Amount)
            throw new AppException($"Insufficient {dto.TargetWallet} wallet balance. Current: {balanceBefore}, Required: {dto.Amount}");

        if (isGaming)
            member.GamingBalance -= dto.Amount;
        else
            member.FoodBalance -= dto.Amount;

        var balanceAfter = isGaming ? member.GamingBalance : member.FoodBalance;
        var occurredAt = DateTimeOffset.UtcNow;
        member.BalanceAsOf = occurredAt;

        _unitOfWork.Repository<Member>().Update(member);

        var walletTx = new WalletTransaction
        {
            MemberId = memberId,
            BranchId = branchId,
            OperatorId = operatorId,
            ShiftId = shiftId,
            Action = WalletAction.Correction,
            TargetWallet = dto.TargetWallet,
            Amount = dto.Amount,
            BalanceBefore = balanceBefore,
            BalanceAfter = balanceAfter,
            PaymentType = "Wallet",
            CashAmount = 0,
            OnlineAmount = 0,
            BillId = dto.BillId,
            Reason = dto.Reason,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _unitOfWork.Repository<WalletTransaction>().AddAsync(walletTx);

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = operatorId,
            UserRole = "Operator",
            UserName = "System",
            Action = AuditActions.WalletDeduction,
            BranchId = branchId,
            TargetType = "wallet",
            TargetId = member.Id,
            Details = new { Amount = dto.Amount, Reason = dto.Reason }
        });

        // The missing half of wallet sync. Top-ups reached Head Office; a member spending
        // their wallet at the counter - the far more common case - never did, so Head
        // Office's figure could only ever climb and never fall. Sending only top-ups upward
        // is worse than sending nothing: it produces a confident, wrong, ever-inflating
        // number rather than an honestly stale one.
        await _outbox.RecordEventAsync(branchId, "Member", member.Id, "wallet.deducted", new
        {
            memberId = member.Id,
            walletTransactionId = walletTx.Id,
            operatorId,
            shiftId,
            targetWallet = dto.TargetWallet.ToString(),
            amount = dto.Amount,
            gamingBalanceAfter = member.GamingBalance,
            foodBalanceAfter = member.FoodBalance,
            billId = dto.BillId,
            reason = dto.Reason,
            occurredAt,
        });

        if (commit)
        {
            await _unitOfWork.CommitTransactionAsync();
        }

        return MapToDto(walletTx);
    }

    public async Task<PaginatedResult<WalletTransactionDto>> GetWalletHistoryAsync(Guid memberId, int page = 1, int pageSize = 50)
    {
        var query = _unitOfWork.Repository<WalletTransaction>().Query()
            .Where(w => w.MemberId == memberId)
            .OrderByDescending(w => w.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var dtos = items.Select(MapToDto).ToList();
        return new PaginatedResult<WalletTransactionDto>(dtos, total, page, pageSize);
    }

    private static WalletTransactionDto MapToDto(WalletTransaction t)
    {
        return new WalletTransactionDto
        {
            Id = t.Id,
            MemberId = t.MemberId,
            Action = t.Action,
            TargetWallet = t.TargetWallet,
            Amount = t.Amount,
            BonusAmount = t.BonusAmount,
            BalanceBefore = t.BalanceBefore,
            BalanceAfter = t.BalanceAfter,
            PaymentType = t.PaymentType,
            Reason = t.Reason,
            CreatedAt = t.CreatedAt
        };
    }

    private async Task SendTopUpEmailsAsync(Member member, decimal amount, decimal bonusAmount, decimal totalCredit)
    {
        if (string.IsNullOrWhiteSpace(member.Email))
            return;

        try
        {
            var receiptBody = $@"
                <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
                    <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                        <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #3b82f6;'>
                            <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                                <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                            </h1>
                        </div>
                        <div style='padding: 40px 30px; text-align: left;'>
                            <h2 style='margin-top: 0; color: #3b82f6; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Wallet Top-Up Receipt</h2>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Hi <strong>{member.FullName}</strong>,</p>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Your wallet top-up has been processed successfully.</p>
                            <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                                <p style='margin: 10px 0; color: #d1d5db;'>Top-up amount: <strong style='color: #ffffff;'>₹{amount:0.00}</strong></p>
                                <p style='margin: 10px 0; color: #d1d5db;'>Bonus amount: <strong style='color: #ffffff;'>₹{bonusAmount:0.00}</strong></p>
                                <p style='margin: 10px 0; color: #d1d5db;'>Total credited: <strong style='color: #ffffff;'>₹{totalCredit:0.00}</strong></p>
                            </div>
                        </div>
                        <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                            <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated notification from Apple Esports ERP.</p>
                        </div>
                    </div>
                </div>";

            await _emailService.SendEmailAsync(member.Email, "Apple Esports - Wallet Top-Up Receipt", receiptBody);

            // The owner sees every rupee that goes onto a wallet. Money entering the business
            // outside the till is exactly what someone paying the bills wants sight of, and
            // until now only the member was ever told.
            await _adminNotifier.NotifyAsync(
                $"Money added to a wallet - Rs {amount:0.00} - {member.FullName}",
                AdminEmailTemplate.Compose(
                    "Money added to a member's wallet",
                    AdminEmailTemplate.Green,
                    bonusAmount > 0
                        ? $"{member.FullName} paid Rs {amount:0.00} to top up their wallet. We added a Rs {bonusAmount:0.00} bonus on top, so Rs {totalCredit:0.00} went into the wallet altogether."
                        : $"{member.FullName} paid Rs {amount:0.00} to top up their wallet. No bonus was added to this one.",
                    new[]
                    {
                        ("Member", member.FullName),
                        ("Member number", string.IsNullOrWhiteSpace(member.MemberNumber) ? "-" : member.MemberNumber),
                        ("", ""),
                        ("Money they paid", $"Rs {amount:0.00}"),
                        ("Bonus we added", $"Rs {bonusAmount:0.00}"),
                        ("Total into wallet", $"Rs {totalCredit:0.00}"),
                        ("", ""),
                        ("Gaming balance now", $"Rs {member.GamingBalance:0.00}"),
                        ("Food balance now", $"Rs {member.FoodBalance:0.00}"),
                        ("When", AppleEsportsErp.Application.Services.IndiaTime.Now.ToString("dd MMM yyyy, hh:mm tt")),
                    },
                    headline: $"Rs {amount:0.00}",
                    footnote: "The member has been sent their own receipt. This copy is for your records."));

            if (string.IsNullOrWhiteSpace(member.Username) || !string.IsNullOrWhiteSpace(member.PasswordHash))
                return;

            if (!string.IsNullOrWhiteSpace(member.ResetToken) && member.ResetTokenExpiry > DateTimeOffset.UtcNow)
                return;

            var setupToken = Guid.NewGuid().ToString("N");
            member.ResetToken = setupToken;
            member.ResetTokenExpiry = DateTimeOffset.UtcNow.AddHours(24);

            _unitOfWork.Repository<Member>().Update(member);
            await _unitOfWork.SaveChangesAsync();

            // The link below always points at Head Office (AppUrlProvider.ResetLinkBaseUrl), the
            // same as every other password link this system sends - but this token was just
            // written into the branch's own local row, and Member updates were never part of
            // sync. Without this, Head Office has no way to recognise it and the member's very
            // first link - the one setting up their password at all - fails outright. Same gap
            // AuthService.ShareMemberResetTokenAsync closes for a self-service forgot-password
            // request; this is the equivalent for a first-top-up welcome email.
            await ShareMemberResetTokenAsync(member, setupToken, member.ResetTokenExpiry.Value);

            string resetLink = _appUrls.BuildResetPasswordLink(member.Email, setupToken);
            string welcomeBody = $@"
                <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
                    <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                        <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #3b82f6;'>
                            <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                                <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                            </h1>
                        </div>
                        <div style='padding: 40px 30px; text-align: left;'>
                            <h2 style='margin-top: 0; color: #3b82f6; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Welcome to Apple Esports!</h2>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Hi <strong>{member.FullName}</strong>,</p>
                            <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>Your Apple Esports membership login has been activated following your initial top-up! To log in and use your wallet for food or gaming, you need to set up a secure password.</p>
                            <div style='text-align:center; margin: 40px 0;'>
                                <a href='{resetLink}' style='background: linear-gradient(to right, #2563eb, #3b82f6); color: #ffffff; padding: 16px 32px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 16px; letter-spacing: 1px; display: inline-block; box-shadow: 0 4px 15px rgba(59, 130, 246, 0.4);'>SET MY PASSWORD</a>
                            </div>
                            <p style='color: #6b7280; font-size: 13px; margin-top: 30px;'>This link will expire in 24 hours. If you did not request this, please contact the branch operator.</p>
                        </div>
                        <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                            <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated notification from Apple Esports ERP.</p>
                        </div>
                    </div>
                </div>";

            await _emailService.SendEmailAsync(member.Email, "Welcome to Apple Esports - Setup Your Password", welcomeBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send wallet top-up email for member {MemberId}", member.Id);
        }
    }
}
