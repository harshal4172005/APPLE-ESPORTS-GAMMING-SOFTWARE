using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using AppleEsportsErp.Api.Hubs;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.DTOs.Members;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Api.Services;

public class HubNotificationService : IHubNotificationService
{
    private readonly IHubContext<PcStatusHub> _pcStatusHub;
    private readonly IHubContext<SessionHub> _sessionHub;
    private readonly IHubContext<ReservationHub> _reservationHub;
    private readonly IHubContext<BillingHub> _billingHub;
    private readonly IHubContext<FoodOrderHub> _foodOrderHub;
    private readonly IHubContext<CashHub> _cashHub;
    private readonly IHubContext<PcOverlayHub> _pcOverlayHub;
    private readonly IHubContext<DashboardHub> _dashboardHub;
    private readonly IHubContext<NotificationHub> _notificationHub;
    private readonly IServiceScopeFactory _scopeFactory;

    public HubNotificationService(
        IHubContext<PcStatusHub> pcStatusHub,
        IHubContext<SessionHub> sessionHub,
        IHubContext<ReservationHub> reservationHub,
        IHubContext<BillingHub> billingHub,
        IHubContext<FoodOrderHub> foodOrderHub,
        IHubContext<CashHub> cashHub,
        IHubContext<PcOverlayHub> pcOverlayHub,
        IHubContext<DashboardHub> dashboardHub,
        IHubContext<NotificationHub> notificationHub,
        IServiceScopeFactory scopeFactory)
    {
        _pcStatusHub = pcStatusHub;
        _sessionHub = sessionHub;
        _reservationHub = reservationHub;
        _billingHub = billingHub;
        _foodOrderHub = foodOrderHub;
        _cashHub = cashHub;
        _pcOverlayHub = pcOverlayHub;
        _dashboardHub = dashboardHub;
        _notificationHub = notificationHub;
        _scopeFactory = scopeFactory;
    }

    public async Task BroadcastPcStatusChangeAsync(Guid branchId, Guid pcId)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var pcStatusService = scope.ServiceProvider.GetRequiredService<IPcStatusService>();
            var pcStatus = await pcStatusService.GetPcStatusAsync(pcId);
            await _pcStatusHub.Clients.Group($"branch:{branchId}")
                .SendAsync("PcStatusChanged", new EventEnvelope<object>(pcStatus));
            await _pcStatusHub.Clients.Group("admin:all")
                .SendAsync("PcStatusChanged", new EventEnvelope<object>(pcStatus));
                
            // Also notify the PC overlay directly
            await _pcOverlayHub.Clients.Group($"pc:{pcId}")
                .SendAsync("PcStatusChanged", pcStatus);
        }
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastSessionUpdateAsync(Guid branchId, Guid sessionId)
    {
        var payload = new { sessionId, branchId };
        await _sessionHub.Clients.Group($"branch:{branchId}")
            .SendAsync("SessionUpdated", new EventEnvelope<object>(payload));
        await _sessionHub.Clients.Group("admin:all")
            .SendAsync("SessionUpdated", new EventEnvelope<object>(payload));
            
        // Also notify PC Overlay
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Pc).FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session != null)
            {
                var pcIdStr = session.PcId.ToString();
                var pcNumStr = session.Pc?.PcNumber ?? pcIdStr;

                if (session.State == SessionState.Completed)
                {
                    // Find if there's an unpaid bill
                    var hasUnpaidBill = await db.Bills.AnyAsync(b => b.SessionId == sessionId && b.Status != BillStatus.Completed);
                    if (hasUnpaidBill)
                    {
                        var bill = await db.Bills.FirstOrDefaultAsync(b => b.SessionId == sessionId && b.Status != BillStatus.Completed);
                        var totalBill = bill?.TotalAmount ?? session.TotalAmount;
                        await _pcOverlayHub.Clients.Group($"pc:{pcIdStr}").SendAsync("SessionStopped", new { totalBill });
                        await _pcOverlayHub.Clients.Group($"pc:{pcNumStr}").SendAsync("SessionStopped", new { totalBill });
                    }
                    else
                    {
                        await _pcOverlayHub.Clients.Group($"pc:{pcIdStr}").SendAsync("SessionEnded");
                        await _pcOverlayHub.Clients.Group($"pc:{pcNumStr}").SendAsync("SessionEnded");
                    }
                }
                else if (session.State == SessionState.Active)
                {
                    await _pcOverlayHub.Clients.Group($"pc:{pcIdStr}").SendAsync("SessionUpdated", payload);
                    await _pcOverlayHub.Clients.Group($"pc:{pcNumStr}").SendAsync("SessionUpdated", payload);
                }
            }
        }

        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastReservationUpdateAsync(Guid branchId, Guid reservationId)
    {
        var payload = new { reservationId, branchId };
        await _reservationHub.Clients.Group($"branch:{branchId}")
            .SendAsync("ReservationUpdated", new EventEnvelope<object>(payload));
        await _reservationHub.Clients.Group("admin:all")
            .SendAsync("ReservationUpdated", new EventEnvelope<object>(payload));
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastBillingUpdateAsync(Guid branchId, Guid billId)
    {
        var payload = new { billId, branchId };
        await _billingHub.Clients.Group($"branch:{branchId}")
            .SendAsync("BillUpdated", new EventEnvelope<object>(payload));
        await _billingHub.Clients.Group("admin:all")
            .SendAsync("BillUpdated", new EventEnvelope<object>(payload));
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastFoodOrderUpdateAsync(Guid branchId, Guid orderId)
    {
        var payload = new { orderId, branchId };
        await _foodOrderHub.Clients.Group($"branch:{branchId}")
            .SendAsync("FoodOrderUpdated", new EventEnvelope<object>(payload));
        await _foodOrderHub.Clients.Group("admin:all")
            .SendAsync("FoodOrderUpdated", new EventEnvelope<object>(payload));
    }

    public async Task BroadcastCashRegisterUpdateAsync(Guid branchId, Guid registerId)
    {
        var payload = new { registerId, branchId };
        await _cashHub.Clients.Group($"branch:{branchId}")
            .SendAsync("CashRegisterUpdated", new EventEnvelope<object>(payload));
        await _cashHub.Clients.Group("admin:all")
            .SendAsync("CashRegisterUpdated", new EventEnvelope<object>(payload));
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastPcManagementUpdateAsync(Guid branchId, Guid pcId, string action)
    {
        var payload = new { pcId, branchId, action };
        await _pcStatusHub.Clients.Group($"branch:{branchId}")
            .SendAsync("PcManagementUpdated", new EventEnvelope<object>(payload));
        await _pcStatusHub.Clients.Group("admin:all")
            .SendAsync("PcManagementUpdated", new EventEnvelope<object>(payload));
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task BroadcastPricingProfileUpdateAsync(Guid branchId)
    {
        // Rate/buffer changed for this branch — push every connected screen (operator PC
        // cards, billing counter, member overlays) to refetch immediately instead of waiting
        // for their next poll, so pricing changes feel instant everywhere.
        var payload = new { branchId };
        await _pcStatusHub.Clients.Group($"branch:{branchId}")
            .SendAsync("PricingProfileUpdated", new EventEnvelope<object>(payload));
        await _pcStatusHub.Clients.Group("admin:all")
            .SendAsync("PricingProfileUpdated", new EventEnvelope<object>(payload));
        await InvalidateDashboardCacheAsync(branchId);
    }

    public async Task SendUnlockCommandToAgentAsync(
        Guid pcId, int durationMinutes, string? customerName,
        decimal? packagePrice = null, int? plannedDurationMin = null, string? packageName = null,
        decimal ratePerHour = 0m, int bufferMinutes = 0, DateTimeOffset? sessionStartUtc = null,
        int? packageFromMin = null,
        List<Domain.Entities.ShortSessionSlab>? shortSessionSlabs = null)
    {
        await _pcStatusHub.Clients.Group($"agent:{pcId}").SendAsync("UnlockSession", new
        {
            DurationMinutes = durationMinutes,
            CustomerName = customerName,
            PackagePrice = packagePrice,
            PlannedDurationMin = plannedDurationMin,
            PackageFromMin = packageFromMin,
            PackageName = packageName,
            RatePerHour = ratePerHour,
            BufferMinutes = bufferMinutes,
            SessionStartUtc = sessionStartUtc,
            ShortSessionSlabs = shortSessionSlabs,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public async Task SendWalletRunningOutToAgentAsync(Guid pcId, int minutesLeft, decimal balance)
    {
        await _pcStatusHub.Clients.Group($"agent:{pcId}").SendAsync("WalletRunningOut", new
        {
            MinutesLeft = minutesLeft,
            Balance = balance,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public async Task SendWalletFinishedToAgentAsync(Guid pcId)
    {
        await _pcStatusHub.Clients.Group($"agent:{pcId}").SendAsync("WalletFinished", new
        {
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public async Task SendLockCommandToAgentAsync(Guid pcId)
    {
        await _pcStatusHub.Clients.Group($"agent:{pcId}").SendAsync("LockSession", new
        {
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private async Task InvalidateDashboardCacheAsync(Guid branchId)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
            await dashboardService.InvalidateCacheAsync(branchId);
        }
    }

    public async Task TriggerDashboardRefreshAsync()
    {
        // Invalidate global cache so the next fetch gets fresh data
        using (var scope = _scopeFactory.CreateScope())
        {
            var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
            await dashboardService.InvalidateCacheAsync(null);
        }
        await _dashboardHub.Clients.All.SendAsync("DashboardRefreshRequired");
    }

    public async Task SendForceLogoutAsync(Guid operatorId, string reason)
    {
        // "user:{id}" is joined by every connection on every BranchAwareHub (see
        // Hubs.cs's OnConnectedAsync), so this reaches the operator's session regardless of
        // which screen happens to be open. SocketContext.jsx has been listening for exactly
        // this event on the notifications hub the whole time - nothing ever sent it.
        await _notificationHub.Clients.Group($"user:{operatorId}").SendAsync("ForceLogout", reason);
    }

    public async Task BroadcastMemberBalanceUpdateAsync(Guid branchId, Guid memberId)
    {
        using var scope = _scopeFactory.CreateScope();
        var memberService = scope.ServiceProvider.GetRequiredService<IMemberService>();
        MemberDto? member;
        try
        {
            member = await memberService.GetMemberByIdAsync(memberId);
        }
        catch
        {
            // Head Office broadcasting about a member id its own database doesn't have a row
            // for (the branch-local edit path, not the remote one) - nothing to push.
            return;
        }

        var payload = new { memberId, branchId, member };
        // NotificationHub, not a new dedicated hub - this is the one already used for
        // miscellaneous branch-wide pushes not tied to one specific domain hub (ForceLogout,
        // PermissionsUpdated), and every screen already connects to it via SocketContext.jsx.
        await _notificationHub.Clients.Group($"branch:{branchId}")
            .SendAsync("MemberBalanceUpdated", new EventEnvelope<object>(payload));

        // And the member's own gaming PC, if they are playing: its screen otherwise only saw a
        // new balance on its next poll, and a raised balance could still show "low balance".
        var db = scope.ServiceProvider.GetRequiredService<AppleEsportsErp.Infrastructure.Data.AppDbContext>();
        var pcIds = await db.Sessions.AsNoTracking()
            .Where(s => s.MemberId == memberId
                        && (s.State == AppleEsportsErp.Domain.Enums.SessionState.Active
                            || s.State == AppleEsportsErp.Domain.Enums.SessionState.Interrupted))
            .Select(s => s.PcId)
            .ToListAsync();
        foreach (var pcId in pcIds)
            await _pcOverlayHub.Clients.Group($"pc:{pcId}").SendAsync("MemberBalanceUpdated", new
            {
                memberId,
                gamingBalance = member?.GamingBalance,
                foodBalance = member?.FoodBalance,
            });
    }
}
