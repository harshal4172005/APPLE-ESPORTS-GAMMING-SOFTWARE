using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using AppleEsportsErp.Application.DTOs.Dashboard;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IServiceProvider _serviceProvider; // Useful if we need to resolve IHubContext dynamically without circular deps

    public DashboardService(AppDbContext context, IMemoryCache cache, IServiceProvider serviceProvider)
    {
        _context = context;
        _cache = cache;
        _serviceProvider = serviceProvider;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(Guid? branchId = null)
    {
        var cacheKey = $"Dashboard_Summary_{branchId?.ToString() ?? "Global"}";

        if (_cache.TryGetValue(cacheKey, out DashboardSummaryDto? cachedSummary))
        {
            if (cachedSummary != null)
                return cachedSummary;
        }

        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        var pcsQuery = _context.Pcs.AsQueryable();
        var sessionsQuery = _context.Sessions.AsQueryable();
        var foodOrdersQuery = _context.FoodOrders.AsQueryable();
        var billsQuery = _context.Bills.AsQueryable();
        var paymentsQuery = _context.Payments.AsQueryable();
        var operatorsQuery = _context.Shifts.AsQueryable();

        if (branchId.HasValue)
        {
            pcsQuery = pcsQuery.Where(p => p.BranchId == branchId.Value);
            sessionsQuery = sessionsQuery.Where(s => s.BranchId == branchId.Value);
            foodOrdersQuery = foodOrdersQuery.Where(f => f.BranchId == branchId.Value);
            billsQuery = billsQuery.Where(b => b.BranchId == branchId.Value);
            paymentsQuery = paymentsQuery.Where(p => p.BranchId == branchId.Value);
            operatorsQuery = operatorsQuery.Where(s => s.BranchId == branchId.Value);
        }

        // Realtime counts (Fast)
        var totalActivePcs = await pcsQuery.CountAsync(p => p.State == PcState.Active);
        var reservedPcs = await pcsQuery.CountAsync(p => p.State == PcState.Reserved);
        var maintenancePcs = await pcsQuery.CountAsync(p => p.State == PcState.UnderMaintenance);
        var awaitingPcs = await pcsQuery.CountAsync(p => p.State == PcState.AwaitingBilling);
        var totalActiveSessions = await sessionsQuery.CountAsync(s => s.State == SessionState.Active);
        var activeFoodOrders = await foodOrdersQuery.CountAsync(f => f.Status == OrderStatus.Pending || f.Status == OrderStatus.Preparing);
        var activeOperators = await operatorsQuery.CountAsync(s => s.Status == ShiftStatus.Active && !s.Operator.Username.StartsWith("system_admin_"));

        // Financials (Cached briefly)
        var todaysBills = await billsQuery.Where(b => b.CompletedAt >= today && b.Status == BillStatus.Completed).ToListAsync();
        var todaysPayments = await paymentsQuery.Where(p => p.CreatedAt >= today).ToListAsync();

        var totalRevenue = todaysPayments.Sum(p => p.TotalAmount);
        var cashTotal = todaysPayments.Sum(p => p.CashAmount);
        var onlineTotal = todaysPayments.Sum(p => p.OnlineAmount);
        var walletTotal = todaysPayments.Sum(p => p.WalletAmount);

        var gamingRevenue = todaysBills.Sum(b => b.GamingAmount);
        var foodRevenue = todaysBills.Sum(b => b.FoodAmount);

        var summary = new DashboardSummaryDto
        {
            TotalActivePcs = totalActivePcs,
            TotalActiveSessions = totalActiveSessions,
            ReservedPcs = reservedPcs,
            PcsUnderMaintenance = maintenancePcs,
            AwaitingBillingPcs = awaitingPcs,
            ActiveFoodOrders = activeFoodOrders,
            ActiveOperators = activeOperators,
            LowStockAlerts = 0, // Mocked for now

            TodayBillsCount = todaysBills.Count,

            TotalRevenueToday = totalRevenue,
            GamingRevenueToday = gamingRevenue,
            FoodRevenueToday = foodRevenue,
            CashTotals = cashTotal,
            OnlineTotals = onlineTotal,
            WalletTotals = walletTotal
        };

        // Cache for 3-5 seconds as requested by SOP decision
        _cache.Set(cacheKey, summary, TimeSpan.FromSeconds(5));

        return summary;
    }

    public async Task<IEnumerable<RecentActivityDto>> GetRecentActivityAsync(Guid? branchId = null, int limit = 20)
    {
        // For simplicity and speed in this phase, we'll fetch recent audit logs and format them as activity
        var logsQuery = _context.AuditLogs
            .AsQueryable();

        if (branchId.HasValue)
        {
            logsQuery = logsQuery.Where(l => l.BranchId == branchId.Value);
        }

        var recentLogs = await logsQuery
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return recentLogs.Select(l => new RecentActivityDto
        {
            Id = l.Id,
            Type = FormatActivityType(l.Action),
            Description = FormatActivityDescription(l.Action, l.Details ?? ""),
            Timestamp = l.CreatedAt.DateTime,
            OperatorName = l.UserName ?? "System",
            BranchId = l.BranchId,
            Category = DetermineCategory(l.Action)
        });
    }

    private string FormatActivityType(string action)
    {
        return action switch
        {
            "login" => "User Login",
            "bill_complete" => "Bill Generated",
            "payment_process" => "Payment Processed",
            "session_start" => "Session Started",
            "session_end" => "Session Ended",
            "session_extend" => "Session Extended",
            "food_order_create" => "Food Order Created",
            "food_order_status_change" => "Food Order Status Updated",
            "cash_register_open" => "Register Opened",
            "cash_register_close" => "Register Closed",
            _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(action.Replace('_', ' '))
        };
    }

    private string FormatActivityDescription(string action, string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson) || detailsJson == "null")
            return "Action completed successfully.";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;

            return action switch
            {
                "login" => "Logged into the system.",
                "bill_complete" => $"Generated bill {root.GetProperty("BillNumber").GetString()}.",
                "payment_process" => $"Received ₹{root.GetProperty("Total").GetDecimal()} via {root.GetProperty("PaymentType").GetString()}.",
                "session_start" => $"Started a new gaming session.",
                "session_end" => $"Ended a gaming session.",
                "cash_register_open" => "Opened the cash register for a new shift.",
                "cash_register_close" => "Closed the cash register and ended the shift.",
                _ => detailsJson // Fallback
            };
        }
        catch
        {
            return detailsJson; // Fallback to raw JSON if parsing fails
        }
    }

    private string DetermineCategory(string action)
    {
        if (action.Contains("Payment") || action.Contains("Bill") || action.Contains("Cash")) return "Financial";
        if (action.Contains("Session") || action.Contains("Pc")) return "Gaming";
        if (action.Contains("Food")) return "Food";
        return "Operational";
    }

    public async Task<IEnumerable<BranchDashboardSummaryDto>> GetBranchSummariesAsync()
    {
        var branches = await _context.Branches
            .Where(b => b.Status == BranchStatus.Active)
            .OrderBy(b => b.Name)
            .ToListAsync();

        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var summaries = new List<BranchDashboardSummaryDto>();

        foreach (var branch in branches)
        {
            var branchId = branch.Id;

            // PCs count
            var totalPcs = await _context.Pcs.CountAsync(p => p.BranchId == branchId && !p.IsDeleted);
            var activePcs = await _context.Pcs.CountAsync(p => p.BranchId == branchId && !p.IsDeleted && p.State == PcState.Active);
            var idlePcs = await _context.Pcs.CountAsync(p => p.BranchId == branchId && !p.IsDeleted && p.State == PcState.Idle);

            // Active operator shift (exclude system admin)
            var activeShift = await _context.Shifts
                .Include(s => s.Operator)
                .FirstOrDefaultAsync(s => s.BranchId == branchId && s.Status == ShiftStatus.Active && !s.Operator.Username.StartsWith("system_admin"));
                
            // OperatorPresenceTracker used to decide this, but it only ever sees a SignalR
            // connection made directly to Head Office - and a branch operator's real work
            // happens against their own branch's local API (see desktop-client/AppConfig.cs),
            // never Head Office directly. That signal can never fire for a real branch, so
            // every operator, on every branch, showed "(Offline)" permanently. IsOnline is
            // what the branch's own heartbeat actually reports (BranchHeartbeatController.
            // ApplyOperatorsOnDutyAsync), so it reflects the branch's own truth instead.
            string activeOperator = "None";
            if (activeShift?.Operator != null)
            {
                activeOperator = activeShift.Operator.IsOnline
                    ? activeShift.Operator.FullName
                    : $"{activeShift.Operator.FullName} (Offline)";
            }

            // Total operators assigned to this branch (registered, regardless of shift)
            var assignedOperatorsCount = await _context.Operators
                .CountAsync(o => o.BranchId == branchId && o.Status == OperatorStatus.Active && !o.Username.StartsWith("system_admin"));

            // Sales (since midnight today)
            var todaysBills = await _context.Bills
                .Where(b => b.BranchId == branchId && b.CompletedAt >= today && b.Status == BillStatus.Completed)
                .ToListAsync();

            var totalSales = todaysBills.Sum(b => b.TotalAmount);
            var gamingSales = todaysBills.Sum(b => b.GamingAmount);
            var foodSales = todaysBills.Sum(b => b.FoodAmount);

            // Cash in drawer (from active register)
            var activeRegister = await _context.CashRegisters
                .FirstOrDefaultAsync(r => r.BranchId == branchId && r.Status == CashRegisterStatus.Open);
            var cashInDrawer = activeRegister?.ExpectedDrawerCash ?? 0m;

            summaries.Add(new BranchDashboardSummaryDto
            {
                BranchId = branchId,
                BranchName = branch.Name,
                TotalPcs = totalPcs,
                ActivePcs = activePcs,
                IdlePcs = idlePcs,
                ActiveOperator = activeOperator,
                AssignedOperatorsCount = assignedOperatorsCount,
                TotalSales = totalSales,
                GamingSales = gamingSales,
                FoodSales = foodSales,
                CashInDrawer = cashInDrawer
            });
        }

        return summaries;
    }

    public Task InvalidateCacheAsync(Guid? branchId = null)
    {
        if (branchId.HasValue)
        {
            _cache.Remove($"Dashboard_Summary_{branchId.Value}");
        }
        _cache.Remove("Dashboard_Summary_Global");
        return Task.CompletedTask;
    }

    public Task BroadcastDashboardUpdateAsync(Guid? branchId = null)
    {
        // This will be invoked by other services to push SignalR updates
        // To avoid circular dependencies, we can inject IHubContext dynamically here or handle in the Controller
        return Task.CompletedTask;
    }
}
