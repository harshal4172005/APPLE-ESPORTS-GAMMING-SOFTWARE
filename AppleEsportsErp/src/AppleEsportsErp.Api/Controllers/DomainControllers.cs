using Microsoft.AspNetCore.Authorization;
using System;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using AppleEsportsErp.Api.Extensions;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Infrastructure.Configuration;

namespace AppleEsportsErp.Api.Controllers;













/// <summary>SOP §13: Menu / Inventory — maps from inventory.routes.js</summary>
[ApiController]
[Route("api/inventory")]
[Authorize]
[BranchIsolation]
public class InventoryController : ControllerBase
{
    private readonly AppleEsportsErp.Application.Interfaces.IUnitOfWork _unitOfWork;
    private readonly ILogger<InventoryController> _logger;
    private readonly IConfiguration _configuration;
    private readonly AppleEsportsErp.Api.Services.IRemoteBranchControl _remote;
    private readonly IAuditService _audit;

    public InventoryController(
        AppleEsportsErp.Application.Interfaces.IUnitOfWork unitOfWork,
        ILogger<InventoryController> logger,
        IConfiguration configuration,
        AppleEsportsErp.Api.Services.IRemoteBranchControl remote,
        IAuditService audit)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _configuration = configuration;
        _remote = remote;
        _audit = audit;
    }

    private Guid CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeAll = false, [FromQuery] Guid? branchId = null)
    {
        Console.WriteLine($"[DEBUG GetAll] HttpContext is null: {HttpContext == null}");
        if (HttpContext != null)
        {
            Console.WriteLine($"[DEBUG GetAll] HttpContext.Items is null: {HttpContext.Items == null}");
            Console.WriteLine($"[DEBUG GetAll] BranchId in Items: {HttpContext.Items["BranchId"]}");
        }
        var branchIdStr = HttpContext?.Items["BranchId"]?.ToString();
        var targetBranchId = branchId 
            ?? (string.IsNullOrEmpty(branchIdStr) ? (Guid?)null : Guid.Parse(branchIdStr));
            
        if (targetBranchId == null)
        {
            var firstBranch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync();
            if (firstBranch == null)
            {
                return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("No branches found in the system"));
            }
            targetBranchId = firstBranch.Id;
        }
        
        // Null unless this branch shares a food group with another - see Branch.FoodGroupId.
        var foodGroupId = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .Where(b => b.Id == targetBranchId)
            .Select(b => b.FoodGroupId)
            .FirstOrDefaultAsync();

        // Scoped to this branch alone, unless it shares a food group - then every branch
        // sharing that group is included too, so an item created or stocked from ANY of them
        // shows up here regardless of which one actually owns the row. Same reasoning as
        // BranchHeartbeatController.ConfigForBranchIfChangedAsync's catalogue push.
        var query = _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .Where(i => i.BranchId == targetBranchId
                || (foodGroupId != null && i.Branch.FoodGroupId == foodGroupId));

        if (!includeAll)
        {
            query = query.Where(i => i.Status != AppleEsportsErp.Domain.Enums.FoodAvailability.Disabled);
        }

        var items = await query
            .OrderBy(i => i.Category)
            .ThenBy(i => i.ItemName)
            .ToListAsync();
            
        var dtos = items.Select(i => new {
            i.Id,
            i.ItemName,
            i.Category,
            i.Price,
            i.CurrentStock,
            i.SoldQty,
            i.MinStockLimit,
            Status = i.Status.ToString(),
            IsLowStock = i.CurrentStock <= i.MinStockLimit,
            IsOversold = i.CurrentStock < 0,
            i.ImageUrl,
            i.CreatedAt,
            i.UpdatedAt
        });

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(dtos));
    }

    /// <summary>
    /// Adds a menu item to the catalogue. Always starts at zero stock.
    ///
    /// Stock used to be settable right here, and a typed number could be silently overwritten
    /// to zero within seconds by the branch's own echo of this same catalogue entry - proven
    /// on the live server: an item created at Head Office with stock 40 came back from the
    /// branch it was pushed to as CurrentStock 0 fourteen seconds later, because the branch had
    /// genuinely never stocked it and correctly said so. Rather than fight that forever, stock
    /// is no longer part of creating an item at all. A new item starts at zero everywhere, and
    /// an actual delivery is recorded afterward through AddStock - see its own doc comment for
    /// why that is a real, permanent fix rather than the same bug moved one field over.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "Dashboard:menu_editor")]
    public async Task<IActionResult> Create([FromBody] CreateInventoryItemDto dto)
    {
        var targetBranchId = dto.BranchId ?? Guid.Parse(HttpContext.Items["BranchId"]!.ToString()!);
        var requestedStock = 0;

        // Null unless this branch shares a food group with another - see Branch.FoodGroupId.
        var foodGroupId = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .Where(b => b.Id == targetBranchId)
            .Select(b => b.FoodGroupId)
            .FirstOrDefaultAsync();

        // Same item, typed twice under a different case, is how this branch ended up with
        // "Red bull" and "redbull" as two unrelated rows with two unrelated stock counts -
        // neither aware the other existed. Checked case-insensitively, per branch (or per food
        // group, when this one shares its menu with another) so a genuinely different,
        // unrelated branch can still stock an item under the same name.
        var duplicate = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .FirstOrDefaultAsync(i => (i.BranchId == targetBranchId
                    || (foodGroupId != null && i.Branch.FoodGroupId == foodGroupId))
                && i.ItemName.ToLower() == dto.ItemName.Trim().ToLower());
        if (duplicate is not null)
        {
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                $"\"{duplicate.ItemName}\" already exists on this menu. Edit that item instead of adding another.",
                "DUPLICATE_ITEM_NAME"));
        }

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var item = new AppleEsportsErp.Domain.Entities.InventoryItem
            {
                Id = Guid.NewGuid(),
                BranchId = targetBranchId,
                ItemName = dto.ItemName,
                Category = dto.Category,
                Price = dto.Price,
                CurrentStock = requestedStock,
                SoldQty = 0,
                MinStockLimit = dto.MinStockLimit,
                Status = dto.Status,
                ImageUrl = dto.ImageUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().AddAsync(item);

            // No "0 -> 0" log line here any more - every item starts at zero now, so there is
            // nothing to record until an actual delivery arrives via AddStock.

            _logger.LogInformation(
                "Creating menu item {ItemName} ({ItemId}): CurrentStock set to {RequestedStock} " +
                "before save.", dto.ItemName, item.Id, requestedStock);

            await _unitOfWork.CommitTransactionAsync();

            // Read back from the database rather than trusting the in-memory object - if
            // something between the assignment above and the physical write is silently
            // reverting the value, trusting `item.CurrentStock` here would hide the very bug
            // this is here to catch.
            var persisted = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
                .GetByIdAsync(item.Id);

            if (persisted is not null && persisted.CurrentStock != requestedStock)
            {
                _logger.LogError(
                    "STOCK MISMATCH on create: {ItemName} ({ItemId}) was created with " +
                    "CurrentStock requested as {Requested}, but reading it back immediately " +
                    "afterward shows {Actual}. Branch {BranchId}, requested by {UserId}.",
                    dto.ItemName, item.Id, requestedStock, persisted.CurrentStock,
                    targetBranchId, User.FindFirstValue(ClaimTypes.NameIdentifier));
            }

            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.ItemCreate,
                TargetType = "inventory_item",
                TargetId = item.Id,
                BranchId = targetBranchId,
                Details = new { itemName = item.ItemName, item.Category, item.Price },
            });

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new {
                item.Id,
                item.ItemName,
                item.Category,
                item.Price,
                CurrentStock = persisted?.CurrentStock ?? item.CurrentStock,
                item.SoldQty,
                item.MinStockLimit,
                Status = item.Status.ToString(),
                IsLowStock = item.CurrentStock <= item.MinStockLimit,
                IsOversold = item.CurrentStock < 0,
                item.ImageUrl,
                item.CreatedAt,
                item.UpdatedAt
            }));
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            _logger.LogError(ex, "Failed to create menu item {ItemName} with stock {RequestedStock}.",
                dto.ItemName, requestedStock);
            throw;
        }
    }

    /// <summary>Same live-confirmed stock-write issue as Create - see its doc comment. Same defences here.</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "Dashboard:menu_editor")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateInventoryItemDto dto)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
        var item = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .FirstOrDefaultAsync(i => i.Id == id);

        if (item == null)
        {
            await _unitOfWork.RollbackTransactionAsync();
            return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));
        }

        var now = DateTimeOffset.UtcNow;
        var isOp = User.FindFirstValue(ClaimTypes.Role) == "operator";
        var logOperatorId = isOp ? Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!) : (Guid?)null;

        // Audit changes if price/status/stock changed
        if (item.Price != dto.Price)
        {
            var log = new AppleEsportsErp.Domain.Entities.InventoryLog
            {
                Id = Guid.NewGuid(),
                InventoryId = item.Id,
                BranchId = item.BranchId,
                OperatorId = logOperatorId,
                Action = "price_change",
                OldValue = item.Price.ToString("F2"),
                NewValue = dto.Price.ToString("F2"),
                Reason = "Price updated via menu editor",
                CreatedAt = now
            };
            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(log);
        }

        if (item.Status != dto.Status)
        {
            var log = new AppleEsportsErp.Domain.Entities.InventoryLog
            {
                Id = Guid.NewGuid(),
                InventoryId = item.Id,
                BranchId = item.BranchId,
                OperatorId = logOperatorId,
                Action = "status_change",
                OldValue = item.Status.ToString(),
                NewValue = dto.Status.ToString(),
                Reason = "Status updated via menu editor",
                CreatedAt = now
            };
            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(log);
        }

        // Stock is deliberately untouched here - not read from dto.CurrentStock at all any
        // more. Editing an item's name, price, category or status must never be able to move
        // its stock as a side effect; only AddStock (a real delivery, Admin/Super Admin only)
        // ever changes this number now.
        item.ItemName = dto.ItemName;
        item.Category = dto.Category;
        item.Price = dto.Price;
        item.MinStockLimit = dto.MinStockLimit;
        item.Status = dto.Status;
        item.ImageUrl = dto.ImageUrl;
        item.UpdatedAt = now;

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(item);

        await _unitOfWork.CommitTransactionAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new {
            item.Id,
            item.ItemName,
            item.Category,
            item.Price,
            item.CurrentStock,
            item.SoldQty,
            item.MinStockLimit,
            Status = item.Status.ToString(),
            IsLowStock = item.CurrentStock <= item.MinStockLimit,
            IsOversold = item.CurrentStock < 0,
            item.ImageUrl,
            item.CreatedAt,
            item.UpdatedAt
        }));
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            _logger.LogError(ex, "Failed to update menu item {ItemId} with requested stock {RequestedStock}.",
                id, dto.CurrentStock);
            throw;
        }
    }

    /// <summary>
    /// Removes a menu item - permanently if nothing references it, deactivated otherwise.
    ///
    /// Previously deleted only Head Office's own copy. The branch's row was never told, so it
    /// sat there untouched - and the next time anything about it changed at the counter (a
    /// price edit, a delivery, even just its own next stock reconciliation), that untouched row
    /// synced straight back up and un-deleted it here. "Permanently deleted" was never true for
    /// anything requested from Head Office; this is that fix, on the same MustTravel pattern as
    /// every other write that only the branch can actually make stick.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "Dashboard:menu_editor")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var item = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .FirstOrDefaultAsync(i => i.Id == id);

        if (item == null)
            return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));

        if (_remote.MustTravel)
        {
            var receipt = await _remote.SendAsync(
                item.BranchId, AppleEsportsErp.Api.Services.BranchCommands.DeleteInventoryItem,
                new { inventoryItemId = item.Id }, CurrentUserId(), ct);

            // Head Office's own copy is removed right away rather than waiting on the branch's
            // confirmation - the branch is the one that must not resurrect it, and it cannot,
            // once it too has been told. Leaving Head Office's copy sitting here in the
            // meantime would only mean showing a "deleted" item for a few more seconds.
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Remove(item);
            await _unitOfWork.SaveChangesAsync();

            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.ItemDelete,
                TargetType = "inventory_item",
                TargetId = item.Id,
                BranchId = item.BranchId,
                Details = new { itemName = item.ItemName, routedToBranch = true, receipt.BranchIsReporting },
            });

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = receipt.Message }));
        }

        try
        {
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Remove(item);
            await _unitOfWork.SaveChangesAsync();

            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.ItemDelete,
                TargetType = "inventory_item",
                TargetId = item.Id,
                BranchId = item.BranchId,
                Details = new { itemName = item.ItemName, permanent = true },
            });

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Item deleted successfully" }));
        }
        catch (Exception)
        {
            // If deleting throws due to foreign keys, soft-delete by setting status to Disabled
            item.Status = AppleEsportsErp.Domain.Enums.FoodAvailability.Disabled;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(item);
            await _unitOfWork.SaveChangesAsync();

            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.ItemDelete,
                TargetType = "inventory_item",
                TargetId = item.Id,
                BranchId = item.BranchId,
                Details = new { itemName = item.ItemName, permanent = false, reason = "existing orders reference it" },
            });

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Item cannot be permanently deleted due to existing orders. It has been deactivated instead." }));
        }
    }

    /// <summary>
    /// Corrects stock to what was actually counted at the shelf. Admin/Super Admin only, and
    /// branch-only - a physical count is, by definition, something done standing in front of
    /// the shelf, which Head Office is never doing.
    /// </summary>
    [HttpPost("{id}/reconcile")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    public async Task<IActionResult> Reconcile(Guid id, [FromBody] ReconcileStockDto dto)
    {
        if (_configuration.IsHeadOffice())
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                "A physical count can only be done at the branch that actually holds the shelf.",
                "BRANCH_ONLY_OPERATION"));

        var item = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .FirstOrDefaultAsync(i => i.Id == id);

        if (item == null)
            return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));

        var oldStock = item.CurrentStock;
        var physicalCount = dto.PhysicalCount;
        var now = DateTimeOffset.UtcNow;
        var logOperatorId = CurrentUserId() is { } uid && uid != Guid.Empty ? uid : (Guid?)null;

        item.CurrentStock = physicalCount;
        item.UpdatedAt = now;

        if (physicalCount == 0)
        {
            item.Status = AppleEsportsErp.Domain.Enums.FoodAvailability.OutOfStock;
        }
        else if (item.Status == AppleEsportsErp.Domain.Enums.FoodAvailability.OutOfStock && physicalCount > 0)
        {
            item.Status = AppleEsportsErp.Domain.Enums.FoodAvailability.Available;
        }

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(item);

        var log = new AppleEsportsErp.Domain.Entities.InventoryLog
        {
            Id = Guid.NewGuid(),
            InventoryId = item.Id,
            BranchId = item.BranchId,
            OperatorId = logOperatorId,
            Action = "discrepancy",
            Quantity = physicalCount - oldStock,
            OldValue = oldStock.ToString(),
            NewValue = physicalCount.ToString(),
            Reason = dto.Reason ?? "Physical inventory reconciliation count mismatch",
            CreatedAt = now
        };
        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(log);

        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { item.Id, item.CurrentStock, Status = item.Status.ToString() }));
    }

    /// <summary>
    /// Records a stock delivery - Admin or Super Admin only, and the only way stock can be
    /// changed at all now. Adds to whatever the branch already honestly has rather than
    /// setting an absolute number, on purpose: an admin at Head Office cannot see the shelf and
    /// has no business declaring what its total should now read, only how many more units just
    /// arrived. The branch adds that to its own real count, whatever that happens to be by the
    /// time the instruction reaches it.
    ///
    /// From a real branch this applies immediately, the same as any other counter action. From
    /// Head Office it travels as an instruction and is carried out by the branch that actually
    /// holds the item - never written here directly, which is the entire fix: Head Office
    /// asking is safe, Head Office writing its own guess of the branch's stock is what corrupted
    /// it the first time.
    /// </summary>
    [HttpPost("{id}/stock/add")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    public async Task<IActionResult> AddStock(Guid id, [FromBody] AddStockDto dto, CancellationToken ct)
    {
        var item = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (item == null)
            return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));

        if (_remote.MustTravel)
        {
            var receipt = await _remote.SendAsync(item.BranchId, AppleEsportsErp.Api.Services.BranchCommands.AdjustStock, new
            {
                inventoryId = id,
                quantity = dto.Quantity,
                reason = dto.Reason,
            }, CurrentUserId(), ct);

            return Accepted(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
            {
                queued = true,
                commandId = receipt.CommandId,
                branchIsReporting = receipt.BranchIsReporting,
                message = receipt.Message,
            }));
        }

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var tracked = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
                .GetByIdAsync(id, ct);

            if (tracked is null)
            {
                await _unitOfWork.RollbackTransactionAsync();
                return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));
            }

            var oldStock = tracked.CurrentStock;
            var now = DateTimeOffset.UtcNow;

            tracked.CurrentStock += dto.Quantity;
            tracked.UpdatedAt = now;

            if (tracked.Status == AppleEsportsErp.Domain.Enums.FoodAvailability.OutOfStock && tracked.CurrentStock > 0)
                tracked.Status = AppleEsportsErp.Domain.Enums.FoodAvailability.Available;

            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(tracked);

            var log = new AppleEsportsErp.Domain.Entities.InventoryLog
            {
                Id = Guid.NewGuid(),
                InventoryId = tracked.Id,
                BranchId = tracked.BranchId,
                OperatorId = CurrentUserId() is { } uid && uid != Guid.Empty ? uid : (Guid?)null,
                Action = "refill",
                Quantity = dto.Quantity,
                OldValue = oldStock.ToString(),
                NewValue = tracked.CurrentStock.ToString(),
                Reason = dto.Reason ?? "Stock delivery",
                CreatedAt = now,
            };
            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(log);

            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.StockAdd,
                TargetType = "inventory_item",
                TargetId = tracked.Id,
                BranchId = tracked.BranchId,
                Details = new { itemName = tracked.ItemName, quantity = dto.Quantity, oldStock, newStock = tracked.CurrentStock, dto.Reason },
            });

            await _unitOfWork.CommitTransactionAsync();

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
            {
                tracked.Id,
                tracked.ItemName,
                tracked.CurrentStock,
                Status = tracked.Status.ToString(),
            }));
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            await _audit.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
            {
                OperatorId = CurrentUserId(),
                UserRole = User.FindFirstValue(ClaimTypes.Role) ?? Roles.Admin,
                Action = AuditActions.StockAdd,
                TargetType = "inventory_item",
                TargetId = id,
                Success = false,
                BranchId = item.BranchId,
                Details = new { itemName = item.ItemName, quantity = dto.Quantity, error = ex.GetBaseException().Message },
            });
            throw;
        }
    }

    [HttpGet("discrepancies")]
    [Authorize(Policy = $"Dashboard:{Dashboards.Reports}")]
    public async Task<IActionResult> GetDiscrepancies([FromQuery] Guid? branchId = null)
    {
        Console.WriteLine($"[DEBUG GetDiscrepancies] HttpContext is null: {HttpContext == null}");
        if (HttpContext != null)
        {
            Console.WriteLine($"[DEBUG GetDiscrepancies] HttpContext.Items is null: {HttpContext.Items == null}");
            Console.WriteLine($"[DEBUG GetDiscrepancies] BranchId in Items: {HttpContext.Items["BranchId"]}");
        }
        var branchIdStr = HttpContext?.Items["BranchId"]?.ToString();
        var targetBranchId = branchId 
            ?? (string.IsNullOrEmpty(branchIdStr) ? (Guid?)null : Guid.Parse(branchIdStr));
            
        if (targetBranchId == null)
        {
            var firstBranch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync();
            if (firstBranch == null)
            {
                return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("No branches found in the system"));
            }
            targetBranchId = firstBranch.Id;
        }

        var logs = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>()
            .Query()
            .Include(l => l.InventoryItem)
            .Where(l => l.BranchId == targetBranchId && l.Action == "discrepancy")
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        var dtos = logs.Select(l => new {
            l.Id,
            l.InventoryId,
            ItemName = l.InventoryItem?.ItemName ?? "Unknown",
            l.OperatorId,
            l.Action,
            l.Quantity,
            l.OldValue,
            l.NewValue,
            l.Reason,
            l.CreatedAt
        });

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(dtos));
    }

    /// <summary>
    /// A physical stock count, set to an absolute figure by somebody standing in front of the
    /// shelf. This is the shift-start / shift-end count (ShiftStartModal, ShiftEndModal,
    /// CashDeskPage), which is why it takes an absolute number rather than a delta the way
    /// AddStock does - the person has just counted what is actually there.
    ///
    /// Two things were wrong with it.
    ///
    /// It carried no authorization at all beyond the class-level [Authorize], unlike every
    /// sibling endpoint here which requires Dashboard:menu_editor. Members authenticate against
    /// this API too, so any logged-in customer could set any item's stock to any number. Now
    /// restricted to staff. Deliberately NOT narrowed to Admin/Super Admin: the operator doing
    /// the count at shift start is the correct person to do this, and locking them out would
    /// break the count itself.
    ///
    /// And at Head Office it wrote a local row that can never travel - the branch's echo
    /// excludes CurrentStock (SyncInboxController's inventory_item.changed), so the figure
    /// would sit here looking authoritative and wrong forever. Head Office cannot count a shelf
    /// in Surat; it records a delivery through AddStock instead.
    /// </summary>
    [HttpPatch("{id}/stock")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    public async Task<IActionResult> UpdateStock(Guid id, [FromBody] UpdateStockRequest request)
    {
        if (_configuration.IsHeadOffice())
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                "A stock count has to be done at the branch, by whoever is looking at the shelf. " +
                "Head Office cannot see it, and a number set here never reaches the shop. " +
                "To record a delivery, use Add Stock instead.",
                "BRANCH_ONLY_OPERATION"));

        var item = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>()
            .Query()
            .FirstOrDefaultAsync(i => i.Id == id);

        if (item == null)
            return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Inventory item not found"));

        // Logged as a count ("discrepancy") - which is also what tells a shared fridge's gate
        // (SharedStockGate) to SET this number at Head Office rather than add the difference.
        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(new AppleEsportsErp.Domain.Entities.InventoryLog
        {
            Id = Guid.NewGuid(),
            InventoryId = item.Id,
            BranchId = item.BranchId,
            OperatorId = CurrentUserId() is { } uid && uid != Guid.Empty ? uid : (Guid?)null,
            Action = "discrepancy",
            Quantity = request.CurrentStock - item.CurrentStock,
            OldValue = item.CurrentStock.ToString(),
            NewValue = request.CurrentStock.ToString(),
            Reason = "Stock count",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        item.CurrentStock = request.CurrentStock;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(item);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { item.Id, item.CurrentStock }));
    }

    public record UpdateStockRequest(int CurrentStock);

    /// <summary>
    /// What the shift-end screen needs before it lets the register be locked: whether this
    /// branch charges for missing stock at all, whether this register's stock has been counted
    /// yet, and every missing-stock bill still unpaid by the operator asking.
    /// </summary>
    [HttpGet("missing-stock")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    public async Task<IActionResult> MissingStockStatus([FromQuery] Guid? cashRegisterId)
    {
        var userId = CurrentUserId();
        var registerQuery = _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.CashRegister>().Query();
        var register = cashRegisterId is { } rid
            ? await registerQuery.Where(r => r.Id == rid).Select(r => new { r.Id, r.BranchId, r.OperatorId }).FirstOrDefaultAsync()
            : null;
        var branchId = register?.BranchId
            ?? (Guid.TryParse(HttpContext.Items["BranchId"]?.ToString(), out var b) ? b : Guid.Empty);
        var operatorId = register?.OperatorId ?? userId;

        var enabled = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .Where(x => x.Id == branchId).Select(x => x.ChargeMissingStock).FirstOrDefaultAsync();

        var counted = register is not null && await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.StockCount>().Query()
            .AnyAsync(c => c.CashRegisterId == register.Id && c.Kind == "closing");

        var pending = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Bill>().Query()
            .Include(x => x.Items)
            .Where(x => x.BranchId == branchId && x.ShortageOperatorId == operatorId
                && x.Status == AppleEsportsErp.Domain.Enums.BillStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
        {
            enabled,
            closingCountDone = counted,
            pending = pending.Select(x => new
            {
                x.Id,
                x.BillNumber,
                x.BranchId,
                x.CustomerName,
                x.TotalAmount,
                x.FoodAmount,
                x.GamingAmount,
                x.DiscountAmount,
                x.CreatedAt,
                items = x.Items.Select(i => new { i.ItemName, i.Quantity, i.UnitPrice, i.TotalPrice }),
            }),
        }));
    }

    /// <summary>
    /// <paramref name="SystemWas"/> is what this screen showed when the operator opened it. Without
    /// it a sale made while they were counting looked like the operator miscounting ("I counted 28
    /// water, it showed 27"). Null from an older screen - then it is simply not reported.
    /// </summary>
    public record StockCountLineRequest(Guid InventoryId, int Counted, int? SystemWas = null);
    public record StockCountRequest(string Kind, Guid? CashRegisterId, List<StockCountLineRequest> Counts, DateTimeOffset? OpenedAt = null);

    /// <summary>
    /// Menu Editor's stock answer for a date range, per item and in total: how much the admin
    /// refilled, how much sold, what the counts corrected, and what is unaccounted for.
    ///
    /// The owner's question in his own words: "900 sold this month and admin put 950 in stock,
    /// where did 50 go?". Read from inventory_logs, which is every movement of every item, so
    /// the four numbers always add up to the stock on the shelf:
    ///   opening + refilled - sold + corrections = closing.
    /// "Missing" is what the shift counts had to take off (a negative correction) - stock that
    /// left without being sold.
    /// </summary>
    [HttpGet("stock-summary")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    public async Task<IActionResult> StockSummary(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? branchId = null)
    {
        var targetBranchId = branchId
            ?? (Guid.TryParse(HttpContext.Items["BranchId"]?.ToString(), out var b) ? b : Guid.Empty);
        if (targetBranchId == Guid.Empty)
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Pick a branch first."));

        var now = DateTimeOffset.UtcNow;
        var fromUtc = (from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)).ToUniversalTime();
        var toUtc = (to ?? now).ToUniversalTime();

        var items = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Query().AsNoTracking()
            .Where(i => i.BranchId == targetBranchId)
            .Select(i => new { i.Id, i.ItemName, i.Category, i.Price, i.CurrentStock, i.Status })
            .ToListAsync();

        var logs = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().Query().AsNoTracking()
            .Where(l => l.BranchId == targetBranchId && l.CreatedAt >= fromUtc && l.CreatedAt <= toUtc)
            .Select(l => new { l.InventoryId, l.Action, l.Quantity, l.OldValue, l.NewValue, l.CreatedAt, l.OperatorId, l.Reason })
            .ToListAsync();

        // Owner's rule (4 Nov 2026): if the missing stock can be traced, trace it - this is when
        // it went, and whose shift it went on. Every count that found less than the shelf should
        // have held, newest first, so "where did 50 go" has an answer and not just a number.
        var whoById = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().AsNoTracking()
            .Where(o => o.BranchId == targetBranchId)
            .ToDictionaryAsync(o => o.Id, o => o.FullName);

        // Where the shelf stood when the range began: today's number, with every movement inside
        // the range taken back off. Nothing to guess and nothing to store.
        static int Delta(string action, int? qty, string? oldValue, string? newValue)
        {
            if (action == "refill") return qty ?? 0;
            if (action == "sale") return -Math.Abs(qty ?? 0);
            if (action == "discrepancy")
                return qty ?? (int.TryParse(newValue, out var n) && int.TryParse(oldValue, out var o) ? n - o : 0);
            return 0;
        }

        var rows = items.Select(i =>
        {
            var mine = logs.Where(l => l.InventoryId == i.Id).ToList();
            var refilled = mine.Where(l => l.Action == "refill").Sum(l => l.Quantity ?? 0);
            var sold = mine.Where(l => l.Action == "sale").Sum(l => Math.Abs(l.Quantity ?? 0));
            var corrections = mine.Where(l => l.Action == "discrepancy")
                .Sum(l => Delta(l.Action, l.Quantity, l.OldValue, l.NewValue));
            var movement = mine.Sum(l => Delta(l.Action, l.Quantity, l.OldValue, l.NewValue));
            var opening = i.CurrentStock - movement;

            return new
            {
                inventoryId = i.Id,
                itemName = i.ItemName,
                i.Category,
                i.Price,
                opening,
                refilled,
                sold,
                corrections,                                  // negative = the count found less
                shouldBe = opening + refilled - sold,
                actual = i.CurrentStock,
                missing = Math.Max(0, -corrections),          // stock that left without being sold
                extra = Math.Max(0, corrections),
                missingValue = Math.Max(0, -corrections) * i.Price,
                status = i.Status.ToString(),
            };
        }).OrderBy(r => r.Category).ThenBy(r => r.itemName).ToList();

        var byId = items.ToDictionary(i => i.Id);
        var wentMissing = logs
            .Where(l => l.Action == "discrepancy" && Delta(l.Action, l.Quantity, l.OldValue, l.NewValue) < 0)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l =>
            {
                var short_ = -Delta(l.Action, l.Quantity, l.OldValue, l.NewValue);
                byId.TryGetValue(l.InventoryId, out var it);
                return new
                {
                    at = l.CreatedAt,
                    itemName = it?.ItemName ?? "Unknown item",
                    quantity = short_,
                    value = short_ * (it?.Price ?? 0m),
                    countedBy = l.OperatorId is { } op && whoById.TryGetValue(op, out var who) ? who : null,
                    was = l.OldValue,
                    found = l.NewValue,
                    l.Reason,
                };
            })
            .ToList();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
        {
            from = fromUtc,
            to = toUtc,
            hasHistory = logs.Count > 0,
            wentMissing,
            totals = new
            {
                // What was already on the shelf when the range started, kept apart from what the
                // admin put in, so "you added 10, 5 were already there, 15 were available" reads
                // off the page without arithmetic (owner, 4 Nov 2026).
                opening = rows.Sum(r => r.opening),
                available = rows.Sum(r => r.opening + r.refilled),
                refilled = rows.Sum(r => r.refilled),
                sold = rows.Sum(r => r.sold),
                missing = rows.Sum(r => r.missing),
                extra = rows.Sum(r => r.extra),
                missingValue = rows.Sum(r => r.missingValue),
            },
            items = rows,
        }));
    }

    /// <summary>
    /// What to put in front of the operator at a shift count: every item, what the system holds
    /// right now, and - for a shared fridge - whether another counter has already counted it.
    /// One physical fridge is counted once (SharedStockController.OneCountStandsFor).
    /// </summary>
    [HttpGet("stock-count/sheet")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    public async Task<IActionResult> StockCountSheet()
    {
        var branchId = Guid.TryParse(HttpContext.Items["BranchId"]?.ToString(), out var b) ? b : Guid.Empty;
        var items = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Query().AsNoTracking()
            .Where(i => i.BranchId == branchId && i.Status != AppleEsportsErp.Domain.Enums.FoodAvailability.Disabled)
            .OrderBy(i => i.Category).ThenBy(i => i.ItemName)
            .Select(i => new { i.Id, i.ItemName, i.Category, i.CurrentStock })
            .ToListAsync();

        var shared = AppleEsportsErp.Infrastructure.Data.SharedStockGate.Membership != null;
        var stands = AppleEsportsErp.Api.Controllers.SharedStockController.OneCountStandsFor;
        var now = DateTimeOffset.UtcNow;

        // Every branch of a shared fridge counts it (owner, 4 Nov 2026). What each one must NOT
        // do is count against its own stale copy - that is what turned a correct count of 14 into
        // "system 0, 14 extra" at Citylight. So "system says" comes from Head Office, live.
        var live = shared
            ? await AppleEsportsErp.Infrastructure.Data.SharedStockGate.LiveCountsAsync(HttpContext.RequestAborted)
            : new Dictionary<Guid, int>();
        var fridgeLive = !shared || AppleEsportsErp.Infrastructure.Data.SharedStockGate.Online;

        // Written into this branch's own row, not just shown. What the operator is charged for is
        // worked out at save time from CurrentStock, so if the screen said 14 (Head Office) while
        // the row still said 0, a correct count of 14 would raise a bill for stock that is on the
        // shelf. Straight to the table, like SharedStockWatcher: this is Head Office's number
        // arriving, not a change this branch made, so it must not be sent back.
        foreach (var i in items)
        {
            if (!live.TryGetValue(i.Id, out var real) || real == i.CurrentStock) continue;
            if (AppleEsportsErp.Infrastructure.Data.SharedStockGate.IsInFlight(i.Id)) continue;
            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Query()
                .Where(x => x.Id == i.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CurrentStock, real)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));
        }

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
        {
            openedAt = now,
            sharedFridge = shared,
            fridgeLive,
            items = items.Select(i =>
            {
                var other = shared
                    && AppleEsportsErp.Infrastructure.Data.SharedStockGate.LastCount.TryGetValue(i.Id, out var last)
                    && now - last.At < stands
                    && !string.Equals(last.By, AppleEsportsErp.Infrastructure.Data.SharedStockGate.BranchName, StringComparison.Ordinal)
                    ? last : ((DateTimeOffset At, string? By)?)null;
                return new
                {
                    inventoryId = i.Id,
                    itemName = i.ItemName,
                    i.Category,
                    system = live.TryGetValue(i.Id, out var s) ? s : i.CurrentStock,
                    shared = shared && live.ContainsKey(i.Id),
                    // Information, not a block: this counter still counts the shelf themselves.
                    lastCountedBy = other?.By,
                    lastCountedAt = other?.At,
                };
            }),
        }));
    }

    /// <summary>
    /// A shift's whole stock count in one go - the shift-start and shift-end counts. Every item
    /// is recorded with what the system said and what was counted (a StockCount, shown under
    /// the note breakdown in the Cash Register history and synced to Head Office), and any item
    /// that differed is set to the counted number, logged as a count the same as UpdateStock.
    /// </summary>
    [HttpPost("stock-count")]
    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    public async Task<IActionResult> RecordStockCount([FromBody] StockCountRequest request)
    {
        if (_configuration.IsHeadOffice())
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                "A stock count has to be done at the branch, by whoever is looking at the shelf.",
                "BRANCH_ONLY_OPERATION"));

        var userId = CurrentUserId();
        var now = DateTimeOffset.UtcNow;

        var ids = request.Counts.Select(c => c.InventoryId).ToList();
        var items = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Query()
            .Where(i => ids.Contains(i.Id)).ToListAsync();

        // An Admin's request carries no branch unless the screen sent one; at a branch install
        // the items being counted say which branch this is.
        var branchId = Guid.TryParse(HttpContext.Items["BranchId"]?.ToString(), out var b) ? b
            : items.FirstOrDefault()?.BranchId ?? Guid.Empty;

        var name = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query()
                .Where(o => o.Id == userId).Select(o => o.FullName).FirstOrDefaultAsync()
            ?? await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.User>().Query()
                .Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync()
            ?? "Unknown";
        var shiftId = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Shift>().Query()
            .Where(s => s.BranchId == branchId && s.OperatorId == userId && s.Status == AppleEsportsErp.Domain.Enums.ShiftStatus.Active)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync();
        var registerId = request.CashRegisterId ?? await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.CashRegister>().Query()
            .Where(r => r.BranchId == branchId && r.Status != AppleEsportsErp.Domain.Enums.CashRegisterStatus.Closed)
            .OrderByDescending(r => r.OpenedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();

        // Anything the shared fridge refuses (already counted by another counter, or no line to
        // Head Office) is collected here by SharedStockGate during the save and reported back.
        AppleEsportsErp.Infrastructure.Data.SharedStockGate.CountNotes.Value = new List<string>();

        var lines = new List<object>();
        var missing = new List<(AppleEsportsErp.Domain.Entities.InventoryItem Item, int Missing)>();
        foreach (var count in request.Counts)
        {
            var item = items.FirstOrDefault(i => i.Id == count.InventoryId);
            if (item is null) continue;
            // Sold (or delivered) between the screen opening and Confirm. Reported so the operator
            // is never told they miscounted when the shelf genuinely moved under them.
            var movedWhileCounting = count.SystemWas is { } was ? item.CurrentStock - was : 0;
            lines.Add(new
            {
                inventoryId = item.Id,
                itemName = item.ItemName,
                system = item.CurrentStock,
                systemWas = count.SystemWas,
                movedWhileCounting,
                counted = count.Counted,
            });
            if (count.Counted < item.CurrentStock) missing.Add((item, item.CurrentStock - count.Counted));
            if (count.Counted == item.CurrentStock) continue;

            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().AddAsync(new AppleEsportsErp.Domain.Entities.InventoryLog
            {
                Id = Guid.NewGuid(),
                InventoryId = item.Id,
                BranchId = item.BranchId,
                OperatorId = userId == Guid.Empty ? null : userId,
                Action = "discrepancy",
                Quantity = count.Counted - item.CurrentStock,
                OldValue = item.CurrentStock.ToString(),
                NewValue = count.Counted.ToString(),
                Reason = request.Kind == "opening" ? "Shift start stock count" : "Shift end stock count",
                CreatedAt = now,
            });
            item.CurrentStock = count.Counted;
            item.UpdatedAt = now;
            AppleEsportsErp.Infrastructure.Data.SharedStockGate.SetAvailability(item, count.Counted);
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryItem>().Update(item);
        }

        // Anything missing is billed to the operator responsible, automatically - see
        // StockShortageBilling. Shift end: whoever's register this is. Shift start: whoever
        // closed the register before, since it went missing in their custody.
        var opening = request.Kind == "opening";
        var registers = _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.CashRegister>().Query();
        var responsible = opening
            ? await registers.Where(r => r.BranchId == branchId && r.ClosedAt != null && r.Id != registerId)
                .OrderByDescending(r => r.ClosedAt).Select(r => new { r.OperatorId, ShiftId = (Guid?)r.ShiftId }).FirstOrDefaultAsync()
            : await registers.Where(r => r.Id == registerId)
                .Select(r => new { r.OperatorId, ShiftId = (Guid?)r.ShiftId }).FirstOrDefaultAsync()
              ?? new { OperatorId = userId, ShiftId = shiftId };
        var responsibleName = responsible is null ? null
            : await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query()
                .Where(o => o.Id == responsible.OperatorId).Select(o => o.FullName).FirstOrDefaultAsync();

        // Only where Super Admin has switched it on for this branch (Branch.ChargeMissingStock).
        var chargeMissing = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .Where(b => b.Id == branchId).Select(b => b.ChargeMissingStock).FirstOrDefaultAsync();

        var shortageBill = !chargeMissing || responsibleName is null ? null
            : AppleEsportsErp.Infrastructure.Services.StockShortageBilling.Build(
                branchId, responsible!.OperatorId, responsibleName, responsible.ShiftId, missing, now);
        if (shortageBill is not null)
            await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Bill>().AddAsync(shortageBill);

        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.StockCount>().AddAsync(new AppleEsportsErp.Domain.Entities.StockCount
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            CashRegisterId = registerId,
            ShiftId = shiftId,
            OperatorId = userId == Guid.Empty ? null : userId,
            OperatorName = name,
            Kind = opening ? "opening" : "closing",
            CountedAt = now,
            OpenedAt = request.OpenedAt,
            ShortageBillId = shortageBill?.Id,
            Lines = System.Text.Json.JsonSerializer.Serialize(lines),
        });
        await _unitOfWork.SaveChangesAsync();

        var sharedNotes = AppleEsportsErp.Infrastructure.Data.SharedStockGate.CountNotes.Value ?? new List<string>();
        AppleEsportsErp.Infrastructure.Data.SharedStockGate.CountNotes.Value = null;

        // Owner's rule (4 Nov 2026): when a count comes up short, find where it went first - and
        // only charge for what cannot be explained. Every movement of a short item since the last
        // count is listed, so a sale at 4:52 PM is visible instead of the operator simply paying.
        // Every item whose shelf does not match what the operator was shown - whether that ended
        // in a bill or was explained by a sale. "Where did it go" has to be answerable either way.
        var shortIds = missing.Select(m => m.Item.Id)
            .Concat(request.Counts.Where(c => c.SystemWas is { } w && c.Counted != w).Select(c => c.InventoryId))
            .Distinct().ToList();
        var since = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.StockCount>().Query().AsNoTracking()
            .Where(c => c.BranchId == branchId && c.CountedAt < now)
            .OrderByDescending(c => c.CountedAt).Select(c => (DateTimeOffset?)c.CountedAt).FirstOrDefaultAsync()
            ?? now.AddHours(-24);
        var trail = shortIds.Count == 0 ? new List<object>()
            : (await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.InventoryLog>().Query().AsNoTracking()
                // Not "discrepancy": that is this count's own correction, not where the stock went.
                .Where(l => shortIds.Contains(l.InventoryId) && l.Action != "discrepancy"
                            && l.CreatedAt >= since && l.CreatedAt <= now)
                .OrderBy(l => l.CreatedAt)
                .Select(l => new { l.InventoryId, l.Action, l.Quantity, l.CreatedAt, l.Reason })
                .ToListAsync())
              .Select(l => (object)new
              {
                  inventoryId = l.InventoryId,
                  itemName = items.FirstOrDefault(i => i.Id == l.InventoryId)?.ItemName,
                  what = l.Action == "sale" ? "sold" : l.Action == "refill" ? "added" : l.Action,
                  quantity = Math.Abs(l.Quantity ?? 0),
                  at = l.CreatedAt,
                  l.Reason,
              }).ToList();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new
        {
            lines,
            sharedNotes,
            // Where the short stock went, if anywhere: shown before the operator pays.
            trail,
            trailSince = since,
            shortage = shortageBill is null ? null : new
            {
                shortageBill.BillNumber,
                amount = shortageBill.TotalAmount,
                chargedTo = responsibleName,
                items = shortageBill.Items.Select(i => new { i.ItemName, i.Quantity, i.TotalPrice }),
            },
        }));
    }
}

public class CreateInventoryItemDto
{
    [Required]
    public string ItemName { get; set; } = null!;
    public string? Category { get; set; }
    [Required]
    [Range(0, 1000000)]
    public decimal Price { get; set; }
    [Required]
    [Range(0, 100000)]
    public int CurrentStock { get; set; }
    [Required]
    [Range(0, 100000)]
    public int MinStockLimit { get; set; } = 5;
    [Required]
    public AppleEsportsErp.Domain.Enums.FoodAvailability Status { get; set; } = AppleEsportsErp.Domain.Enums.FoodAvailability.Available;
    public string? ImageUrl { get; set; }
    public Guid? BranchId { get; set; }
}

public class UpdateInventoryItemDto
{
    [Required]
    public string ItemName { get; set; } = null!;
    public string? Category { get; set; }
    [Required]
    [Range(0, 1000000)]
    public decimal Price { get; set; }
    [Required]
    [Range(0, 100000)]
    public int CurrentStock { get; set; }
    [Required]
    [Range(0, 100000)]
    public int MinStockLimit { get; set; } = 5;
    [Required]
    public AppleEsportsErp.Domain.Enums.FoodAvailability Status { get; set; }
    public string? ImageUrl { get; set; }
}

public class ReconcileStockDto
{
    [Required]
    public int PhysicalCount { get; set; }
    public string? Reason { get; set; }
}

public class AddStockDto
{
    [Required]
    [Range(1, 1000000)]
    public int Quantity { get; set; }
    public string? Reason { get; set; }
}






/// <summary>SOP §16: Branches — maps from branches.routes.js (Super Admin only)</summary>
[ApiController]
[Route("api/branches")]
[Authorize(Policy = "Dashboard:settings")]
public class BranchesController : ControllerBase
{
    private readonly AppleEsportsErp.Application.Interfaces.IUnitOfWork _unitOfWork;

    public BranchesController(AppleEsportsErp.Application.Interfaces.IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var branches = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>()
            .Query()
            .Include(b => b.FoodGroup)
            .OrderBy(b => b.Name)
            .ToListAsync();

        var dtos = branches.Select(b => new AppleEsportsErp.Application.DTOs.Settings.BranchDto
        {
            Id = b.Id,
            Name = b.Name,
            Address = b.Address,
            OpeningTime = b.OpeningTime.ToString("HH:mm"),
            ClosingTime = b.ClosingTime.ToString("HH:mm"),
            Status = b.Status.ToString(),
            CreatedAt = b.CreatedAt,
            ConfiguredReservationDurations = b.ConfiguredReservationDurations,
            DefaultOpeningFloat = b.DefaultOpeningFloat,
            FoodGroupId = b.FoodGroupId,
            FoodGroupName = b.FoodGroup?.Name,
            ChargeMissingStock = b.ChargeMissingStock,
        });

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(dtos));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppleEsportsErp.Application.DTOs.Settings.CreateBranchDto dto)
    {
        var branch = new AppleEsportsErp.Domain.Entities.Branch
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Address = dto.Address,
            OpeningTime = TimeOnly.Parse(dto.OpeningTime),
            ClosingTime = TimeOnly.Parse(dto.ClosingTime),
            Status = AppleEsportsErp.Domain.Enums.BranchStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ConfiguredReservationDurations = dto.ConfiguredReservationDurations,
            DefaultOpeningFloat = dto.DefaultOpeningFloat ?? 1000m,
            FoodGroupId = dto.FoodGroupId
        };

        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().AddAsync(branch);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { branch.Id, branch.Name }));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] AppleEsportsErp.Application.DTOs.Settings.UpdateBranchDto dto)
    {
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync(b => b.Id == id);
        if (branch == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Branch not found"));

        branch.Name = dto.Name;
        branch.Address = dto.Address;
        branch.OpeningTime = TimeOnly.Parse(dto.OpeningTime);
        branch.ClosingTime = TimeOnly.Parse(dto.ClosingTime);
        branch.ConfiguredReservationDurations = dto.ConfiguredReservationDurations;
        branch.DefaultOpeningFloat = dto.DefaultOpeningFloat ?? branch.DefaultOpeningFloat;
        branch.FoodGroupId = dto.FoodGroupId;
        branch.UpdatedAt = DateTimeOffset.UtcNow;

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Update(branch);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { branch.Id, branch.Name }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync(b => b.Id == id);
        if (branch == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Branch not found"));

        // Soft delete: toggle to Inactive
        branch.Status = AppleEsportsErp.Domain.Enums.BranchStatus.Inactive;
        branch.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Update(branch);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Branch deactivated successfully" }));
    }

    public record ChargeMissingStockRequest(bool Enabled);

    /// <summary>
    /// Super Admin's per-branch switch for charging operators for missing stock (see
    /// Branch.ChargeMissingStock). Saved here and, from Head Office, sent to the branch as a
    /// queued command - the branch's own copy is the one its screens and counts actually use.
    /// </summary>
    [HttpPut("{id}/charge-missing-stock")]
    [Authorize(Roles = AppleEsportsErp.Application.Constants.Roles.SuperAdmin)]
    public async Task<IActionResult> SetChargeMissingStock(Guid id, [FromBody] ChargeMissingStockRequest request,
        [FromServices] AppleEsportsErp.Api.Services.IRemoteBranchControl remote, CancellationToken ct)
    {
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (branch == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Branch not found"));

        branch.ChargeMissingStock = request.Enabled;
        branch.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);

        var message = request.Enabled ? "Missing stock will be charged to operators at this branch." : "Missing stock charging is off at this branch.";
        if (remote.MustTravel)
        {
            var actor = Guid.TryParse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier), out var uid) ? uid : Guid.Empty;
            var receipt = await remote.SendAsync(id, AppleEsportsErp.Api.Services.BranchCommands.SetChargeMissingStock,
                new { enabled = request.Enabled }, actor, ct);
            message = receipt.Message;
        }
        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message, enabled = request.Enabled }));
    }

    [HttpPost("{id}/activate")]
    public async Task<IActionResult> Activate(Guid id)
    {
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync(b => b.Id == id);
        if (branch == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Branch not found"));

        branch.Status = AppleEsportsErp.Domain.Enums.BranchStatus.Active;
        branch.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Update(branch);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Branch activated successfully" }));
    }

    [HttpDelete("{id}/permanent")]
    public async Task<IActionResult> DeletePermanent(Guid id)
    {
        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query().FirstOrDefaultAsync(b => b.Id == id);
        if (branch == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Branch not found"));

        // Block deletion if the branch has any financial/session history — irreversible data loss
        var hasBills = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Bill>().Query().AnyAsync(b => b.BranchId == id);
        var hasSessions = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Session>().Query().AnyAsync(s => s.BranchId == id);
        var hasShifts = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Shift>().Query().AnyAsync(s => s.BranchId == id);

        if (hasBills || hasSessions || hasShifts)
        {
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                "Cannot permanently delete this branch because it has transaction history (bills, sessions, or shifts). Deactivate the branch instead to preserve audit trails."));
        }

        // No financial history — safe to cascade delete in dependency order
        var pcs = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Pc>().Query().Where(p => p.BranchId == id).ToListAsync();
        foreach (var pc in pcs)
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Pc>().Remove(pc);

        var operators = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().Where(o => o.BranchId == id).ToListAsync();
        foreach (var op in operators)
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Remove(op);

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Remove(branch);

        try
        {
            await _unitOfWork.SaveChangesAsync();
            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Branch and its operators/rigs deleted permanently." }));
        }
        catch (DbUpdateException ex)
        {
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail(
                $"Deletion blocked by remaining linked records: {ex.InnerException?.Message ?? ex.Message}"));
        }
    }
}

/// <summary>
/// Links branches that share one food/snacks menu and stock count — see FoodGroup.cs.
/// Gated the same as branch management itself, since deciding who shares a pantry with
/// whom is a Head Office/Super Admin decision, not a counter-level one.
/// </summary>
[ApiController]
[Route("api/food-groups")]
[Authorize(Policy = "Dashboard:settings")]
public class FoodGroupsController : ControllerBase
{
    private readonly AppleEsportsErp.Application.Interfaces.IUnitOfWork _unitOfWork;

    public FoodGroupsController(AppleEsportsErp.Application.Interfaces.IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var groups = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>()
            .Query()
            .Include(g => g.Branches)
            .OrderBy(g => g.Name)
            .ToListAsync();

        var dtos = groups.Select(g => new AppleEsportsErp.Application.DTOs.Settings.FoodGroupDto
        {
            Id = g.Id,
            Name = g.Name,
            CreatedAt = g.CreatedAt,
            OfflineSellerBranchId = g.OfflineSellerBranchId,
            Branches = g.Branches
                .OrderBy(b => b.Name)
                .Select(b => new AppleEsportsErp.Application.DTOs.Settings.FoodGroupBranchDto { Id = b.Id, Name = b.Name })
                .ToList()
        });

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(dtos));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppleEsportsErp.Application.DTOs.Settings.CreateFoodGroupDto dto)
    {
        var group = new AppleEsportsErp.Domain.Entities.FoodGroup
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>().AddAsync(group);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { group.Id, group.Name }));
    }

    /// <summary>Replaces the group's whole branch membership with exactly the ids given — the
    /// simplest shape for a checkbox-list settings screen. A branch removed from the list keeps
    /// whatever menu items it already has locally; it just stops receiving future changes from
    /// (or sending its own to) the group, same as it never having joined.</summary>
    [HttpPut("{id}/branches")]
    public async Task<IActionResult> SetBranches(Guid id, [FromBody] List<Guid> branchIds)
    {
        var group = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>()
            .Query().FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Food group not found"));

        var branchRepo = _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>();

        var currentMembers = await branchRepo.Query().Where(b => b.FoodGroupId == id).ToListAsync();
        foreach (var b in currentMembers.Where(b => !branchIds.Contains(b.Id)))
        {
            b.FoodGroupId = null;
            branchRepo.Update(b);
        }

        var toAdd = await branchRepo.Query()
            .Where(b => branchIds.Contains(b.Id) && b.FoodGroupId != id)
            .ToListAsync();
        foreach (var b in toAdd)
        {
            b.FoodGroupId = id;
            branchRepo.Update(b);
        }

        if (group.OfflineSellerBranchId is { } seller && !branchIds.Contains(seller))
            group.OfflineSellerBranchId = null;

        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Updated." }));
    }

    public record OfflineSellerRequest(Guid? BranchId);

    /// <summary>Which member branch keeps selling the shared fridge when the internet is down -
    /// every other branch in the group pauses shared items until it is back (SharedStockGate).</summary>
    [HttpPut("{id}/offline-seller")]
    public async Task<IActionResult> SetOfflineSeller(Guid id, [FromBody] OfflineSellerRequest request)
    {
        var group = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>()
            .Query().Include(g => g.Branches).FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Food group not found"));
        if (request.BranchId is { } b && group.Branches.All(x => x.Id != b))
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("That branch is not in this group."));

        group.OfflineSellerBranchId = request.BranchId;
        await _unitOfWork.SaveChangesAsync();
        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Updated." }));
    }

    /// <summary>Member branches simply revert to independent (FoodGroupId set to null by the
    /// database itself, DeleteBehavior.SetNull) — nothing about their existing menu is touched.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>()
            .Query().FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Food group not found"));

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.FoodGroup>().Remove(group);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Food group deleted." }));
    }
}

/// <summary>SOP §5: Operators — maps from operators.routes.js</summary>
[ApiController]
[Route("api/operators")]
[Authorize(Policy = "Dashboard:settings")]
public class OperatorsController : ControllerBase
{
    private readonly AppleEsportsErp.Application.Interfaces.IUnitOfWork _unitOfWork;
    private readonly AppleEsportsErp.Application.Interfaces.IAuditService _auditService;
    private readonly AppleEsportsErp.Application.Interfaces.IEmailService _emailService;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<AppleEsportsErp.Api.Hubs.NotificationHub> _notificationHub;
    private readonly AppleEsportsErp.Application.Interfaces.IAdminNotifier _adminNotifier;

    public OperatorsController(
        AppleEsportsErp.Application.Interfaces.IUnitOfWork unitOfWork,
        AppleEsportsErp.Application.Interfaces.IAuditService auditService,
        AppleEsportsErp.Application.Interfaces.IEmailService emailService,
        Microsoft.AspNetCore.SignalR.IHubContext<AppleEsportsErp.Api.Hubs.NotificationHub> notificationHub,
        AppleEsportsErp.Application.Interfaces.IAdminNotifier adminNotifier)
    {
        _unitOfWork = unitOfWork;
        _auditService = auditService;
        _emailService = emailService;
        _notificationHub = notificationHub;
        _adminNotifier = adminNotifier;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var operators = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>()
            .Query()
            .Where(o => !o.Username.StartsWith("system_admin_"))
            .Include(o => o.Branch)
            .OrderBy(o => o.FullName)
            .ToListAsync();

        var dtos = operators.Select(o => new AppleEsportsErp.Application.DTOs.Settings.OperatorDto
        {
            Id = o.Id,
            FullName = o.FullName,
            Username = o.Username,
            Email = o.Email,
            BranchId = o.BranchId,
            BranchName = o.Branch?.Name ?? "Unknown",
            Status = o.Status.ToString(),
            DashboardPermissions = o.DashboardPermissions ?? "{}",
            IsGlobalAdmin = o.IsGlobalAdmin,
            HasAccessPin = !string.IsNullOrEmpty(o.AccessPin),
            CreatedAt = o.CreatedAt
        });

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(dtos));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppleEsportsErp.Application.DTOs.Settings.CreateOperatorDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Password))
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Password is required to create an operator."));

        // Check username uniqueness within the branch
        var usernameTaken = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>()
            .Query().AnyAsync(o => o.Username == dto.Username.Trim() && o.BranchId == dto.BranchId);
        if (usernameTaken)
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail($"Username '{dto.Username}' is already taken in this branch."));

        var op = new AppleEsportsErp.Domain.Entities.Operator
        {
            Id = Guid.NewGuid(),
            FullName = dto.FullName,
            Username = dto.Username.Trim().ToLowerInvariant(),
            Email = dto.Email.Trim().ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
            BranchId = dto.BranchId,
            DashboardPermissions = dto.DashboardPermissions,
            Status = AppleEsportsErp.Domain.Enums.OperatorStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().AddAsync(op);
        await _unitOfWork.SaveChangesAsync();

        var branch = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>().Query()
            .FirstOrDefaultAsync(b => b.Id == op.BranchId);
        var branchName = branch?.Name ?? "Unknown Branch";

        string emailBody = $@"
        <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
            <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #dc2626;'>
                    <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                        <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                    </h1>
                </div>
                <div style='padding: 40px 30px; text-align: left;'>
                    <h2 style='margin-top: 0; color: #a3a3a3; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>New Operator Registered</h2>
                    <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>A new operator account has been provisioned in the Apple Esports system.</p>
                    
                    <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Name:</span> <strong style='color: #ffffff;'>{op.FullName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Username:</span> <strong style='color: #ffffff;'>{op.Username}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Branch:</span> <strong style='color: #ffffff;'>{branchName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Time:</span> <strong style='color: #ffffff;'>{op.CreatedAt.ToString("MMM dd, yyyy HH:mm")}</strong></p>
                    </div>
                </div>
                <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                    <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated security notification from Apple Esports ERP.</p>
                    <p style='margin: 5px 0 0 0; color: #4b5563; font-size: 11px;'>© {DateTime.UtcNow.Year} Apple Esports. All rights reserved.</p>
                </div>
            </div>
        </div>";

        await SendNotificationAsync($"New Operator Joined: {op.FullName}", emailBody);

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { op.Id, op.Username }));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] AppleEsportsErp.Application.DTOs.Settings.UpdateOperatorDto dto)
    {
        var op = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().FirstOrDefaultAsync(o => o.Id == id);
        if (op == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Operator not found"));

        // Only the branch. Which screens an operator may open is a routine settings tweak and
        // emailing the owner for each checkbox would bury the changes that matter - a first
        // version of this alerted on those too, and it was noise.
        var previousBranchId = op.BranchId;
        var previousBranchName = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>()
            .Query().Where(b => b.Id == previousBranchId).Select(b => b.Name).FirstOrDefaultAsync();

        op.FullName = dto.FullName;
        op.Username = dto.Username;
        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            op.Email = dto.Email;
        }
        op.BranchId = dto.BranchId;
        op.DashboardPermissions = dto.DashboardPermissions;
        op.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            op.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        }

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Update(op);
        await _unitOfWork.SaveChangesAsync();

        await _notificationHub.Clients.Group($"user:{op.Id}").SendAsync("PermissionsUpdated");

        // Moving staff between branches is a real staffing change and worth telling the owner
        // about. Correcting a spelling, or ticking a dashboard box, is not.
        if (previousBranchId != op.BranchId)
        {
            var newBranchName = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>()
                .Query().Where(b => b.Id == op.BranchId).Select(b => b.Name).FirstOrDefaultAsync();

            await _adminNotifier.NotifyAsync(
                $"Operator moved branch: {op.FullName}",
                AppleEsportsErp.Infrastructure.Services.AdminEmailTemplate.Compose(
                    "A staff member was moved to another branch",
                    AppleEsportsErp.Infrastructure.Services.AdminEmailTemplate.Amber,
                    $"{op.FullName} now works at {newBranchName ?? "a different branch"} instead of {previousBranchName ?? "their old branch"}. From now on they can only see and use that branch.",
                    new[]
                    {
                        ("Staff member", op.FullName),
                        ("Login name", op.Username),
                        ("", ""),
                        ("Used to work at", previousBranchName ?? "unknown"),
                        ("Now works at", newBranchName ?? "unknown"),
                        ("When", AppleEsportsErp.Application.Services.IndiaTime.Now.ToString("dd MMM yyyy, hh:mm tt")),
                    },
                    footnote: "If you did not do this, open Settings and change it back."));
        }

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { op.Id, op.Username }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var op = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().FirstOrDefaultAsync(o => o.Id == id);
        if (op == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Operator not found"));

        // Soft delete: toggle status to Disabled
        op.Status = AppleEsportsErp.Domain.Enums.OperatorStatus.Disabled;
        op.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Update(op);
        await _unitOfWork.SaveChangesAsync();

        string emailBody = $@"
        <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
            <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #dc2626;'>
                    <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                        <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                    </h1>
                </div>
                <div style='padding: 40px 30px; text-align: left;'>
                    <h2 style='margin-top: 0; color: #f59e0b; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Operator Disabled</h2>
                    <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>An operator account has been disabled and its access has been revoked.</p>
                    
                    <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Name:</span> <strong style='color: #ffffff;'>{op.FullName}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Username:</span> <strong style='color: #ffffff;'>{op.Username}</strong></p>
                        <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Time:</span> <strong style='color: #ffffff;'>{op.UpdatedAt.ToString("MMM dd, yyyy HH:mm")}</strong></p>
                    </div>
                </div>
                <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                    <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated security notification from Apple Esports ERP.</p>
                    <p style='margin: 5px 0 0 0; color: #4b5563; font-size: 11px;'>© {DateTime.UtcNow.Year} Apple Esports. All rights reserved.</p>
                </div>
            </div>
        </div>";

        await SendNotificationAsync($"Operator Disabled: {op.FullName}", emailBody);

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Operator disabled successfully" }));
    }

    [HttpPost("{id}/activate")]
    public async Task<IActionResult> Activate(Guid id)
    {
        var op = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().FirstOrDefaultAsync(o => o.Id == id);
        if (op == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Operator not found"));

        op.Status = AppleEsportsErp.Domain.Enums.OperatorStatus.Active;
        op.UpdatedAt = DateTimeOffset.UtcNow;
        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Update(op);
        await _unitOfWork.SaveChangesAsync();

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Operator activated successfully" }));
    }

    [HttpDelete("{id}/permanent")]
    public async Task<IActionResult> DeletePermanent(Guid id)
    {
        var op = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().FirstOrDefaultAsync(o => o.Id == id);
        if (op == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Operator not found"));

        try
        {
            _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Remove(op);
            await _unitOfWork.SaveChangesAsync();

            string emailBody = $@"
            <div style='background-color:#050505; color:#ffffff; font-family:""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif; padding:40px 20px; text-align:center;'>
                <div style='max-width: 600px; margin: 0 auto; background-color: #111111; border: 1px solid #333333; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.5);'>
                    <div style='background: linear-gradient(135deg, #1a1a24 0%, #0d0d14 100%); padding: 30px 20px; border-bottom: 2px solid #dc2626;'>
                        <h1 style='margin: 0; font-size: 28px; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                            <img src='https://appleesports.in/apple-touch-icon.png' alt='Logo' style='height: 40px; vertical-align: middle; margin-right: 15px;' /> APPLE ESPORTS
                        </h1>
                    </div>
                    <div style='padding: 40px 30px; text-align: left;'>
                        <h2 style='margin-top: 0; color: #ef4444; font-size: 24px; border-bottom: 2px solid #333333; padding-bottom: 15px;'>Operator Deleted</h2>
                        <p style='font-size: 16px; color: #d1d5db; line-height: 1.6;'>An operator account has been permanently deleted from the system.</p>
                        
                        <div style='background-color: #0a0a0a; border: 1px solid #222222; border-radius: 8px; padding: 20px; margin-top: 25px;'>
                            <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Name:</span> <strong style='color: #ffffff;'>{op.FullName}</strong></p>
                            <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Username:</span> <strong style='color: #ffffff;'>{op.Username}</strong></p>
                            <p style='margin: 10px 0;'><span style='color: #6b7280; display: inline-block; width: 100px;'>Time:</span> <strong style='color: #ffffff;'>{DateTimeOffset.UtcNow.ToString("MMM dd, yyyy HH:mm")}</strong></p>
                        </div>
                    </div>
                    <div style='background-color: #080808; padding: 20px; border-top: 1px solid #222222; text-align: center;'>
                        <p style='margin: 0; color: #6b7280; font-size: 12px;'>This is an automated security notification from Apple Esports ERP.</p>
                        <p style='margin: 5px 0 0 0; color: #4b5563; font-size: 11px;'>© {DateTime.UtcNow.Year} Apple Esports. All rights reserved.</p>
                    </div>
                </div>
            </div>";

            await SendNotificationAsync($"Operator Deleted: {op.FullName}", emailBody);

            return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = "Operator deleted permanently" }));
        }
        catch (DbUpdateException)
        {
            return BadRequest(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Cannot delete operator permanently because they have associated transactional history (shifts, sessions, bills, etc.). You can disable their account instead."));
        }
    }

    /// <summary>
    /// Tells the owner and any admins about a staffing change.
    ///
    /// This used to read a "receivers" string out of system settings and send only if
    /// somebody had filled it in. Nobody had, on the live system, so every operator alert
    /// ever raised here was addressed to an empty string and dropped — and the only record
    /// of it was a line appended to a file called email_log.txt next to the executable.
    ///
    /// The notifier still honours that setting, and adds the admins from the database, so
    /// the alert reaches someone whether or not the field was ever filled in.
    /// </summary>
    private Task SendNotificationAsync(string subject, string body)
        => _adminNotifier.NotifyAsync(subject, body);

    [HttpPost("{id}/admin-role")]
    [Authorize(Policy = "AdminOrSuperAdmin")]
    public async Task<IActionResult> ManageAdminRole(Guid id, [FromBody] AppleEsportsErp.Application.DTOs.Settings.ManageAdminRoleDto dto, [FromServices] AppleEsportsErp.Application.Interfaces.IAuthService authService)
    {
        var op = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Query().FirstOrDefaultAsync(o => o.Id == id);
        if (op == null) return NotFound(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Fail("Operator not found"));

        if (dto.IsGlobalAdmin)
        {
            // ── PROMOTE ──
            // Snapshot current permissions BEFORE adding admin perms, so we can restore them on demotion
            // Only snapshot if not already a global admin (to avoid overwriting original snapshot on re-promotion)
            if (!op.IsGlobalAdmin)
            {
                op.PreAdminDashboardPermissions = op.DashboardPermissions;
            }

            // Add admin-level permissions on top of existing permissions
            var currentPerms = new System.Collections.Generic.Dictionary<string, bool>();
            if (!string.IsNullOrEmpty(op.DashboardPermissions))
            {
                try { currentPerms = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, bool>>(op.DashboardPermissions) ?? new System.Collections.Generic.Dictionary<string, bool>(); }
                catch { }
            }
            currentPerms["settings"] = dto.CanAccessSettings;
            currentPerms["discount"] = dto.CanGiveDiscount;
            op.DashboardPermissions = System.Text.Json.JsonSerializer.Serialize(currentPerms);
            op.AccessPin = dto.AccessPin;
        }
        else
        {
            // ── DEMOTE ──
            // Restore the permissions snapshot taken at promotion time
            if (!string.IsNullOrEmpty(op.PreAdminDashboardPermissions))
            {
                op.DashboardPermissions = op.PreAdminDashboardPermissions;
                op.PreAdminDashboardPermissions = null; // clear snapshot
            }
            else
            {
                // Fallback: no snapshot exists — just strip admin-only keys
                var currentPerms = new System.Collections.Generic.Dictionary<string, bool>();
                if (!string.IsNullOrEmpty(op.DashboardPermissions))
                {
                    try { currentPerms = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, bool>>(op.DashboardPermissions) ?? new System.Collections.Generic.Dictionary<string, bool>(); }
                    catch { }
                }
                currentPerms.Remove("settings");
                currentPerms.Remove("discount");
                op.DashboardPermissions = System.Text.Json.JsonSerializer.Serialize(currentPerms);
            }

            // Instantly revoke the demoted admin's session and force them to login again
            var adminIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(adminIdStr, out var adminId))
            {
                await authService.ForceLogoutAsync(adminId, op.Id);
            }

            op.AccessPin = null;
        }

        // Captured before it changes, so the alert can say whether this was a promotion or a
        // demotion rather than just stating the new value.
        var wasAdmin = op.IsGlobalAdmin;

        op.IsGlobalAdmin = dto.IsGlobalAdmin;
        op.UpdatedAt = DateTimeOffset.UtcNow;

        _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Operator>().Update(op);
        await _unitOfWork.SaveChangesAsync();

        var adminName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "System Admin";
        
        await _auditService.LogAsync(new AppleEsportsErp.Application.Interfaces.AuditEntry
        {
            UserId = Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : Guid.Empty,
            UserRole = AppleEsportsErp.Application.Constants.Roles.SuperAdmin,
            UserName = adminName,
            Action = dto.IsGlobalAdmin ? "operator_promoted_to_admin" : "operator_demoted_from_admin",
            TargetType = "operator",
            TargetId = op.Id,
            BranchId = op.BranchId,
            Details = new { 
                operatorName = op.FullName, 
                canAccessSettings = dto.CanAccessSettings,
                canGiveDiscount = dto.CanGiveDiscount,
                permissionsRestored = !dto.IsGlobalAdmin && op.PreAdminDashboardPermissions == null
            }
        });

        // The real role change: an operator becoming an admin, or ceasing to be one. This is
        // the biggest single change anyone's account can undergo - an admin sees every branch
        // and every figure - so the owner is told even when they made the change themselves,
        // because the value is in hearing about the one they did not.
        if (wasAdmin != dto.IsGlobalAdmin)
        {
            var branchName = await _unitOfWork.Repository<AppleEsportsErp.Domain.Entities.Branch>()
                .Query().Where(b => b.Id == op.BranchId).Select(b => b.Name).FirstOrDefaultAsync();

            await _adminNotifier.NotifyAsync(
                dto.IsGlobalAdmin
                    ? $"Promoted to Admin: {op.FullName}"
                    : $"Admin rights removed: {op.FullName}",
                AppleEsportsErp.Infrastructure.Services.AdminEmailTemplate.Compose(
                    dto.IsGlobalAdmin ? "A staff member was made an Admin" : "Admin access was taken away",
                    dto.IsGlobalAdmin ? AppleEsportsErp.Infrastructure.Services.AdminEmailTemplate.Red : AppleEsportsErp.Infrastructure.Services.AdminEmailTemplate.Amber,
                    dto.IsGlobalAdmin
                        ? $"{op.FullName} used to be an operator at {branchName ?? "their branch"}. They are now an Admin, which means they can see all of your branches and all of the money figures."
                        : $"{op.FullName} is no longer an Admin. They are back to being an operator at {branchName ?? "their branch"}, and can only see that one branch.",
                    new[]
                    {
                        ("Staff member", op.FullName),
                        ("Login name", op.Username),
                        ("Their branch", branchName ?? "unknown"),
                        ("", ""),
                        ("They were", wasAdmin ? "an Admin" : "an operator"),
                        ("They are now", dto.IsGlobalAdmin ? "an Admin" : "an operator"),
                        ("", ""),
                        ("Changed by", adminName),
                        ("When", AppleEsportsErp.Application.Services.IndiaTime.Now.ToString("dd MMM yyyy, hh:mm tt")),
                    },
                    headline: dto.IsGlobalAdmin ? "Now an Admin" : "Back to operator",
                    footnote: dto.IsGlobalAdmin
                        ? "An Admin sees every branch and every rupee. If you did not do this, open Settings and take it away now."
                        : "This person can only see their own branch again."));
        }

        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<object>.Ok(new { message = dto.IsGlobalAdmin ? "Operator promoted to Global Admin" : "Operator demoted — original permissions restored" }));
    }
}

/// <summary>SOP §18: Main Dashboard — maps from dashboard.routes.js</summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
[BranchIsolation]
public class DashboardController : ControllerBase
{
    private readonly AppleEsportsErp.Application.Interfaces.IDashboardService _dashboardService;

    public DashboardController(AppleEsportsErp.Application.Interfaces.IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] Guid? branchId = null)
    {
        var targetBranchId = branchId;
        if (targetBranchId == null && HttpContext.Items.TryGetValue("BranchId", out var itemVal) && itemVal != null)
        {
            var parsedVal = itemVal.ToString();
            if (!string.IsNullOrEmpty(parsedVal) && Guid.TryParse(parsedVal, out var g))
            {
                targetBranchId = g;
            }
        }
        var result = await _dashboardService.GetSummaryAsync(targetBranchId);
        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<AppleEsportsErp.Application.DTOs.Dashboard.DashboardSummaryDto>.Ok(result));
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> GetRecentTransactions([FromQuery] Guid? branchId = null, [FromQuery] int limit = 20)
    {
        var targetBranchId = branchId;
        if (targetBranchId == null && HttpContext.Items.TryGetValue("BranchId", out var itemVal) && itemVal != null)
        {
            var parsedVal = itemVal.ToString();
            if (!string.IsNullOrEmpty(parsedVal) && Guid.TryParse(parsedVal, out var g))
            {
                targetBranchId = g;
            }
        }
        var result = await _dashboardService.GetRecentActivityAsync(targetBranchId, limit);
        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<IEnumerable<AppleEsportsErp.Application.DTOs.Dashboard.RecentActivityDto>>.Ok(result));
    }

    [HttpGet("branches-summary")]
    [Authorize(Policy = "OperatorOrAdmin")]
    public async Task<IActionResult> GetBranchSummaries()
    {
        var result = await _dashboardService.GetBranchSummariesAsync();
        return Ok(AppleEsportsErp.Application.DTOs.Common.ApiResponse<IEnumerable<AppleEsportsErp.Application.DTOs.Dashboard.BranchDashboardSummaryDto>>.Ok(result));
    }
}

