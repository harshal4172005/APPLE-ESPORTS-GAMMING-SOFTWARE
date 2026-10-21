using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Services;

/// <summary>
/// The owner's rule: whatever a shift stock count finds missing, the operator responsible pays
/// for - at menu price, automatically, no one has to decide it. The system had 41 Predator, the
/// shelf has 39: a ₹120 bill for 2 × Predator goes to that operator on the Billing Counter, paid
/// cash, online or split like any other bill.
///
/// Who is responsible: at shift end, the operator whose shift it was; at shift start (or when
/// taking over a shift left open), the operator who handed over - the stock went missing in
/// their custody, not the custody of the person who has just arrived.
///
/// The bill moves money only. Its items carry no InventoryId, so paying, discounting or
/// removing it never touches stock: the count itself already set the shelf to what is there.
/// </summary>
public static class StockShortageBilling
{
    public static Bill? Build(Guid branchId, Guid operatorId, string operatorName, Guid? shiftId,
        IEnumerable<(InventoryItem Item, int Missing)> missing, DateTimeOffset now)
    {
        var lines = missing.Where(m => m.Missing > 0 && m.Item.Price > 0).ToList();
        if (lines.Count == 0) return null;

        var bill = new Bill
        {
            Id = Guid.NewGuid(),
            BillNumber = $"MISS-{now:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
            BranchId = branchId,
            OperatorId = operatorId,
            ShiftId = shiftId,
            CustomerName = $"Missing stock - {operatorName}",
            ShortageOperatorId = operatorId,
            Status = BillStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var (item, count) in lines)
        {
            bill.Items.Add(new BillItem
            {
                Id = Guid.NewGuid(),
                BillId = bill.Id,
                ItemType = "food",
                ItemName = $"{item.ItemName} - missing in stock count",
                Quantity = count,
                UnitPrice = item.Price,
                TotalPrice = item.Price * count,
                CreatedAt = now,
            });
        }

        bill.FoodAmount = bill.Items.Sum(i => i.TotalPrice);
        bill.Subtotal = bill.FoodAmount;
        bill.TotalAmount = bill.FoodAmount;
        return bill;
    }
}
