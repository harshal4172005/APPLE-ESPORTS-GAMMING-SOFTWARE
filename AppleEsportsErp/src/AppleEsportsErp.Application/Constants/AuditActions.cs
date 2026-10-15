namespace AppleEsportsErp.Application.Constants;

/// <summary>SOP §22: Audit Action Types — every critical action must be logged</summary>
public static class AuditActions
{
    // Auth
    public const string Login = "login";
    public const string Logout = "logout";
    public const string FailedLogin = "failed_login";
    public const string AccountLocked = "account_locked";
    public const string PasswordReset = "password_reset";
    public const string ForcedLogout = "forced_logout";
    public const string AdminSwitchIn = "admin_switch_in";
    public const string AdminSwitchOut = "admin_switch_out";

    // Sessions
    public const string SessionStart = "session_start";
    public const string SessionStop = "session_stop";
    public const string SessionExtend = "session_extend";
    public const string SessionTransfer = "session_transfer";
    public const string SessionResume = "session_resume";

    // Reservations
    public const string ReservationCreate = "reservation_create";
    public const string ReservationCancel = "reservation_cancel";
    public const string ReservationDelete = "reservation_delete";
    public const string ReservationOverride = "reservation_override";
    public const string ReservationExpire = "reservation_expire";

    // Billing
    public const string BillCreate = "bill_create";
    public const string BillComplete = "bill_complete";
    public const string PaymentProcess = "payment_process";
    public const string DiscountApply = "discount_apply";
    public const string PaymentMethodEdit = "payment_method_edit";

    /// <summary>
    /// A customer credit (money owed from an earlier session left unpaid) was settled.
    /// Previously unlogged entirely - CreditService.ClearCreditAsync updated the credit, wrote
    /// its Payment and CashTransaction rows, and never once called the audit service, so the
    /// one action this whole trail exists to answer for - who cleared this, when, how - had no
    /// record of its own at all.
    /// </summary>
    public const string CreditClear = "credit_clear";

    /// <summary>A member's wallet top-up given on credit (balance granted now, paid later) -
    /// see MemberTopUpCredit's own docstring for why it's tracked separately from a bill credit.</summary>
    public const string MemberTopUpCreditGiven = "member_topup_credit_given";
    public const string MemberTopUpCreditClear = "member_topup_credit_clear";

    // Food orders - previously two raw strings ("food_order_create", "food_order_update") that
    // did not live here and were not recognised by anything reading the trail.
    public const string FoodOrderPlace = "food_order_place";
    public const string FoodOrderStatusChange = "food_order_status_change";

    // Cash
    public const string CashOpening = "cash_opening";
    public const string CashVerification = "cash_verification";
    public const string CashMismatch = "cash_mismatch";
    public const string DenominationCount = "denomination_count";

    // Members
    public const string MemberCreate = "member_create";
    public const string WalletRecharge = "wallet_recharge";
    public const string WalletDeduction = "wallet_deduction";
    public const string PointsRedeem = "points_redeem";

    // Operators
    public const string OperatorCreate = "operator_create";
    public const string OperatorRemove = "operator_remove";
    public const string OperatorSuspend = "operator_suspend";
    public const string AccessGrant = "access_grant";
    public const string AccessRevoke = "access_revoke";

    // Inventory
    public const string StockRefill = "stock_refill";
    public const string PriceChange = "price_change";
    public const string ItemDisable = "item_disable";
    public const string WastageLog = "wastage_log";

    /// <summary>A stock delivery recorded through InventoryController.AddStock. Admin/Super Admin only.</summary>
    public const string StockAdd = "stock_add";

    /// <summary>A menu item was added to the catalogue - previously unlogged entirely.</summary>
    public const string ItemCreate = "item_create";

    /// <summary>
    /// A menu item was removed - permanently if nothing references it, deactivated otherwise.
    /// Previously unlogged entirely, and previously never told the branch either; see
    /// BranchCommands.DeleteInventoryItem for the other half of that fix.
    /// </summary>
    public const string ItemDelete = "item_delete";

    // System
    public const string ShiftStart = "shift_start";
    public const string ShiftEnd = "shift_end";

    /// <summary>One operator closed a shift that was left open by another, and counted its drawer.</summary>
    public const string ShiftTakeover = "shift_takeover";
    public const string EodFinalize = "eod_finalize";
    public const string ForceClose = "force_close";
    public const string SettingsChange = "settings_change";

    /// <summary>
    /// Head Office asked a branch to do something - stop a session, start one, move it to
    /// another PC, take a PC out of service - through the command queue rather than a direct
    /// write. Recorded the moment it is queued, by whoever is at Head Office, separately from
    /// the branch's own record of actually carrying it out (which still logs under its usual
    /// action - SessionStop, SessionStart and so on - exactly as if the counter had done it).
    /// Reading both together answers two different questions: who asked, and what happened.
    /// </summary>
    public const string RemoteCommandIssued = "remote_command_issued";

    /// <summary>
    /// An Admin (not Super Admin, who already moved between branches freely) confirmed their
    /// own PIN to switch into another branch's data. Access itself was never the gap - Branch
    /// IsolationAttribute already lets Admin reach any branch - the gap was that it happened
    /// silently. This is the accountability record: who, which branch, when.
    /// </summary>
    public const string BranchSwitch = "branch_switch";
}
