using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppleEsports.Desktop;

/// <summary>
/// Runtime settings for the desktop client.
///
/// Resolution order (later wins):
///   1. Built-in defaults below
///   2. AppleEsports.config.json sitting next to the .exe  — the deployment default,
///      so a branch can ship a pre-pointed copy without anyone touching the UI
///   3. %APPDATA%\AppleEsports\config.json                 — per-machine override,
///      written when someone changes the server from the Settings dialog
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// The branch API on this machine. Must match the port setup-api.ps1 binds the service
    /// to; they are the two ends of the same wire.
    ///
    /// 127.0.0.1 rather than "localhost", deliberately. "localhost" resolves to both ::1 and
    /// 127.0.0.1, and the embedded browser tries ::1 first. The API binds 0.0.0.0 — IPv4
    /// only — so anything else holding the IPv6 loopback on this port answers instead of it.
    /// A machine running Docker or WSL does exactly that, and because the wrong server
    /// *accepts* the connection there is no failure to fall back from: the dashboard just
    /// refuses to load while every check against 127.0.0.1 says the branch is perfectly fine.
    /// </summary>
    public const string LocalBranchUrl = "http://127.0.0.1:5016";

    /// <summary>
    /// The server this client talks to for everything it does — sessions, billing, members.
    ///
    /// This is the BRANCH, never Head Office. On the counter PC that is this machine; on a
    /// gaming PC it is the counter PC across the shop LAN. Head Office is reached only by
    /// the branch's own background sync, so the shop keeps trading with the internet down.
    ///
    /// Defaulting this to a public server address was the bug behind "it stops working when
    /// I turn the internet off": every click travelled to the cloud and back, so the shop
    /// depended on the internet for work happening entirely inside one building.
    /// </summary>
    public string ServerUrl { get; set; } = LocalBranchUrl;

    /// <summary>
    /// Credentials for the nginx Basic Auth gate in front of the dashboard.
    /// Left blank means "don't auto-fill" — the browser prompt is shown instead.
    /// </summary>
    public string GateUsername { get; set; } = "";

    public string GatePassword { get; set; } = "";

    /// <summary>
    /// "operator" for the counter PC, "user" for a customer-facing gaming PC.
    ///
    /// A user PC is locked down: no close button, no minimise, no Alt+F4, no full-screen
    /// toggle. A customer must not be able to shut the app and reach the Windows desktop,
    /// which is the whole point of a kiosk. Everything that could let them out is behind
    /// <see cref="AdminPin"/>.
    /// </summary>
    public string Role { get; set; } = "operator";

    /// <summary>
    /// PIN that unlocks the protected shortcuts — change server, reconfigure the PC,
    /// un-configure it, or exit a locked kiosk. Without this the shortcuts are inert on a
    /// user PC, because a customer discovering a key combination must not be able to
    /// unbind the machine or escape to the desktop.
    /// </summary>
    public string AdminPin { get; set; } = "";

    /// <summary>Which PC this machine is set up as, e.g. "PC-1". Empty until setup is done.</summary>
    public string PcNumber { get; set; } = "";

    /// <summary>
    /// Head Office's own id for the seat named by <see cref="PcNumber"/>. Null until a
    /// "customer gaming PC" role has actually been provisioned against
    /// <c>/api/agent/provision</c> - never assumed from the PC number alone, which is only a
    /// label a person chose and has no guaranteed relationship to any real record.
    ///
    /// This is what <see cref="MainForm"/> needs to send the browser to
    /// <c>/pc-overlay/{PcId}</c> instead of the operator dashboard. Without it a "user" role
    /// PC had nothing to route to and silently showed the same dashboard as an operator PC -
    /// the role changed the window chrome and nothing else.
    /// </summary>
    public Guid? PcId { get; set; }

    /// <summary>Branch this machine belongs to, as Head Office knows it.</summary>
    public string BranchId { get; set; } = "";

    public string BranchName { get; set; } = "";

    /// <summary>
    /// Set only once Head Office has confirmed the claim. Until then the setup wizard runs
    /// instead of the dashboard — a machine that has not been claimed cannot take customers,
    /// so showing the dashboard would only invite an operator to seat someone at a PC that
    /// will never unlock.
    /// </summary>
    public bool IsSetUp { get; set; }

    public bool IsUserPc => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    // ── Paths ─────────────────────────────────────────────────────────────

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppleEsports");

    public static string UserConfigPath => Path.Combine(AppDataDirectory, "config.json");

    private static string ExeDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string DeploymentConfigPath =>
        Path.Combine(ExeDirectory, "AppleEsports.config.json");

    /// <summary>WebView2 needs a writable folder for its profile; Program Files is not.</summary>
    public static string WebViewDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppleEsports", "WebView2");

    // ── Load / save ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Load()
    {
        var config = new AppConfig();
        Merge(config, DeploymentConfigPath);
        Merge(config, UserConfigPath);
        MigrateToLocalBranch(config);
        return config;
    }

    /// <summary>
    /// True when the branch API is installed alongside this client — which is only the case
    /// on the counter PC, because that is the only machine the installer puts it on.
    /// </summary>
    private static bool BranchApiInstalledHere() =>
        File.Exists(Path.Combine(ExeDirectory, "api", "AppleEsportsErp.Api.exe"));

    /// <summary>
    /// Points a counter PC at its own branch API.
    ///
    /// The branch API is installed on this machine, so there is exactly one correct answer
    /// and it is not worth being clever about preserving a different one. Two separate faults
    /// are corrected by the same rule:
    ///
    ///   • a public server address, left over from when the branch ran in the cloud — every
    ///     click went out to the internet and back, so the shop stopped when the line did;
    ///   • "localhost", which resolves to IPv6 first and can reach an entirely different
    ///     server than the branch, as it does on any machine running Docker or WSL.
    ///
    /// Both live in the user profile, which survives an upgrade untouched — so correcting the
    /// default alone would never have reached a machine that was already set up.
    /// </summary>
    private static void MigrateToLocalBranch(AppConfig config)
    {
        if (!BranchApiInstalledHere()) return;      // a gaming PC keeps its counter's address
        if (string.Equals(config.ServerUrl?.TrimEnd('/'), LocalBranchUrl, StringComparison.OrdinalIgnoreCase))
            return;

        config.ServerUrl = LocalBranchUrl;

        // These belong to the reverse proxy in front of the cloud dashboard. The branch API
        // is not behind one, and leaving credentials in a file on a shop PC serves nothing.
        config.GateUsername = "";
        config.GatePassword = "";

        try { config.Save(); }
        catch { /* running from a read-only location must not stop the app starting */ }
    }

    private static void Merge(AppConfig target, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
            if (loaded is null) return;

            if (!string.IsNullOrWhiteSpace(loaded.ServerUrl)) target.ServerUrl = loaded.ServerUrl.Trim();
            if (loaded.GateUsername is not null) target.GateUsername = loaded.GateUsername;
            if (loaded.GatePassword is not null) target.GatePassword = loaded.GatePassword;
            if (!string.IsNullOrWhiteSpace(loaded.Role)) target.Role = loaded.Role.Trim();
            if (loaded.AdminPin is not null) target.AdminPin = loaded.AdminPin;
            if (loaded.PcNumber is not null) target.PcNumber = loaded.PcNumber;
            if (loaded.PcId is not null) target.PcId = loaded.PcId;
            if (loaded.BranchId is not null) target.BranchId = loaded.BranchId;
            if (loaded.BranchName is not null) target.BranchName = loaded.BranchName;
            if (loaded.IsSetUp) target.IsSetUp = true;
        }
        catch
        {
            // A malformed config must never stop the app from starting —
            // fall through and keep whatever we already have.
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(UserConfigPath, json);

        // Also beside the exe, which is the only config the SYSTEM auto-update task reads
        // (apply-update.ps1). Without the counter's address there, a gaming PC's updater found
        // no branch and skipped every pass - gaming PCs never updated. Succeeds only when this
        // process is elevated (the "Set up this PC now" run straight after install); the
        // installer fills it from this user copy on the next install otherwise.
        try { File.WriteAllText(DeploymentConfigPath, json); }
        catch { /* not elevated - Program Files is admin-only */ }
    }

    /// <summary>Normalised base address, guaranteed to have a scheme and no trailing slash.</summary>
    public string NormalisedUrl()
    {
        var url = (ServerUrl ?? "").Trim();
        if (url.Length == 0) return LocalBranchUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        return url.TrimEnd('/');
    }
}
