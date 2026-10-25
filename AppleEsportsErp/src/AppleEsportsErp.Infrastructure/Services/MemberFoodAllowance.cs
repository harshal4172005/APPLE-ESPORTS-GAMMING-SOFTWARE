using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

/// <summary>
/// Owner's rule (25 Oct 2026): a member can only order food their food wallet covers. Food is
/// taken from the wallet at logout, so what is still free right now is the wallet minus every
/// food order already placed in this session. Anything beyond it is refused - on the gaming PC
/// and when the counter adds food to the member's session - and the counter makes a separate
/// walk-in food bill, paid on the spot. That is what stops member food credits, which EOD
/// could not count correctly (the member's logout bill is not in the day's bills, its credit was).
/// </summary>
public static class MemberFoodAllowance
{
    public static async Task<decimal> AvailableAsync(
        IQueryable<Member> members, IQueryable<FoodOrder> orders, Guid memberId, Guid? sessionId)
    {
        var balance = await members.Where(m => m.Id == memberId).Select(m => m.FoodBalance).FirstOrDefaultAsync();
        var ordered = sessionId is { } sid
            ? await orders.Where(o => o.SessionId == sid && o.Status != OrderStatus.Cancelled).SumAsync(o => o.TotalAmount)
            : 0m;
        return Math.Max(0, balance - ordered);
    }
}
