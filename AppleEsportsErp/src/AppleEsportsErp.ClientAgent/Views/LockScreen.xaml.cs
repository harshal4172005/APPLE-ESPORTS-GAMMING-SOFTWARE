using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using System.Windows.Input;

namespace AppleEsportsErp.ClientAgent.Views;

/// <summary>
/// What a session is actually being charged, as sent by SendUnlockCommandToAgentAsync - see
/// the Session Pricing PRD's issue 07. This ClientAgent project deliberately has no reference
/// to the main backend's own AppleEsportsErp.Application project (it is a small, independently
/// updated app; pulling in that whole dependency chain for one formula is a worse trade than a
/// second, small, clearly-labelled copy of it) - so PriceForElapsed below is a deliberate,
/// minimal mirror of SessionPricingCalculator.CalculateLiveGamingAmount. Keep the two in step
/// by hand if that formula ever changes.
/// </summary>
public sealed record SessionPricingInfo(
    decimal? PackagePrice, int? PlannedDurationMin, string? PackageName,
    decimal RatePerHour, int BufferMinutes, DateTimeOffset? SessionStartUtc, int? PackageFromMin = null,
    List<Services.SlabEntry>? ShortSessionSlabs = null)
{
    public static readonly SessionPricingInfo None = new(null, null, null, 0m, 0, null);

    public decimal? PriceForElapsed(decimal elapsedMinutes)
    {
        if (SessionStartUtc is null) return null;
        if (elapsedMinutes <= BufferMinutes) return 0m;

        if (PackagePrice.HasValue && PlannedDurationMin.HasValue)
        {
            decimal overrunMinutes = elapsedMinutes - PlannedDurationMin.Value;
            if (elapsedMinutes < (PackageFromMin ?? PlannedDurationMin.Value - BufferMinutes)) return HourlyOrSlab(elapsedMinutes);
            if (overrunMinutes <= BufferMinutes) return PackagePrice.Value;
            return PackagePrice.Value + Math.Round((overrunMinutes / 60m) * RatePerHour, 2);
        }

        return HourlyOrSlab(elapsedMinutes);
    }

    private decimal HourlyOrSlab(decimal elapsedMinutes)
    {
        if (ShortSessionSlabs is { Count: > 0 })
        {
            var match = ShortSessionSlabs.Where(s => elapsedMinutes > s.FromMinutes && elapsedMinutes <= s.ToMinutes)
                .OrderByDescending(s => s.FromMinutes).FirstOrDefault();
            if (match != null) return match.Amount;
        }
        return Math.Round((elapsedMinutes / 60m) * RatePerHour, 2);
    }
}

/// <summary>
/// Lock Screen code-behind — manages UI state, timer display, and connection status.
/// The actual session control and dual-connection logic lives in the Services.
/// </summary>
public partial class LockScreen : Window
{
    private readonly DispatcherTimer _sessionTimer;
    private readonly Services.SystemLockService _systemLock;
    private readonly Services.DualConnectionService _dualConnection;
    private readonly Services.SessionControlService _sessionControl;
    private int _remainingSeconds = 0;
    private SessionPricingInfo _pricing = SessionPricingInfo.None;

    public LockScreen()
    {
        InitializeComponent();

        // Set PC number from config
        PcNumberText.Text = App.AgentConfig.PcNumber;

        // Read straight from the assembly, never a hand-typed string - see the comment on
        // VersionText in the XAML for what this replaces.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "© Apple Esports ERP — Gaming PC Agent"
            : $"© Apple Esports ERP — Gaming PC Agent v{version.Major}.{version.Minor}.{version.Build}";

        // Start glow animation
        var storyboard = (Storyboard)FindResource("PulseAnimation");
        storyboard.Begin();

        // Initialize services
        _systemLock = new Services.SystemLockService();
        _sessionControl = new Services.SessionControlService(this);
        _dualConnection = new Services.DualConnectionService(this, _sessionControl);

        // Session countdown timer
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionTimer.Tick += SessionTimer_Tick;

        // Lock the system on startup
        _systemLock.EnableLock();

        // Start dual connection
        _ = _dualConnection.StartAsync();

        // Prevent closing via Alt+F4
        Closing += (s, e) => e.Cancel = true;

        // The one way out of a locked machine (Ctrl+Shift+Alt+U), behind the admin PIN.
        KeyDown += LockScreen_KeyDown;
    }

    /// <summary>
    /// The escape hatch. It used to call DisableLock() and Shutdown() the moment the keys were
    /// pressed, with no check of any kind — anyone who knew the combination was out to the
    /// Windows desktop of a machine sitting in front of the public. It now goes through the same
    /// PIN gate as every other way out, and refuses outright when no PIN has been set.
    /// </summary>
    private void LockScreen_KeyDown(object sender, KeyEventArgs e)
    {
        // With Alt held, WPF reports the key as Key.System and puts the real one in SystemKey.
        // Reading e.Key alone means an Alt combination is never recognised — the same class of
        // fault as the counter shell's quit shortcut, which silently did nothing for weeks.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key != Key.U) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        e.Handled = true;

        if (!Services.AdminPinService.Current.RequirePin(this, "quit Apple Esports and unlock this PC"))
            return;

        _systemLock.DisableLock();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Called by SessionControlService when an unlock command is received - including a
    /// mid-session extension re-sending this with updated totals (see
    /// SessionService.ExtendSessionAsync). Safe to call while already unlocked: nothing here
    /// re-shows the window, so an extension never visibly flashes locked-then-unlocked.
    /// </summary>
    public void UnlockPc(int durationMinutes, string? customerName, SessionPricingInfo? pricing = null)
    {
        Dispatcher.Invoke(() =>
        {
            _systemLock.DisableLock();
            _pricing = pricing ?? SessionPricingInfo.None;

            if (durationMinutes > 0)
            {
                _remainingSeconds = durationMinutes * 60;
                TimerText.Visibility = Visibility.Visible;
                UpdatePriceText();
                _sessionTimer.Start();
            }

            // Hide the lock screen (don't close — we need to show it again later)
            Hide();
        });
    }

    /// <summary>Refreshes PriceText from the live elapsed time - ticked alongside the
    /// countdown, same reasoning as the Session Pricing PRD's issue 07: a static number shown
    /// once at unlock goes stale the moment a minute passes.</summary>
    private void UpdatePriceText()
    {
        if (_pricing.SessionStartUtc is null)
        {
            PriceText.Visibility = Visibility.Collapsed;
            return;
        }

        var elapsedMinutes = (decimal)(DateTimeOffset.UtcNow - _pricing.SessionStartUtc.Value).TotalMinutes;
        var amount = _pricing.PriceForElapsed(elapsedMinutes);
        if (amount is null)
        {
            PriceText.Visibility = Visibility.Collapsed;
            return;
        }

        PriceText.Text = _pricing.PackagePrice.HasValue
            ? $"{_pricing.PackageName ?? "Package"} — ₹{amount:0.##}"
            : $"₹{amount:0.##} so far (₹{_pricing.RatePerHour:0.##}/hr)";
        PriceText.Visibility = Visibility.Visible;
    }

    /// <summary>Called by SessionControlService when a lock command is received</summary>
    public void LockPc()
    {
        Dispatcher.Invoke(() =>
        {
            _sessionTimer.Stop();
            TimerText.Visibility = Visibility.Collapsed;
            PriceText.Visibility = Visibility.Collapsed;
            _remainingSeconds = 0;
            _pricing = SessionPricingInfo.None;

            _systemLock.EnableLock();
            Show();
            Activate();
            Topmost = true;
        });
    }

    /// <summary>Update the connection status indicator</summary>
    public void UpdateConnectionStatus(string mode, bool isConnected)
    {
        Dispatcher.Invoke(() =>
        {
            if (isConnected)
            {
                StatusDot.Fill = mode == "LAN" 
                    ? new SolidColorBrush(Color.FromRgb(0, 255, 136))   // Green for LAN
                    : new SolidColorBrush(Color.FromRgb(255, 165, 0));  // Orange for Cloud
                StatusText.Text = mode == "LAN" 
                    ? "Connected — LAN Mode" 
                    : "Connected — ☁️ Cloud Mode (Operator offline)";
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 50, 50)); // Red
                StatusText.Text = "Disconnected — Attempting to reconnect...";
            }
        });
    }

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            // Time's up — auto-lock
            _sessionTimer.Stop();
            LockPc();
            _ = _dualConnection.NotifySessionExpired();
            return;
        }

        // Update timer display
        var hours = _remainingSeconds / 3600;
        var minutes = (_remainingSeconds % 3600) / 60;
        var seconds = _remainingSeconds % 60;

        TimerText.Text = hours > 0 
            ? $"{hours:D2}:{minutes:D2}:{seconds:D2}" 
            : $"{minutes:D2}:{seconds:D2}";

        // Flash timer red when less than 5 minutes remain
        if (_remainingSeconds <= 300)
        {
            TimerText.Foreground = _remainingSeconds % 2 == 0
                ? new SolidColorBrush(Color.FromRgb(255, 50, 50))
                : new SolidColorBrush(Color.FromRgb(255, 215, 0));
        }

        UpdatePriceText();
    }
}
