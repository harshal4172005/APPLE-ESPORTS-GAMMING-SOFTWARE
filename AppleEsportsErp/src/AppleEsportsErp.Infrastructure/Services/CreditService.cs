using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Credits;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;

    public CreditService(IUnitOfWork unitOfWork, IAuditService auditService)
    {
        _unitOfWork = unitOfWork;
        _auditService = auditService;
    }

    public async Task<PaginatedResult<CreditDto>> GetCreditsAsync(Guid branchId, string status = "pending", int page = 1, int pageSize = 50)
    {
        var query = _unitOfWork.Repository<CustomerCredit>().Query()
            .Where(c => c.BranchId == branchId);

        if (!string.IsNullOrEmpty(status) && status.ToLower() != "all")
        {
            query = query.Where(c => c.Status.ToLower() == status.ToLower());
        }

        query = query.OrderByDescending(c => c.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var dtos = items.Select(c => new CreditDto
        {
            Id = c.Id,
            BranchId = c.BranchId,
            OperatorId = c.OperatorId,
            BillId = c.BillId,
            CustomerName = c.CustomerName,
            CustomerPhone = c.CustomerPhone,
            PcNumber = c.PcNumber,
            OriginalBillAmount = c.OriginalBillAmount,
            AmountPaidInitially = c.AmountPaidInitially,
            CreditAmount = c.CreditAmount,
            Status = c.Status,
            CreatedAt = c.CreatedAt,
            ClearedAt = c.ClearedAt
        }).ToList();

        return new PaginatedResult<CreditDto>(dtos, total, page, pageSize);
    }

    public async Task<CreditDto> ClearCreditAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid creditId, ClearCreditDto dto)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var credit = await _unitOfWork.Repository<CustomerCredit>().Query()
                .FirstOrDefaultAsync(c => c.Id == creditId && c.BranchId == branchId)
                ?? throw new NotFoundException("Credit record not found.");

            if (credit.Status == "cleared")
                throw new AppException("Credit is already cleared.");

            var (cashAmountToApply, cashReceived, changeReturned, actualCashCollected) =
                ResolvePayment(dto, credit.CreditAmount);

            // Create Payment record for the cleared amount
            var payment = new Payment
            {
                BillId = credit.BillId,
                BranchId = branchId,
                OperatorId = operatorId,
                PaymentType = dto.PaymentType,
                TotalAmount = credit.CreditAmount, // equals cashAmountToApply + dto.OnlineAmount - ResolvePayment already validated this
                CashAmount = cashAmountToApply,
                OnlineAmount = dto.OnlineAmount,
                WalletAmount = 0,
                CashReceived = cashReceived,
                ChangeReturned = changeReturned,
                ActualCashCollected = actualCashCollected,
                // Assign portions as 0 or calculate if needed, for simplicity we leave them as 0 since the original bill holds the breakdown
                GamingPortion = 0,
                FoodPortion = 0,
                Status = "completed",
                CreatedAt = DateTimeOffset.UtcNow
            };
            await _unitOfWork.Repository<Payment>().AddAsync(payment);

            // Update Cash Register
            if (dto.CashAmount > 0)
            {
                var activeRegister = await _unitOfWork.Repository<CashRegister>().Query()
                    .FirstOrDefaultAsync(cr => cr.BranchId == branchId && cr.ShiftId == shiftId && cr.Status == CashRegisterStatus.Open)
                    ?? throw new AppException("No active cash register found for this shift.");

                activeRegister.ExpectedDrawerCash += actualCashCollected;
                activeRegister.TotalCashSales += actualCashCollected; // Count as sale on the day it's collected
                _unitOfWork.Repository<CashRegister>().Update(activeRegister);

                var cashTx = new CashTransaction
                {
                    CashRegisterId = activeRegister.Id,
                    BillId = credit.BillId, // Optional, links to original bill
                    BranchId = branchId,
                    OperatorId = operatorId,
                    PcNumber = credit.PcNumber,
                    TransactionType = "credit_clear",
                    CashAmount = cashAmountToApply,
                    CashReceived = cashReceived,
                    ChangeReturned = changeReturned,
                    ActualCashCollected = actualCashCollected,
                    GamingAmount = cashAmountToApply, // Assign entirely to gaming for simplicity or prorate if necessary
                    FoodAmount = 0,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _unitOfWork.Repository<CashTransaction>().AddAsync(cashTx);
            }

            // Mark Credit as Cleared
            credit.Status = "cleared";
            credit.ClearedAt = DateTimeOffset.UtcNow;
            credit.ClearedByOperatorId = operatorId;
            _unitOfWork.Repository<CustomerCredit>().Update(credit);

            // The one action this whole trail exists to answer for, previously missing from it
            // entirely - see AuditActions.CreditClear.
            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = AuditActions.CreditClear,
                BranchId = branchId,
                TargetType = "customer_credit",
                TargetId = credit.Id,
                Details = new
                {
                    CustomerName = credit.CustomerName,
                    Amount = credit.CreditAmount,
                    PaymentType = dto.PaymentType.ToString(),
                    Cash = cashAmountToApply,
                    Online = dto.OnlineAmount,
                },
            });

            await _unitOfWork.CommitTransactionAsync();

            return new CreditDto
            {
                Id = credit.Id,
                BranchId = credit.BranchId,
                OperatorId = credit.OperatorId,
                BillId = credit.BillId,
                CustomerName = credit.CustomerName,
                PcNumber = credit.PcNumber,
                OriginalBillAmount = credit.OriginalBillAmount,
                AmountPaidInitially = credit.AmountPaidInitially,
                CreditAmount = credit.CreditAmount,
                Status = credit.Status,
                CreatedAt = credit.CreatedAt,
                ClearedAt = credit.ClearedAt
            };
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public async Task<PaginatedResult<MemberTopUpCreditDto>> GetMemberTopUpCreditsAsync(
        Guid branchId, string status = "pending", int page = 1, int pageSize = 50)
    {
        var query = _unitOfWork.Repository<MemberTopUpCredit>().Query()
            .Where(c => c.BranchId == branchId);

        if (!string.IsNullOrEmpty(status) && status.ToLower() != "all")
            query = query.Where(c => c.Status.ToLower() == status.ToLower());

        query = query.OrderByDescending(c => c.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Include(c => c.Member)
            .ToListAsync();

        var dtos = items.Select(c => new MemberTopUpCreditDto
        {
            Id = c.Id,
            BranchId = c.BranchId,
            OperatorId = c.OperatorId,
            MemberId = c.MemberId,
            MemberName = c.Member.FullName,
            MemberUsername = c.Member.Username ?? "",
            CreditAmount = c.CreditAmount,
            Status = c.Status,
            CreatedAt = c.CreatedAt,
            ClearedAt = c.ClearedAt,
        }).ToList();

        return new PaginatedResult<MemberTopUpCreditDto>(dtos, total, page, pageSize);
    }

    /// <summary>
    /// Clears a member top-up credit - same shape as ClearCreditAsync (cash goes to the
    /// register, same audit trail), but never touches Payment or CustomerCredit: there is no
    /// bill behind this, so the cash receipt is recorded as its own CashTransaction
    /// (BillId null, same as a normal top-up's own cash receipt already is) instead of a
    /// Payment row, which requires one.
    /// </summary>
    public async Task<MemberTopUpCreditDto> ClearMemberTopUpCreditAsync(
        Guid branchId, Guid operatorId, Guid shiftId, Guid creditId, ClearCreditDto dto)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var credit = await _unitOfWork.Repository<MemberTopUpCredit>().Query()
                .Include(c => c.Member)
                .FirstOrDefaultAsync(c => c.Id == creditId && c.BranchId == branchId)
                ?? throw new NotFoundException("Credit record not found.");

            if (credit.Status == "cleared")
                throw new AppException("Credit is already cleared.");

            var (cashAmountToApply, cashReceived, changeReturned, actualCashCollected) =
                ResolvePayment(dto, credit.CreditAmount);

            if (cashAmountToApply > 0)
            {
                var activeRegister = await _unitOfWork.Repository<CashRegister>().Query()
                    .FirstOrDefaultAsync(cr => cr.BranchId == branchId && cr.ShiftId == shiftId && cr.Status == CashRegisterStatus.Open)
                    ?? throw new AppException("No active cash register found for this shift.");

                activeRegister.ExpectedDrawerCash += actualCashCollected;
                _unitOfWork.Repository<CashRegister>().Update(activeRegister);

                var cashTx = new CashTransaction
                {
                    CashRegisterId = activeRegister.Id,
                    BillId = null, // Not tied to a bill - see MemberTopUpCredit's own docstring
                    BranchId = branchId,
                    OperatorId = operatorId,
                    TransactionType = "member_topup_credit_clear",
                    CashAmount = cashAmountToApply,
                    CashReceived = cashReceived,
                    ChangeReturned = changeReturned,
                    ActualCashCollected = actualCashCollected,
                    GamingAmount = 0,
                    FoodAmount = 0,
                    CustomerName = credit.Member.Username,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await _unitOfWork.Repository<CashTransaction>().AddAsync(cashTx);
            }

            credit.Status = "cleared";
            credit.ClearedAt = DateTimeOffset.UtcNow;
            credit.ClearedByOperatorId = operatorId;
            _unitOfWork.Repository<MemberTopUpCredit>().Update(credit);

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = AuditActions.MemberTopUpCreditClear,
                BranchId = branchId,
                TargetType = "member_topup_credit",
                TargetId = credit.Id,
                Details = new
                {
                    MemberUsername = credit.Member.Username,
                    Amount = credit.CreditAmount,
                    PaymentType = dto.PaymentType.ToString(),
                    Cash = cashAmountToApply,
                    Online = dto.OnlineAmount,
                },
            });

            await _unitOfWork.CommitTransactionAsync();

            return new MemberTopUpCreditDto
            {
                Id = credit.Id,
                BranchId = credit.BranchId,
                OperatorId = credit.OperatorId,
                MemberId = credit.MemberId,
                MemberName = credit.Member.FullName,
                MemberUsername = credit.Member.Username ?? "",
                CreditAmount = credit.CreditAmount,
                Status = credit.Status,
                CreatedAt = credit.CreatedAt,
                ClearedAt = credit.ClearedAt,
            };
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// The cash/online split and change math shared by both ClearCreditAsync and
    /// ClearMemberTopUpCreditAsync - identical in both, extracted rather than duplicated.
    /// Pure arithmetic only; neither caller's own records (Payment vs CashTransaction, which
    /// table gets marked cleared) are touched here.
    /// </summary>
    private static (decimal cashAmountToApply, decimal cashReceived, decimal changeReturned, decimal actualCashCollected)
        ResolvePayment(ClearCreditDto dto, decimal amountDue)
    {
        decimal cashAmountToApply = dto.CashAmount;
        decimal cashReceived = dto.CashReceived;

        if (dto.PaymentType == PaymentType.Cash)
        {
            cashAmountToApply = amountDue;
            if (cashReceived <= 0)
                cashReceived = dto.CashAmount > 0 ? dto.CashAmount : cashAmountToApply;
        }

        decimal totalPayment = cashAmountToApply + dto.OnlineAmount;
        if (totalPayment != amountDue)
            throw new AppException($"Payment mismatch. Expected: {amountDue}, Provided: {totalPayment}");

        decimal changeReturned = 0;
        decimal actualCashCollected = cashAmountToApply;
        if (cashAmountToApply > 0)
        {
            if (cashReceived < cashAmountToApply)
                throw new AppException("Cash received is less than cash amount.");
            changeReturned = cashReceived - cashAmountToApply;
        }

        return (cashAmountToApply, cashReceived, changeReturned, actualCashCollected);
    }
}
