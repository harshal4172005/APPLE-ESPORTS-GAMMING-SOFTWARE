using System.Net.Http.Json;
using System.Text.Json;

namespace AppleEsports.Desktop;

/// <summary>
/// Ends a session when its gaming PC goes off, instead of leaving it running (and billing) on a
/// dark machine until someone notices. Three ways a PC goes off, one rule - the session ends at
/// the moment the PC did, through the server's own Stop (see PublicController.ReportPcPoweredOff):
///
///  - Windows shut down / restarted without Logout: reported as it happens (OnShutdown).
///  - Same, but the branch server could not be reached right then: kept on disk, sent next boot.
///  - Power cut / crash (no warning at all): the last "alive" note on disk, sent on next boot -
///    but only if Windows really rebooted since; an app restart on a PC that stayed on never
///    ends anyone's session.
///
/// Reports carry the exact session id the PC was showing, so a late report can only ever end
/// that session, never a newer one.
/// </summary>
internal sealed class PowerWatch
{
    private sealed record State(Guid SessionId, DateTimeOffset AliveAt, DateTimeOffset? OffAt);

    private readonly string _endpoint;
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleEsports", "power-state.json");

    private Guid? _sessionId;
    private bool _bootChecked;

    public PowerWatch(string baseUrl, Guid pcId) =>
        _endpoint = $"{baseUrl.TrimEnd('/')}/api/public/pcs/{pcId}/powered-off";

    /// <summary>What the overlay page says is on screen right now (null = no session).</summary>
    public void SetSession(Guid? sessionId)
    {
        _sessionId = sessionId;
        // Until the boot check has run, the file may hold a report from before this boot -
        // don't let the page's first message overwrite or delete it.
        if (!_bootChecked) return;
        if (sessionId is { } id) Write(new State(id, DateTimeOffset.UtcNow, null));
        else if (Read() is { OffAt: null }) Delete();
    }

    /// <summary>Every 30 s from MainForm's background loop.</summary>
    public async Task TickAsync()
    {
        if (!_bootChecked)
        {
            _bootChecked = true;
            await ReportAfterBootAsync();
        }

        var pending = Read();
        if (pending?.OffAt is { } offAt)
        {
            if (await PostAsync(pending.SessionId, offAt)) Delete();
            return;
        }

        if (_sessionId is { } id) Write(new State(id, DateTimeOffset.UtcNow, null));
    }

    /// <summary>Windows is shutting down - tell the server now, while there is still time.</summary>
    public void OnShutdown()
    {
        if (_sessionId is not { } id) return;
        var now = DateTimeOffset.UtcNow;
        Write(new State(id, now, now));   // kept for the next boot if the server can't hear us
        if (PostSync(id, now)) Delete();
    }

    private async Task ReportAfterBootAsync()
    {
        var state = Read();
        if (state is null) return;

        if (state.OffAt is null)
        {
            var bootedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (state.AliveAt >= bootedAt) return;   // only the app restarted - the session carries on
            state = state with { OffAt = state.AliveAt };
            Write(state);
        }

        if (await PostAsync(state.SessionId, state.OffAt!.Value)) Delete();
    }

    private async Task<bool> PostAsync(Guid sessionId, DateTimeOffset at)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var response = await http.PostAsJsonAsync(_endpoint, new { sessionId, at });
            return response.IsSuccessStatusCode;   // any answer means it arrived; retry only when unreachable
        }
        catch { return false; }
    }

    private bool PostSync(Guid sessionId, DateTimeOffset at)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new { sessionId, at }),
            };
            return http.Send(request).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private State? Read()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) : null; }
        catch { return null; }
    }

    private void Write(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(state));
        }
        catch { /* a full or locked disk must never take the kiosk down */ }
    }

    private void Delete()
    {
        try { File.Delete(_file); } catch { }
    }
}
