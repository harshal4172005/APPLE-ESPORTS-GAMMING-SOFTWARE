using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Application.Exceptions;

namespace AppleEsportsErp.Api.Extensions;

public static class ControllerExtensions
{
    public static async Task<Guid> GetOperatorIdAsync(this ControllerBase controller)
    {
        var user = controller.User;
        
        // If it's a regular operator, just return their ID from the JWT
        if (!user.IsInRole(Roles.SuperAdmin) && !user.IsInRole(Roles.Admin) && !user.IsInRole("Member"))
        {
            return Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }

        // SuperAdmin or Member is acting. Find or create a System Operator for the active branch.
        var branchIdStr = controller.HttpContext.Items["BranchId"]?.ToString();
        if (string.IsNullOrEmpty(branchIdStr))
            throw new InvalidOperationException("BranchId is not set in HttpContext. Cannot resolve System Operator.");

        var branchId = Guid.Parse(branchIdStr);
        var db = controller.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        var sysUsername = $"system_admin_{branchId:N}";
        var sysOp = await db.Operators.FirstOrDefaultAsync(o => o.BranchId == branchId && o.Username == sysUsername);
        if (sysOp == null)
        {
            sysOp = new Operator
            {
                // Deterministic, not Guid.NewGuid() - this same placeholder gets created
                // independently on the branch's own database AND on Head Office's, whichever
                // one needs it first. A random id meant the two sides invented DIFFERENT ids
                // for what is meant to be the same account, sharing only the username (itself
                // already deterministic below) - so every later sync of anything belonging to
                // this operator (a shift, in particular) hit Head Office's own copy under a
                // different id and failed a foreign key check, forever, on a 15-minute retry.
                // Deriving the id the same way the username already is means both sides land
                // on the exact same Guid the first time either of them creates it, with
                // nothing to reconcile afterward.
                Id = DeterministicSystemAdminId(branchId),
                BranchId = branchId,
                FullName = "System Administrator",
                Username = sysUsername,
                Email = $"{sysUsername}@system.local",
                PasswordHash = "LOCKED",
                Status = OperatorStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Operators.Add(sysOp);
            await db.SaveChangesAsync();
        }

        return sysOp.Id;
    }

    /// <summary>
    /// Same Guid every time for the same branch, on any database - MD5 of a fixed, namespaced
    /// string is stable across machines and .NET versions, unlike Guid.NewGuid(). Not used for
    /// anything security-sensitive (the account's password is the literal string "LOCKED", not
    /// a real credential), so a hash being predictable from a branch id is not a weakness here.
    /// </summary>
    private static Guid DeterministicSystemAdminId(Guid branchId)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var seed = System.Text.Encoding.UTF8.GetBytes($"system_admin:{branchId:N}");
        return new Guid(md5.ComputeHash(seed));
    }

    /// <summary>
    /// Refuses a Super Admin cash-register action outright, any hour, operator on shift or not -
    /// Super Admin is not physically at the branch and cannot count a real drawer, so any
    /// register they touch is either meaningless or a duplicate of the real one. Confirmed live
    /// at Citylight: a Super Admin's remote action didn't fail loudly, it silently created a
    /// second, disconnected register under a "system_admin" identity - once with an operator's
    /// shift still running, once after close with nobody on shift at all. A narrower version of
    /// this that only blocked the first case left the second wide open; this replaces it.
    ///
    /// Deliberately Super Admin only, not Admin (Roles.Admin) - Quick Admin Switch is a real
    /// operator physically at the counter with elevated permissions for that session, not a
    /// remote action, and blocking them would break their own register mid-shift.
    ///
    /// Only guards cash-register endpoints - GetShiftIdAsync itself stays untouched, since
    /// Billing/Wallet/Sessions/FoodOrders remote actions from Head Office are a separate,
    /// already-relied-on capability this was never meant to restrict.
    /// </summary>
    public static Task EnsureNotSuperAdminForCashAsync(this ControllerBase controller)
    {
        if (controller.User.IsInRole(Roles.SuperAdmin))
            throw new AppException(
                "Super Admin cannot open, count, close, or add to a cash register. These have to be done by an operator physically at the branch.",
                System.Net.HttpStatusCode.Forbidden,
                "SUPER_ADMIN_CASH_READ_ONLY");

        return Task.CompletedTask;
    }

    public static async Task<Guid> GetShiftIdAsync(this ControllerBase controller)
    {
        var user = controller.User;
        var branchIdStr = controller.HttpContext.Items["BranchId"]?.ToString();
        if (string.IsNullOrEmpty(branchIdStr))
            throw new InvalidOperationException("BranchId is not set in HttpContext.");

        var branchId = Guid.Parse(branchIdStr);
        var db = controller.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        // For regular operators: the operator's own most recent active shift, full stop - the
        // exact same query AuthService.LoginOperatorAsync itself uses to decide resume-vs-new
        // at login. This used to trust the JWT's own "shiftId" claim first, checking only that
        // SOME shift with that id was still Active - never that it belonged to this operator,
        // and never that it was still their CURRENT shift. A browser tab left open holding a
        // token from an earlier shift that was never properly closed (a crash, a power cut)
        // could carry that stale id straight into a brand new login's actions: a cash register
        // opened minutes after a fresh login attached itself to a shift from days earlier
        // instead of the one that login had just created, and everything recorded against it
        // silently followed that orphaned shift - including failing to sync to Head Office at
        // all, since nothing had touched that old row in the meantime to give it a fresh chance
        // to be sent. Querying fresh every time costs one indexed lookup and cannot go stale.
        if (!user.IsInRole(Roles.SuperAdmin) && !user.IsInRole(Roles.Admin) && !user.IsInRole("Member"))
        {
            var operatorId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var operatorShift = await db.Shifts
                .Where(s => s.BranchId == branchId && s.OperatorId == operatorId && s.Status == ShiftStatus.Active)
                .OrderByDescending(s => s.LoginTime)
                .FirstOrDefaultAsync();

            if (operatorShift != null)
                return operatorShift.Id;

            // If no operator shift found, throw error with clear message
            // A recognisable code, because the browser can still be holding a perfectly valid
            // session whose shift has since been closed - by an admin force-logout, or by the
            // shift being ended somewhere else. The login itself is fine, so a 401 is wrong:
            // the client would refresh the token, succeed, retry, and loop. With no Logout
            // button for operators any more, that traps them on a dead screen with no way out.
            throw new AppException(
                "Your shift has been closed. Please sign in again to start a new one.",
                System.Net.HttpStatusCode.Conflict,
                "SHIFT_CLOSED");
        }

        // For SuperAdmin/Admin: the system-admin operator's OWN active shift, never "whichever
        // shift is Active for the branch" - a branch can easily have a real operator's shift
        // Active at the exact same moment an admin acts on it, and the old query here could not
        // tell the two apart. Confirmed live at Citylight: a Super Admin trying to open a cash
        // register for an operator who had just started their shift instead created a brand new
        // shift and register for the system-admin operator, because "most recent Active shift
        // for the branch" happened to resolve to a real operator's own shift - the admin action
        // silently landed nowhere near the operator it was meant to fix, with no error to say so.
        // A real operator on shift at this counter owns whatever happens there - a member's
        // logout bill, a Super Admin's walk-in bill. Putting it on a hidden "System
        // Administrator" shift instead kept the operator's drawer short and left that shift
        // open for the next morning's login to "take over" (77 of 79 takeovers, Oct-Nov 2026).
        var operatorShiftId = await db.Shifts
            .Where(s => s.BranchId == branchId && s.Status == ShiftStatus.Active
                        && !s.Operator.Username.StartsWith("system_admin_"))
            .OrderByDescending(s => s.LoginTime)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync();
        if (operatorShiftId is { } onShift)
            return onShift;

        // Nobody on shift (a member playing before opening, say): fall back to the placeholder.
        // The next operator login closes it rather than asking anyone to count it.
        var sysOpId = await controller.GetOperatorIdAsync();
        var activeShift = await db.Shifts
            .Where(s => s.BranchId == branchId && s.OperatorId == sysOpId && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .FirstOrDefaultAsync();

        if (activeShift == null)
        {
            // Shift only - never a CashRegister. A system-admin shift exists purely so
            // Billing/Wallet/Sessions/FoodOrders remote actions have something to attribute
            // themselves to; it has no drawer of its own and must never appear to have one.
            // This used to also open a CashRegister here, which is exactly how a Super Admin
            // viewing or acting on an unrelated endpoint could end up creating a phantom
            // register with no one ever asking it to.
            activeShift = new Shift
            {
                Id = Guid.NewGuid(),
                BranchId = branchId,
                OperatorId = sysOpId,
                LoginTime = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = ShiftStatus.Active
            };
            db.Shifts.Add(activeShift);
            await db.SaveChangesAsync();
        }

        return activeShift.Id;
    }
}
