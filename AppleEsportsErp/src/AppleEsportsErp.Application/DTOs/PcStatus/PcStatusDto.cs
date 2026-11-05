using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Application.DTOs.PcStatus;

public class PcStatusDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string IpAddress { get; set; } = null!;
    public PcState State { get; set; }
    public Guid BranchId { get; set; }
    
    // Agent Connectivity
    public bool IsAgentOnline { get; set; }
    public string? ConnectionMode { get; set; }

    /// <summary>The version this PC's own agent last reported on a heartbeat. Null means it has
    /// never reported one - either it has not connected since this field existed, or (for a
    /// console) it has no agent to report one at all.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>The version of AppleEsports.exe itself this PC last reported - the program a
    /// customer actually plays through, separate from the screen-lock agent above. See
    /// Pc.AppVersion for why the two are tracked apart.</summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// True if PcStatusHub's shutdown command was sent to this PC and it has not reconnected
    /// since (see Pc.PoweredOff). Combined on the frontend with State being Active/AwaitingBilling
    /// to tell "shut down, idle" (red) apart from "shut down while a session is still billing"
    /// (orange) - two states that look identical from this field alone.
    /// </summary>
    public bool PoweredOff { get; set; }

    // Active session details (if busy or awaiting billing)
    public Guid? ActiveSessionId { get; set; }
    public Guid? ActiveBillId { get; set; }
    public DateTimeOffset? SessionStartTime { get; set; }
    public DateTimeOffset? SessionEndTime { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerType { get; set; }  // "Walk-in" | "Member"
    public decimal RatePerHour { get; set; }   // For live charge calculation on frontend
    public int BufferMinutes { get; set; }     // Free grace period before billing starts (from PricingProfile)
    public decimal TotalAmount { get; set; }   // Actual total accumulated charge for the session (gaming + food), live for Active sessions
    public int PausedSeconds { get; set; }    // Downtime credited back — the client subtracts this from wall-clock elapsed
    public decimal FoodAmount { get; set; }    // Food/add-on portion of TotalAmount, so the frontend can tick the gaming portion between polls without drifting
    public string? Zone { get; set; }           // Standard / VIP / Console / Streaming
    public string? MonitorHz { get; set; }
    
    // For quickly restarting a recently completed session
    public string? LastCustomerName { get; set; }
    public Guid? LastMemberId { get; set; }

    // Upcoming reservation details (if reserved)
    public Guid? NextReservationId { get; set; }
    public DateTimeOffset? NextReservationTime { get; set; }

    public bool HasOverrunWarning { get; set; }
    public string? OverrunWarningMessage { get; set; }
}
