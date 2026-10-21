using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Hubs;

/// <summary>
/// Unauthenticated Hub for PC Overlay Clients.
/// Since PCs run in an anonymous overlay context, we do not require [Authorize].
/// </summary>
public class PcOverlayHub : Hub
{
    // In-process store of pending walk-in requests. Acts as polling fallback when SignalR delivery is unreliable.
    public static readonly ConcurrentDictionary<string, PendingWalkinData> PendingWalkinRequests
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<PcOverlayHub> _logger;
    private readonly IHubContext<PcStatusHub> _pcStatusHub;
    private readonly IHubContext<FoodOrderHub> _foodOrderHub;
    private readonly IHubContext<SessionHub> _sessionHub;
    private readonly IHubContext<NotificationHub> _notificationHub;
    private readonly IHubNotificationService _hubNotificationService;
    private readonly AppDbContext _db;

    public PcOverlayHub(
        ILogger<PcOverlayHub> logger,
        IHubContext<PcStatusHub> pcStatusHub,
        IHubContext<FoodOrderHub> foodOrderHub,
        IHubContext<SessionHub> sessionHub,
        IHubContext<NotificationHub> notificationHub,
        IHubNotificationService hubNotificationService,
        AppDbContext db)
    {
        _logger = logger;
        _pcStatusHub = pcStatusHub;
        _foodOrderHub = foodOrderHub;
        _sessionHub = sessionHub;
        _notificationHub = notificationHub;
        _hubNotificationService = hubNotificationService;
        _db = db;
    }

    public async Task ConnectPc(string pcId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"pc:{pcId}");
        _logger.LogInformation("PC Overlay Connected: {PcId} (ConnectionId: {ConnectionId})", pcId, Context.ConnectionId);

        // The PC is back and running its overlay again, which by itself is proof it is not the
        // powered-off machine SendShutdownCommand marked a moment ago - true whether it came back
        // from an operator's command, someone pressing the power button themselves, or an
        // unplanned reboot. This is the one place that is true for every PC regardless of how it
        // went down, so it is the one place this flag gets cleared. See Pc.PoweredOff.
        await ClearPoweredOffAsync(pcId, TimeSpan.Zero);
    }

    private async Task ClearPoweredOffAsync(string pcId, TimeSpan markedAtLeast)
    {
        var pc = await GetPcAsync(pcId);
        if (pc != null && pc.PoweredOff && pc.UpdatedAt <= DateTimeOffset.UtcNow - markedAtLeast)
        {
            pc.PoweredOff = false;
            pc.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();

            await _hubNotificationService.BroadcastPcStatusChangeAsync(pc.BranchId, pc.Id);
        }
    }

    public async Task Heartbeat(HeartbeatPayload payload)
    {
        // Every 30 s from a PC whose screen is up - proof it is on. Connecting alone was not
        // enough: a PC already connected when it got marked off (Shutdown All, or last night's
        // shutdown arriving late) stayed "OFF - BILLING" all day with a customer playing on it.
        // Only marks 2+ minutes old: a PC still powering down after Shutdown sends one last
        // heartbeat, and that one must not undo the mark. (UpdatedAt is when it was marked.)
        await ClearPoweredOffAsync(payload.PcId, TimeSpan.FromMinutes(2));


        // Broadcast to operators that this PC is active
        _logger.LogDebug("Heartbeat received from {PcId}", payload.PcId);
        await _pcStatusHub.Clients.All.SendAsync("PcStatusUpdated", new
        {
            PcId = payload.PcId,
            Status = "active",
            LastActive = payload.Timestamp
        });
    }

    public async Task IdleDetected(IdlePayload payload)
    {
        _logger.LogWarning("PC {PcId} went idle at {IdleSince}", payload.PcId, payload.IdleSince);
        await _pcStatusHub.Clients.All.SendAsync("PcStatusUpdated", new
        {
            PcId = payload.PcId,
            Status = "idle",
            LastActive = payload.IdleSince
        });
    }

    public async Task ActivityEvent(ActivityPayload payload)
    {
        _logger.LogInformation("Activity on {PcId}: {Event}", payload.PcId, payload.Event);
    }

    public async Task<object> PlaceFoodOrder(FoodOrderPayload? payload)
    {
        if (payload == null)
        {
            _logger.LogError("PlaceFoodOrder called with NULL payload! JSON binding failed.");
            return new { success = false, error = "Invalid payload" };
        }

        _logger.LogInformation("Food order {OrderId} placed from {PcId}", payload.OrderId, payload.PcId);

        try
        {
            Pc? pc = null;
            if (Guid.TryParse(payload.PcId, out Guid pcIdGuid))
            {
                pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcIdGuid);
            }
            if (pc == null)
            {
                pc = await _db.Pcs.FirstOrDefaultAsync(p => p.PcNumber == payload.PcId || p.PcName == payload.PcId);
            }

            if (pc != null)
            {
                Session? session = null;
                if (!string.IsNullOrEmpty(payload.SessionId) && Guid.TryParse(payload.SessionId, out Guid sessionIdGuid))
                {
                    session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionIdGuid);
                }
                else
                {
                    session = await _db.Sessions
                        .Where(s => s.PcId == pc.Id && s.State == SessionState.Active)
                        .OrderByDescending(s => s.StartTime)
                        .FirstOrDefaultAsync();
                }

                var orderNumber = $"ORD-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{new Random().Next(100, 999)}";

                var order = new FoodOrder
                {
                    Id = Guid.NewGuid(),
                    OrderNumber = string.IsNullOrEmpty(payload.OrderId) ? orderNumber : payload.OrderId,
                    SessionId = session?.Id,
                    PcId = pc.Id,
                    BranchId = pc.BranchId,
                    CustomerName = session?.CustomerName,
                    MemberId = session?.MemberId,
                    TotalAmount = payload.TotalAmount,
                    PaymentType = "session_bill",
                    Status = OrderStatus.Pending,
                    OrderTime = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Items = payload.Items.Select(i => new FoodOrderItem
                    {
                        Id = Guid.NewGuid(),
                        InventoryId = Guid.TryParse(i.MenuItemId, out var mId) ? mId : Guid.Empty,
                        ItemName = i.Name,
                        Quantity = i.Quantity,
                        UnitPrice = i.Price,
                        TotalPrice = i.Quantity * i.Price,
                        CreatedAt = DateTimeOffset.UtcNow
                    }).ToList()
                };

                // Taken off the shelf now, same as an order placed at the counter
                // (FoodOrderService) - this path never checked stock at all, so a gaming PC
                // could order something the fridge no longer had. For a shared fridge the save
                // below asks Head Office (SharedStockGate) and may refuse with "sold out".
                foreach (var line in order.Items.Where(i => i.InventoryId != Guid.Empty))
                {
                    var stock = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == line.InventoryId);
                    if (stock is null) continue;
                    if (stock.CurrentStock < line.Quantity)
                        return new { success = false, error = stock.CurrentStock <= 0
                            ? $"{stock.ItemName} is sold out."
                            : $"Only {stock.CurrentStock} {stock.ItemName} left." };

                    _db.InventoryLogs.Add(new InventoryLog
                    {
                        Id = Guid.NewGuid(),
                        InventoryId = stock.Id,
                        BranchId = pc.BranchId,
                        Action = "sale",
                        Quantity = line.Quantity,
                        OldValue = stock.CurrentStock.ToString(),
                        NewValue = (stock.CurrentStock - line.Quantity).ToString(),
                        Reason = $"Order {order.OrderNumber} placed from {pc.PcName ?? pc.PcNumber}",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                    stock.CurrentStock -= line.Quantity;
                    stock.UpdatedAt = DateTimeOffset.UtcNow;
                }
                order.StockTaken = true;

                _db.FoodOrders.Add(order);

                if (session != null)
                {
                    session.FoodAmount += payload.TotalAmount;
                    session.TotalAmount = session.GamingAmount + session.FoodAmount;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _db.SaveChangesAsync();

                bool isOperatorOnline = pc != null && AppleEsportsErp.Application.Services.OperatorPresenceTracker.IsOperatorAvailable(pc.BranchId.ToString());
                if (pc != null && isOperatorOnline)
                {
                    await _foodOrderHub.Clients.Group($"branch:{pc.BranchId}").SendAsync("NewFoodOrder", new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        pcId = pc.Id,
                        pcName = pc.PcName ?? pc.PcNumber,
                        branchId = pc.BranchId,
                        customerName = order.CustomerName,
                        totalAmount = order.TotalAmount,
                        status = order.Status.ToString(),
                        items = payload.Items,
                        orderTime = order.OrderTime
                    });
                }
                else
                {
                    await _foodOrderHub.Clients.Group("admin:all").SendAsync("NewFoodOrder", new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        pcId = pc.Id,
                        pcName = pc.PcName ?? pc.PcNumber,
                        branchId = pc?.BranchId,
                        customerName = order.CustomerName,
                        totalAmount = order.TotalAmount,
                        status = order.Status.ToString(),
                        items = payload.Items,
                        orderTime = order.OrderTime
                    });
                }

                await Clients.Group($"pc:{payload.PcId}").SendAsync("BillUpdated", new
                {
                    foodCharges = session?.FoodAmount ?? payload.TotalAmount,
                    totalBill = session?.TotalAmount ?? payload.TotalAmount,
                    newOrderId = order.Id,
                    newOrderNumber = order.OrderNumber
                });

                return new { success = true, orderId = order.Id.ToString(), orderNumber = order.OrderNumber };
            }

            // PC not found fallback
            await _foodOrderHub.Clients.Group("admin:all").SendAsync("NewFoodOrder", payload);
            return new { success = true, orderId = payload.OrderId };
        }
        catch (AppleEsportsErp.Application.Exceptions.AppException ex)
        {
            // Sold out at the other counter, or the shared fridge is offline here - say so.
            return new { success = false, error = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place food order");
            return new { success = false, error = "Server error while placing order" };
        }
    }

    private async Task<Pc?> GetPcAsync(string pcIdentifier)
    {
        if (Guid.TryParse(pcIdentifier, out Guid pcGuid))
        {
            var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcGuid);
            if (pc != null) return pc;
        }
        return await _db.Pcs.FirstOrDefaultAsync(p => p.PcNumber == pcIdentifier || p.PcName == pcIdentifier);
    }

    public async Task<object> RequestExtension(ExtensionPayload payload)
    {
        _logger.LogInformation("Extension request from {PcId} for {Duration} mins", payload.PcId, payload.Duration);
        
        var pc = await GetPcAsync(payload.PcId);
        bool isOperatorOnline = pc != null && AppleEsportsErp.Application.Services.OperatorPresenceTracker.IsOperatorAvailable(pc.BranchId.ToString());
        var payloadObj = new 
        {
            PcId = payload.PcId,
            BranchId = pc?.BranchId,
            Duration = payload.Duration,
            SessionId = payload.SessionId
        };
        
        // Notify operators in this branch, OR admins globally if operator is offline
        if (pc != null && isOperatorOnline)
        {
            await _sessionHub.Clients.Group($"branch:{pc.BranchId}").SendAsync("ExtensionRequested", payloadObj);
        }
        else
        {
            await _sessionHub.Clients.Group("admin:all").SendAsync("ExtensionRequested", payloadObj);
        }
        
        // Simulating approval for the sake of the overlay testing. 
        // In real life, we would await an operator response.
        return new { success = true, status = "pending_operator_approval" };
    }

    public async Task<object> CallOperator(CallPayload payload)
    {
        _logger.LogInformation("Operator called to {PcId}", payload.PcId);
        var pc = await GetPcAsync(payload.PcId);
        bool isOperatorOnline = pc != null && AppleEsportsErp.Application.Services.OperatorPresenceTracker.IsOperatorAvailable(pc.BranchId.ToString());
        string alertMsg = $"Assistance required at {payload.PcId}";
        if (!isOperatorOnline) alertMsg = "[Operator Offline] " + alertMsg;

        var payloadObj = new 
        { 
            Type = "OperatorCall",
            PcId = payload.PcId, 
            BranchId = pc?.BranchId,
            Timestamp = payload.Timestamp,
            Message = alertMsg 
        };
        
        // Alert operators in the PC's branch, OR admins globally if operator is offline
        if (pc != null && isOperatorOnline)
        {
            await _notificationHub.Clients.Group($"branch:{pc.BranchId}").SendAsync("Alert", payloadObj);
        }
        else
        {
            await _notificationHub.Clients.Group("admin:all").SendAsync("Alert", payloadObj);
        }
        
        return new { success = true };
    }

    public async Task<object> RequestWalkinSession(WalkinSessionPayload payload)
    {
        _logger.LogInformation("Walk-in session request from {PcId} by {CustomerName} for {Duration} mins (Package: {PackageName})", payload.PcId, payload.CustomerName, payload.Duration, payload.PackageName);

        var pc = await GetPcAsync(payload.PcId);
        var pending = new PendingWalkinData
        {
            PcId = payload.PcId,
            BranchId = pc?.BranchId.ToString(),
            CustomerName = payload.CustomerName,
            Duration = payload.Duration,
            PackageName = payload.PackageName,
            Timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        // Persist so operators can poll for missed SignalR events
        PendingWalkinRequests[payload.PcId] = pending;

        bool isOperatorOnline = pc != null && AppleEsportsErp.Application.Services.OperatorPresenceTracker.IsOperatorAvailable(pc.BranchId.ToString());
        string alertMsg = $"Walk-in request: {payload.CustomerName} wants {payload.PackageName ?? (payload.Duration + " mins")} at {payload.PcId}";
        if (!isOperatorOnline) alertMsg = "[Operator Offline] " + alertMsg;

        var payloadObj = new
        {
            Type = "WalkinSessionRequest",
            pcId = payload.PcId,
            branchId = pc?.BranchId,
            customerName = payload.CustomerName,
            duration = payload.Duration,
            packageName = payload.PackageName,
            timestamp = pending.Timestamp,
            message = alertMsg
        };

        // Real-time push to operator dashboards in this branch, OR admins globally if operator is offline
        if (pc != null && isOperatorOnline)
        {
            await _notificationHub.Clients.Group($"branch:{pc.BranchId}").SendAsync("Alert", payloadObj);
        }
        else
        {
            await _notificationHub.Clients.Group("admin:all").SendAsync("Alert", payloadObj);
        }

        return new { success = true, status = "pending_operator_approval" };
    }

    public async Task<object> DeclineWalkinRequest(string pcId, string reason)
    {
        _logger.LogInformation("Walk-in request for {PcId} was declined: {Reason}", pcId, reason);

        PendingWalkinRequests.TryRemove(pcId, out _);

        await Clients.Group($"pc:{pcId}").SendAsync("WalkinRequestDeclined", new { reason = reason });

        return new { success = true };
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("PC Overlay Disconnected (ConnectionId: {ConnectionId})", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

// DTOs

public class HeartbeatPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}

public class IdlePayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("idleSince")] public string IdleSince { get; set; } = string.Empty;
}

public class ActivityPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("event")] public string Event { get; set; } = string.Empty;
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
}

public class FoodOrderPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("orderId")] public string OrderId { get; set; } = string.Empty;
    [JsonPropertyName("totalAmount")] public decimal TotalAmount { get; set; }
    [JsonPropertyName("items")] public List<FoodItemPayload> Items { get; set; } = new();
}

public class FoodItemPayload
{
    [JsonPropertyName("menuItemId")] public string MenuItemId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
}

public class ExtensionPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
}

public class CallPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
}

public class WalkinSessionPayload
{
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("customerName")] public string CustomerName { get; set; } = string.Empty;
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("packageName")] public string PackageName { get; set; } = string.Empty;
}

public class PendingWalkinData
{
    [JsonPropertyName("type")] public string Type { get; set; } = "WalkinSessionRequest";
    [JsonPropertyName("pcId")] public string PcId { get; set; } = string.Empty;
    [JsonPropertyName("branchId")] public string? BranchId { get; set; }
    [JsonPropertyName("customerName")] public string CustomerName { get; set; } = string.Empty;
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("packageName")] public string PackageName { get; set; } = string.Empty;
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
}
