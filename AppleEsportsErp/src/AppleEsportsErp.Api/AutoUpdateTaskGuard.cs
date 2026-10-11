using System.Diagnostics;

namespace AppleEsportsErp.Api;

/// <summary>
/// Makes sure the branch's own auto-update scheduled task exists, the same way DbUpdater makes
/// sure the schema does - checked on every startup, not only once.
///
/// The task ("AppleEsports Auto Update", see apply-update.ps1) is what actually installs a new
/// version with nothing to click. Until now the ONLY thing that ever created it was the
/// installer's own [Run] step, and the only thing that ever tried to repair it afterwards was
/// the desktop app, on every launch (KioskGuard.EnsureAutoUpdateTask). That repair attempt looks
/// like self-healing but is not: creating a task that runs /RU SYSTEM /RL HIGHEST needs an
/// elevated caller, and the desktop app runs as whoever is logged in - ordinarily not elevated,
/// even on an administrator's own account, because Windows does not elevate an app just because
/// the user could approve a prompt. So the desktop app's attempt has been silently failing on
/// every ordinary launch all along; it only ever worked once, immediately after install, when it
/// inherited the installer's own elevation for that one run.
///
/// The result: the task survives fine on its own once created, right up until something outside
/// this app's control disturbs it - a Windows Update, security software flagging an unusual
/// SYSTEM-level task, someone tidying Task Scheduler by hand. From that moment on nothing could
/// ever recreate it again short of a full reinstall, and the branch would sit on the same
/// version forever, with the Updates page still honestly reporting a newer one waiting - "the
/// update is coming, but it never installs" is exactly the shape of this exact fault.
///
/// The branch API is the fix, because it is the one thing on this machine that is guaranteed
/// both elevated and running: it starts as a Windows service at boot, before anyone logs in, and
/// keeps running whether anyone ever does. Doing this here instead reaches the one class of
/// machine this whole problem was reported on - a counter PC, where the branch API is a
/// service - without needing to touch anything else.
/// </summary>
public static class AutoUpdateTaskGuard
{
    private const string TaskName = "AppleEsports Auto Update";

    /// <summary>
    /// Never throws: a branch that cannot repair its own update task must still serve customers,
    /// and an unhandled error here must not be the reason the API fails to start.
    /// </summary>
    public static void EnsureRegistered(ILogger logger)
    {
        // A no-op away from a real branch: Head Office runs in Linux containers, where
        // schtasks.exe does not exist, and this task would mean nothing there anyway - Head
        // Office is never the thing that installs an update, only the thing that offers one.
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // AppContext.BaseDirectory is {app}\api\ - apply-update.ps1 ships one level up, in
            // {app} itself, next to AppleEsports.exe (see AppleEsportsBranch.iss's [Files]).
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "apply-update.ps1"));
            if (!File.Exists(script))
            {
                // Head Office's own API build never ships this file at all - nothing to repair,
                // and not an error.
                return;
            }

            var tr = $"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"";

            // ArgumentList, not a hand-built Arguments string. schtasks.exe's /TR value needs a
            // quote nested inside the outer quoted argument (the install path always has a space
            // - "Program Files"), and getting that nesting right by hand, character for
            // character, is exactly the kind of thing that silently breaks in one way on every
            // single run. That is what was happening here: this failed identically on every
            // branch, every startup, since the day it shipped (schtasks exit -2147467259).
            // ArgumentList hands each argument to CreateProcess separately and lets .NET apply
            // the actual Win32 quoting rules, which is the one thing that reliably gets nested
            // quotes right.
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
            {
                "/Create", "/F", "/TN", TaskName,
                "/SC", "MINUTE", "/MO", "15",
                "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", tr,
            })
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);

            p?.WaitForExit(15000);

            if (p != null && p.ExitCode == 0)
            {
                logger.LogInformation("Auto-update task verified/repaired ✓");
            }
            else
            {
                var stderr = p?.StandardError.ReadToEnd();
                logger.LogWarning("Could not create the auto-update task (schtasks exit {Code}): {Error}",
                    p?.ExitCode, stderr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-update task check failed - continuing without it");
        }
    }
}
