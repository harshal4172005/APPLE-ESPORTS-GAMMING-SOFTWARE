using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using System.Drawing.Imaging;

namespace AppleEsports.Desktop;

public sealed class MainForm : Form
{
    // Palette lifted from the web dashboard's dark theme so the shell doesn't flash white.
    private static readonly Color Backdrop = Color.FromArgb(10, 10, 15);
    private static readonly Color Foreground = Color.FromArgb(230, 230, 234);
    private static readonly Color Muted = Color.FromArgb(140, 140, 155);
    private static readonly Color Accent = Color.FromArgb(200, 30, 40);

    private readonly AppConfig _config;
    private readonly WebView2 _web = new();

    private readonly Panel _overlay = new();
    private readonly Label _overlayTitle = new();
    private readonly Label _overlayMessage = new();
    private readonly Button _retryButton = new();
    private readonly Button _settingsButton = new();

    /// <summary>
    /// Fallback sizes for the small floating widget shown while a customer PC has an active
    /// session, in the CSS pixels the page is laid out in. The page normally sends its own —
    /// see ApplyOverlayLayout — and these are only used for a branch still serving a dashboard
    /// older than this shell. Keep them in step with OVERLAY_SIZES in UserOverlayApp.jsx.
    /// </summary>
    private const int BubbleSize = 60;
    private const int PanelWidth = 380;
    private const int PanelHeight = 620;
    private const int FloatMargin = 24;

    /// <summary>Corner radius the page draws on the expanded panel (Tailwind's rounded-2xl), matched by the window's own shape.</summary>
    private const int PanelCornerRadius = 16;

    /// <summary>
    /// CSS pixels to device pixels for the page currently loaded, as last reported by it.
    /// Everything sized to match that page — the widget, its margin, its corner radius, the
    /// drag handle — goes through this rather than the window's DPI, which is a different
    /// number: see the layoutMode effect in UserOverlayApp.jsx.
    /// </summary>
    private double _overlayScale = 1;

    private int Scaled(int design) => (int)Math.Round(design * _overlayScale);

    /// <summary>Whether this window is currently the small floating widget (bubble or panel) rather than full screen — only true while "overlay-drag" messages should move it.</summary>
    private bool _floatDraggable;

    /// <summary>The last "overlay-layout" mode applied, so repeated identical messages are a no-op instead of re-snapping the window on every silent refetch.</summary>
    private string? _overlayMode;
    private LockGuard? _lockGuardInstance;
    private LockGuard _lockGuard => _lockGuardInstance ??= new LockGuard(this);

    private bool _isFullScreen;
    private FormBorderStyle _borderBeforeFullScreen;
    private FormWindowState _stateBeforeFullScreen;

    /// <summary>Set only by the PIN-protected exit path, so a locked PC can still be closed.</summary>
    private bool _allowClose;

    /// <summary>
    /// How long a failure to reach the branch is treated as "still starting" rather than
    /// reported as a fault.
    ///
    /// On the counter PC the branch API is a Windows service on this same machine, and at
    /// boot Windows starts it and this app at once — the service has a database to bring up
    /// and migrations to apply, so the dashboard reliably gets there first. Without this the
    /// shop would meet an error screen every single morning and learn to click through it,
    /// which is exactly how a real fault later goes unnoticed.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The same, while an update is being installed.
    ///
    /// Two minutes is right for an ordinary morning start. It is nowhere near enough for an
    /// upgrade: the installer stops the API and PostgreSQL, replaces 300 MB, brings the
    /// database back up and applies migrations. The old grace ran out somewhere in the middle
    /// of that and the operator got "Cannot reach the branch system" with a list of things to
    /// check, none of which were wrong - the software blaming the shop for something it was
    /// doing to itself.
    ///
    /// Twenty minutes covers a slow disk and a large migration with room to spare, and it only
    /// applies when an update is genuinely under way. A real fault at any other time is still
    /// reported in two minutes, which is the whole reason not to simply raise the grace for
    /// everything.
    /// </summary>
    private static readonly TimeSpan UpdateGrace = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Written the moment before the installer is launched, and deleted the moment the
    /// dashboard loads again.
    ///
    /// It has to be a file. The app deliberately exits so the installer can replace its own
    /// executable, so nothing held in memory survives to tell the next launch why it could
    /// not connect. Kept beside the config rather than in the program folder, because the
    /// program folder is exactly what is being overwritten.
    /// </summary>
    private static string UpdateMarkerPath =>
        Path.Combine(AppConfig.AppDataDirectory, "installing-update.txt");

    /// <summary>The version being installed, when one is. Null the rest of the time.</summary>
    private static string? UpdateInProgress()
    {
        try
        {
            if (!File.Exists(UpdateMarkerPath)) return null;

            // A stale marker must not excuse a genuine outage for ever. If an install was
            // interrupted - power cut mid-upgrade - the shop is back to reporting faults
            // normally within the hour rather than sitting on a reassuring message that has
            // stopped being true.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdateMarkerPath) > TimeSpan.FromHours(1))
            {
                ClearUpdateMarker();
                return null;
            }

            var version = File.ReadAllText(UpdateMarkerPath).Trim();
            return string.IsNullOrWhiteSpace(version) ? "a new version" : version;
        }
        catch
        {
            return null;   // unreadable marker is not a reason to fail to start
        }
    }

    private static void MarkUpdateStarting(string version)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.AppDataDirectory);
            File.WriteAllText(UpdateMarkerPath, version);
        }
        catch { /* best effort - the update still proceeds, the message is just less specific */ }
    }

    private static void ClearUpdateMarker()
    {
        try { if (File.Exists(UpdateMarkerPath)) File.Delete(UpdateMarkerPath); } catch { }
    }

    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 3000 };
    private DateTime? _firstFailureAt;

    public MainForm(AppConfig config)
    {
        _config = config;

        // A real, legitimate reason this process is about to end that has nothing to do with
        // the operator's own Ctrl+Alt+Q: Windows itself shutting down, restarting, or the
        // kiosk account logging off. Without this, restarting the PC for routine maintenance
        // (or any ordinary reboot) would look identical to a Task Manager kill to
        // restart-on-kill.ps1 and needlessly restart it a second time. Windows gives an app a
        // real, if short, window to respond to this before forcing it closed - long enough to
        // write one small file.
        SystemEvents.SessionEnding += (_, _) => WriteIntentionalExitMarker();

        // Says where this window is pointed from the moment it opens. Previously this was
        // only filled in once a page finished loading, so a PC that could not reach its
        // branch — exactly when someone needs to know what it is trying to talk to — showed
        // a bare title and gave them nothing to go on.
        Text = $"Apple Esports ERP  —  {HostLabel()}";
        BackColor = Backdrop;

        // Use the icon baked into this executable, so the window, taskbar and
        // Alt-Tab entry all show the same Apple Esports logo as the file itself.
        try
        {
            if (Environment.ProcessPath is { } exePath)
                Icon = Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            // Cosmetic only — a missing icon must not stop the app.
        }

        // Both kinds of PC run with no title bar and no close button - an operator clicking
        // the corner X mid-shift was closing the whole counter by accident, which is exactly
        // the "goes some where else" this replaces. The one real difference between the two
        // roles is what it costs to get out: Ctrl+Alt+Q always works (see ProcessCmdKey),
        // asking for the admin PIN only when one has actually been configured - the same rule
        // that already applied to this shortcut, now the only door on either kind of machine
        // instead of one of several. Deliberate day-to-day exit is End Shift, inside the
        // dashboard itself; this shortcut is for actually quitting the program.
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;

        if (_config.IsUserPc)
        {
            // This window used to be a permanently-docked 380px right-hand panel, full screen
            // height, whether or not anyone was actually playing — which left a customer with
            // no real desktop to play on even while a session was live, and an opaque strip
            // blocking the screen even on the walk-in/member gate before any session existed.
            //
            // The actual size now tracks what the page itself is showing, driven live by the
            // "overlay-layout" bridge message (see WebMessageReceived / ApplyOverlayLayout):
            // full screen for the walk-in/member gate and the locked "session ended" screen —
            // both render themselves fixed inset-0, so they need the window to actually be the
            // screen — and just enough to cover the small floating widget once a session is
            // live, freeing the rest of the screen for the customer to actually use. Full
            // screen here is only the safe starting point before that first message arrives.
            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen;

            // Always-on-top is deliberately NOT set here. It is earned by the page, in
            // ApplyOverlayLayout, the moment it first says what size it wants to be.
            //
            // Setting it up front assumed the page would always answer. A branch whose dashboard
            // is older than this shell does not send that message at all - the bridge does not
            // exist in it - so this window kept the full-screen bounds above and pinned them
            // over everything, permanently, with no way past it. That is a locked-up PC, and it
            // is a worse failure than the one it came from: the older shell never set TopMost,
            // so its full-screen window could at least be alt-tabbed past. A mismatch between
            // the two halves of a release has to degrade into something a shop can still work
            // around, never into a machine nobody can use.

            // Deliberately no MinimumSize. ApplyOverlayLayout is the only thing that sizes this
            // window, it works in the page's pixels rather than this form's, and WinForms scales
            // a MinimumSize set here by the window's DPI - a third unit in a place that already
            // has two. A floor expressed in the wrong one can only ever fight the widget.
        }
        else
        {
            MinimumSize = new Size(1024, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1440, 900);
            ShowInTaskbar = true;
            WindowState = FormWindowState.Maximized;
        }

        BuildOverlay();

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Backdrop;
        _web.Visible = false;

        Controls.Add(_web);
        Controls.Add(_overlay);
        _overlay.BringToFront();

        Load += OnLoadAsync;
    }

    // ── UI ────────────────────────────────────────────────────────────────

    private void BuildOverlay()
    {
        _overlay.Dock = DockStyle.Fill;
        _overlay.BackColor = Backdrop;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Backdrop,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,   // centres the block inside its cell
            WrapContents = false,
            BackColor = Backdrop,
        };

        _overlayTitle.Text = "APPLE ESPORTS";
        _overlayTitle.Font = new Font("Segoe UI", 22F, FontStyle.Bold);
        _overlayTitle.ForeColor = Accent;
        _overlayTitle.AutoSize = true;
        _overlayTitle.Margin = new Padding(0, 0, 0, 10);

        _overlayMessage.Font = new Font("Segoe UI", 11F);
        _overlayMessage.ForeColor = Muted;
        _overlayMessage.AutoSize = true;
        _overlayMessage.MaximumSize = new Size(560, 0);
        _overlayMessage.Margin = new Padding(0, 0, 0, 22);

        StyleButton(_retryButton, "Try again", Accent, Color.White);
        _retryButton.Visible = false;
        _retryButton.Click += (_, _) => Connect();

        StyleButton(_settingsButton, "Change server…", Color.FromArgb(38, 38, 48), Foreground);
        _settingsButton.Visible = false;
        _settingsButton.Click += (_, _) => OpenSettings();

        stack.Controls.Add(_overlayTitle);
        stack.Controls.Add(_overlayMessage);
        stack.Controls.Add(_retryButton);
        stack.Controls.Add(_settingsButton);

        layout.Controls.Add(stack, 0, 1);
        _overlay.Controls.Add(layout);
    }

    private static void StyleButton(Button button, string text, Color back, Color fore)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(220, 42);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 0, 10);
    }

    private void ShowOverlay(string message, bool showActions)
    {
        _overlayMessage.Text = message;
        _retryButton.Visible = showActions;
        _settingsButton.Visible = showActions;
        _overlay.Visible = true;
        _overlay.BringToFront();
        _web.Visible = false;
    }

    private void HideOverlay()
    {
        _overlay.Visible = false;
        _web.Visible = true;
        _web.BringToFront();
        _web.Focus();
    }

    // ── Startup / navigation ──────────────────────────────────────────────

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        ShowOverlay("Starting…", showActions: false);

        try
        {
            // WebView2 needs a writable profile folder — Program Files is not writable,
            // which is exactly the kind of thing that makes an installed app die on launch.
            Directory.CreateDirectory(AppConfig.WebViewDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppConfig.WebViewDataDirectory);

            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            ShowOverlay($"Could not start the embedded browser.\n\n{ex.Message}", showActions: true);
            return;
        }

        var core = _web.CoreWebView2;

        // Ctrl+Alt+Q has to work regardless of where keyboard focus is, and focus is on the
        // dashboard - inside WebView2 - almost the entire time this app is in use. This SDK's
        // WinForms wrapper keeps its CoreWebView2Controller private, so there is no supported
        // way to intercept the key at the .NET host level while WebView2 has focus; the
        // documented fix (Controller.AcceleratorKeyPressed) simply is not reachable here.
        // Caught in the page itself instead - the same postMessage bridge WebMessageReceived
        // below already trusts for "check-for-updates" - and run on every document, since the
        // dashboard is a client-side router that never reloads the outer page as it navigates.
        // e.code, not e.key: Ctrl+Alt is the AltGr modifier on plenty of real keyboard
        // layouts (anything but plain US English), and when that fires the browser can
        // report e.key as some other character entirely, or a dead-key marker, even though
        // the physical Q key was the one pressed - confirmed live, the shortcut did nothing
        // on exactly this. e.code reports the physical key position and ignores what
        // character the current layout says it produces, so it is unaffected either way.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.addEventListener('keydown', function(e) {" +
            "  if (e.ctrlKey && e.altKey && !e.shiftKey && !e.repeat && e.code === 'KeyQ') {" +
            "    e.preventDefault();" +
            "    window.chrome.webview.postMessage('close-app');" +
            "  }" +
            "}, true);");

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        // Answer the nginx Basic Auth gate in front of the dashboard automatically,
        // so operators are not asked for a second password every launch.
        core.BasicAuthenticationRequested += (_, args) =>
        {
            if (string.IsNullOrEmpty(_config.GateUsername)) return;
            args.Response.UserName = _config.GateUsername;
            args.Response.Password = _config.GatePassword;
        };

        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _firstFailureAt = null;

                // The dashboard loaded, so whatever was being installed has finished and the
                // branch is back. Clearing it here rather than on launch means an update that
                // needs a second restart still gets the patient message.
                ClearUpdateMarker();

                HideOverlay();
                return;
            }

            _firstFailureAt ??= DateTime.UtcNow;

            // An update in progress is not a fault, and must never be dressed up as one. The
            // installer has deliberately stopped the branch to replace it; saying "Cannot
            // reach the branch system" and asking the operator to check the network is both
            // untrue and alarming, and it teaches them to click through error screens.
            var installing = UpdateInProgress();
            var grace = installing is null ? StartupGrace : UpdateGrace;
            var remaining = grace - (DateTime.UtcNow - _firstFailureAt.Value);

            if (remaining > TimeSpan.Zero)
            {
                // No buttons while this is running: there is nothing for anyone to do, and
                // offering "Change server..." to an operator whose branch is merely still
                // booting invites them to break a correct setting.
                ShowOverlay(
                    installing is null
                        ? $"Starting the branch system…  ({(int)remaining.TotalSeconds}s)"
                        : $"Installing update {installing}.\n\n" +
                          "This takes a few minutes and finishes on its own. Nothing is wrong, " +
                          "and there is nothing you need to do — the screen comes back by itself.\n\n" +
                          "Please do not switch this PC off.",
                    showActions: false);

                _retryTimer.Start();
                return;
            }

            // Past even the update grace. Say so plainly rather than silently reverting to a
            // network error, because "the update did not finish" is a different problem with a
            // different answer, and the person reading this needs to know which one they have.
            if (installing is not null)
            {
                ClearUpdateMarker();
                ShowOverlay(
                    $"The update to {installing} has not finished, and it is taking longer than it should.\n\n" +
                    "Your takings and records are safe — an update never touches them. " +
                    "Tell the owner, and try switching this PC off and on again.",
                    showActions: true);
                return;
            }

            ShowOverlay(DescribeError(args.WebErrorStatus), showActions: true);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            Text = string.IsNullOrWhiteSpace(title)
                ? $"Apple Esports ERP  —  {HostLabel()}"
                : $"{title}  —  {HostLabel()}";
        };

        core.ProcessFailed += (_, _) =>
            ShowOverlay("The embedded browser stopped responding.", showActions: true);

        // What the dashboard is allowed to ask of the machine it runs on.
        //
        // The Updates page can see that a newer version exists - it asks Head Office directly -
        // but only this process can install one. Without a way across, the best that page could
        // ever offer was "it will happen on its own eventually", which is no use to somebody
        // standing at a counter wondering whether updates work at all.
        //
        // "close-app" is the other side of the injected keydown listener above - Ctrl+Alt+Q
        // caught in the page, carried back here the same way, because the WinForms host
        // cannot see the key press directly while WebView2 has focus.
        //
        // Deliberately not a general bridge. Both are fixed words with no parameters: the page
        // cannot name a file, a version or a URL, so the worst a compromised dashboard achieves
        // here is asking Head Office for the update it was going to take anyway, or triggering
        // the same PIN-gated close a keypress would have.
        core.WebMessageReceived += (_, args) =>
        {
            string message;
            try { message = args.TryGetWebMessageAsString() ?? string.Empty; }
            catch { return; }   // not a string - not ours

            if (string.Equals(message, "close-app", StringComparison.Ordinal))
            {
                if (Unlocked("close Apple Esports")) ForceExit();
                return;
            }

            if (string.Equals(message, "session-ended", StringComparison.Ordinal))
            {
                if (_config.IsUserPc) RunEndSessionCleanup();
                return;
            }

            if (string.Equals(message, "check-for-updates", StringComparison.Ordinal))
            {
                // Wakes the loop rather than starting a second check, so pressing the button
                // twice cannot have two downloads writing the same file.
                try { _checkNow.Cancel(); } catch { /* already awake */ }
                return;
            }

            // Posted by OverlaySocketContext.jsx on PcStatusHub's ShutdownPc - the operator's
            // "Shut Down" button, carried over the same live connection this PC already holds
            // for its lock screen and session overlay. Only meaningful on a gaming PC; a counter
            // PC ignores it. Fixed-word, no parameter, same as close-app above.
            if (string.Equals(message, "shutdown-pc", StringComparison.Ordinal))
            {
                if (_config.IsUserPc) ShutdownMachine();
                return;
            }

            // Everything else is the JSON overlay-layout/overlay-drag bridge UserOverlayApp.jsx
            // posts on every mode change and every drag movement — only meaningful, and only
            // handled, on a customer gaming PC.
            if (_config.IsUserPc) HandleOverlayLayoutMessage(message);
        };

        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            Connect();
        };

        Connect();

        // Fire and forget. An update check must never delay the dashboard appearing — the
        // shop opens whether or not Head Office is reachable.
        _ = Task.Run(UpdateLoopAsync);

        // Same reasoning, and off the UI thread for the same reason: registering the startup
        // entry and the watchdog task shells out to schtasks, and none of it is worth making the
        // shop wait behind. Done on every launch rather than only at install time so it repairs
        // itself - a Run key removed by a customer, by cleanup software, or by somebody tidying
        // msconfig would otherwise leave the PC silently unprotected until it was reinstalled.
        _ = Task.Run(() => KioskGuard.EnsureRegistered(_config.IsUserPc));
    }

    /// <summary>
    /// Checks Head Office for an approved update, then keeps checking every few minutes.
    ///
    /// Runs for the life of the app rather than only at launch: a branch PC can stay on for
    /// days, and a fix nobody restarts to collect is a fix nobody has.
    ///
    /// This used to wait four hours between looks, which made every release feel broken. A
    /// version published at nine in the evening would not be noticed until the small hours,
    /// and in the meantime the branch sat on the old build with no sign that anything was
    /// coming - indistinguishable, from the counter, from updates not working at all. During
    /// development that is worse still: a fix goes out, the branch does not take it, and the
    /// next hour goes on hunting a bug that was already fixed.
    ///
    /// Thirty seconds instead, so a fix published at Head Office is on its way to every shop
    /// before anyone thinks to ask whether it worked.
    ///
    /// Frugality was the wrong thing to optimise for. A check is one request answered with a
    /// version number and a hash - a few hundred bytes, far less than a single page of the
    /// dashboard. The branch already reports its own status to Head Office every three
    /// seconds, so this adds about a tenth to a conversation that is happening anyway. The
    /// 172 MB download still only happens when there is genuinely something new.
    /// </summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after the app opens before the first look.
    ///
    /// This is the one that matters for a shop that was closed. A counter PC switched on in
    /// the morning should collect whatever was published overnight straight away, while the
    /// operator is still logging in - not two minutes later, in the middle of their first
    /// customer. Ten seconds is enough for the dashboard to finish loading first.
    /// </summary>
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(10);

    private async Task UpdateLoopAsync()
    {
        // Let the dashboard finish loading first, so the two are not competing for a branch
        // connection at the one moment somebody is watching the screen.
        await Task.Delay(FirstCheckAfter);

        while (!IsDisposed)
        {
            try
            {
                await CheckForUpdateOnceAsync();
            }
            catch
            {
                // Never let an update check take the app down with it.
            }

            // Piggybacked on the same loop rather than a second timer - this app already wakes
            // up every 30 seconds for the update check, and reporting "what am I running" costs
            // nothing extra to ask at the same moment.
            await ReportOwnAppVersionOnceAsync();

            // Woken early when somebody presses "Check for updates now", so the button is a
            // real check rather than a message saying one will happen eventually.
            try { await Task.Delay(CheckEvery, _checkNow.Token); }
            catch (OperationCanceledException) { /* asked to look now */ }

            if (_checkNow.IsCancellationRequested)
            {
                var old = _checkNow;
                _checkNow = new CancellationTokenSource();
                old.Dispose();
            }
        }
    }

    /// <summary>
    /// Cancelled to cut the wait short when the Updates page asks for a check right now.
    /// Replaced each time, because a CancellationTokenSource cannot be un-cancelled.
    /// </summary>
    private CancellationTokenSource _checkNow = new();

    private async Task CheckForUpdateOnceAsync()
    {
        using var updates = new UpdateService(_config);

        var available = await updates.CheckAsync();
        if (available is null) return;

        // Left to the SYSTEM task when there is one, and there is on every 3.1.1 machine onward.
        //
        // Not a fallback or a race - this path physically cannot finish the job. Installing needs
        // elevation, this process does not have it, so Install() asks Windows and Windows puts up a
        // prompt. On a gaming PC that prompt lands in front of a customer, over their game, asking
        // for an administrator password they do not have; on a counter PC at 3am nobody sees it at
        // all. Either way the update does not happen, and Process.Start only reports a failure when
        // a prompt is actively refused, so an ignored one looks exactly like nothing to install.
        // That is how gaming PCs sat four releases behind with no error anywhere.
        //
        // apply-update.ps1 runs as SYSTEM, which is already privileged and has no desktop to draw a
        // prompt on, so it just installs. Standing down here also stops a pointless UAC box
        // appearing over a customer's game every fifteen minutes.
        if (KioskGuard.AutoUpdateTaskInstalled()) return;

        // Reversed back, on the owner's later and equally explicit instruction: an update must
        // never land under somebody mid-session, on a gaming PC or the counter. The version of
        // this comment that used to be here removed exactly this wait, reasoning that the
        // restart was cheap enough not to matter - in practice it was not: a gaming PC came back
        // needing its session manually resumed rather than picking up where it left off, which
        // is the disruption this exists to prevent. IsSessionRunningAsync already existed for
        // this - built, and never actually called from anywhere - so this is wiring in a check
        // that was always meant to be here.
        //
        // Skipping rather than waiting: this method already runs on CheckForUpdateOnceAsync's
        // own timer (see wherever that is scheduled), so a busy machine simply tries again next
        // time round rather than blocking here.
        if (await IsSessionRunningAsync())
        {
            await ReportUpdateProgressAsync(
                "waiting", 0,
                $"Version {available.Version} is available but a session is active here right now. " +
                "Will install once it ends.");
            return;
        }

        // On a gaming PC it costs almost nothing. The app is replaced and restarted, so the
        // overlay goes and comes back; the session is a row in the branch database and the
        // installer never touches it, so the customer's time and money are safe either way.
        //
        // This process cannot install anything itself any more - see the comment on the
        // AutoUpdateTaskInstalled() check above. That used to fall through to here instead,
        // launching the installer with Verb=runas so Windows could raise an elevation prompt
        // this process has no way to answer. On a gaming PC that prompt appeared over a
        // customer's game, asking for an administrator password they do not have - and because
        // this whole method re-runs every 30 seconds, a machine where AutoUpdateTaskInstalled()
        // was wrongly answering "no" (not "sometimes", every single time, on one specific PC)
        // asked the same unanswerable question again every 30 seconds, forever - which is what
        // "I clicked Yes eight times and it kept coming back" actually was: not eight separate
        // failures, one broken detection repeating on a fixed clock.
        //
        // There is nothing left for this process to fall back to, and there should not be. The
        // SYSTEM task is the only supported way to install an update now, and KioskGuard already
        // re-registers it - correctly, idempotently - on every single launch, so a wrong "not
        // installed" answer here repairs itself the next time the app starts rather than needing
        // a customer to approve a prompt that was never going to end the loop anyway. All this
        // path can safely do is say so.
        await ReportUpdateProgressAsync(
            "waiting", 0,
            $"Version {available.Version} is ready, but the automatic updater on this PC is not " +
            "registered correctly yet. It repairs itself the next time this app starts - restarting " +
            "the PC is usually enough. No prompt will appear, and nothing needs approving here.");
    }

    /// <summary>
    /// The percent figure the Updates page's progress bar draws, throttled hard.
    ///
    /// A 164 MB download raises progress thousands of times. Forwarding each one would put
    /// thousands of HTTP calls behind a single update and make the reporting cost more than the
    /// download. Five-percent steps are as much resolution as a progress bar can show anyway.
    /// </summary>
    private IProgress<int> DownloadProgressReporter()
    {
        var lastReported = -1;

        return new Progress<int>(percent =>
        {
            if (percent < lastReported + 5 && percent < 100) return;
            lastReported = percent;
            _ = ReportUpdateProgressAsync("downloading", percent, null);
        });
    }

    /// <summary>
    /// Tells whoever is watching the Updates page what this branch is doing about an update.
    ///
    /// This existed on paper and nowhere else. The database columns, the API endpoint, the DTO
    /// and the whole Updates page - progress bar, stage badge, message, even a "this has not
    /// moved, it may have stopped" warning - were all built and shipped, and nothing ever wrote
    /// to them. VersionController's own comment said so plainly: "Nothing calls this yet." So a
    /// Super Admin asking why a branch had not updated got a blank cell, and the honest answer -
    /// "it is waiting for two customers to finish playing" - was only discoverable by reading
    /// that branch's database directly. The guard in CheckForUpdateOnceAsync even promised this
    /// visibility in its own comment as the reason waiting was acceptable.
    ///
    /// Sent to this branch's OWN API rather than straight to Head Office, for two reasons. The
    /// branch API is the only thing here that knows Head Office's address (Sync:HeadOfficeUrl);
    /// the desktop client has never been told it and adding it to config.json would leave every
    /// already-installed branch unable to report until somebody re-ran setup. And writing locally
    /// first means the branch's own Updates page is right with no internet at all, which is the
    /// same reason BranchVersionReporterService writes locally before it reports upward.
    ///
    /// Never throws and never blocks anything real. A branch that cannot say what it is doing
    /// must still do it - failing to report an install is not a reason to skip the install.
    /// </summary>
    private async Task ReportUpdateProgressAsync(string stage, int percent, string? message)
    {
        if (string.IsNullOrWhiteSpace(_config.BranchId)) return;   // not adopted yet

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                stage,
                progressPercent = percent,
                message,
            });

            await http.PostAsync(
                $"{_config.NormalisedUrl()}/api/versions/branch/{_config.BranchId}/progress",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Reporting is not the job. The update is.
        }
    }

    /// <summary>
    /// Tells the branch what version of AppleEsports.exe this gaming PC is actually running, so
    /// the Updates page's "N of M gaming PCs up to date" is judged against the program a
    /// customer plays through rather than the separate screen-lock agent
    /// (see Pc.AppVersion/BranchVersionReporterService for why those two used to disagree).
    ///
    /// Only a gaming PC has a single Pc row of its own to report against - an operator counter
    /// PC is the branch itself, already covered by BranchVersionReporterService, and has no
    /// PcId to report one of many seats against.
    /// </summary>
    private async Task ReportOwnAppVersionOnceAsync()
    {
        if (!_config.IsUserPc || _config.PcId is not { } pcId) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = UpdateService.InstalledVersion.ToString(3),
            });

            await http.PostAsync(
                $"{_config.NormalisedUrl()}/api/public/pcs/{pcId}/app-version",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Best-effort telemetry - never worth taking the app down over.
        }
    }

    /// <summary>
    /// Whether anyone is playing right now, from the point of view of whatever this machine is.
    /// Asked of the page itself rather than guessed, and a failed answer counts as "yes" —
    /// interrupting a paying customer is far worse than postponing an update by half a minute.
    ///
    /// Two attributes, because the two roles have different questions. A gaming PC asks about
    /// its own seat (data-session-active, set by UserOverlayApp); a counter PC asks about the
    /// whole branch (data-sessions-active, set by BranchBusySignal in the dashboard's AppShell),
    /// since its own upgrade stops the database and API every seat depends on. Either one being
    /// present means wait.
    /// </summary>
    private async Task<bool> IsSessionRunningAsync()
    {
        try
        {
            var result = await _web.CoreWebView2!.ExecuteScriptAsync(
                "(function(){try{return !!document.querySelector(" +
                "'[data-session-active=\"true\"],[data-sessions-active=\"true\"]');}catch(e){return true;}})()");
            return result?.Trim() != "false";
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Where this window should actually be pointed: the operator dashboard, or - for a
    /// "customer gaming PC" role that has genuinely been provisioned - the locked customer
    /// overlay for its own seat.
    ///
    /// Previously this was always the dashboard root regardless of role: Role only changed
    /// window chrome (taskbar, kiosk shortcuts), never what actually loaded, so a "user" PC
    /// showed staff the exact same screen an operator PC did. Null return means "cannot
    /// connect yet" - a user role PC that has never been provisioned (no PcId), which Connect
    /// reports rather than silently falling back to the dashboard a customer must never see.
    /// </summary>
    private string? TargetUrl()
    {
        if (!_config.IsUserPc) return _config.NormalisedUrl();

        if (_config.PcId is not { } pcId)
            return null;

        return $"{_config.NormalisedUrl()}/pc-overlay/{pcId}";
    }

    private void Connect()
    {
        if (_web.CoreWebView2 is null) return;

        var target = TargetUrl();
        if (target is null)
        {
            ShowOverlay(
                "This machine is set up as a customer gaming PC but has never actually claimed a seat.\n\n" +
                "Press Ctrl+Shift+P and set it up again to fix this.",
                showActions: true);
            return;
        }

        ShowOverlay($"Connecting to {HostLabel()}…", showActions: false);

        try
        {
            _web.CoreWebView2.Navigate(target);
        }
        catch (Exception ex)
        {
            ShowOverlay($"That server address doesn't look valid.\n\n{ex.Message}", showActions: true);
        }
    }

    /// <summary>
    /// What the title bar says about where this window is pointed.
    ///
    /// Deliberately says whether the branch is being run locally rather than just printing
    /// an address: a bare public IP in the title was what made it look — correctly, at the
    /// time — as though the shop were being run out of the cloud.
    /// </summary>
    private string HostLabel()
    {
        try
        {
            var uri = new Uri(_config.NormalisedUrl());
            var host = uri.Host;

            if (host is "localhost" or "127.0.0.1" or "::1")
                return "This PC";

            return IsShopNetwork(host)
                ? $"Counter PC · {uri.Authority}"
                : $"Head Office · {uri.Authority}";
        }
        catch
        {
            return _config.ServerUrl;
        }
    }

    /// <summary>
    /// True for the private address ranges a shop LAN uses. Anything else is out on the
    /// internet, which for day-to-day work means something is misconfigured.
    /// </summary>
    private static bool IsShopNetwork(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;

        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;

        return b[0] == 10
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    private string DescribeError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved or
        CoreWebView2WebErrorStatus.CannotConnect or
        CoreWebView2WebErrorStatus.ServerUnreachable =>
            $"Can't reach {HostLabel()}.\n\n" +
            "Check that this PC is on the network and that the server address below is right.",

        CoreWebView2WebErrorStatus.Timeout =>
            $"{HostLabel()} took too long to answer.\n\nThe server may be starting up — try again in a moment.",

        CoreWebView2WebErrorStatus.ConnectionAborted or
        CoreWebView2WebErrorStatus.ConnectionReset or
        CoreWebView2WebErrorStatus.Disconnected =>
            $"The connection to {HostLabel()} dropped.",

        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or
        CoreWebView2WebErrorStatus.CertificateRevoked =>
            $"There's a problem with the security certificate on {HostLabel()}.",

        CoreWebView2WebErrorStatus.OperationCanceled => "Loading was cancelled.",

        _ => $"Couldn't load the dashboard from {HostLabel()}.\n\n({status})",
    };

    // ── Dialogs ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a dialog owned by this window, in whichever z-order band this window is in.
    ///
    /// A customer gaming PC runs this window full screen and always-on-top (WS_EX_TOPMOST).
    /// Windows keeps an owned window above its owner only *within* a band, and a WinForms
    /// Form does not inherit its owner's topmost flag - so a dialog opened from the kiosk
    /// window was created underneath it while still taking the keyboard focus. On screen that
    /// is exactly what Ctrl+Shift+P did: the setup dialog painted for a moment, the kiosk
    /// window covered it, and the PC then looked frozen because a modal loop was running that
    /// nobody could see or reach. Setup was simply impossible to complete on a gaming PC.
    ///
    /// Win32's own MessageBox does inherit the flag from its owner - measured, not assumed -
    /// so the message boxes on either side of these dialogs were never affected and need
    /// nothing. This is only about Form.ShowDialog.
    /// </summary>
    private DialogResult ShowOwnedDialog(Form dialog)
    {
        dialog.TopMost = TopMost;
        return dialog.ShowDialog(this);
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsDialog(_config);
        if (ShowOwnedDialog(dialog) != DialogResult.OK) return;

        _config.ServerUrl = dialog.ServerUrl;
        _config.GateUsername = dialog.GateUsername;
        _config.GatePassword = dialog.GatePassword;

        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save settings.\n\n{ex.Message}", "Apple Esports",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Connect();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────

    // Keep this in step with SHORTCUT_KEYS.md — that file is what the branch staff are given.
    //
    // Shared between two very different paths: ProcessCmdKey sees keys when a native WinForms
    // control has focus (a dialog, this form itself), but the dashboard lives inside WebView2,
    // and Chromium's own out-of-process message handling swallows key presses before .NET's
    // message loop - and therefore ProcessCmdKey - ever sees them. That is the entire form
    // most of the time this app is actually in use, which meant Ctrl+Alt+Q silently did
    // nothing the moment the dashboard finished loading. Ctrl+Alt+Q specifically is instead
    // caught by a script injected in OnLoadAsync and carried back via WebMessageReceived
    // ("close-app") - see the comment there for why: this SDK's WinForms wrapper keeps its
    // CoreWebView2Controller private, so the documented AcceleratorKeyPressed fix for this
    // exact problem is not reachable from here. The other shortcuts (F5, F11, Ctrl+Shift+…)
    // still only work when a native control has focus - a real gap, just a smaller one, since
    // none of them are the only way to do something the way Ctrl+Alt+Q now is.
    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                _web.CoreWebView2?.Reload();
                return true;

            case Keys.F11:
                ToggleFullScreen();
                return true;

            case Keys.Escape when _isFullScreen:
                ToggleFullScreen();
                return true;

            // ── Protected: each needs the admin PIN on a customer-facing PC ──

            case Keys.Control | Keys.Shift | Keys.S:
                if (Unlocked("change the server address")) OpenSettings();
                return true;

            case Keys.Control | Keys.Shift | Keys.P:
                if (Unlocked("change which PC this machine is set up as")) ChangePcSetup();
                return true;

            case Keys.Control | Keys.Shift | Keys.U:
                if (Unlocked("un-configure this machine")) Unconfigure();
                return true;

            // Ctrl+Alt+Q is "Exit Kiosk Mode / Switch to Windows" on a gaming PC: staff have
            // asked, with a PIN, for this machine to be an ordinary Windows PC for a while.
            // There is no watchdog left to stand down - closing the app is the whole thing. It
            // stays closed until somebody opens it again (the desktop shortcut) or the PC is
            // restarted, whichever comes first.
            case Keys.Control | Keys.Alt | Keys.Q:
                if (Unlocked("close Apple Esports")) ForceExit();
                return true;

            // Swallowed on both roles now that neither has a close button - Alt+F4 must not
            // reopen the exact door removing the title bar was meant to close. Ctrl+Alt+Q is
            // the one way out on either kind of machine.
            case Keys.Alt | Keys.F4:
                return true;
        }

        return false;
    }

    // Native WinForms controls only (a dialog, this form itself) - see HandleShortcut's own
    // comment for why the dashboard, which has focus almost all the time, cannot be reached
    // this way, and needs the separate injected-script path in OnLoadAsync instead.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Gate for anything that could unbind this machine or let a customer out to Windows.
    /// On an operator PC with no PIN set there is nothing to protect against, so it passes
    /// straight through. On a customer PC with no PIN set it refuses outright — an
    /// unprotected escape hatch on a public machine is worse than no shortcut at all.
    /// </summary>
    private bool Unlocked(string action)
    {
        if (string.IsNullOrEmpty(_config.AdminPin))
        {
            if (!_config.IsUserPc) return true;

            MessageBox.Show(this,
                $"No admin PIN is set on this PC, so it cannot be unlocked here.\n\n" +
                "Set one in AppleEsports.config.json, or ask a Super Admin.",
                "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        using var prompt = new PinPromptDialog(action);
        if (ShowOwnedDialog(prompt) != DialogResult.OK) return false;

        if (prompt.Pin == _config.AdminPin) return true;

        MessageBox.Show(this, "Incorrect PIN.", "Apple Esports",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    /// <summary>
    /// A PC number and a role are only a local label. A "customer gaming PC" is also a real
    /// seat Head Office bills sessions against, so that role additionally has to actually
    /// claim one through <c>/api/agent/provision</c> - the same call the setup wizard would
    /// make for it - or this machine ends up with a role but no <see cref="AppConfig.PcId"/>
    /// to send the browser to, which is exactly the bug this replaces: Role changed the window
    /// chrome and nothing else, so a "user" PC quietly showed the operator dashboard.
    /// </summary>
    private async void ChangePcSetup()
    {
        using var dialog = new PcSetupDialog(_config);
        if (ShowOwnedDialog(dialog) != DialogResult.OK) return;

        var pcNumber = dialog.PcNumber;
        var role = dialog.Role;

        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
        {
            // The operator counter is not a billable seat - nothing to claim, just a label.
            _config.PcNumber = pcNumber;
            _config.Role = role;
            _config.PcId = null;
            SaveConfig();

            MessageBox.Show(this,
                $"This machine is now set up as {pcNumber} ({role}).\n\n" +
                "Apple Esports will restart to apply it.",
                "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Restart();
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            using var client = new HeadOfficeClient(_config.ServerUrl, _config.GateUsername, _config.GatePassword);

            // A gaming PC has no branch id of its own - only the counter PC across the LAN
            // knows what branch it is, from its own adoption.
            var (adopted, branchId, _, reason) = await client.GetIdentityAsync();
            if (!adopted || branchId is null)
            {
                MessageBox.Show(this,
                    $"Could not confirm which branch this is from {HostLabel()}.\n\n{reason}",
                    "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var (ok, message, _, pcId) = await client.ProvisionAsync(branchId.Value, pcNumber, MachineIdentity.Current());
            if (!ok || pcId is null)
            {
                // Left exactly as it was rather than half-applied — the setup wizard follows
                // the same rule for the same reason.
                MessageBox.Show(this, message, "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _config.PcNumber = pcNumber;
            _config.Role = role;
            _config.PcId = pcId;
            SaveConfig();

            MessageBox.Show(this,
                $"{message}\n\nApple Esports will restart to apply it.",
                "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Restart();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not set this PC up.\n\n{ex.Message}",
                "Apple Esports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void Unconfigure()
    {
        var confirm = MessageBox.Show(this,
            "Remove this machine's setup?\n\n" +
            "It will stop being assigned to a PC number and will ask to be set up again " +
            "next time it starts. Any session running here should be stopped first.",
            "Apple Esports", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _config.PcNumber = "";
        _config.Role = "operator";
        _config.PcId = null;
        SaveConfig();

        Restart();
    }

    private void SaveConfig()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save settings.\n\n{ex.Message}", "Apple Esports",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });

        ForceExit();
    }

    private void ForceExit()
    {
        WriteIntentionalExitMarker();
        _allowClose = true;
        Application.Exit();
    }

    /// <summary>
    /// Proves to restart-on-kill.ps1 (a SYSTEM-level task reacting to this process ending -
    /// see the installer) that this particular exit was legitimate: the operator's own
    /// Ctrl+Alt+Q, or Windows itself shutting the machine down. A Task Manager "End Task" runs
    /// none of this app's own code at all - it cannot write this file, which is exactly what
    /// makes its absence a reliable signal.
    ///
    /// Timestamped and re-written every call, so a stale marker from a much earlier legitimate
    /// exit (this app can sit closed for a while between customers) can never be mistaken for
    /// permission covering a *later*, different exit - restart-on-kill.ps1 only accepts one
    /// written in roughly the last minute.
    /// </summary>
    private static void WriteIntentionalExitMarker()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple Esports");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "intentional-exit.marker"), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Best effort - a failure here must never be the reason the app cannot close.
        }
    }

    /// <summary>
    /// "Shut Down" from the operator's PC screen (PcStatusHub.SendShutdownCommand), carried here
    /// over the connection this PC already holds for its lock screen and session overlay - see
    /// the shutdown-pc case in WebMessageReceived above. Windows' own shutdown.exe, not a script:
    /// one built-in command, same as calling schtasks.exe elsewhere in this app.
    /// </summary>
    private void ShutdownMachine()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0 /f")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* nothing else to do if Windows itself refuses to shut down */ }
    }

    private CancellationTokenSource? _recordingCts;

    /// <summary>
    /// How often a frame is taken while a PC is under maintenance. A still image every few
    /// seconds, not video: enough to see what was actually done on the PC during maintenance -
    /// a real game update versus a customer let on to play - without a video encoder, without
    /// the file sizes real video would produce over a maintenance window that could run for
    /// hours, and without anything here that could itself hang or crash mid-recording.
    /// </summary>
    private static readonly TimeSpan MaintenanceFrameEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts saving still frames of this PC's own screen to
    /// %ProgramData%\Apple Esports\maintenance-recordings\&lt;when maintenance started&gt;\ -
    /// local to this machine only, nothing uploaded anywhere, kept until whoever runs this
    /// branch deletes the folder themselves. Idempotent: a second "active: true" while one is
    /// already running (a page reload mid-maintenance re-sends its current state) does nothing.
    ///
    /// Stops only when maintenance itself ends (StopMaintenanceRecording, posted the same way)
    /// or this process exits for any reason - there is deliberately no separate control inside
    /// the app to pause or cancel it while maintenance is still active.
    /// </summary>
    private void StartMaintenanceRecording()
    {
        if (_recordingCts != null) return;

        string dir;
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Apple Esports", "maintenance-recordings",
                DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(root);
            dir = root;
        }
        catch
        {
            return;   // nowhere to save to - nothing this app can do about that from in here
        }

        var cts = new CancellationTokenSource();
        _recordingCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CaptureScreenFrame(dir);
                }
                catch
                {
                    // One failed frame (a display that briefly went to sleep, a locked
                    // session) must never stop the rest of the maintenance window from
                    // being recorded.
                }

                try { await Task.Delay(MaintenanceFrameEvery, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void StopMaintenanceRecording()
    {
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordingCts = null;
    }

    /// <summary>
    /// One screenshot of the full virtual desktop (every monitor this PC has, not just the
    /// primary one - a customer or operator could easily be doing something on a second screen
    /// during maintenance). JPEG, not PNG: a screen full of text and UI compresses far smaller
    /// as a photo than losslessly, and a maintenance window can run for hours at one frame every
    /// five seconds.
    /// </summary>
    private static void CaptureScreenFrame(string dir)
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        var path = Path.Combine(dir, $"{DateTimeOffset.Now:HH-mm-ss}.jpg");
        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (jpegEncoder is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 60L);
        bitmap.Save(path, jpegEncoder, quality);
    }

    /// <summary>
    /// Closes whatever a customer opened during their session, the moment it actually ends -
    /// triggered by the overlay page posting "session-ended" on SessionStopped (see
    /// OverlaySocketContext.jsx), not the later SessionEnded, since play is over the instant
    /// the time runs out whether or not billing has been settled yet.
    ///
    /// The work itself lives in end-session-cleanup.ps1 rather than here, so it can be
    /// re-tuned - a spared process added, the "visible window" rule replaced with real PID
    /// tracking later - without a rebuild. Run hidden: a console flashing on a locked kiosk
    /// screen is not something a customer should ever see.
    /// </summary>
    private void RunEndSessionCleanup()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var script = Path.Combine(exeDir, "end-session-cleanup.ps1");
            if (!File.Exists(script)) return;   // not installed on this build - nothing to run

            // powershell.exe, not pwsh.exe: PowerShell 7 is not part of Windows, so on a gaming
            // PC this threw (swallowed below) and nothing was ever closed - the game kept
            // running under the lock screen. Every other script here already uses this one.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // A failed cleanup must never take the kiosk down - the next customer gets a
            // messier desktop, not a broken PC.
        }
    }

    /// <summary>
    /// Parses the JSON layout/drag messages the overlay page posts on every mode change and
    /// every drag movement (see UserOverlayApp.jsx's postToHost / useDragToMoveHost) and
    /// applies them to this window. Anything that isn't one of the two known message shapes —
    /// a stale page, a future addition — is silently ignored rather than treated as an error;
    /// this bridge must never be able to crash the host it is running inside of.
    /// </summary>
    private void HandleOverlayLayoutMessage(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;

            switch (typeEl.GetString())
            {
                case "overlay-layout" when root.TryGetProperty("mode", out var modeEl):
                    ApplyOverlayLayout(
                        modeEl.GetString() ?? "full",
                        Number(root, "width"),
                        Number(root, "height"),
                        Number(root, "dpr"));
                    break;

                case "overlay-drag" when root.TryGetProperty("dx", out var dxEl) && root.TryGetProperty("dy", out var dyEl):
                    DragFloatingWindow((int)Math.Round(dxEl.GetDouble()), (int)Math.Round(dyEl.GetDouble()));
                    break;

                case "maintenance-recording" when root.TryGetProperty("active", out var activeEl):
                    if (activeEl.GetBoolean()) StartMaintenanceRecording();
                    else StopMaintenanceRecording();
                    break;
            }
        }
        catch
        {
            // Not a message this bridge understands — nothing to apply.
        }
    }

    /// <summary>Reads an optional number from a bridge message, ignoring anything that isn't one.</summary>
    private static double? Number(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number
            ? el.GetDouble()
            : null;

    /// <summary>
    /// Resizes and repositions this window to match what the overlay page is currently
    /// showing. "full" covers the whole screen, for the walk-in/member gate and the locked
    /// "session ended" screen — both render themselves fixed inset-0 and need the window to
    /// actually be the screen for that to mean anything. "bubble" and "panel" are both just
    /// the small floating widget at two different sizes; switching between them keeps
    /// whatever corner the customer last dragged it to rather than jumping back to a default,
    /// clamped back on screen if the new size would otherwise push it past an edge.
    ///
    /// The widget's size comes from the page in its own CSS pixels, with the scale to turn
    /// them into real ones — the host has no way to derive either for itself.
    /// </summary>
    private void ApplyOverlayLayout(string mode, double? cssWidth, double? cssHeight, double? dpr)
    {
        // Pinned above everything only while it IS the gate, and never once it has shrunk to the
        // little floating widget.
        //
        // "full" is the walk-in/member screen and the locked "session ended" screen. Those have to
        // win against anything on the desktop: a game drawn over the gate would be a customer
        // playing without a session, which is the whole thing the lock screen prevents.
        //
        // The widget is the opposite case and used to inherit the same TopMost, which put it over
        // the top of whatever the customer was playing - a small panel sitting above a full-screen
        // game, in the corner, unavoidable. Nobody wants our minimise button floating over their
        // match. It sits in the normal window order now, so a game covers it like any other
        // window, and the customer brings it back the ordinary way when they want it. It still
        // parks bottom-right by default, out of the way (see the origin below).
        //
        // The page having spoken is still what gates this - see the constructor for why an older
        // dashboard that cannot drive the window must never be pinned on top.
        // "alert" is the panel shown for the "save your game" warning: floating like the panel,
        // but above the game - pinned WITHOUT taking focus, so the player is warned rather than
        // thrown out of a fullscreen match early.
        var floating = mode is "bubble" or "panel" or "alert";
        var wantTopMost = !floating;
        if (TopMost != wantTopMost) TopMost = wantTopMost;
        if (mode == "alert") LockGuard.PinWithoutFocus(Handle, true);
        else if (_overlayMode == "alert" && floating) LockGuard.PinWithoutFocus(Handle, false);

        if (mode == "full") _lockGuard.Engage();
        else _lockGuard.Release();

        if (mode == _overlayMode) return;
        _overlayMode = mode;

        if (!floating)
        {
            _floatDraggable = false;
            SetFloatingShape(null, Size.Empty);
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return;
        }

        // The working area rather than the whole screen. The widget has to stay clear of the
        // taskbar: expanding used to clamp the panel hard against the bottom-right corner of
        // the full screen rectangle, which put its nav bar - Info, Food, Extend, Call, Bill -
        // underneath the taskbar, where a customer could see it and not press it.
        var area = Screen.FromControl(this).WorkingArea;

        // The page's own CSS-pixel scale when it sends one, this window's DPI when it does not.
        // The fallback only matters for a branch still serving a dashboard older than this
        // shell; it is the wrong number often enough that it is a fallback and not the rule.
        _overlayScale = dpr is > 0 ? dpr.Value : DeviceDpi / 96.0;
        var margin = Scaled(FloatMargin);

        var design = mode == "bubble"
            ? new SizeF((float)(cssWidth ?? BubbleSize), (float)(cssHeight ?? BubbleSize))
            : new SizeF((float)(cssWidth ?? PanelWidth), (float)(cssHeight ?? PanelHeight));

        // Never larger than the screen it has to sit on. A high devicePixelRatio - a 4K panel,
        // or Windows' own text scaling on top of display scaling - can ask for a window taller
        // than the desktop, and a widget that runs off the bottom takes its nav bar with it.
        var size = new Size(
            Math.Min((int)Math.Round(design.Width * _overlayScale), area.Width - margin * 2),
            Math.Min((int)Math.Round(design.Height * _overlayScale), area.Height - margin * 2));

        var origin = _floatDraggable
            ? Location
            : new Point(area.Right - size.Width - margin, area.Bottom - size.Height - margin);

        // Keep the margin on every edge, not just the one it started on, so switching between
        // bubble and panel never tucks part of the widget off the usable screen.
        origin.X = Math.Clamp(origin.X, area.Left + margin, Math.Max(area.Left + margin, area.Right - size.Width - margin));
        origin.Y = Math.Clamp(origin.Y, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - size.Height - margin));

        Bounds = new Rectangle(origin, size);
        SetFloatingShape(mode, size);
        _floatDraggable = true;
    }

    /// <summary>
    /// Clips the window to the shape the page actually draws inside it.
    ///
    /// This window is an opaque rectangle and the page's widget is not: the bubble is a circle
    /// and the panel has rounded corners, so the difference between them was showing as bare
    /// dark window. At bubble size that is nearly all of what a customer sees - a small black
    /// square sitting on their desktop with a button in the middle of it, which is exactly how
    /// it was described. Clipping the window itself is what makes it read as a floating button
    /// instead. Applied to the top-level window, so it clips the WebView2 child with it.
    ///
    /// Null mode clears the shape, which the full-screen lock screen needs - a stale rounded
    /// region would otherwise carve the corners out of a screen that is meant to cover
    /// everything.
    /// </summary>
    private void SetFloatingShape(string? mode, Size size)
    {
        var previous = Region;

        if (mode is null)
        {
            Region = null;
            previous?.Dispose();
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();

        if (mode == "bubble")
        {
            path.AddEllipse(0, 0, size.Width - 1, size.Height - 1);
        }
        else
        {
            var d = Scaled(PanelCornerRadius) * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(size.Width - d - 1, 0, d, d, 270, 90);
            path.AddArc(size.Width - d - 1, size.Height - d - 1, d, d, 0, 90);
            path.AddArc(0, size.Height - d - 1, d, d, 90, 90);
            path.CloseFigure();
        }

        Region = new Region(path);
        previous?.Dispose();
    }

    /// <summary>
    /// Moves the floating widget by a mouse delta reported from inside the page (see
    /// useDragToMoveHost in UserOverlayApp.jsx). Has to be driven this way rather than the
    /// usual WM_NCHITTEST/HTCAPTION "drag anywhere on a borderless window" trick: WebView2's
    /// own content is a separate child window, so a hit test played by this Form never sees
    /// mouse activity that happens over it. Clamped so a fast drag can't throw the widget
    /// somewhere off screen the customer can no longer reach.
    /// </summary>
    private void DragFloatingWindow(int dx, int dy)
    {
        if (!_floatDraggable) return;

        // Working area and a scaled handle, for the same reasons as ApplyOverlayLayout: a
        // widget dragged under the taskbar is a widget the customer cannot get back.
        var area = Screen.FromControl(this).WorkingArea;
        var handle = Scaled(40);
        var x = Math.Clamp(Location.X + dx, area.Left - Width + handle, area.Right - handle);
        var y = Math.Clamp(Location.Y + dy, area.Top, area.Bottom - handle);
        Location = new Point(x, y);
    }

    // Blocks the window's own close path (task switcher, taskbar right-click, a script
    // sending WM_CLOSE) on either kind of machine now - there is no title bar for this to be
    // "the X button" any more, but Windows offers several other ways to ask a window to
    // close and all of them used to work. _allowClose is set only by ForceExit (Ctrl+Alt+Q,
    // itself PIN-gated when a PIN is configured) and by the update installer's own restart,
    // which is the deliberate exit this is not meant to block.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason is CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    private void ToggleFullScreen()
    {
        if (_config.IsUserPc) return;   // already borderless and locked by design

        if (_isFullScreen)
        {
            FormBorderStyle = _borderBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
            _isFullScreen = false;
        }
        else
        {
            _borderBeforeFullScreen = FormBorderStyle;
            _stateBeforeFullScreen = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;   // forces a re-maximise below
            WindowState = FormWindowState.Maximized;
            _isFullScreen = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lockGuardInstance?.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
