using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Sessions;

namespace AppleEsportsErp.Application.Interfaces;

public interface ISessionService
{
    Task<PaginatedResult<SessionDto>> GetActiveSessionsAsync(Guid branchId, int page, int pageSize);
    Task<SessionDto> StartSessionAsync(Guid branchId, Guid operatorId, Guid shiftId, SessionStartDto dto);
    /// <summary>
    /// <paramref name="asOfOverride"/>: bills up to this moment instead of the real current
    /// time. Used only by the stale-session watchdog, which may run hours after a PC's agent
    /// actually went dark - billing "now" there would charge the customer for time nobody
    /// could have played. Every human-triggered stop leaves this null and bills to the actual
    /// current time, same as always.
    /// </summary>
    Task<SessionDto> StopSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, bool deferPayment = false, DateTimeOffset? asOfOverride = null);

    /// <summary>
    /// Puts a session held after an outage back to Active, with the customer's unused paid
    /// time intact. The wait between the power returning and this call is credited back too,
    /// so a customer is never billed for the minutes an operator spent finding them.
    /// </summary>
    Task<SessionDto> ResumeSessionAsync(Guid branchId, Guid operatorId, Guid sessionId);
    Task<SessionDto> ExtendSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, SessionExtendDto dto);
    /// <summary>Every package this running session could become now, with price, time left and why not.</summary>
    Task<PackageOptionsDto> GetPackageOptionsAsync(Guid branchId, Guid sessionId);
    /// <summary>The session becomes that package from its own start: its price, its length.</summary>
    Task<SessionDto> ChangePackageAsync(Guid branchId, Guid operatorId, Guid sessionId, Guid packageId);
    Task<SessionDto> TransferSessionAsync(Guid branchId, Guid operatorId, Guid sessionId, SessionTransferDto dto);
}
