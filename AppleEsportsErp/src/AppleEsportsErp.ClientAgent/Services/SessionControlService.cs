using System.Diagnostics;
using System.Text.Json;

namespace AppleEsportsErp.ClientAgent.Services;

/// <summary>
/// Handles incoming commands (Unlock, Lock, Shutdown) from the SignalR hub.
/// Delegates UI updates to the LockScreen.
/// </summary>
public class SessionControlService
{
    private readonly Views.LockScreen _lockScreen;

    public SessionControlService(Views.LockScreen lockScreen)
    {
        _lockScreen = lockScreen;
    }

    /// <summary>Handle an unlock command from Operator or Admin</summary>
    public void HandleUnlock(object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            var command = JsonSerializer.Deserialize<UnlockCommand>(json, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            var duration = command?.DurationMinutes ?? 60;
            var customerName = command?.CustomerName;

            _lockScreen.UnlockPc(duration, customerName, new Views.SessionPricingInfo(
                command?.PackagePrice, command?.PlannedDurationMin, command?.PackageName,
                command?.RatePerHour ?? 0m, command?.BufferMinutes ?? 0, command?.SessionStartUtc,
                command?.PackageFromMin, command?.ShortSessionSlabs));
        }
        catch
        {
            // Fallback — unlock with default 60 minutes
            _lockScreen.UnlockPc(60, null, Views.SessionPricingInfo.None);
        }
    }

    /// <summary>Handle a lock command from Operator or Admin</summary>
    public void HandleLock()
    {
        _lockScreen.LockPc();
    }

    /// <summary>Handle a force shutdown command from Admin</summary>
    public void HandleShutdown()
    {
        // First lock the screen
        _lockScreen.LockPc();

        // Then initiate Windows shutdown
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/s /t 10 /c \"Apple Esports: Remote shutdown initiated by Admin.\"",
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }
        catch
        {
            // If shutdown fails, at least the screen is locked
        }
    }
}

internal class UnlockCommand
{
    public int DurationMinutes { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>The session's own committed package price/duration, if it has one - null
    /// means genuine Pay-As-You-Go, priced from RatePerHour/BufferMinutes instead. See the
    /// Session Pricing PRD's issue 07 - this PC used to show a countdown and nothing else.</summary>
    public decimal? PackagePrice { get; set; }
    public int? PlannedDurationMin { get; set; }
    public int? PackageFromMin { get; set; }
    public string? PackageName { get; set; }
    public decimal RatePerHour { get; set; }
    public int BufferMinutes { get; set; }

    /// <summary>Lets LockScreen tick the live amount itself between pushes, the same way it
    /// already ticks the countdown, instead of showing a number frozen at send time.</summary>
    public DateTimeOffset? SessionStartUtc { get; set; }
    public List<SlabEntry>? ShortSessionSlabs { get; set; }
}

public class SlabEntry
{
    public int FromMinutes { get; set; }
    public int ToMinutes { get; set; }
    public decimal Amount { get; set; }
}
