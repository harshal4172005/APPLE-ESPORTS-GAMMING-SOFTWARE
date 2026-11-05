namespace AppleEsportsErp.Application.Interfaces;

public interface IHubNotificationService
{
    Task BroadcastPcStatusChangeAsync(Guid branchId, Guid pcId);
    Task BroadcastSessionUpdateAsync(Guid branchId, Guid sessionId);
    Task BroadcastReservationUpdateAsync(Guid branchId, Guid reservationId);
    Task BroadcastBillingUpdateAsync(Guid branchId, Guid billId);
    Task BroadcastFoodOrderUpdateAsync(Guid branchId, Guid orderId);
    Task BroadcastCashRegisterUpdateAsync(Guid branchId, Guid registerId);
    Task BroadcastPcManagementUpdateAsync(Guid branchId, Guid pcId, string action);
    Task BroadcastPricingProfileUpdateAsync(Guid branchId);
    /// <summary>
    /// The pricing fields (everything after <paramref name="customerName"/>) exist so the
    /// customer's own PC shows what they're actually being charged, not just a countdown -
    /// see the Session Pricing PRD, issue 07. <paramref name="packagePrice"/>/
    /// <paramref name="plannedDurationMin"/> are the session's own committed package, if it has
    /// one; null means genuine Pay-As-You-Go, priced purely from
    /// <paramref name="ratePerHour"/>/<paramref name="bufferMinutes"/>. <paramref name="sessionStartUtc"/>
    /// lets the agent tick the live amount itself between pushes, the same way it already
    /// ticks the countdown, instead of showing a number frozen at whatever moment this was sent.
    /// </summary>
    Task SendUnlockCommandToAgentAsync(
        Guid pcId, int durationMinutes, string? customerName,
        decimal? packagePrice = null, int? plannedDurationMin = null, string? packageName = null,
        decimal ratePerHour = 0m, int bufferMinutes = 0, DateTimeOffset? sessionStartUtc = null,
        int? packageFromMin = null,
        List<Domain.Entities.ShortSessionSlab>? shortSessionSlabs = null);
    Task SendLockCommandToAgentAsync(Guid pcId);

    /// <summary>Warns the member at this PC that their balance is nearly used up.</summary>
    Task SendWalletRunningOutToAgentAsync(Guid pcId, int minutesLeft, decimal balance);

    /// <summary>
    /// Tells the member their balance is finished and the session has ended. Sent before the
    /// session is stopped, so the explanation is on screen before the PC locks — otherwise the
    /// machine simply goes dead in front of them and looks broken.
    /// </summary>
    Task SendWalletFinishedToAgentAsync(Guid pcId);
    Task TriggerDashboardRefreshAsync();

    /// <summary>
    /// Tells this operator's own browser, right now, that Super Admin ended their shift out
    /// from under them - the DB/token-revocation side of a force-logout already happened by
    /// the time this is called. Without this the operator's screen kept showing the shift as
    /// open until their token separately expired or they reloaded - Super Admin saw the
    /// closure immediately (their view re-queries the DB), the operator did not, for however
    /// long they happened to keep working unaware.
    /// </summary>
    Task SendForceLogoutAsync(Guid operatorId, string reason);

    /// <summary>A member's wallet balance changed - local branch edit or a Head Office remote
    /// command applied here - so the operator PC screen showing it (session start, wallet
    /// desk) can update in place instead of waiting for its next manual refresh.</summary>
    Task BroadcastMemberBalanceUpdateAsync(Guid branchId, Guid memberId);
}
