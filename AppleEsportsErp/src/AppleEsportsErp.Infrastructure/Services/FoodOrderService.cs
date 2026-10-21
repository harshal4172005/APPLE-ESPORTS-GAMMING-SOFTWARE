using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.FoodOrders;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;

namespace AppleEsportsErp.Infrastructure.Services;

public class FoodOrderService : IFoodOrderService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditService _auditService;
    private readonly IHubNotificationService _hubNotification;
    private readonly IOutboxService _outbox;
    private readonly IConfiguration _configuration;

    public FoodOrderService(
        IUnitOfWork unitOfWork,
        IAuditService auditService,
        IHubNotificationService hubNotification,
        IOutboxService outbox,
        IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _hubNotification = hubNotification;
        _outbox = outbox;
        _configuration = configuration;
    }

    /// <summary>
    /// Refuses to place or move a food order forward from Head Office.
    ///
    /// The same rule as a session, and for the same reason: a walk-in order placed here writes
    /// only Head Office's own copy of the kitchen. Nobody in the kitchen is told, nothing gets
    /// cooked, and the customer standing at the counter is left waiting on an order that exists
    /// nowhere real. Food orders never even had this refusal - they could be placed and marked
    /// delivered from Head Office's screen with no branch involved at all, silently, because
    /// unlike a session there was no error to notice anything had gone wrong.
    /// </summary>
    private void RefuseIfHeadOffice(string what)
    {
        if (!_configuration.IsHeadOffice()) return;

        throw new AppException(
            $"A food order cannot be {what} from Head Office - only at the branch's own counter. " +
            "This screen shows what each shop is doing. Doing it here would be invisible to the " +
            "kitchen, so nothing would actually be prepared or served.",
            System.Net.HttpStatusCode.BadRequest,
            "BRANCH_ONLY_OPERATION");
    }

    public async Task<PaginatedResult<FoodOrderDto>> GetActiveOrdersAsync(Guid branchId, int page = 1, int pageSize = 50)
    {
        var query = _unitOfWork.Repository<FoodOrder>().Query()
            .Include(o => o.Items)
            .Include(o => o.Pc)
            .Where(o => o.BranchId == branchId && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled)
            .OrderByDescending(o => o.OrderTime);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var dtos = items.Select(MapToDto).ToList();
        return new PaginatedResult<FoodOrderDto>(dtos, total, page, pageSize);
    }

    /// <summary>
    /// Every order in a date range, whatever its status - unlike GetActiveOrdersAsync, which
    /// deliberately excludes Completed/Cancelled because it feeds the live kitchen board and
    /// those have nothing left to do. A look back at a past day wants exactly the opposite: the
    /// finished orders are the point.
    /// </summary>
    public async Task<List<FoodOrderDto>> GetOrderHistoryAsync(Guid branchId, DateOnly fromDate, DateOnly toDate)
    {
        var (dayStart, _) = IndiaTime.BusinessDayRange(fromDate);
        var (_, dayEnd) = IndiaTime.BusinessDayRange(toDate);

        var orders = await _unitOfWork.Repository<FoodOrder>().Query()
            .Include(o => o.Items)
            .Include(o => o.Pc)
            .Where(o => o.BranchId == branchId && o.OrderTime >= dayStart && o.OrderTime < dayEnd)
            .OrderByDescending(o => o.OrderTime)
            .ToListAsync();

        return orders.Select(MapToDto).ToList();
    }

    public async Task<FoodOrderDto> GetOrderAsync(Guid branchId, Guid id)
    {
        var order = await _unitOfWork.Repository<FoodOrder>().Query()
            .Include(o => o.Items)
            .Include(o => o.Pc)
            .FirstOrDefaultAsync(o => o.Id == id && o.BranchId == branchId)
            ?? throw new NotFoundException("Order not found.");

        return MapToDto(order);
    }

    public async Task<FoodOrderDto> PlaceOrderAsync(Guid branchId, Guid operatorId, Guid shiftId, CreateFoodOrderDto dto)
    {
        RefuseIfHeadOffice("placed");

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            
            // Generate order number
            var count = await _unitOfWork.Repository<FoodOrder>().Query().CountAsync(o => o.OrderTime >= new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero));
            var orderNum = $"ORD-{now:yyMMdd}-{count + 1:D4}";

            var order = new FoodOrder
            {
                OrderNumber = orderNum,
                SessionId = dto.SessionId,
                PcId = dto.PcId,
                BranchId = branchId,
                OperatorId = operatorId,
                CustomerName = dto.CustomerName,
                OrderTime = now,
                Status = OrderStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };

            decimal totalAmount = 0;

            foreach (var itemDto in dto.Items)
            {
                var inventoryItem = await _unitOfWork.Repository<InventoryItem>().GetByIdAsync(itemDto.InventoryId)
                    ?? throw new NotFoundException($"Inventory item {itemDto.InventoryId} not found.");

                if (inventoryItem.CurrentStock < itemDto.Quantity)
                    throw new AppException($"Insufficient stock for {inventoryItem.ItemName}. Available: {inventoryItem.CurrentStock}");

                // Taken now, not on delivery: checking here and taking later let two orders both
                // pass for the last one. For a shared fridge this is where Head Office decides
                // (SharedStockGate, on save) - one of two same-moment orders gets it.
                await TakeStockAsync(inventoryItem, itemDto.Quantity, branchId, operatorId, $"Order {orderNum} placed", now);

                // Add order item
                var totalPrice = inventoryItem.Price * itemDto.Quantity;
                totalAmount += totalPrice;
                
                order.Items.Add(new FoodOrderItem
                {
                    InventoryId = inventoryItem.Id,
                    ItemName = inventoryItem.ItemName,
                    Quantity = itemDto.Quantity,
                    UnitPrice = inventoryItem.Price,
                    TotalPrice = totalPrice,
                    CreatedAt = now
                });
            }

            order.TotalAmount = totalAmount;
            order.StockTaken = true;

            // If linked to a Session, lookup active bill to verify it exists and set order attributes
            Bill? activeBill = null;
            if (dto.SessionId.HasValue)
            {
                activeBill = await _unitOfWork.Repository<Bill>().Query()
                    .FirstOrDefaultAsync(b => b.SessionId == dto.SessionId && b.Status != BillStatus.Completed);

                if (activeBill == null)
                    throw new AppException("Cannot place order. No active bill found for the session.");

                order.PaymentType = "session_bill";
                order.MemberId = activeBill.MemberId;
                order.BillId = activeBill.Id;
            }
            else
            {
                // Walk-in customer (no PC session): bill for this order immediately so it
                // can be found and paid at the Billing Counter — nothing else creates it.
                activeBill = new Bill
                {
                    Id = Guid.NewGuid(),
                    BillNumber = $"BILL-{now:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 4).ToUpper()}",
                    SessionId = null,
                    PcId = dto.PcId,
                    BranchId = branchId,
                    OperatorId = operatorId,
                    ShiftId = shiftId == Guid.Empty ? null : shiftId,
                    CustomerName = dto.CustomerName,
                    GamingAmount = 0,
                    FoodAmount = totalAmount,
                    Subtotal = totalAmount,
                    TotalAmount = totalAmount,
                    Status = BillStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                foreach (var item in order.Items)
                {
                    activeBill.Items.Add(new BillItem
                    {
                        ItemType = "food",
                        ItemName = item.ItemName,
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        TotalPrice = item.TotalPrice,
                        InventoryId = item.InventoryId,
                        CreatedAt = now
                    });
                }

                await _unitOfWork.Repository<Bill>().AddAsync(activeBill);

                order.PaymentType = "walkin_bill";
                order.BillId = activeBill.Id;
            }

            await _unitOfWork.Repository<FoodOrder>().AddAsync(order);

            // Tell Head Office this order exists, with everything actually ordered - not just
            // its eventual money. Until now nothing did: a walk-in order's Bill happened to
            // sync because Bill is separately watched, but a session-linked order updated no
            // synced row at all until the food was marked delivered, and even then Head Office
            // only ever learned a total, never which dishes or how many.
            await _outbox.RecordEventAsync(branchId, "FoodOrder", order.Id, "food_order.placed", new
            {
                orderId = order.Id,
                orderNumber = orderNum,
                branchId,
                sessionId = order.SessionId,
                pcId = order.PcId,
                billId = order.BillId,
                operatorId,
                memberId = order.MemberId,
                customerName = order.CustomerName,
                paymentType = order.PaymentType,
                totalAmount,
                orderTime = order.OrderTime,
                items = order.Items.Select(i => new
                {
                    inventoryId = i.InventoryId,
                    itemName = i.ItemName,
                    quantity = i.Quantity,
                    unitPrice = i.UnitPrice,
                    totalPrice = i.TotalPrice,
                }),
            });

            // Audit
            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                Action = AuditActions.FoodOrderPlace,
                BranchId = branchId,
                TargetType = "food_order",
                TargetId = order.Id,
                Details = new { OrderNumber = orderNum, Total = totalAmount, ItemCount = order.Items.Count }
            });

            await _unitOfWork.CommitTransactionAsync();
            
            await _hubNotification.BroadcastFoodOrderUpdateAsync(branchId, order.Id);
            if (activeBill != null)
            {
                await _hubNotification.BroadcastBillingUpdateAsync(branchId, activeBill.Id);
            }

            return MapToDto(order);
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                Action = AuditActions.FoodOrderPlace,
                TargetType = "food_order",
                Success = false,
                BranchId = branchId,
                Details = new { error = ex.GetBaseException().Message },
            });
            throw;
        }
    }

    public async Task<FoodOrderDto> UpdateOrderStatusAsync(Guid branchId, Guid operatorId, Guid id, UpdateOrderStatusDto dto)
    {
        RefuseIfHeadOffice("updated");

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var order = await _unitOfWork.Repository<FoodOrder>().Query()
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == id && o.BranchId == branchId)
                ?? throw new NotFoundException("Order not found.");

            if (order.Status == OrderStatus.Completed || order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Delivered)
                throw new AppException($"Order is already {order.Status} and cannot be modified.");

            var now = DateTimeOffset.UtcNow;
            var oldStatus = order.Status;
            order.Status = dto.Status;
            order.UpdatedAt = now;

            if (dto.Status == OrderStatus.Preparing)
                order.AcceptedAt = now;
            else if (dto.Status == OrderStatus.Ready)
                order.ReadyAt = now;
            else if (dto.Status == OrderStatus.Delivered)
            {
                order.DeliveredAt = now;

                // 1. Deduct stock and increment SoldQty for each item in the order
                foreach (var item in order.Items)
                {
                    var inventoryItem = await _unitOfWork.Repository<InventoryItem>().GetByIdAsync(item.InventoryId);
                    if (inventoryItem != null && order.StockTaken)
                    {
                        // Stock already came off when the order was placed.
                        inventoryItem.SoldQty += item.Quantity;
                        inventoryItem.UpdatedAt = now;
                        _unitOfWork.Repository<InventoryItem>().Update(inventoryItem);
                    }
                    else if (inventoryItem != null)
                    {
                        if (inventoryItem.CurrentStock < item.Quantity)
                            throw new AppException($"Insufficient stock for {inventoryItem.ItemName}. Available: {inventoryItem.CurrentStock}");

                        int oldStock = inventoryItem.CurrentStock;
                        inventoryItem.CurrentStock -= item.Quantity;
                        inventoryItem.SoldQty += item.Quantity;
                        inventoryItem.UpdatedAt = now;
                        _unitOfWork.Repository<InventoryItem>().Update(inventoryItem);

                        // Log the inventory deduction (Action = "sale")
                        var log = new InventoryLog
                        {
                            InventoryId = inventoryItem.Id,
                            BranchId = branchId,
                            OperatorId = operatorId,
                            Action = "sale",
                            Quantity = item.Quantity,
                            OldValue = oldStock.ToString(),
                            NewValue = inventoryItem.CurrentStock.ToString(),
                            Reason = $"Delivered order {order.OrderNumber}",
                            CreatedAt = now
                        };
                        await _unitOfWork.Repository<InventoryLog>().AddAsync(log);
                    }
                }

                // 2. Automatically append to active bill if linked to a session
                if (order.SessionId.HasValue)
                {
                    var activeBill = await _unitOfWork.Repository<Bill>().Query()
                        .Include(b => b.Items)
                        .FirstOrDefaultAsync(b => b.SessionId == order.SessionId.Value && b.Status != BillStatus.Completed);

                    if (activeBill == null)
                        throw new AppException("Cannot deliver order. No active bill found for the session.");

                    foreach (var item in order.Items)
                    {
                        activeBill.Items.Add(new BillItem
                        {
                            ItemType = "food",
                            ItemName = item.ItemName,
                            Quantity = item.Quantity,
                            UnitPrice = item.UnitPrice,
                            TotalPrice = item.TotalPrice,
                            InventoryId = item.InventoryId,
                            CreatedAt = now
                        });
                    }

                    // Food items are real menu-priced products — keep their total exact, and
                    // let the Gaming line (a derived, not a fixed, price) absorb any rounding.
                    decimal newFoodAmount = activeBill.FoodAmount + order.TotalAmount;
                    var (displayGaming, displayFood, roundedTotal) = SessionPricingCalculator.ComputeRoundedBreakdown(
                        activeBill.GamingAmount, newFoodAmount, activeBill.DiscountAmount);

                    activeBill.FoodAmount = displayFood;
                    activeBill.GamingAmount = displayGaming;
                    activeBill.Subtotal = displayGaming + displayFood;
                    activeBill.TotalAmount = roundedTotal;
                    activeBill.UpdatedAt = now;

                    var gamingItem = activeBill.Items.FirstOrDefault(i => i.ItemType == "gaming");
                    if (gamingItem != null)
                    {
                        gamingItem.TotalPrice = displayGaming;
                        gamingItem.UnitPrice = displayGaming;
                    }
                    _unitOfWork.Repository<Bill>().Update(activeBill);

                    await _hubNotification.BroadcastBillingUpdateAsync(branchId, activeBill.Id);
                }
            }
            else if (dto.Status == OrderStatus.Completed)
                order.CompletedAt = now;
            else if (dto.Status == OrderStatus.Cancelled)
            {
                order.CancelledReason = dto.Reason;

                // Taken at placement, so a cancel puts it back on the shelf.
                if (order.StockTaken)
                {
                    foreach (var item in order.Items)
                    {
                        var inventoryItem = await _unitOfWork.Repository<InventoryItem>().GetByIdAsync(item.InventoryId);
                        if (inventoryItem == null) continue;
                        await TakeStockAsync(inventoryItem, -item.Quantity, branchId, operatorId, $"Order {order.OrderNumber} cancelled", now);
                    }
                    order.StockTaken = false;
                }
            }

            _unitOfWork.Repository<FoodOrder>().Update(order);

            // Same status the kitchen just reached, told to Head Office explicitly rather than
            // left to arrive only as a side effect of the bill changing on delivery - the
            // status itself (accepted, ready, cancelled and why) was never visible there at all.
            await _outbox.RecordEventAsync(branchId, "FoodOrder", order.Id, "food_order.status_changed", new
            {
                orderId = order.Id,
                status = order.Status.ToString(),
                reason = order.CancelledReason,
                acceptedAt = order.AcceptedAt,
                readyAt = order.ReadyAt,
                deliveredAt = order.DeliveredAt,
                completedAt = order.CompletedAt,
                totalAmount = order.TotalAmount,
            });

            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                Action = AuditActions.FoodOrderStatusChange,
                BranchId = branchId,
                TargetType = "food_order",
                TargetId = order.Id,
                Details = new { Status = dto.Status.ToString(), Reason = dto.Reason }
            });

            await _unitOfWork.CommitTransactionAsync();
            await _hubNotification.BroadcastFoodOrderUpdateAsync(branchId, order.Id);

            return MapToDto(order);
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            await _auditService.LogAsync(new AuditEntry
            {
                OperatorId = operatorId,
                UserRole = "Operator",
                Action = AuditActions.FoodOrderStatusChange,
                TargetType = "food_order",
                TargetId = id,
                Success = false,
                BranchId = branchId,
                Details = new { error = ex.GetBaseException().Message },
            });
            throw;
        }
    }

    /// <summary>Takes <paramref name="quantity"/> off the shelf (negative puts it back) and logs it.</summary>
    private async Task TakeStockAsync(InventoryItem item, int quantity, Guid branchId, Guid operatorId, string reason, DateTimeOffset now)
    {
        var oldStock = item.CurrentStock;
        item.CurrentStock -= quantity;
        item.UpdatedAt = now;
        _unitOfWork.Repository<InventoryItem>().Update(item);

        await _unitOfWork.Repository<InventoryLog>().AddAsync(new InventoryLog
        {
            InventoryId = item.Id,
            BranchId = branchId,
            OperatorId = operatorId == Guid.Empty ? null : operatorId,
            Action = quantity > 0 ? "sale" : "void_return",
            Quantity = Math.Abs(quantity),
            OldValue = oldStock.ToString(),
            NewValue = item.CurrentStock.ToString(),
            Reason = reason,
            CreatedAt = now,
        });
    }

    private static FoodOrderDto MapToDto(FoodOrder o)
    {
        return new FoodOrderDto
        {
            Id = o.Id,
            OrderNumber = o.OrderNumber,
            SessionId = o.SessionId,
            PcId = o.PcId,
            PcNumber = o.Pc?.PcNumber,
            BillId = o.BillId,
            BranchId = o.BranchId,
            OperatorId = o.OperatorId,
            CustomerName = o.CustomerName,
            TotalAmount = o.TotalAmount,
            PaymentType = o.PaymentType,
            Status = o.Status,
            CancelledReason = o.CancelledReason,
            OrderTime = o.OrderTime,
            DeliveredAt = o.DeliveredAt,
            Items = o.Items?.Select(i => new FoodOrderItemDto
            {
                Id = i.Id,
                InventoryId = i.InventoryId,
                ItemName = i.ItemName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                TotalPrice = i.TotalPrice
            }).ToList() ?? new List<FoodOrderItemDto>()
        };
    }
}
