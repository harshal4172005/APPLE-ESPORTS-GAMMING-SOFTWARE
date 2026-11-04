using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Application.DTOs.Sessions;

public class SessionDto
{
    public Guid Id { get; set; }
    public Guid PcId { get; set; }
    public string PcName { get; set; } = null!;
    public Guid BranchId { get; set; }
    public Guid OperatorId { get; set; }
    public Guid ShiftId { get; set; }
    
    public string? CustomerName { get; set; }
    public Guid? MemberId { get; set; }
    
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    
    public decimal DurationMinutes { get; set; }
    public decimal ExpectedAmount { get; set; }
    public string PackageName { get; set; } = null!;
    
    public SessionState Status { get; set; }
    public Guid BillId { get; set; }

    public decimal? WalletDeductedAmount { get; set; }
    public decimal? WalletShortfallAmount { get; set; }
}

public class SessionStartDto
{
    public Guid PcId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? MemberId { get; set; }
    public decimal DurationMinutes { get; set; }
    public string PackageName { get; set; } = null!;
    public decimal ExpectedAmount { get; set; }
    public string? Notes { get; set; }
}

public class SessionExtendDto
{
    public decimal AdditionalMinutes { get; set; }

    /// <summary>
    /// No longer used to price anything - SessionService.ExtendSessionAsync computes the real
    /// charge itself, from the branch's own plans for AdditionalMinutes, and ignores whatever
    /// arrives here. Kept only so an older client sending it does not fail validation; a client
    /// billing a fixed-duration extension at a plain hourly rate (Rs 200 for a 4-hour plan
    /// priced at Rs 180) was exactly the bug this stopped trusting.
    /// </summary>
    public decimal AdditionalAmount { get; set; }

    public string PackageName { get; set; } = null!;
}

public class SessionTransferDto
{
    public Guid TargetPcId { get; set; }
}

public class StopSessionDto
{
    public bool DeferPayment { get; set; } = false;
}

/// <summary>One package a running session could switch to, priced and checked by the server.</summary>
public class PackageOptionDto
{
    public Guid PackageId { get; set; }
    public string Name { get; set; } = null!;
    public int Minutes { get; set; }
    public decimal Price { get; set; }
    public bool IsCurrent { get; set; }
    public bool Allowed { get; set; }
    /// <summary>Why not, in words the customer can read ("Already played 3 hr 10 min").</summary>
    public string? Reason { get; set; }
    /// <summary>Playing time left if switched now.</summary>
    public int MinutesLeftAfter { get; set; }
}

public class PackageOptionsDto
{
    public Guid SessionId { get; set; }
    public string? CurrentName { get; set; }
    public decimal CurrentPrice { get; set; }
    public int PlayedMinutes { get; set; }
    public string? Unavailable { get; set; }
    public List<PackageOptionDto> Options { get; set; } = new();
}

public class ChangePackageDto
{
    public Guid PackageId { get; set; }
}
