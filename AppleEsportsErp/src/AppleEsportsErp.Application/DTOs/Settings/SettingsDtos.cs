using System;
using System.Collections.Generic;

namespace AppleEsportsErp.Application.DTOs.Settings;

public class CreateBranchDto
{
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public string OpeningTime { get; set; } = "10:00";
    public string ClosingTime { get; set; } = "02:00";
    public string? ConfiguredReservationDurations { get; set; }
    public decimal? DefaultOpeningFloat { get; set; }

    /// <summary>Null (the default) means this branch's food/snacks stay fully independent.
    /// See FoodGroup.</summary>
    public Guid? FoodGroupId { get; set; }
}

public class UpdateBranchDto
{
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public string OpeningTime { get; set; } = null!;
    public string ClosingTime { get; set; } = null!;
    public string? ConfiguredReservationDurations { get; set; }
    public decimal? DefaultOpeningFloat { get; set; }
    public Guid? FoodGroupId { get; set; }
}

public class BranchDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Address { get; set; }
    public string OpeningTime { get; set; } = "10:00";
    public string ClosingTime { get; set; } = "02:00";
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string? ConfiguredReservationDurations { get; set; }
    public decimal DefaultOpeningFloat { get; set; }
    public Guid? FoodGroupId { get; set; }
    public string? FoodGroupName { get; set; }
}

/// <summary>A named link between branches that share one food/snacks menu and stock count —
/// see FoodGroup.</summary>
public class FoodGroupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public List<FoodGroupBranchDto> Branches { get; set; } = new();

    /// <summary>The branch that keeps selling the shared fridge when the internet is down.</summary>
    public Guid? OfflineSellerBranchId { get; set; }
}

public class FoodGroupBranchDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class CreateFoodGroupDto
{
    public string Name { get; set; } = null!;
}

public class CreateOperatorDto
{
    public string FullName { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Password { get; set; } = null!;
    public Guid BranchId { get; set; }
    public string DashboardPermissions { get; set; } = "{}";
}

public class UpdateOperatorDto
{
    public string FullName { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? Email { get; set; }
    public string? Password { get; set; } // Optional on update
    public Guid BranchId { get; set; }
    public string DashboardPermissions { get; set; } = "{}";
}

public class OperatorDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public Guid BranchId { get; set; }
    public string BranchName { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string DashboardPermissions { get; set; } = "{}";
    public bool IsGlobalAdmin { get; set; } = false;
    public bool HasAccessPin { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; }
}

public class ManageAdminRoleDto
{
    public bool IsGlobalAdmin { get; set; }
    public bool CanAccessSettings { get; set; }
    public bool CanGiveDiscount { get; set; }
    public string? AccessPin { get; set; }
}

public class AuditLogDto
{
    public Guid Id { get; set; }
    public string? UserName { get; set; }
    public string? UserRole { get; set; }
    public string Action { get; set; } = null!;
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public bool Success { get; set; } = true;
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Added for the Activity Log screen. Every branch's rows now land in the same table at
    /// Head Office, so which shop a row belongs to is no longer implied by which branch you
    /// happened to be looking at when you asked - it has to be said outright.
    /// </summary>
    public Guid? BranchId { get; set; }
    public string? BranchName { get; set; }
}

public class CreatePcDto
{
    /// <summary>
    /// Set by Head Office itself when queuing a remote add (see PcsController.Create), so the
    /// row it creates in its own mirror and the row the branch creates locally share the same
    /// identity from the start - without this, the branch's own row got a fresh id nothing at
    /// Head Office had ever seen, and its heartbeat reports on that id were silently ignored
    /// forever (ApplyPcStatesAsync only ever updates an id it already recognises, never creates
    /// one). Left null for a PC added locally at a branch's own Settings page, which has no
    /// mirror to keep in step with.
    /// </summary>
    public Guid? Id { get; set; }
    public string PcNumber { get; set; } = null!;
    public string? PcName { get; set; }
    public Guid BranchId { get; set; }
    public string? IpAddress { get; set; }
    public string? Specs { get; set; }
    public string? Zone { get; set; }
    public string? HardwareNotes { get; set; }
    public Guid? PricingProfileId { get; set; }
}

public class UpdatePcDto
{
    public string PcNumber { get; set; } = null!;
    public string? PcName { get; set; }
    public string? IpAddress { get; set; }
    public string? Specs { get; set; }
    public string? Zone { get; set; }
    public string? HardwareNotes { get; set; }
}

/// <summary>UpdatePcDto plus the id of the PC being edited, for the branch_commands payload - see BranchCommands.UpdatePc.</summary>
public class UpdatePcCommandDto : UpdatePcDto
{
    public Guid Id { get; set; }
}

/// <summary>For BranchCommands.DeletePc's payload - nothing else about the PC needs to travel, only which one.</summary>
public class DeletePcCommandDto
{
    public Guid Id { get; set; }
}
