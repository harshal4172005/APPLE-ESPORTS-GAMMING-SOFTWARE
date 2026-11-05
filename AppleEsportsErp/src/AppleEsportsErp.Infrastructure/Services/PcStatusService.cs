using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AppleEsportsErp.Application.DTOs.PcStatus;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Infrastructure.Services;

public class PcStatusService : IPcStatusService
{
    private readonly AppDbContext _db;
    private readonly IHubNotificationService _hubNotifier;
    private readonly ILogger<PcStatusService> _logger;

    public PcStatusService(AppDbContext db, IHubNotificationService hubNotifier, ILogger<PcStatusService> logger)
    {
        _db = db;
        _hubNotifier = hubNotifier;
        _logger = logger;
    }

    public async Task<IEnumerable<PcStatusDto>> GetBranchPcStatusesAsync(Guid branchId)
    {
        var pcs = await _db.Pcs
            .AsNoTracking()
            .Include(p => p.PricingProfile)
            .Where(p => p.BranchId == branchId && !p.IsDeleted)
            .OrderBy(p => p.PcNumber)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;

        // Fetch active sessions for these PCs
        var activeSessions = (await _db.Sessions
            .AsNoTracking()
            .Include(s => s.Bills)
            .Where(s => s.BranchId == branchId && (s.State == SessionState.Active || s.State == SessionState.AwaitingBilling))
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.StartTime)
            .ToListAsync())
            .GroupBy(s => s.PcId)
            .ToDictionary(g => g.Key, g => g.First());

        // Fetch the most recent completed session for each PC (for quick restart)
        var recentCompletedSessions = (await _db.Sessions
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.State == SessionState.Completed)
            .OrderByDescending(s => s.EndTime)
            .ThenByDescending(s => s.UpdatedAt)
            .ToListAsync())
            .GroupBy(s => s.PcId)
            .ToDictionary(g => g.Key, g => g.First());

        // Fetch pending reservations for these PCs (current + upcoming)
        var upcomingReservations = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.State == ReservationState.Pending)
            .OrderBy(r => r.ReservationTime)
            .ToListAsync();

        var reservationDict = upcomingReservations
            .GroupBy(r => r.PcId)
            .ToDictionary(g => g.Key, g => g.First()); // Get the next immediate reservation

        var result = new List<PcStatusDto>();

        foreach (var pc in pcs)
        {
            decimal calculatedRate = SessionPricingCalculator.DefaultRatePerHour;
            int bufferMinutes = SessionPricingCalculator.DefaultBufferMinutes;
            if (pc.PricingProfile != null)
            {
                calculatedRate = pc.PricingProfile.BaseHourlyRate;
                bufferMinutes = pc.PricingProfile.BufferMinutes;
            }

            var dto = new PcStatusDto
            {
                Id = pc.Id,
                Name = pc.PcNumber,
                IpAddress = pc.IpAddress ?? string.Empty,
                State = pc.State,
                BranchId = pc.BranchId,
                Zone = pc.Zone ?? "Standard",
                MonitorHz = pc.MonitorHz,
                RatePerHour = calculatedRate,
                BufferMinutes = bufferMinutes,
                ShortSessionSlabs = pc.PricingProfile?.ShortSessionSlabs?.Select(s => new AppleEsportsErp.Application.DTOs.Settings.ShortSessionSlabDto { FromMinutes = s.FromMinutes, ToMinutes = s.ToMinutes, Amount = s.Amount }).OrderBy(s => s.FromMinutes).ToList(),
                IsAgentOnline = pc.IsAgentOnline,
                ConnectionMode = pc.ConnectionMode,
                AgentVersion = pc.AgentVersion,
                AppVersion = pc.AppVersion,
                PoweredOff = pc.PoweredOff
            };

            // Whether this PC is holding a customer at all is decided by the PC's own row, never
            // by a Session row found lying next to it.
            //
            // Head Office had a Citylight machine showing a customer for two days. CTL-PC-01's
            // own row said idle and said so correctly - the branch had closed that session and
            // the branch is the only place that can know. What Head Office also held was the
            // Session row from when it started, still marked Active, because a session's close
            // is a separate sync event and that one never arrived. Sync only ever sends changes,
            // so a lost close is lost for good: the row sat there claiming to be live, and the
            // block below trusted it over the PC row and overwrote a correct idle with Active.
            //
            // It was not only a wrong colour on a tile. The live charge is accrued from
            // StartTime to now, so a phantom bills onward at the hourly rate for as long as it
            // is believed - that one was showing about 2,100 rupees of revenue that never existed,
            // inside the branch's headline "live accrued" figure.
            //
            // The two rows cannot disagree at a branch: StartSessionAsync and StopSessionAsync
            // write the Session and the Pc in one unit of work, so requiring them to agree
            // changes nothing there. At Head Office they can disagree, and when they do the PC
            // row is the one to believe - the heartbeat rewrites it every three seconds, while a
            // Session row is only touched when the session itself changes. The fresher row wins.
            var pcIsHoldingSession = pc.State is PcState.Active or PcState.AwaitingBilling;

            Session? session = null;
            if (pcIsHoldingSession && activeSessions.TryGetValue(pc.Id, out session))
            {
                dto.State = session.State == SessionState.Active
                    ? PcState.Active
                    : PcState.AwaitingBilling;
                dto.ActiveSessionId = session.Id;
                dto.CustomerName = session.CustomerName;
                dto.SessionStartTime = session.StartTime;
                dto.CustomerType = session.MemberId.HasValue ? "Member" : "Walk-in";
                var activeBill = session.Bills.FirstOrDefault();
                dto.ActiveBillId = activeBill?.Id;
                dto.FoodAmount = session.FoodAmount;

                if (session.State == SessionState.Active)
                {
                    // Still running — compute the live charge with the exact same formula
                    // StopSessionAsync will use, so this number never diverges from the real bill.
                    // Package-aware: a session running under a fixed package shows that price,
                    // not hours x BaseHourlyRate - see CalculateLiveGamingAmount.
                    decimal elapsedMinutes = SessionTimeCalculator.ElapsedMinutes(
                        session.StartTime, session.PausedSeconds, now);
                    decimal liveGamingAmount = SessionPricingCalculator.CalculateLiveGamingAmount(
                        session.PackagePrice, session.PackageMinutes ?? session.PlannedDurationMin, calculatedRate, bufferMinutes, elapsedMinutes, session.PackageFromMin, pc.PricingProfile?.ShortSessionSlabs);
                    dto.TotalAmount = liveGamingAmount + session.FoodAmount;
                }
                else
                {
                    // Already stopped (Awaiting Billing) — amount is final, just display it.
                    dto.TotalAmount = activeBill?.TotalAmount ?? session.TotalAmount;
                }

                dto.PausedSeconds = session.PausedSeconds;

                if (session.EndTime.HasValue)
                    dto.SessionEndTime = session.EndTime;
            }
            else if (pc.State is PcState.Active or PcState.AwaitingBilling && pc.CurrentSessionId.HasValue)
            {
                // Head Office. There is no local Session row for this PC's session - Session is
                // never synced (only Bill is, once paid) - so this is Head Office's only source
                // for what the branch's own screen already knows directly: everything below
                // comes from the heartbeat snapshot rather than a live session read, refreshed
                // every three seconds the same as State and CurrentSessionId themselves. See
                // Pc.CurrentSessionStartTime for why this exists.
                //
                // ActiveSessionId matters as much as the timing fields do: it is what tells the
                // client this PC's Pay-As-You-Go-or-not is a real, known answer rather than a
                // missing one - see PcTile.jsx's isPayAsYouGo.
                dto.ActiveSessionId = pc.CurrentSessionId;
                dto.SessionStartTime = pc.CurrentSessionStartTime;
                dto.SessionEndTime = pc.CurrentSessionEndTime;
                dto.PausedSeconds = pc.CurrentSessionPausedSeconds;

                if (pc.State == PcState.Active && pc.CurrentSessionStartTime.HasValue)
                {
                    decimal elapsedMinutes = SessionTimeCalculator.ElapsedMinutes(
                        pc.CurrentSessionStartTime.Value, pc.CurrentSessionPausedSeconds, now);
                    // Package-aware from the heartbeat snapshot (Pc.CurrentSessionPackagePrice) -
                    // Head Office has no local Session row to read this from directly.
                    dto.TotalAmount = SessionPricingCalculator.CalculateLiveGamingAmount(
                        pc.CurrentSessionPackagePrice, pc.CurrentSessionPlannedDurationMin,
                        calculatedRate, bufferMinutes, elapsedMinutes, pc.CurrentSessionPackageFromMin, pc.PricingProfile?.ShortSessionSlabs);
                }
            }
            else if (recentCompletedSessions.TryGetValue(pc.Id, out var lastSession) && lastSession != null)
            {
                // If there's no active session, provide the last customer details for a quick restart
                dto.LastCustomerName = lastSession.CustomerName;
                dto.LastMemberId = lastSession.MemberId;
            }

            if (reservationDict.TryGetValue(pc.Id, out var res))
            {
                dto.NextReservationId = res.Id;
                dto.NextReservationTime = res.ReservationTime;
                dto.CustomerName = dto.CustomerName ?? res.CustomerName;

                if (session != null)
                {
                    if (session.EndTime.HasValue)
                    {
                        if (session.EndTime.Value > res.ReservationTime)
                        {
                            dto.HasOverrunWarning = true;
                            dto.OverrunWarningMessage = $"Active session duration extends past reservation time ({res.ReservationTime.DateTime:HH:mm}).";
                        }
                    }
                    else
                    {
                        if (res.ReservationTime <= now.AddMinutes(30))
                        {
                            dto.HasOverrunWarning = true;
                            dto.OverrunWarningMessage = $"Open-ended session might overlap with upcoming reservation starting at {res.ReservationTime.DateTime:HH:mm}.";
                        }
                    }
                }
            }

            result.Add(dto);
        }

        return result;
    }

    public async Task<PcStatusDto> GetPcStatusAsync(Guid pcId)
    {
        var pc = await _db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId);
        if (pc == null)
            throw new NotFoundException("PC not found", "PC_NOT_FOUND");

        var statuses = await GetBranchPcStatusesAsync(pc.BranchId);
        return statuses.First(s => s.Id == pcId);
    }

    public async Task BroadcastPcStatusChangeAsync(Guid branchId, Guid pcId)
    {
        await _hubNotifier.BroadcastPcStatusChangeAsync(branchId, pcId);
        _logger.LogInformation("Broadcasted PC status change for PC {PcId} on Branch {BranchId}", pcId, branchId);
    }
}
