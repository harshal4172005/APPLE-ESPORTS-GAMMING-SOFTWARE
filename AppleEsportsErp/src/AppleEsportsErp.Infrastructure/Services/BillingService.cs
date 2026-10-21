using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Billing;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;

namespace AppleEsportsErp.Infrastructure.Services;

public class BillingService : IBillingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;
    private readonly IHubNotificationService _hubNotification;
    private readonly IWalletService _walletService;
    private readonly IOutboxService _outbox;
    private readonly IConfiguration _configuration;

    public BillingService(
        IUnitOfWork unitOfWork,
        IAuditService auditService,
        IHubNotificationService hubNotification,
        IWalletService walletService,
        IOutboxService outbox,
        IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _hubNotification = hubNotification;
        _walletService = walletService;
        _outbox = outbox;
        _configuration = configuration;
    }

    /// <summary>
    /// A payment taken here and nowhere else. Head Office holds a synced copy of every branch's
    /// bills, and writing "paid" into that copy is easy and looks correct immediately - the
    /// screen updates, the audit log gets an entry - but the branch that actually has the
    /// customer, the till and the cash was never told. Its own register stays uncredited and its
    /// PC stays locked showing Billing, forever, because nothing there ever changed. Same reason
    /// SessionService refuses to start/stop a session directly at Head Office: see
    /// BillingController.ProcessPayment for the branch-command instruction this sends instead.
    /// </summary>
    /// <summary>
    /// Shared by every bill-mutating method here, so <paramref name="what"/> is the only thing
    /// that ever changes between callers. It used to not be shared cleanly - this message body
    /// was written for ProcessPaymentAsync specifically ("a payment written here...", pointing
    /// at api/bills/{id}/pay) and then reused verbatim for ApplyDiscountAsync, so refusing a
    /// discount came back talking about a payment and pointing at the pay endpoint - correct
    /// for one caller, wrong and confusing for the other. Kept generic now on purpose: whoever
    /// calls this next does not get to be the one who has to remember to update the wording.
    /// </summary>
    private void RefuseIfHeadOffice(string what)
    {
        if (!_configuration.IsHeadOffice()) return;

        throw new AppException(
            $"This bill has to be {what} by the branch itself, not written here at Head Office - " +
            "a change written here is invisible to the counter, so the branch's own copy never " +
            "agrees with what Head Office just did. Send it as an instruction instead and the " +
            "branch will carry it out within a few seconds and report back.",
            System.Net.HttpStatusCode.BadRequest,
            "BRANCH_ONLY_OPERATION");
    }

    public async Task<PaginatedResult<BillDto>> GetActiveBillsAsync(Guid branchId, int page = 1, int pageSize = 50)
    {
        // Pending only, not "not Completed" - Voided is neither. A voided bill is dead, not
        // owed; != Completed swept it back into the counter's unpaid list right alongside
        // bills that genuinely need collecting, which is exactly backwards from why a bill
        // gets voided in the first place. Confirmed live: a bill voided as unrecoverable test
        // data kept showing up here as if it still needed action.
        var query = _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Items)
            .Include(b => b.Payments)
            .Include(b => b.Pc)
            .Where(b => b.BranchId == branchId && b.Status == BillStatus.Pending)
            .OrderByDescending(b => b.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var dtos = items.Select(MapToDto).ToList();
        return new PaginatedResult<BillDto>(dtos, total, page, pageSize);
    }

    public async Task<List<BillDto>> GetDeferredBillsAsync(Guid branchId)
    {
        var bills = await _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Items)
            .Include(b => b.Payments)
            .Include(b => b.Pc)
            .Include(b => b.Session)
            .Where(b => b.BranchId == branchId && b.IsDeferred && b.Status == BillStatus.Pending)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return bills.Select(b => MapToDto(b)).ToList();
    }

    public async Task<BillDto> GetBillAsync(Guid branchId, Guid id)
    {
        var bill = await _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Items)
            .Include(b => b.Payments)
            .Include(b => b.Pc).ThenInclude(p => p!.PricingProfile)
            .Include(b => b.Session)
            .FirstOrDefaultAsync(b => b.Id == id && b.BranchId == branchId)
            ?? throw new NotFoundException("Bill not found.");

        return MapToDtoWithLiveAmount(bill);
    }

    public async Task<BillDto> GetBillByNumberAsync(Guid branchId, string billNumber)
    {
        var bill = await _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Items)
            .Include(b => b.Payments)
            .Include(b => b.Pc).ThenInclude(p => p!.PricingProfile)
            .Include(b => b.Session)
            .FirstOrDefaultAsync(b => b.BillNumber == billNumber && b.BranchId == branchId)
            ?? throw new NotFoundException("Bill not found.");

        return MapToDtoWithLiveAmount(bill);
    }

    /// <summary>
    /// Same mapping as MapToDto, except while the session is still Active it recomputes the
    /// gaming charge live (same formula as the operator PC card / member overlay) instead of
    /// returning the stale amount stored at session start — this is what keeps the Billing
    /// Counter's bill panel from showing a different number than everywhere else.
    ///
    /// Gated on the bill's own Status too, not just the session's — a bill closes (food paid
    /// separately, ₹0 buffer auto-close, deferred) while its session keeps running as genuine
    /// pay-as-you-go, and that combination is completely normal. Without this check, a bill
    /// paid two minutes into a session that is still open four days later showed its gaming
    /// line still climbing off four days of elapsed time — a live total on a bill that had
    /// nothing left to collect. A Completed bill is done; it shows the amount it was actually
    /// settled for, not a number that keeps moving underneath a receipt already printed.
    /// </summary>
    private static BillDto MapToDtoWithLiveAmount(Bill bill)
    {
        var dto = MapToDto(bill);

        if (bill.Status != BillStatus.Completed
            && bill.Session != null && bill.Session.State == Domain.Enums.SessionState.Active)
        {
            decimal ratePerHour = bill.Pc?.PricingProfile?.BaseHourlyRate ?? Application.Services.SessionPricingCalculator.DefaultRatePerHour;
            int bufferMinutes = bill.Pc?.PricingProfile?.BufferMinutes ?? Application.Services.SessionPricingCalculator.DefaultBufferMinutes;
            decimal elapsedMinutes = (decimal)(DateTimeOffset.UtcNow - bill.Session.StartTime).TotalMinutes;
            // Package-aware, not hours x BaseHourlyRate alone - see CalculateLiveGamingAmount.
            // A session running under "4 hrs - Rs 180" now shows Rs 180 climbing exactly the
            // way the final Stop bill would, instead of a flat hourly number nobody is really
            // being charged.
            decimal liveGamingAmount = Application.Services.SessionPricingCalculator.CalculateLiveGamingAmount(
                bill.Session.PackagePrice, bill.Session.PlannedDurationMin, ratePerHour, bufferMinutes, elapsedMinutes);

            // The sticker price first — what the customer sees with no discount — THEN the
            // discount comes off that. See ApplyDiscountAsync for why: rounding the raw
            // elapsed-time figure and the discount together in one step is what let a real
            // discount vanish back into the nearest ₹10.
            var (stickerGaming, stickerFood, preDiscountTotal) = Application.Services.SessionPricingCalculator.ComputeRoundedBreakdown(
                liveGamingAmount, dto.FoodAmount, 0m);

            decimal discount = Math.Min(dto.DiscountAmount, preDiscountTotal);
            decimal total = Math.Max(0m, Math.Round(preDiscountTotal - discount, 2));

            dto.GamingAmount = stickerGaming;
            dto.Subtotal = stickerGaming + stickerFood;
            dto.TotalAmount = total;

            var gamingItem = dto.Items.FirstOrDefault(i => i.ItemType == "gaming");
            if (gamingItem != null)
            {
                gamingItem.UnitPrice = stickerGaming;
                gamingItem.TotalPrice = stickerGaming;
            }
        }

        return dto;
    }

    public async Task<BillDto> ApplyDiscountAsync(Guid branchId, Guid actorId, string actorRole, Guid id, ApplyDiscountDto dto)
    {
        RefuseIfHeadOffice("discounted");

        var bill = await _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Items)
            .Include(b => b.Payments)
            .Include(b => b.Pc).ThenInclude(p => p!.PricingProfile)
            .Include(b => b.Session)
            .FirstOrDefaultAsync(b => b.Id == id && b.BranchId == branchId)
            ?? throw new NotFoundException("Bill not found.");

        if (bill.Status == BillStatus.Completed)
            throw new AppException("Cannot apply discount to a completed bill.");

        // Owner's rule: missing stock is paid in full by the operator - no one can waive it.
        if (bill.ShortageOperatorId.HasValue)
            throw new AppException("A missing-stock bill is paid in full and cannot be discounted.");

        // The sticker price — what the customer would pay with NO discount — rounded to the
        // nearest ₹10 first, on its own, before the discount ever enters the arithmetic.
        //
        // This used to compute the discount off the raw, un-rounded elapsed-time figure and
        // let the rounding and the discount happen in the same step. For an 11-minute session
        // at ₹60/hr that raw figure is ₹11, which itself rounds down to the ₹10 everyone
        // already sees on screen — so a 10% discount, ~₹1, was being taken off ₹11 (giving
        // ₹9.90), and THEN the final total was rounded to the nearest ₹10 again — and ₹9.90
        // rounds straight back UP to ₹10. The discount was computed correctly and then
        // erased by the very next line. Any discount smaller than about ₹5 on a small bill
        // disappeared completely; the customer could watch a 10% discount change nothing.
        //
        // Rounding the sticker FIRST fixes both halves at once: the discount is now taken off
        // the ₹10 a customer can actually see and be shown reduced, and the result is never
        // rounded to the nearest ₹10 a second time — only to the nearest paisa, because a
        // deliberate discount is a precise figure an admin chose, not a cash-drawer
        // convenience to be smoothed away.
        //
        // Recomputed from elapsed time on every call rather than trusted from storage, so a
        // second press is never discounting an already-discounted number — see the note on
        // GamingAmount below for why storage is safe to read once a discount also stops
        // hiding inside it.
        decimal rawGaming = bill.GamingAmount;
        if (bill.Session != null && bill.Session.State == Domain.Enums.SessionState.Active)
        {
            decimal ratePerHour = bill.Pc?.PricingProfile?.BaseHourlyRate
                ?? Application.Services.SessionPricingCalculator.DefaultRatePerHour;
            int bufferMinutes = bill.Pc?.PricingProfile?.BufferMinutes
                ?? Application.Services.SessionPricingCalculator.DefaultBufferMinutes;
            decimal elapsedMinutes = (decimal)(DateTimeOffset.UtcNow - bill.Session.StartTime).TotalMinutes;
            // Package-aware - see CalculateLiveGamingAmount. A discount on a package session
            // must come off the package's real price (plus any overrun), not off a plain
            // hours x BaseHourlyRate figure that was never the customer's actual charge.
            rawGaming = Application.Services.SessionPricingCalculator.CalculateLiveGamingAmount(
                bill.Session.PackagePrice, bill.Session.PlannedDurationMin, ratePerHour, bufferMinutes, elapsedMinutes);
        }

        var (stickerGaming, stickerFood, preDiscountTotal) = Application.Services.SessionPricingCalculator.ComputeRoundedBreakdown(
            rawGaming, bill.FoodAmount, 0m);

        decimal discountAmount = dto.DiscountType == DiscountType.Percentage
            ? preDiscountTotal * (dto.DiscountValue / 100m)
            : dto.DiscountValue;

        if (discountAmount > preDiscountTotal)
            throw new AppException("Discount amount cannot exceed the bill's total.");

        decimal roundedTotal = Math.Max(0m, Math.Round(preDiscountTotal - discountAmount, 2));

        bill.DiscountType = dto.DiscountType;
        bill.DiscountValue = dto.DiscountValue;
        bill.DiscountAmount = discountAmount;

        // DiscountBy is a foreign key into THIS branch's own local Users table - and only
        // Users, never Operators (FK_bills_users_DiscountBy). A discount applied locally by a
        // genuine branch Admin/Super Admin satisfies that fine. One routed down from Head
        // Office never can: Users accounts are Head Office's own and are never synced to a
        // branch (BranchConfigDto pushes Operators, MenuItems and Members - not Users), so
        // actorId is an id this branch's database has literally never seen. Every single
        // Head-Office-issued discount was failing outright on this line with a 23503 foreign
        // key violation - confirmed live in the Audit Trail, not a guess. Left null when the
        // actor cannot be resolved locally; the audit entry below still records who it was via
        // AuditService's own lenient (non-FK) lookup across both tables.
        bill.DiscountBy = await _unitOfWork.Repository<User>().Query().AnyAsync(u => u.Id == actorId)
            ? actorId
            : (Guid?)null;
        bill.DiscountReason = dto.Reason;

        // Gaming and Food now hold the STICKER price, not a discount-adjusted one. The
        // discount is its own line (DiscountAmount) rather than something folded invisibly
        // into Gaming, which is what ProcessPaymentAsync already assumed everywhere it reads
        // this bill: wallet deductions and GamingPortion/FoodPortion on the Payment row both
        // prorate DiscountAmount across (GamingAmount / Subtotal) themselves. With the old
        // fold-it-into-Gaming behaviour, a discounted bill paid by wallet had the discount
        // subtracted TWICE - once here, once again at payment. It also means GamingAmount is
        // always safe to read back on a second discount press: it never carries a previous
        // discount to compound.
        bill.GamingAmount = stickerGaming;
        bill.FoodAmount = stickerFood;
        bill.Subtotal = stickerGaming + stickerFood;
        bill.TotalAmount = roundedTotal;
        bill.UpdatedAt = DateTimeOffset.UtcNow;

        var discountGamingItem = bill.Items.FirstOrDefault(i => i.ItemType == "gaming");
        if (discountGamingItem != null)
        {
            discountGamingItem.TotalPrice = stickerGaming;
            discountGamingItem.UnitPrice = stickerGaming;
        }

        _unitOfWork.Repository<Bill>().Update(bill);

        await _auditService.LogAsync(new AuditEntry
        {
            // Whoever actually pressed it. This said UserName = "System" and hardcoded the role
            // to SuperAdmin, so the audit trail recorded every discount as having been applied
            // by nobody in particular - on the one action in this system most likely to be
            // questioned later. The id was always here; only the name and role were invented.
            // Both, because the actor may live in either table - a branch Admin is an
            // operator row, a Super Admin is a user row - and AuditService resolves the
            // real name from whichever one matches.
            OperatorId = actorId,
            UserId = actorId,
            UserRole = actorRole,
            UserName = string.Empty,
            Action = AuditActions.DiscountApply,
            BranchId = branchId,
            TargetType = "bill",
            TargetId = bill.Id,
            Details = new
            {
                DiscountType = dto.DiscountType.ToString(),
                Value = dto.DiscountValue,
                Reason = dto.Reason,
                // The figures it was actually computed against, so the amount can be checked
                // afterwards rather than taken on trust.
                StickerPrice = preDiscountTotal,
                DiscountAmount = discountAmount,
                NewTotal = roundedTotal,
            }
        });

        await _unitOfWork.SaveChangesAsync();
        await _hubNotification.BroadcastBillingUpdateAsync(branchId, bill.Id);

        return MapToDto(bill);
    }

    /// <summary>
    /// Corrects only the payment method on an already-completed bill — e.g. marked Online, the
    /// bank later declined it, the customer paid Cash instead. Line items, totals, and discounts
    /// stay exactly as they were; only PaymentType/CashAmount/OnlineAmount move, and the delta is
    /// booked as a NEW cash-register adjustment dated now, not folded into the original payment's
    /// own CashTransaction. That original row is left untouched deliberately: it is what that
    /// day's (possibly already-closed, already-reported) register actually recorded at the time,
    /// and rewriting it would silently change a day's numbers that may have already been handed
    /// over and signed off. The correction belongs to today, because today is when it happened.
    /// </summary>
    public async Task<BillDto> EditPaymentMethodAsync(
        Guid branchId, Guid actorId, string actorRole, Guid id, EditPaymentMethodDto dto)
    {
        RefuseIfHeadOffice("payment-corrected");

        if (dto.NewPaymentType == PaymentType.Wallet)
            throw new AppException(
                "Payment method cannot be corrected to or from Wallet here - a wallet payment's " +
                "Gaming/Food split isn't retained per-payment, so it can't be safely reversed or " +
                "re-applied from this screen. Use a fresh payment/refund for that instead.",
                System.Net.HttpStatusCode.BadRequest, "WALLET_CORRECTION_NOT_SUPPORTED");

        var bill = await _unitOfWork.Repository<Bill>().Query()
            .Include(b => b.Payments)
            .Include(b => b.Pc)
            .FirstOrDefaultAsync(b => b.Id == id && b.BranchId == branchId)
            ?? throw new NotFoundException("Bill not found.");

        if (bill.Status != BillStatus.Completed)
            throw new AppException(
                "Only a completed bill's payment method can be corrected.",
                System.Net.HttpStatusCode.BadRequest, "BILL_NOT_COMPLETED");

        if (bill.PaymentType == PaymentType.Wallet || bill.WalletAmount > 0)
            throw new AppException(
                "This bill was paid (at least partly) from a member's wallet, so its payment " +
                "method cannot be corrected here - see EditPaymentMethodAsync's wallet note.",
                System.Net.HttpStatusCode.BadRequest, "WALLET_CORRECTION_NOT_SUPPORTED");

        if (dto.CashAmount < 0 || dto.OnlineAmount < 0)
            throw new AppException(
                "A payment cannot contain a negative amount.",
                System.Net.HttpStatusCode.BadRequest, "NEGATIVE_PAYMENT_AMOUNT");

        if (Math.Round(dto.CashAmount + dto.OnlineAmount, 2) != Math.Round(bill.TotalAmount, 2))
            throw new AppException(
                $"The corrected split must still add up to the bill's total of {bill.TotalAmount:0.00} " +
                $"(cash {dto.CashAmount:0.00} + online {dto.OnlineAmount:0.00} = " +
                $"{dto.CashAmount + dto.OnlineAmount:0.00}).",
                System.Net.HttpStatusCode.BadRequest, "PAYMENT_SPLIT_MISMATCH");

        var oldPaymentType = bill.PaymentType;
        var oldCashAmount = bill.CashAmount;
        var oldOnlineAmount = bill.OnlineAmount;
        decimal cashDelta = dto.CashAmount - oldCashAmount;

        bill.PaymentType = dto.NewPaymentType;
        bill.CashAmount = dto.CashAmount;
        bill.OnlineAmount = dto.OnlineAmount;
        // ActualCashCollected/CashReceived/ChangeReturned describe a specific tendering moment
        // that already happened and isn't being redone here - only left consistent so a cash
        // amount of zero doesn't leave a stale "collected" figure sitting behind it.
        bill.ActualCashCollected = dto.CashAmount;
        if (dto.CashAmount == 0)
        {
            bill.CashReceived = 0;
            bill.ChangeReturned = 0;
        }
        bill.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<Bill>().Update(bill);

        // The Bill row above is what the Billing Counter and this bill's own receipt read - but
        // End of Day's Cash/Online totals (EodService.GenerateReportAsync) sum the ORIGINAL
        // Payment row instead, not the Bill: `payments.Sum(p => p.CashAmount/OnlineAmount)`. A
        // correction that only touched Bill left that Payment row exactly as it was tendered,
        // so a bill corrected from Cash to Online kept counting as Cash on End of Day forever -
        // "online in online, cash in cash" never actually held once anything was corrected.
        // Every non-wallet Payment tied to this bill (in practice always exactly one - a
        // Completed bill can only be paid once) is corrected the same way as the Bill itself.
        var nonWalletPayments = bill.Payments.Where(p => p.WalletAmount == 0).ToList();
        if (nonWalletPayments.Count == 1)
        {
            var payment = nonWalletPayments[0];
            payment.PaymentType = dto.NewPaymentType;
            payment.CashAmount = dto.CashAmount;
            payment.OnlineAmount = dto.OnlineAmount;
            payment.ActualCashCollected = dto.CashAmount;
            if (dto.CashAmount == 0)
            {
                payment.CashReceived = 0;
                payment.ChangeReturned = 0;
            }
            _unitOfWork.Repository<Payment>().Update(payment);
        }
        else if (nonWalletPayments.Count > 1)
        {
            // Genuinely unexpected shape for a Completed bill - refuse rather than guess which
            // of several payment rows to rewrite and risk End of Day disagreeing with itself in
            // a new way.
            throw new AppException(
                "This bill has more than one payment record, so its payment method can't be " +
                "safely corrected here - which one to change is ambiguous.",
                System.Net.HttpStatusCode.Conflict, "AMBIGUOUS_PAYMENT_RECORDS");
        }

        // Book the cash delta into TODAY's drawer, not the original day's - see the method
        // comment. Looked up by branch alone, not the correcting actor's own shift: a Super
        // Admin correcting from Head Office is not on any shift here at all, and even locally
        // the bill's OWN (probably long-closed) original shift is exactly the wrong one to ask
        // - the point is whichever register is open right now, today. No open register right
        // now (e.g. corrected outside branch hours) means there is nothing to adjust; the bill
        // and audit trail still record the correction either way.
        CashRegister? activeRegister = null;
        if (cashDelta != 0)
        {
            activeRegister = await _unitOfWork.Repository<CashRegister>().Query()
                .FirstOrDefaultAsync(cr => cr.BranchId == branchId && cr.Status == CashRegisterStatus.Open);

            if (activeRegister != null)
            {
                activeRegister.ExpectedDrawerCash += cashDelta;
                activeRegister.TotalCashSales += cashDelta;
                _unitOfWork.Repository<CashRegister>().Update(activeRegister);

                // CashTransaction.OperatorId is a non-nullable FK into THIS branch's own local
                // Operators table - same fault class as ApplyDiscountAsync's DiscountBy note. A
                // correction issued locally by a genuine branch Operator/Admin/Super Admin
                // satisfies that fine; one routed down as a remote command from Head Office
                // never can, because actorId is then a Head Office User's own id, which this
                // branch's database has never seen. Confirmed live: every payment correction
                // issued from Head Office failed outright with a 23503 foreign key violation,
                // the same failure mode discounts had before that fix. Falls back to the open
                // register's own operator - it is their drawer being adjusted either way - so
                // the correction still applies instead of failing the whole transaction over
                // who gets named on one internal bookkeeping row; the real actor is still
                // recorded in full on the audit log entry below regardless.
                var operatorIdForTransaction = await _unitOfWork.Repository<Operator>().Query()
                    .AnyAsync(o => o.Id == actorId)
                    ? actorId
                    : activeRegister.OperatorId;

                await _unitOfWork.Repository<CashTransaction>().AddAsync(new CashTransaction
                {
                    CashRegisterId = activeRegister.Id,
                    BillId = bill.Id,
                    BranchId = branchId,
                    OperatorId = operatorIdForTransaction,
                    PcNumber = bill.Pc?.PcNumber,
                    CustomerName = bill.CustomerName ?? "Walk-in",
                    TransactionType = "payment_method_correction",
                    CashAmount = cashDelta,
                    CashReceived = 0,
                    ChangeReturned = 0,
                    ActualCashCollected = cashDelta,
                    GamingAmount = 0,
                    FoodAmount = 0,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await _auditService.LogAsync(new AuditEntry
        {
            OperatorId = actorId,
            UserId = actorId,
            UserRole = actorRole,
            UserName = string.Empty,
            Action = AuditActions.PaymentMethodEdit,
            BranchId = branchId,
            TargetType = "bill",
            TargetId = bill.Id,
            Details = new
            {
                OldPaymentType = oldPaymentType?.ToString(),
                NewPaymentType = dto.NewPaymentType.ToString(),
                OldCashAmount = oldCashAmount,
                NewCashAmount = dto.CashAmount,
                OldOnlineAmount = oldOnlineAmount,
                NewOnlineAmount = dto.OnlineAmount,
                Reason = dto.Reason,
            }
        });

        await _unitOfWork.SaveChangesAsync();
        await _hubNotification.BroadcastBillingUpdateAsync(branchId, bill.Id);
        if (activeRegister != null)
            await _hubNotification.BroadcastCashRegisterUpdateAsync(branchId, activeRegister.Id);

        return MapToDto(bill);
    }

    public async Task<BillDto> ProcessPaymentAsync(Guid branchId, Guid operatorId, Guid shiftId, Guid id, ProcessPaymentDto dto)
    {
        RefuseIfHeadOffice("paid");

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // Serializes concurrent payment attempts on the same bill (double-click, slow-UI
            // retry) so the second request waits for the first to commit instead of reading a
            // stale "not completed" and recording a second Payment for the same bill.
            await _unitOfWork.LockAsync(id);

            var bill = await _unitOfWork.Repository<Bill>().Query()
                .Include(b => b.Items)
                .Include(b => b.Payments)
                .Include(b => b.Pc)
                .Include(b => b.Session)
                .Include(b => b.Member)
                .FirstOrDefaultAsync(b => b.Id == id && b.BranchId == branchId)
                ?? throw new NotFoundException("Bill not found.");

            if (bill.Status == BillStatus.Completed)
                throw new AppException("Bill is already completed.");

            // Link member if passed in payment dto and not already linked
            if (dto.MemberId.HasValue && bill.MemberId == null)
            {
                bill.MemberId = dto.MemberId.Value;
                if (bill.SessionId.HasValue)
                {
                    var session = await _unitOfWork.Repository<Session>().GetByIdAsync(bill.SessionId.Value);
                    if (session != null)
                    {
                        session.MemberId = dto.MemberId.Value;
                        _unitOfWork.Repository<Session>().Update(session);
                    }
                }
            }

            // No leg of a payment may be negative, and this is where the negative figures came
            // from.
            //
            // The only check was that the parts add up to the bill, which a negative leg passes
            // effortlessly: Rs 500 cash and minus Rs 400 online sums to Rs 100 and settles a
            // Rs 100 bill. Nothing else objected, because nothing else looked. Rs 500 then went
            // into the till's expected cash while the takings recorded Rs 100, so the drawer
            // over-counted by Rs 400 at End of Day; the online column went negative; and the
            // session it belonged to reported a negative total in the reports.
            //
            // There is no validator on this DTO at all - only Auth and Sessions have any - so
            // the guard belongs here in the service, where every route in (counter, member
            // checkout, overlay) has to pass through it, rather than in one controller.
            //
            // Money moving the other way is a refund, which is a different operation with
            // different rules about who may authorise it. It is not a payment with a minus sign.
            if (dto.CashAmount < 0 || dto.OnlineAmount < 0 || dto.WalletAmount < 0
                || dto.CreditAmount < 0 || dto.CashReceived < 0)
            {
                throw new AppException(
                    "A payment cannot contain a negative amount. Cash, online, wallet, credit and " +
                    "cash received must each be zero or more.",
                    System.Net.HttpStatusCode.BadRequest,
                    "NEGATIVE_PAYMENT_AMOUNT");
            }

            // Calculate total paid vs expected
            decimal totalPayment = dto.CashAmount + dto.OnlineAmount + dto.WalletAmount;
            if (totalPayment + dto.CreditAmount != bill.TotalAmount)
                throw new AppException($"Payment amount mismatch. Expected: {bill.TotalAmount}, Provided: {totalPayment} + Credit: {dto.CreditAmount}");

            decimal changeReturned = 0;
            decimal actualCashCollected = dto.CashAmount;
            if (dto.CashAmount > 0)
            {
                if (dto.CashReceived < dto.CashAmount)
                    throw new AppException("Cash received is less than cash amount to be paid.");
                changeReturned = dto.CashReceived - dto.CashAmount;
                actualCashCollected = dto.CashAmount;
            }

            // Perform Wallet Deduction first if Wallet payment is involved
            if (dto.WalletAmount > 0)
            {
                if (bill.MemberId == null)
                    throw new AppException("Cannot pay via Wallet for a walk-in customer. Member registration required.");

                var walletMember = await _unitOfWork.Repository<Member>().GetByIdAsync(bill.MemberId.Value)
                    ?? throw new NotFoundException("Member not found.");

                // What the member can actually pay from their wallets, before anything is split.
                //
                // A member with 20 rupees of food credit ordering a 120 rupee drink could not be
                // served at all: the wallet leg was refused outright, and refusing the wallet
                // refused the whole payment, so the sale simply could not be completed. Nothing
                // ever went negative - it just would not sell.
                decimal walletAvailable = walletMember.GamingBalance + walletMember.FoodBalance;

                if (dto.WalletAmount > walletAvailable)
                    throw new AppException(
                        $"This member's wallets hold {walletAvailable:0.00} in total, so " +
                        $"{dto.WalletAmount:0.00} cannot be taken from them. Put {walletAvailable:0.00} " +
                        $"in as the wallet amount and collect the remaining " +
                        $"{(bill.TotalAmount - walletAvailable - dto.CreditAmount):0.00} as cash, UPI, or a " +
                        "split of the two.");

                decimal totalBill = bill.Subtotal > 0 ? bill.Subtotal : 1;

                // Apportioned by what the bill is actually for, then capped at what each wallet
                // really holds, with either wallet covering what the other could not.
                //
                // The bare proportional split was the second half of the same fault. On a mixed
                // bill it charged each wallet its share of the total whether that wallet had the
                // money or not, so a member with plenty of food credit and an empty gaming wallet
                // was refused over the gaming share - while holding more than enough between the
                // two. It is one person's money; which pocket it sits in is our bookkeeping, not
                // a reason to decline them.
                decimal gamingDeduction = Math.Round(dto.WalletAmount * (bill.GamingAmount / totalBill), 2);
                decimal foodDeduction = dto.WalletAmount - gamingDeduction;

                gamingDeduction = Math.Min(gamingDeduction, walletMember.GamingBalance);
                foodDeduction = Math.Min(foodDeduction, walletMember.FoodBalance);

                decimal uncovered = dto.WalletAmount - gamingDeduction - foodDeduction;
                if (uncovered > 0)
                {
                    var extraGaming = Math.Min(uncovered, walletMember.GamingBalance - gamingDeduction);
                    gamingDeduction += extraGaming;
                    uncovered -= extraGaming;

                    var extraFood = Math.Min(uncovered, walletMember.FoodBalance - foodDeduction);
                    foodDeduction += extraFood;
                    uncovered -= extraFood;
                }

                // Cannot happen - the total was checked against both balances above - but if the
                // arithmetic ever disagreed with itself, saying so beats quietly collecting less
                // than the bill and calling it settled.
                if (uncovered > 0.001m)
                    throw new AppException(
                        $"Could not take {dto.WalletAmount:0.00} from this member's wallets; " +
                        $"{uncovered:0.00} of it is not covered. Nothing has been charged.");

                if (gamingDeduction > 0)
                {
                    await _walletService.DeductWalletAsync(branchId, operatorId, bill.ShiftId, bill.MemberId.Value, new Application.DTOs.Wallets.DeductWalletDto
                    {
                        TargetWallet = WalletType.Gaming,
                        Amount = gamingDeduction,
                        Reason = $"Gaming Payment for Bill {bill.BillNumber}",
                        BillId = bill.Id
                    });
                }
                
                if (foodDeduction > 0)
                {
                    await _walletService.DeductWalletAsync(branchId, operatorId, bill.ShiftId, bill.MemberId.Value, new Application.DTOs.Wallets.DeductWalletDto
                    {
                        TargetWallet = WalletType.Food,
                        Amount = foodDeduction,
                        Reason = $"Food Payment for Bill {bill.BillNumber}",
                        BillId = bill.Id
                    });
                }
            }

            // Process Payment Record
            var payment = new Payment
            {
                BillId = bill.Id,
                BranchId = branchId,
                OperatorId = operatorId,
                PaymentType = dto.PaymentType,
                TotalAmount = totalPayment,
                CashAmount = dto.CashAmount,
                OnlineAmount = dto.OnlineAmount,
                WalletAmount = dto.WalletAmount,
                CashReceived = dto.CashReceived,
                ChangeReturned = changeReturned,
                ActualCashCollected = actualCashCollected,
                GamingPortion = bill.GamingAmount - (bill.DiscountAmount * (bill.GamingAmount / (bill.Subtotal > 0 ? bill.Subtotal : 1))), // Prorate discount
                FoodPortion = bill.FoodAmount - (bill.DiscountAmount * (bill.FoodAmount / (bill.Subtotal > 0 ? bill.Subtotal : 1))),
                Status = "completed",
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _unitOfWork.Repository<Payment>().AddAsync(payment);

            if (dto.CreditAmount > 0)
            {
                var customerCredit = new CustomerCredit
                {
                    BranchId = branchId,
                    OperatorId = operatorId,
                    BillId = bill.Id,
                    CustomerName = !string.IsNullOrWhiteSpace(dto.CustomerName) ? dto.CustomerName : (bill.CustomerName ?? bill.Member?.Username ?? "Walk-in"),
                    CustomerPhone = dto.CustomerPhone ?? "N/A",
                    PcNumber = bill.Pc?.PcNumber ?? "N/A",
                    OriginalBillAmount = bill.TotalAmount,
                    AmountPaidInitially = totalPayment,
                    CreditAmount = dto.CreditAmount,
                    Status = "pending",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _unitOfWork.Repository<CustomerCredit>().AddAsync(customerCredit);
            }

            // Update Bill
            bill.PaymentType = dto.PaymentType;
            bill.CashAmount = dto.CashAmount;
            bill.OnlineAmount = dto.OnlineAmount;
            bill.WalletAmount = dto.WalletAmount;
            bill.CashReceived = dto.CashReceived;
            bill.ChangeReturned = changeReturned;
            bill.ActualCashCollected = actualCashCollected;
            bill.Status = BillStatus.Completed;
            bill.CompletedAt = DateTimeOffset.UtcNow;
            bill.IsDeferred = false;
            bill.UpdatedAt = DateTimeOffset.UtcNow;
            
            _unitOfWork.Repository<Bill>().Update(bill);

            // Cash Register Tracking (SOP §10.2)
            if (dto.CashAmount > 0)
            {
                var activeRegister = await _unitOfWork.Repository<CashRegister>().Query()
                    .FirstOrDefaultAsync(cr => cr.BranchId == branchId && cr.ShiftId == shiftId && cr.Status == CashRegisterStatus.Open)
                    ?? throw new AppException("No active cash register found for this shift.");

                activeRegister.ExpectedDrawerCash += actualCashCollected;
                activeRegister.TotalCashSales += actualCashCollected;
                _unitOfWork.Repository<CashRegister>().Update(activeRegister);

                var cashTx = new CashTransaction
                {
                    CashRegisterId = activeRegister.Id,
                    BillId = bill.Id,
                    BranchId = branchId,
                    OperatorId = operatorId,
                    PcNumber = bill.Pc?.PcNumber,
                    CustomerName = !string.IsNullOrWhiteSpace(dto.CustomerName) ? dto.CustomerName : (bill.CustomerName ?? bill.Member?.Username ?? "Walk-in"),
                    TransactionType = "billing",
                    CashAmount = dto.CashAmount,
                    CashReceived = dto.CashReceived,
                    ChangeReturned = changeReturned,
                    ActualCashCollected = actualCashCollected,
                    GamingAmount = payment.GamingPortion * (dto.CashAmount / totalPayment), // Prorate cash to gaming
                    FoodAmount = payment.FoodPortion * (dto.CashAmount / totalPayment),     // Prorate cash to food
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _unitOfWork.Repository<CashTransaction>().AddAsync(cashTx);
            }

            Guid? completedSessionId = null;
            Guid? releasedPcId = null;

            // Release PC & Session (SOP §9.2)
            if (bill.Pc != null)
            {
                var pc = bill.Pc;
                
                // If there's an active session, stop it automatically upon payment
                if (bill.SessionId.HasValue)
                {
                    var session = await _unitOfWork.Repository<Session>().GetByIdAsync(bill.SessionId.Value);
                    if (session != null && session.State == SessionState.Active)
                    {
                        var now = DateTimeOffset.UtcNow;
                        session.State = SessionState.Completed;
                        session.UpdatedAt = now;
                        session.EndTime = now;
                        session.ActualDurationMin = (int)(now - session.StartTime).TotalMinutes;
                        _unitOfWork.Repository<Session>().Update(session);
                        completedSessionId = session.Id;
                    }
                }

                // If PC is AwaitingBilling or Active, we release it back to Idle
                if (pc.State == PcState.AwaitingBilling || pc.State == PcState.Active)
                {
                    pc.State = PcState.Idle;
                    pc.CurrentSessionId = null;
                    _unitOfWork.Repository<Pc>().Update(pc);
                    releasedPcId = pc.Id;
                }
            }

            // Log Audit
            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = AuditActions.PaymentProcess,
                BranchId = branchId,
                TargetType = "bill",
                TargetId = bill.Id,
                // Every leg, not just cash. This used to read
                // `new { PaymentType, Total, Cash }`, so a Split of Rs 5 cash + Rs 5 online
                // was recorded as "Split, Total 10, Cash 5" and the other Rs 5 simply was not
                // there. Anyone reading the audit trail to settle a dispute could see the
                // money was short and nothing saying where it went.
                Details = new
                {
                    PaymentType = dto.PaymentType.ToString(),
                    Total = totalPayment,
                    Cash = dto.CashAmount,
                    Online = dto.OnlineAmount,
                    Wallet = dto.WalletAmount,
                    Credit = dto.CreditAmount,
                }
            });

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = AuditActions.BillComplete,
                BranchId = branchId,
                TargetType = "bill",
                TargetId = bill.Id,
                Details = new { BillNumber = bill.BillNumber }
            });

            // Cash actually taken. This is the figure the owner reconciles the day against,
            // so it goes up with the split intact — gaming, food and discount separately,
            // plus how it was paid — rather than a single total Head Office cannot break down.
            await _outbox.RecordEventAsync(branchId, "Bill", bill.Id, "bill.paid", new
            {
                billId = bill.Id,
                billNumber = bill.BillNumber,
                sessionId = completedSessionId,
                operatorId,
                shiftId,
                paymentType = dto.PaymentType.ToString(),
                // Each leg travels as itself. Only cashAmount used to go up, and Head Office
                // reconstructed the rest as `totalPaid - cash`, guessing from the payment type
                // whether that remainder was online or wallet. For a Split it could only ever
                // guess one of them, so a Rs 5 + Rs 5 split arrived as a bill Head Office
                // recorded as Rs 10 cash - overstating the drawer and losing the online
                // settlement, on every split bill the company has ever taken.
                cashAmount = dto.CashAmount,
                onlineAmount = dto.OnlineAmount,
                walletAmount = dto.WalletAmount,
                creditAmount = dto.CreditAmount,
                actualCashCollected,
                totalPaid = totalPayment,
                gamingAmount = bill.GamingAmount,
                foodAmount = bill.FoodAmount,
                discountAmount = bill.DiscountAmount,
                billTotal = bill.TotalAmount,
                paidAt = DateTimeOffset.UtcNow,
            });

            await _unitOfWork.CommitTransactionAsync();
            await _hubNotification.BroadcastBillingUpdateAsync(branchId, bill.Id);
            
            if (completedSessionId.HasValue)
                await _hubNotification.BroadcastSessionUpdateAsync(branchId, completedSessionId.Value);
            
            if (releasedPcId.HasValue)
                await _hubNotification.BroadcastPcStatusChangeAsync(branchId, releasedPcId.Value);

            return MapToDto(bill);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public async Task<BillDto> RemoveBillItemAsync(Guid branchId, Guid operatorId, Guid billId, Guid billItemId)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var bill = await _unitOfWork.Repository<Bill>().Query()
                .Include(b => b.Items)
                .Include(b => b.Payments)
                .Include(b => b.Pc)
                .FirstOrDefaultAsync(b => b.Id == billId && b.BranchId == branchId)
                ?? throw new NotFoundException("Bill not found.");

            if (bill.Status == BillStatus.Completed)
                throw new AppException("Cannot modify a completed bill.");

            var itemToRemove = bill.Items.FirstOrDefault(i => i.Id == billItemId)
                ?? throw new NotFoundException("Bill item not found.");

            // The system raised it for stock found missing (StockShortageBilling); the operator it
            // charges must not be able to delete it, and nobody can waive it (see ApplyDiscountAsync).
            if (bill.ShortageOperatorId.HasValue)
                throw new AppException("This is a missing-stock bill. Its items cannot be removed.");

            if (itemToRemove.ItemType.ToLower() == "gaming")
                throw new AppException("Cannot manually remove gaming items.");

            // Restore Inventory if applicable
            if (itemToRemove.InventoryId.HasValue)
            {
                var inventoryItem = await _unitOfWork.Repository<InventoryItem>().GetByIdAsync(itemToRemove.InventoryId.Value);
                if (inventoryItem != null)
                {
                    inventoryItem.CurrentStock += itemToRemove.Quantity;
                    inventoryItem.SoldQty -= itemToRemove.Quantity;
                    inventoryItem.UpdatedAt = DateTimeOffset.UtcNow;
                    _unitOfWork.Repository<InventoryItem>().Update(inventoryItem);

                    var log = new InventoryLog
                    {
                        InventoryId = inventoryItem.Id,
                        OperatorId = operatorId,
                        BranchId = branchId,
                        Action = "void_return",
                        Quantity = itemToRemove.Quantity,
                        Reason = "Item removed from bill",
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    await _unitOfWork.Repository<InventoryLog>().AddAsync(log);
                }
            }

            // Adjust Bill Totals
            bill.FoodAmount -= itemToRemove.TotalPrice;
            if (bill.FoodAmount < 0) bill.FoodAmount = 0;
            
            bill.Subtotal -= itemToRemove.TotalPrice;
            if (bill.Subtotal < 0) bill.Subtotal = 0;

            // Recalculate discount if percentage based
            if (bill.DiscountType == DiscountType.Percentage && bill.Subtotal > 0)
            {
                bill.DiscountAmount = bill.Subtotal * (bill.DiscountValue / 100);
            }
            else if (bill.DiscountType == DiscountType.Flat)
            {
                // Ensure flat discount doesn't exceed new subtotal
                if (bill.DiscountAmount > bill.Subtotal)
                    bill.DiscountAmount = bill.Subtotal;
            }

            var (displayGaming, displayFood, roundedTotal) = Application.Services.SessionPricingCalculator.ComputeRoundedBreakdown(
                bill.GamingAmount, bill.FoodAmount, bill.DiscountAmount);
            bill.GamingAmount = displayGaming;
            bill.FoodAmount = displayFood;
            bill.Subtotal = displayGaming + displayFood;
            bill.TotalAmount = roundedTotal;

            var removalGamingItem = bill.Items.FirstOrDefault(i => i.ItemType == "gaming");
            if (removalGamingItem != null)
            {
                removalGamingItem.TotalPrice = displayGaming;
                removalGamingItem.UnitPrice = displayGaming;
            }

            bill.UpdatedAt = DateTimeOffset.UtcNow;

            bill.Items.Remove(itemToRemove);
            _unitOfWork.Repository<BillItem>().Remove(itemToRemove);
            _unitOfWork.Repository<Bill>().Update(bill);

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                UserName = "System",
                Action = "bill_item_removed",
                BranchId = branchId,
                TargetType = "bill",
                TargetId = bill.Id,
                Details = new { ItemName = itemToRemove.ItemName, Quantity = itemToRemove.Quantity, Amount = itemToRemove.TotalPrice }
            });

            await _unitOfWork.CommitTransactionAsync();
            await _hubNotification.BroadcastBillingUpdateAsync(branchId, bill.Id);

            return MapToDto(bill);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    private static BillDto MapToDto(Bill b)
    {
        return new BillDto
        {
            Id = b.Id,
            BillNumber = b.BillNumber,
            SessionId = b.SessionId,
            PcId = b.PcId,
            PcNumber = b.Pc?.PcNumber,
            BranchId = b.BranchId,
            OperatorId = b.OperatorId,
            ShiftId = b.ShiftId,
            CustomerName = b.CustomerName,
            MemberId = b.MemberId,
            GamingAmount = b.GamingAmount,
            FoodAmount = b.FoodAmount,
            Subtotal = b.Subtotal,
            DiscountType = b.DiscountType,
            DiscountValue = b.DiscountValue,
            DiscountAmount = b.DiscountAmount,
            DiscountReason = b.DiscountReason,
            TotalAmount = b.TotalAmount,
            Status = b.Status,
            IsDeferred = b.IsDeferred,
            CreatedAt = b.CreatedAt,
            SessionEndTime = b.Session?.EndTime,
            Items = b.Items?.Select(i => new BillItemDto
            {
                Id = i.Id,
                ItemType = i.ItemType,
                ItemName = i.ItemName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                TotalPrice = i.TotalPrice
            }).ToList() ?? new List<BillItemDto>(),
            Payments = b.Payments?.Select(p => new PaymentDto
            {
                Id = p.Id,
                PaymentType = p.PaymentType,
                TotalAmount = p.TotalAmount,
                CashAmount = p.CashAmount,
                OnlineAmount = p.OnlineAmount,
                WalletAmount = p.WalletAmount,
                CashReceived = p.CashReceived,
                ChangeReturned = p.ChangeReturned,
                ActualCashCollected = p.ActualCashCollected,
                CreatedAt = p.CreatedAt
            }).ToList() ?? new List<PaymentDto>()
        };
    }
}
