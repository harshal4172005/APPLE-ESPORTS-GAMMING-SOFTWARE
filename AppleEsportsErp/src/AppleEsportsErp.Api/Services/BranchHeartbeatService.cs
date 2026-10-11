using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Sessions;
using AppleEsportsErp.Application.DTOs.Sync;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Infrastructure.Services;

namespace AppleEsportsErp.Api.Services;

/// <summary>
/// Tells Head Office what this shop is doing, every thirty seconds.
///
/// The reason this exists rather than another dozen event types: everything anybody wanted to
/// see at Head Office had to be wired by hand into whichever service happened to change it, and
/// the list of what synced became whatever somebody remembered. Sessions were remembered. Bills
/// were remembered. Operator status was not, so a branch trading all evening showed its staff as
/// logged out. PC state was not, so Head Office displayed early August for four days running.
///
/// State is not history and does not want the same machinery. Only the newest beat matters, so
/// there is no queue and no retry: a missed one costs nothing because the next is thirty seconds
/// away. That is what makes it safe to send this often, and why nothing here can lose money.
///
/// Runs on branches only. Head Office is the one being told.
/// </summary>
public class BranchHeartbeatService : BackgroundService
{
    private readonly ILogger<BranchHeartbeatService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Three seconds, so Head Office is watching the shop rather than reading a report about it.
    ///
    /// Thirty was chosen to be frugal and was simply too slow to trust: an owner who starts a
    /// session and stares at an unchanged screen for half a minute concludes sync is broken, and
    /// checking by waiting is no way to run four branches. At three seconds the two screens
    /// agree while you are still looking at them.
    ///
    /// What makes this affordable is on the receiving side, not here: Head Office now writes only
    /// rows whose values actually changed. A quiet shop therefore costs one small row update per
    /// beat no matter how many PCs it has, instead of rewriting every PC twenty times a minute.
    ///
    /// The cost that remains is bandwidth - a few KB each way, so roughly 100 MB a day per
    /// branch. Nothing on a broadband line. Worth knowing if a branch ever runs on a phone
    /// tether for a day.
    /// </summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Logged at most this often when the line is down. Without it a shop offline for a night
    /// writes a warning every thirty seconds — two and a half thousand lines saying the same
    /// thing, burying whatever else happened.
    /// </summary>
    private static readonly TimeSpan ComplainAtMost = TimeSpan.FromMinutes(15);
    private DateTimeOffset _lastComplaint = DateTimeOffset.MinValue;

    /// <summary>
    /// Own throttle, separate from <see cref="_lastComplaint"/> above - that one is about
    /// losing the line to Head Office; this one is about a specific row (an operator, a menu
    /// item, a member) that keeps failing to apply every single beat. Sharing one timer would
    /// let either kind of problem silence the other's log line.
    /// </summary>
    private DateTimeOffset _lastConfigRowComplaint = DateTimeOffset.MinValue;

    public BranchHeartbeatService(
        ILogger<BranchHeartbeatService> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
    }

    private static string RunningVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.IsHeadOffice())
        {
            _logger.LogInformation(
                "This instance is Head Office, so it reports its state to nobody. Branches report here.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BeatAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                if (DateTimeOffset.UtcNow - _lastComplaint > ComplainAtMost)
                {
                    _lastComplaint = DateTimeOffset.UtcNow;
                    _logger.LogWarning(
                        "Cannot reach Head Office to report this branch's status ({Reason}). " +
                        "The shop is unaffected; this is only reporting.",
                        ex.GetBaseException().Message);
                }
            }

            await Task.Delay(Every, stoppingToken);
        }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        var headOffice = _configuration["Sync:HeadOfficeUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(headOffice)) return;

        using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One branch row, its own, once adopted. Before that there is nothing to report about.
        // Ordered, so a branch whose local database somehow holds more than one row reports
        // the same one every time. Unordered, PostgreSQL is free to return them in any order
        // and the shop could report itself as a different branch between beats.
        var branchId = await db.Branches.AsNoTracking()
            .OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefaultAsync(ct);

        if (branchId == Guid.Empty) return;
        _branchId = branchId;

        var today = IndiaTime.BusinessDayOf(DateTimeOffset.UtcNow);
        var (dayStart, dayEnd) = IndiaTime.BusinessDayRange(today);

        // Who is actually standing at a counter, taken from open shifts rather than from the
        // operator's own status flag. A flag can be left behind by a crash; an open shift is
        // the thing the shop is actually working against.
        var onDuty = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == branchId && s.Status == ShiftStatus.Active)
            .Join(db.Operators.AsNoTracking(), s => s.OperatorId, o => o.Id,
                  (s, o) => new OperatorOnDutyDto
                  {
                      OperatorId = o.Id,
                      FullName = o.FullName,
                      ShiftStartedAt = s.LoginTime,
                  })
            .ToListAsync(ct);

        // SessionStartTime/SessionEndTime ride along here for the same reason State and
        // CurrentSessionId do: Head Office has no other way to learn them. Session itself is
        // never synced (only Bill is, once paid), so without this a session that just started,
        // or that was just transferred onto a different PC, showed at Head Office as a bare
        // "Active" with nothing to time it against - or worse, was read as confirmed
        // open-ended simply because the real answer was missing. See Pc.CurrentSessionStartTime.
        var pcs = await db.Pcs.AsNoTracking()
            .Where(p => p.BranchId == branchId && !p.IsDeleted)
            .Select(p => new PcStateDto
            {
                PcId = p.Id,
                State = p.State.ToString().ToLowerInvariant(),
                CurrentSessionId = p.CurrentSessionId,
                SessionStartTime = p.CurrentSession != null ? p.CurrentSession.StartTime : (DateTimeOffset?)null,
                SessionEndTime = p.CurrentSession != null ? p.CurrentSession.EndTime : null,
                SessionPackagePrice = p.CurrentSession != null ? p.CurrentSession.PackagePrice : null,
                SessionPlannedDurationMin = p.CurrentSession != null ? p.CurrentSession.PlannedDurationMin : null,
                PoweredOff = p.PoweredOff,
            })
            .ToListAsync(ct);

        var activeSessions = await db.Sessions.AsNoTracking()
            .CountAsync(s => s.BranchId == branchId && s.State == SessionState.Active, ct);

        // Null when nothing is open, and that is not the same as zero: an empty drawer and no
        // drawer at all mean different things to whoever is looking at this.
        var drawer = await db.CashRegisters.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.BusinessDay == today && r.Status != CashRegisterStatus.Closed)
            .OrderByDescending(r => r.OpenedAt)
            .Select(r => (decimal?)r.ExpectedDrawerCash)
            .FirstOrDefaultAsync(ct);

        var takings = await db.Payments.AsNoTracking()
            .Where(p => p.BranchId == branchId && p.CreatedAt >= dayStart && p.CreatedAt < dayEnd)
            .SumAsync(p => (decimal?)p.TotalAmount, ct) ?? 0m;

        // How far behind sync is. Anything queued and undelivered is money Head Office cannot
        // see yet, so it belongs on the same screen as the takings rather than buried in a log.
        var undelivered = await db.SyncOutboxEntries.AsNoTracking()
            .CountAsync(e => e.SyncedAt == null, ct);

        var beat = new BranchHeartbeatDto
        {
            BranchId = branchId,
            Version = RunningVersion,
            MachineName = Environment.MachineName,
            ConfigVersion = _configVersion,
            BranchLocalTime = IndiaTime.Now,
            OperatorsOnDuty = onDuty,
            Pcs = pcs,
            ActiveSessions = activeSessions,
            DrawerExpected = drawer,
            TakingsToday = takings,
            UndeliveredRecords = undelivered,
        };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var response = await client.PostAsync(
            $"{headOffice}/api/branch-status",
            new StringContent(JsonSerializer.Serialize(beat), Encoding.UTF8, "application/json"),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            if (DateTimeOffset.UtcNow - _lastComplaint > ComplainAtMost)
            {
                _lastComplaint = DateTimeOffset.UtcNow;
                _logger.LogWarning("Head Office refused this branch's status: {Status}.", response.StatusCode);
            }
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        // Each independent of the other, and this is the fix for a real failure: applying
        // config and running commands used to be two plain calls in a row inside this method,
        // with only the OUTER catch in ExecuteAsync able to see either one throw - and that
        // catch logs "Cannot reach Head Office", which is wrong and actively misleading when
        // the branch is talking to Head Office perfectly well and something else broke.
        //
        // On a real branch this meant every start_session and stop_session command sent to it
        // sat unconfirmed for the full five-minute give-up window, over and over, with the
        // branch's own log insisting the connection was down the entire time. Whatever the
        // true cause was is still unknown, because the exception that would have named it was
        // never seen - it was swallowed four call-frames up and replaced with a guess.
        //
        // Isolating the two means a config apply that fails no longer prevents a command from
        // being tried, and a command that throws now reports back a real reason instead of
        // going quiet - so the next time this happens, the result message says what broke.
        try
        {
            // No shared scope is passed in at all any more - see ApplyConfigFromReplyAsync's own
            // comment for why. It used to take one DbContext for the whole config (every
            // operator, every menu item, every member on one SaveChangesAsync), which meant a
            // single colliding row anywhere in that batch failed everything else in it too - and
            // did so on every beat forever, since Head Office resends the same batch until the
            // branch's reported fingerprint changes, which it never did while the save kept
            // throwing. Now each row gets its own scope and its own save, isolated the same way
            // RunCommandsFromReplyAsync already isolates one command from the next.
            await ApplyConfigFromReplyAsync(body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Applying Head Office's settings failed outside the per-row handling that would " +
                "normally have caught it and reported back why. Nothing else this beat is affected " +
                "by this alone, but the branch keeps whatever settings it already had.");
        }

        try
        {
            await RunCommandsFromReplyAsync(_serviceProvider, client, headOffice, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Something threw while carrying out Head Office's instructions, outside the " +
                "per-command handling that would normally have caught it and reported back why. " +
                "Whatever commands were pending this beat received no answer and will be retried.");
        }
    }

    /// <summary>
    /// Carries out whatever Head Office has asked this branch to do, and reports back.
    ///
    /// This is the other missing direction, and it is deliberately narrow: a command is never
    /// allowed to write a session, a PC, or any other fact directly. It calls the exact same
    /// service method an operator's own click calls - ISessionService.StopSessionAsync - so a
    /// remote stop is billed, logged and synced upward exactly like a local one. Head Office
    /// asking and Head Office writing are not the same thing, and only the first is safe: the
    /// second is what put an unbillable ₹60 session on ADJ-PC-01 in the first place.
    ///
    /// Takes the root provider, not an existing scope, and opens a fresh one per command inside
    /// the loop below - never one scope shared across the whole batch. A batch is any number of
    /// unrelated commands; sharing one DbContext across all of them means a failed insert in
    /// command 1 stays tracked as Added after its own SaveChanges throws, and command 2's own
    /// SaveChanges - or CommitTransactionAsync, same thing underneath - tries to flush it right
    /// alongside whatever command 2 actually did, and fails for command 1's reason with no
    /// mention of command 1 anywhere. This already happened once (the 2.4.11 stock-delivery /
    /// audit-log fault) and reappeared in a different shape (a start_session command failing on
    /// IX_members_Username - a table StartSessionAsync never touches - because an earlier member
    /// sync's failed insert was still sitting in the same shared DbContext). Fixing the specific
    /// insert each time treats the symptom; a fresh scope per command removes the cross-talk
    /// itself.
    /// </summary>
    private async Task RunCommandsFromReplyAsync(
        IServiceProvider serviceProvider, HttpClient client, string headOffice, string body, CancellationToken ct)
    {
        List<BranchCommandDto>? commands;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("commands", out var node) || node.ValueKind != JsonValueKind.Array) return;

            commands = JsonSerializer.Deserialize<List<BranchCommandDto>>(node.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the instructions Head Office sent ({Reason}).",
                ex.GetBaseException().Message);
            return;
        }

        if (commands is null || commands.Count == 0) return;

        foreach (var command in commands)
        {
            bool succeeded;
            string message;
            try
            {
                using var commandScope = serviceProvider.CreateAsyncScope();
                (succeeded, message) = await RunOneCommandAsync(commandScope.ServiceProvider, command, ct);
            }
            catch (Exception ex)
            {
                // The one change that matters most here: this used to be uncaught, which meant
                // an exception here killed the confirmation POST below AND every command still
                // left in this batch, and surfaced nowhere except a generic "cannot reach Head
                // Office" four frames away. Now it becomes an ordinary failure result like any
                // other - reported back honestly, and the next command in the batch still runs.
                succeeded = false;
                message = $"Unexpected error: {ex.GetBaseException().Message}";
                _logger.LogError(ex, "Command {Type} ({Id}) threw outside its own error handling.",
                    command.CommandType, command.Id);
            }

            _logger.LogInformation("Command {Type} ({Id}) from Head Office: {Result} - {Message}",
                command.CommandType, command.Id, succeeded ? "done" : "failed", message);

            try
            {
                var result = new BranchCommandResultDto
                {
                    CommandId = command.Id,
                    Succeeded = succeeded,
                    Message = message,
                };

                await client.PostAsync(
                    $"{headOffice}/api/branch-status/commands/result",
                    new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json"),
                    ct);
            }
            catch (Exception ex)
            {
                // The command still ran (or was refused) - only the confirmation was lost. Head
                // Office will hand the same command back on the next beat, this branch will find
                // the session already stopped, and will confirm it then. Nothing to do here but
                // let that happen.
                _logger.LogWarning("Ran command {Id} but could not confirm it to Head Office ({Reason}).",
                    command.Id, ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>
    /// One command, dispatched to whatever actually knows how to do it.
    ///
    /// An unrecognised CommandType fails cleanly rather than throwing - a branch on an older
    /// build meeting a command type invented after it shipped should say so plainly, not crash
    /// the beat that everything else here depends on.
    /// </summary>
    private static async Task<(bool succeeded, string message)> RunOneCommandAsync(
        IServiceProvider scoped, BranchCommandDto command, CancellationToken ct)
    {
        switch (command.CommandType)
        {
            case BranchCommands.StopSession:
                return await RunStopSessionAsync(scoped, command.Payload, ct);

            case BranchCommands.StartSession:
                return await RunStartSessionAsync(scoped, command.Payload, ct);

            case BranchCommands.SetPcState:
                return await RunSetPcStateAsync(scoped, command.Payload, ct);

            case BranchCommands.AddPc:
                return await RunAddPcAsync(scoped, command.Payload, ct);

            case BranchCommands.UpdatePc:
                return await RunUpdatePcAsync(scoped, command.Payload, ct);

            case BranchCommands.DeletePc:
                return await RunDeletePcAsync(scoped, command.Payload, ct);

            case BranchCommands.TransferSession:
                return await RunTransferSessionAsync(scoped, command.Payload, ct);

            case BranchCommands.InstallVersion:
                return await RunInstallVersionAsync(scoped, command.Payload, ct);

            case BranchCommands.ProcessPayment:
                return await RunProcessPaymentAsync(scoped, command.Payload, ct);

            case BranchCommands.SetMaintenance:
                return await RunSetMaintenanceAsync(scoped, command.Payload, ct);

            case BranchCommands.AdjustStock:
                return await RunAdjustStockAsync(scoped, command.Payload, ct);

            case BranchCommands.CreateReservation:
                return await RunCreateReservationAsync(scoped, command.Payload, ct);

            case BranchCommands.CancelReservation:
                return await RunCancelReservationAsync(scoped, command.Payload, ct);

            case BranchCommands.DeleteReservation:
                return await RunDeleteReservationAsync(scoped, command.Payload, ct);

            case BranchCommands.StartReservation:
                return await RunStartReservationAsync(scoped, command.Payload, ct);

            case BranchCommands.OverrideReservation:
                return await RunOverrideReservationAsync(scoped, command.Payload, ct);

            case BranchCommands.ApplyDiscount:
                return await RunApplyDiscountAsync(scoped, command.Payload, ct);

            case BranchCommands.EditPaymentMethod:
                return await RunEditPaymentMethodAsync(scoped, command.Payload, ct);

            case BranchCommands.DeleteInventoryItem:
                return await RunDeleteInventoryItemAsync(scoped, command.Payload, ct);

            case BranchCommands.SetMemberPassword:
                return await RunSetMemberPasswordAsync(scoped, command.Payload, ct);

            case BranchCommands.AdminEditMemberValues:
                return await RunAdminEditMemberValuesAsync(scoped, command.Payload, ct);

            case BranchCommands.RelaySharedStockDelta:
                return await RunRelaySharedStockDeltaAsync(scoped, command.Payload, ct);

            default:
                return (false, $"This branch does not know the command '{command.CommandType}' yet.");
        }
    }

    /// <summary>
    /// Starts play on one of this branch's PCs because Head Office asked.
    ///
    /// Every check an operator's own Start would hit still applies - PC free, pricing profile
    /// present, member's wallet funded, member not already playing elsewhere - because this is
    /// literally the same method. A remote start that skipped them would be the old bug wearing
    /// a different hat: a session the counter cannot account for.
    ///
    /// The operator recorded is whoever is on shift here right now, since the session's takings
    /// belong in that person's shift and till. If nobody is on shift the branch refuses, and
    /// says so - money taken outside a shift has nowhere to be counted at End of Day.
    /// </summary>
    private static async Task<(bool, string)> RunStartSessionAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid pcId;
        SessionStartDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            pcId = root.GetProperty("pcId").GetGuid();

            dto = new SessionStartDto
            {
                PcId = pcId,
                MemberId = root.TryGetProperty("memberId", out var m) && m.ValueKind != JsonValueKind.Null
                    ? m.GetGuid() : null,
                CustomerName = root.TryGetProperty("customerName", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null,
                DurationMinutes = root.TryGetProperty("durationMinutes", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDecimal() : 0m,
                PackageName = root.TryGetProperty("packageName", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "Head Office" : "Head Office",
                ExpectedAmount = root.TryGetProperty("expectedAmount", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetDecimal() : 0m,
                Notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null,
            };
        }
        catch
        {
            return (false, "The start command arrived without a readable PC and duration.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();

        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null) return (false, "No such PC exists at this branch.");

        var shift = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == pc.BranchId && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .FirstOrDefaultAsync(ct);

        if (shift is null)
            return (false,
                "Nobody is on shift at this branch, so there is no till to bill this into. " +
                "Start a shift at the counter first.");

        try
        {
            var sessionService = scoped.GetRequiredService<ISessionService>();
            var result = await sessionService.StartSessionAsync(pc.BranchId, shift.OperatorId, shift.Id, dto);

            return (true, $"Started on {result.PcName} for {result.DurationMinutes} min, Rs {result.ExpectedAmount}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Moves an active session to a different PC at this branch, because Head Office asked.
    ///
    /// Runs through ISessionService.TransferSessionAsync - the exact method a drag on the
    /// counter's own screen calls - so a remote transfer is never anything but the branch
    /// physically doing what it looks like it did. This exists because the direct write it
    /// replaces was worse than doing nothing: it made Head Office's own screen show the move
    /// as complete while the real PC and the real customer had not moved at all, and left the
    /// session's own PcId pointing at the wrong machine for good, with no heartbeat ever going
    /// to correct it back.
    /// </summary>
    private static async Task<(bool, string)> RunTransferSessionAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid sessionId, targetPcId;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            sessionId = doc.RootElement.GetProperty("sessionId").GetGuid();
            targetPcId = doc.RootElement.GetProperty("targetPcId").GetGuid();
        }
        catch
        {
            return (false, "The transfer command arrived without a readable session and target PC.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return (false, "No such session exists at this branch.");

        if (session.PcId == targetPcId)
            return (true, "Already on that PC - nothing to do.");

        if (session.State != SessionState.Active)
            return (false, $"Session is {session.State.ToString().ToLowerInvariant()}, not active - cannot transfer.");

        try
        {
            var sessionService = scoped.GetRequiredService<ISessionService>();
            var result = await sessionService.TransferSessionAsync(
                session.BranchId, session.OperatorId, sessionId, new SessionTransferDto { TargetPcId = targetPcId });

            return (true, $"Moved to {result.PcName}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Applies a stock delivery an Admin or Super Admin recorded from Head Office, exactly as
    /// if it had been entered here at the counter - see InventoryController.AddStock for why
    /// this only ever adds to what the branch already has, never sets an absolute number.
    /// </summary>
    private static async Task<(bool, string)> RunAdjustStockAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid inventoryId;
        int quantity;
        string? reason;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            inventoryId = root.GetProperty("inventoryId").GetGuid();
            quantity = root.GetProperty("quantity").GetInt32();
            reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
        }
        catch
        {
            return (false, "The stock delivery arrived without a readable item and quantity.");
        }

        if (quantity <= 0)
            return (false, "A stock delivery must add at least one unit.");

        var db = scoped.GetRequiredService<AppDbContext>();
        var item = await db.Set<InventoryItem>().FirstOrDefaultAsync(i => i.Id == inventoryId, ct);

        if (item is null)
            return (false, "No such menu item exists at this branch.");

        var oldStock = item.CurrentStock;
        var now = DateTimeOffset.UtcNow;

        item.CurrentStock += quantity;
        item.UpdatedAt = now;

        if (item.Status == FoodAvailability.OutOfStock && item.CurrentStock > 0)
            item.Status = FoodAvailability.Available;

        // Whoever is on shift, the same convention RunSetMaintenanceAsync uses - they are the
        // person who will actually be asked about a delivery that shows up at their counter.
        //
        // Unlike a PC, a stock delivery has no reasonable "refuse if nobody's on shift" rule:
        // a supplier can drop off stock overnight, and Head Office logging that while the shop
        // is closed is a completely ordinary case, not an error. So this never refuses - only
        // OperatorId is left null when there is truly nobody to name, and UserName is always
        // set explicitly below regardless, so there is nothing left for AuditService's own
        // Operator/User lookup to fail at.
        //
        // This is the fix for the bug that shipped in 2.4.11: this audit entry was built with
        // none of UserId, OperatorId or UserName set. AuditService found nothing to resolve a
        // name from and inserted NULL into audit_logs.UserName, which the table's NOT NULL
        // constraint rejects - and that failed INSERT, still tracked as a pending change on
        // this same DbContext, then failed the *next* SaveChangesAsync too: the one actually
        // recording the stock delivery. The whole command reported "Unexpected error: 23502
        // ... UserName" and nothing was saved, stock included.
        var onShiftOperatorId = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == item.BranchId && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .Select(s => (Guid?)s.OperatorId)
            .FirstOrDefaultAsync(ct);

        string? onShiftName = onShiftOperatorId is { } opId
            ? await db.Operators.AsNoTracking().Where(o => o.Id == opId).Select(o => o.FullName).FirstOrDefaultAsync(ct)
            : null;

        db.Add(new InventoryLog
        {
            Id = Guid.NewGuid(),
            InventoryId = item.Id,
            BranchId = item.BranchId,
            OperatorId = onShiftOperatorId,
            Action = "refill",
            Quantity = quantity,
            OldValue = oldStock.ToString(),
            NewValue = item.CurrentStock.ToString(),
            Reason = reason ?? "Stock delivery (from Head Office)",
            CreatedAt = now,
        });

        var audit = scoped.GetRequiredService<IAuditService>();
        await audit.LogAsync(new AuditEntry
        {
            OperatorId = onShiftOperatorId,
            Action = AuditActions.StockAdd,
            UserRole = Roles.Admin,
            UserName = string.IsNullOrWhiteSpace(onShiftName) ? "Head Office" : onShiftName,
            TargetType = "inventory_item",
            TargetId = item.Id,
            BranchId = item.BranchId,
            Details = new { itemName = item.ItemName, quantity, oldStock, newStock = item.CurrentStock, reason, viaRemoteCommand = true },
        });

        await db.SaveChangesAsync(ct);

        return (true, $"{item.ItemName}: {oldStock} + {quantity} = {item.CurrentStock}.");
    }

    /// <summary>
    /// Removes a menu item at the branch that actually holds it - see
    /// BranchCommands.DeleteInventoryItem for why a Head Office delete has to travel here to
    /// mean anything. Same hard-delete-or-deactivate fallback InventoryController.Delete uses
    /// locally, so a branch cannot end up more or less deletable than Head Office is.
    /// </summary>
    /// <summary>
    /// Stores the password a member just set from the link in their email.
    ///
    /// Head Office did the hashing, because Head Office is where the link led - see
    /// BranchCommands.SetMemberPassword for why it has to lead there. This end only writes the
    /// result into the branch's own copy of the member, which is the copy the gaming PCs check,
    /// and is what lets that member log in at the counter afterwards with the internet down.
    ///
    /// The reset token is cleared here as well. Head Office has already spent it, and a token
    /// left sitting valid in a second database is a second chance to use it.
    /// </summary>
    private static async Task<(bool, string)> RunSetMemberPasswordAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid memberId;
        string passwordHash;
        DateTimeOffset? passwordChangedAt;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            memberId = doc.RootElement.GetProperty("memberId").GetGuid();
            passwordHash = doc.RootElement.GetProperty("passwordHash").GetString() ?? "";
            passwordChangedAt = doc.RootElement.TryGetProperty("passwordChangedAt", out var pca) && pca.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return (false, "The password command arrived without a readable member id and hash.");
        }

        // Never store an empty hash. BCrypt.Verify against "" throws rather than returning
        // false, so a blank arriving here would not open the account - it would break logging
        // into it at all, and only for that one member, which is the kind of fault nobody
        // connects back to a release.
        if (string.IsNullOrWhiteSpace(passwordHash))
            return (false, "The password command carried no hash.");

        var db = scoped.GetRequiredService<AppDbContext>();
        var member = await db.Set<Member>().FirstOrDefaultAsync(m => m.Id == memberId, ct);

        // Genuinely absent (a member deleted at Head Office, say) and merely "not synced here
        // yet" look identical from this query alone - a member created seconds ago, or a
        // password reset requested moments after signup, simply has not reached this branch's
        // own copy through the ordinary heartbeat push yet. Treating that as done would be
        // exactly the old bug: Head Office already told the customer their reset succeeded, and
        // nothing would ever tell the branch to actually store it. See
        // BranchCommands.MemberNotYetSyncedMessage for why this exact wording matters: Head
        // Office's CommandResult handler leaves the command Pending for this one message
        // instead of closing it, so it keeps trying on later heartbeats until the member has
        // arrived and this can actually apply - or the ordinary 48-hour give-up closes it for
        // a member that truly never will.
        if (member is null) return (false, BranchCommands.MemberNotYetSyncedMessage);

        // Same "whichever is actually newer wins" guard (Member.IsPasswordNewer) the ordinary
        // heartbeat push uses - this command can be delivered late (the branch was offline, or
        // it simply took a few heartbeats to land), and applying it blindly could overwrite a
        // password the member set even more recently through some other path with this older one.
        if (member.IsPasswordNewer(passwordChangedAt))
        {
            member.PasswordHash = passwordHash;
            member.PasswordChangedAt = passwordChangedAt;
        }
        member.ResetToken = null;
        member.ResetTokenExpiry = null;
        await db.SaveChangesAsync(ct);

        var audit = scoped.GetRequiredService<IAuditService>();
        await audit.LogAsync(new AuditEntry
        {
            Action = AuditActions.PasswordReset,
            UserRole = Roles.Admin,
            UserName = "Head Office",
            TargetType = "member",
            TargetId = memberId,
            BranchId = member.HomeBranchId ?? Guid.Empty,
            Details = new { memberNumber = member.MemberNumber, viaRemoteCommand = true },
        });

        return (true, "Password updated at this branch.");
    }

    /// <summary>
    /// A Super Admin's direct balance/stat override, carried out on this branch's own copy of
    /// the member - the one its counter actually reads. Runs through the exact same
    /// MemberService.AdminEditValuesAsync a local admin-edit screen would call, so the branch
    /// gets its own Correction wallet transaction and audit entry out of this too, not just a
    /// changed number with nothing explaining it later.
    /// </summary>
    private static async Task<(bool, string)> RunAdminEditMemberValuesAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid memberId, adminId;
        string? adminName;
        Application.DTOs.Members.AdminEditMemberValuesDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            memberId = root.GetProperty("memberId").GetGuid();
            adminId = root.GetProperty("adminId").GetGuid();
            adminName = root.TryGetProperty("adminName", out var n) ? n.GetString() : null;
            dto = JsonSerializer.Deserialize<Application.DTOs.Members.AdminEditMemberValuesDto>(
                root.GetProperty("dto").GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Empty member-edit payload.");
        }
        catch
        {
            return (false, "The member-edit command arrived without a readable member id and values.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var member = await db.Set<Member>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId, ct);

        // Same reasoning as RunSetMemberPasswordAsync just above - see that comment. A member
        // this branch has not yet received via its own heartbeat push is not distinguishable
        // here from one that never will arrive, so this retries rather than closing quietly.
        if (member is null) return (false, BranchCommands.MemberNotYetSyncedMessage);

        try
        {
            var memberService = scoped.GetRequiredService<IMemberService>();
            // remoteAdminName (never null here - this command only ever comes from Head Office)
            // is what tells AdminEditValuesAsync this actor's id is a Head Office id, not a
            // local one: WalletTransaction.AdminId is FK'd to this branch's own `users` table,
            // which has never heard of a Head Office account, so writing adminId there failed
            // outright on every single remote balance edit. See that method's own comment.
            var result = await memberService.AdminEditValuesAsync(
                member.HomeBranchId ?? Guid.Empty, adminId, memberId, dto, adminName ?? "Head Office");

            return (true, $"Updated at this branch. Gaming balance now Rs {result.GamingBalance}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// A sibling branch's shared food/snacks stock moving by some amount, applied here as the
    /// same movement to this branch's own local copy of that item.
    ///
    /// Clamped to zero. Two branches sharing one pantry can each only ever know their own count
    /// between sync beats, so a same-moment sale at both is still possible - each branch's own
    /// local sale already refused to oversell against what it knew (FoodOrderService), but the
    /// two branches' otherwise-valid sales can still add up to more than the shared item ever
    /// had. Letting the total go negative here just moves that same overselling into the
    /// stock count itself instead of preventing it; zero is the honest floor for "how much is
    /// left," and the sibling branch that sold into an already-empty shelf is the one this
    /// shows up for by seeing 0 instead of the sale count it expected.
    /// </summary>
    private static async Task<(bool, string)> RunRelaySharedStockDeltaAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid inventoryItemId, relayEventId;
        int delta;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            inventoryItemId = root.GetProperty("inventoryItemId").GetGuid();
            delta = root.GetProperty("delta").GetInt32();
            relayEventId = root.GetProperty("relayEventId").GetGuid();
        }
        catch
        {
            return (false, "The shared-stock command arrived without a readable item id and amount.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();

        // The dedupe guard: a redelivery of the exact same relay (this command retried, or
        // Head Office's own fan-out retried after a partial failure) must not move the count
        // twice. See InventoryLog.SourceRelayEventId.
        if (await db.Set<InventoryLog>().AnyAsync(l => l.SourceRelayEventId == relayEventId, ct))
            return (true, "Already applied.");

        var item = await db.Set<InventoryItem>().FirstOrDefaultAsync(i => i.Id == inventoryItemId, ct);

        // Not a failure worth logging as one: this branch's own copy of a shared item is
        // materialised by the ordinary catalogue push (BranchHeartbeatController.
        // ConfigForBranchIfChangedAsync), which cannot have landed after a sale already
        // happened at a sibling for an item nobody here has ever heard of. Retrying on the
        // next beat gives that push a chance to arrive first.
        if (item is null) return (false, "This branch does not know this shared item yet.");

        item.CurrentStock = Math.Max(0, item.CurrentStock + delta);

        db.Set<InventoryLog>().Add(new InventoryLog
        {
            Id = Guid.NewGuid(),
            InventoryId = item.Id,
            BranchId = item.BranchId,
            Action = "shared_sync",
            Quantity = delta,
            Reason = "Shared stock update from a linked branch",
            SourceRelayEventId = relayEventId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        return (true, $"Applied. {item.ItemName} now at {item.CurrentStock}.");
    }

    private static async Task<(bool, string)> RunDeleteInventoryItemAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid inventoryItemId;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            inventoryItemId = doc.RootElement.GetProperty("inventoryItemId").GetGuid();
        }
        catch
        {
            return (false, "The delete command arrived without a readable item id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var item = await db.Set<InventoryItem>().FirstOrDefaultAsync(i => i.Id == inventoryItemId, ct);

        // Already gone at this branch - not a failure. Head Office may be closing out a
        // command against a row the branch removed some other way in the meantime.
        if (item is null) return (true, "This item no longer exists at this branch.");

        var itemName = item.ItemName;
        var audit = scoped.GetRequiredService<IAuditService>();

        try
        {
            db.Remove(item);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(new AuditEntry
            {
                Action = AuditActions.ItemDelete,
                UserRole = Roles.Admin,
                UserName = "Head Office",
                TargetType = "inventory_item",
                TargetId = inventoryItemId,
                BranchId = item.BranchId,
                Details = new { itemName, permanent = true, viaRemoteCommand = true },
            });

            return (true, $"{itemName} permanently deleted.");
        }
        catch (Exception)
        {
            // The failed Remove above left this entity tracked as Deleted - the same trap
            // that broke RunAdjustStockAsync once already (2.4.11's UserName 23502). Setting
            // Status on an entity still marked Deleted does not turn it into an Update: EF
            // still tries to DELETE it, hits the identical foreign key violation a second
            // time, and this catch has nothing to catch that second failure - it was
            // reaching the caller as a raw, unhandled "Unexpected error: 23503...", not the
            // graceful "deactivated instead" this branch was written to give. Confirmed live
            // on Lays at Citylight. Unchanged undoes the pending delete before the update.
            db.Entry(item).State = EntityState.Unchanged;
            item.Status = FoodAvailability.Disabled;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(new AuditEntry
            {
                Action = AuditActions.ItemDelete,
                UserRole = Roles.Admin,
                UserName = "Head Office",
                TargetType = "inventory_item",
                TargetId = inventoryItemId,
                BranchId = item.BranchId,
                Details = new { itemName, permanent = false, reason = "existing orders reference it", viaRemoteCommand = true },
            });

            return (true, $"{itemName} cannot be permanently deleted here (existing orders reference it) - deactivated instead.");
        }
    }

    /// <summary>
    /// Whoever a remote-triggered reservation action should be attributed to: the person on
    /// shift right now, since they are the one who will actually deal with the customer and
    /// the PC this affects. If nobody is on shift - Head Office booking ahead for a branch
    /// that is currently closed is an entirely ordinary case, not an error - falls back to any
    /// operator this branch has, so the booking still has a real person behind it rather than
    /// being refused over an attribution problem alone.
    /// </summary>
    private static async Task<Guid?> OnShiftOrAnyOperatorAsync(AppDbContext db, Guid branchId, CancellationToken ct)
    {
        var onShift = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == branchId && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .Select(s => (Guid?)s.OperatorId)
            .FirstOrDefaultAsync(ct);

        if (onShift is not null) return onShift;

        return await db.Operators.AsNoTracking()
            .Where(o => o.BranchId == branchId && o.Status == OperatorStatus.Active)
            .OrderByDescending(o => o.LastLogin)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Books a PC because Head Office asked for it on the customer's behalf.
    ///
    /// The PC, not a header, decides the branch - the same reasoning as every other
    /// PC-addressed command here, and it matters more for a booking than most: a stale or
    /// missing branch id here would hold the wrong shop's machine for a customer who is
    /// standing in a different city.
    /// </summary>
    private static async Task<(bool, string)> RunCreateReservationAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid pcId;
        Application.DTOs.Reservations.CreateReservationDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            pcId = doc.RootElement.GetProperty("pcId").GetGuid();
            dto = JsonSerializer.Deserialize<Application.DTOs.Reservations.CreateReservationDto>(
                payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Empty reservation payload.");
            dto.PcId = pcId;
        }
        catch
        {
            return (false, "The reservation command arrived without a readable PC and time.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null) return (false, "No such PC exists at this branch.");

        var actorId = await OnShiftOrAnyOperatorAsync(db, pc.BranchId, ct);
        if (actorId is null)
            return (false, "This branch has no operator at all to record the reservation against.");

        try
        {
            var reservationService = scoped.GetRequiredService<IReservationService>();
            var result = await reservationService.CreateReservationAsync(pc.BranchId, actorId.Value, dto);
            return (true, $"Booked {result.CustomerName} on {result.PcName ?? pc.PcNumber} for {result.ReservationTime:g}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static async Task<(bool, string)> RunCancelReservationAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid reservationId;
        string? reason;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            reservationId = root.GetProperty("reservationId").GetGuid();
            reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
        }
        catch
        {
            return (false, "The cancel command arrived without a readable reservation id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var reservation = await db.Set<Reservation>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null) return (false, "No such reservation exists at this branch.");

        // The operator may already have cancelled it themselves in the time this took to
        // arrive - the correct outcome either way is that it is cancelled.
        if (reservation.State != ReservationState.Pending)
            return (true, $"Already {reservation.State.ToString().ToLowerInvariant()} - nothing to do.");

        var actorId = await OnShiftOrAnyOperatorAsync(db, reservation.BranchId, ct);
        if (actorId is null)
            return (false, "This branch has no operator at all to record the cancellation against.");

        try
        {
            var reservationService = scoped.GetRequiredService<IReservationService>();
            await reservationService.CancelReservationAsync(
                reservation.BranchId, actorId.Value, reservationId,
                new Application.DTOs.Reservations.CancelReservationDto { Reason = reason });
            return (true, $"Cancelled the booking for {reservation.CustomerName}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static async Task<(bool, string)> RunDeleteReservationAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid reservationId;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            reservationId = doc.RootElement.GetProperty("reservationId").GetGuid();
        }
        catch
        {
            return (false, "The remove command arrived without a readable reservation id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var reservation = await db.Set<Reservation>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        // Already gone at this branch (an operator here may have removed it themselves in the
        // time this took to arrive) - the correct outcome either way is that it no longer exists.
        if (reservation is null) return (true, "Already removed - nothing to do.");

        var actorId = await OnShiftOrAnyOperatorAsync(db, reservation.BranchId, ct);
        if (actorId is null)
            return (false, "This branch has no operator at all to record the removal against.");

        try
        {
            var reservationService = scoped.GetRequiredService<IReservationService>();
            await reservationService.DeleteReservationAsync(reservation.BranchId, actorId.Value, reservationId);
            return (true, $"Removed the booking for {reservation.CustomerName}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static async Task<(bool, string)> RunStartReservationAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid reservationId;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            reservationId = doc.RootElement.GetProperty("reservationId").GetGuid();
        }
        catch
        {
            return (false, "The start command arrived without a readable reservation id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var reservation = await db.Set<Reservation>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null) return (false, "No such reservation exists at this branch.");

        if (reservation.State != ReservationState.Pending)
            return (true, $"Already {reservation.State.ToString().ToLowerInvariant()} - nothing to do.");

        var actorId = await OnShiftOrAnyOperatorAsync(db, reservation.BranchId, ct);
        if (actorId is null)
            return (false, "Nobody is on shift at this branch, so there is no till to bill this into.");

        try
        {
            var reservationService = scoped.GetRequiredService<IReservationService>();
            var result = await reservationService.StartReservedSessionAsync(
                reservation.BranchId, actorId.Value, reservationId);
            return (true, $"Started the session for {result.CustomerName}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static async Task<(bool, string)> RunOverrideReservationAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid reservationId;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            reservationId = root.GetProperty("reservationId").GetGuid();
            reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "Overridden from Head Office" : "Overridden from Head Office";
        }
        catch
        {
            return (false, "The override command arrived without a readable reservation id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var reservation = await db.Set<Reservation>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null) return (false, "No such reservation exists at this branch.");

        if (reservation.State != ReservationState.Pending)
            return (true, $"Already {reservation.State.ToString().ToLowerInvariant()} - nothing to do.");

        var actorId = await OnShiftOrAnyOperatorAsync(db, reservation.BranchId, ct);
        if (actorId is null)
            return (false, "This branch has no operator at all to record the override against.");

        try
        {
            var reservationService = scoped.GetRequiredService<IReservationService>();
            await reservationService.OverrideReservationAsync(
                reservation.BranchId, actorId.Value, reservationId,
                new Application.DTOs.Reservations.OverrideReservationDto { Reason = reason });
            return (true, $"Overrode the booking for {reservation.CustomerName}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Takes a PC out of service, or puts it back, because Head Office asked.
    ///
    /// Refuses while somebody is playing on it. Flipping a busy machine to maintenance would
    /// leave a live session attached to a PC that no longer admits to having one, and the
    /// operator would be unable to bill it - the exact shape of the original problem. Stop the
    /// session first; that is a separate command and Head Office can send both.
    /// </summary>
    private static async Task<(bool, string)> RunSetPcStateAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid pcId;
        string wanted;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            pcId = doc.RootElement.GetProperty("pcId").GetGuid();
            wanted = doc.RootElement.GetProperty("state").GetString() ?? string.Empty;
        }
        catch
        {
            return (false, "The PC state command arrived without a readable PC and state.");
        }

        if (!Enum.TryParse<PcState>(wanted.Replace("_", string.Empty), ignoreCase: true, out var state))
            return (false, $"This branch does not recognise the PC state '{wanted}'.");

        var db = scoped.GetRequiredService<AppDbContext>();
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null) return (false, "No such PC exists at this branch.");

        if (pc.State == state)
            return (true, $"Already {state.ToString().ToLowerInvariant()} - nothing to do.");

        if (pc.State is PcState.Active or PcState.AwaitingBilling)
            return (false,
                $"{pc.PcNumber} is {pc.State.ToString().ToLowerInvariant()} - stop the session first, " +
                "or the bill for it could not be collected.");

        pc.State = state;
        pc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return (true, $"{pc.PcNumber} is now {state.ToString().ToLowerInvariant()}.");
    }

    /// <summary>
    /// Adds a PC or console because Head Office asked - mirrors PcsController.Create exactly
    /// (duplicate check, pricing-profile fallback, console-vs-PC initial state), since that
    /// controller action is the one this replaces when the caller is at Head Office rather
    /// than at the branch itself.
    /// </summary>
    private static async Task<(bool, string)> RunAddPcAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        AppleEsportsErp.Application.DTOs.Settings.CreatePcDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppleEsportsErp.Application.DTOs.Settings.CreatePcDto>(payload);
        }
        catch
        {
            dto = null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.PcNumber))
            return (false, "The add-PC command arrived without a readable PC to create.");

        var db = scoped.GetRequiredService<AppDbContext>();

        var exists = await db.Pcs.AnyAsync(
            p => p.BranchId == dto.BranchId && p.PcNumber == dto.PcNumber && !p.IsDeleted, ct);
        if (exists)
            return (false, $"PC number {dto.PcNumber} already exists at this branch.");

        var pricingProfile = dto.PricingProfileId.HasValue
            ? await db.PricingProfiles.FirstOrDefaultAsync(
                p => p.Id == dto.PricingProfileId.Value && p.BranchId == dto.BranchId, ct)
            : await db.PricingProfiles.FirstOrDefaultAsync(
                p => p.BranchId == dto.BranchId && p.IsActive, ct);

        if (pricingProfile is null)
            return (false, "This branch has no Pricing Profile yet - create one before adding a PC.");

        var isConsole = string.Equals(dto.Zone, "Console", StringComparison.OrdinalIgnoreCase);

        var pc = new Pc
        {
            // Head Office's own id when this command came from there (see CreatePcDto.Id), so
            // its mirror and this branch's own row are the same row from the start, not two
            // that happen to share a PcNumber. Without this the branch generated its own fresh
            // id, Head Office's heartbeat-apply never learns a fresh id (it only ever updates
            // one it already recognises), and every state change on this PC was silently
            // ignored at Head Office forever. A fresh id only for a PC added locally at a
            // branch's own Settings page, which has no mirror to match.
            Id = dto.Id ?? Guid.NewGuid(),
            PcNumber = dto.PcNumber,
            PcName = dto.PcName ?? dto.PcNumber,
            BranchId = dto.BranchId,
            IpAddress = isConsole ? null : dto.IpAddress,
            Specs = dto.Specs ?? "{}",
            Zone = dto.Zone ?? "Standard",
            HardwareNotes = dto.HardwareNotes,
            PricingProfileId = pricingProfile.Id,
            State = isConsole ? PcState.Idle : PcState.AwaitingSetup,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Pcs.Add(pc);
        await db.SaveChangesAsync(ct);

        return (true, $"{pc.PcNumber} added to the fleet.");
    }

    /// <summary>
    /// Edits a PC's details on this branch's own database, carried out here so a change made
    /// from Head Office actually reaches the machine that shows it - see the comment on
    /// PcsController.Update for the fault this replaces.
    /// </summary>
    private static async Task<(bool, string)> RunUpdatePcAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        AppleEsportsErp.Application.DTOs.Settings.UpdatePcCommandDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppleEsportsErp.Application.DTOs.Settings.UpdatePcCommandDto>(payload);
        }
        catch
        {
            dto = null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.PcNumber))
            return (false, "The update-PC command arrived without a readable PC to update.");

        var db = scoped.GetRequiredService<AppDbContext>();
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == dto.Id && !p.IsDeleted, ct);

        if (pc is null)
            return (false, "No such PC exists at this branch to update.");

        var exists = await db.Pcs.AnyAsync(
            p => p.BranchId == pc.BranchId && p.PcNumber == dto.PcNumber && p.Id != dto.Id && !p.IsDeleted, ct);
        if (exists)
            return (false, $"PC number {dto.PcNumber} already exists at this branch.");

        var isConsole = string.Equals(dto.Zone, "Console", StringComparison.OrdinalIgnoreCase);

        pc.PcNumber = dto.PcNumber;
        pc.PcName = dto.PcName ?? dto.PcNumber;
        pc.IpAddress = isConsole ? null : dto.IpAddress;
        pc.Specs = dto.Specs ?? "{}";
        pc.Zone = dto.Zone ?? "Standard";
        pc.HardwareNotes = dto.HardwareNotes;
        pc.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return (true, $"{pc.PcNumber} updated.");
    }

    /// <summary>
    /// Soft-deletes a PC on this branch's own database - the other half of the same fault:
    /// removing a PC from Head Office's Settings page used to only ever remove it from Head
    /// Office's own mirror. The branch's own copy, and its own heartbeat's PcsTotal count, kept
    /// counting the row forever, which is the confirmed root cause of a branch reporting far
    /// more PCs than are physically real.
    /// </summary>
    private static async Task<(bool, string)> RunDeletePcAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        AppleEsportsErp.Application.DTOs.Settings.DeletePcCommandDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppleEsportsErp.Application.DTOs.Settings.DeletePcCommandDto>(payload);
        }
        catch
        {
            dto = null;
        }

        if (dto is null || dto.Id == Guid.Empty)
            return (false, "The delete-PC command arrived without a readable PC to remove.");

        var db = scoped.GetRequiredService<AppDbContext>();
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.Id == dto.Id && !p.IsDeleted, ct);

        // Already gone locally - not a failure. Nothing left to do, which is exactly what was
        // asked for.
        if (pc is null)
            return (true, "Already removed from this branch - nothing to do.");

        pc.IsDeleted = true;
        pc.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return (true, $"{pc.PcNumber} removed from the fleet.");
    }

    private static async Task<(bool, string)> RunStopSessionAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid sessionId;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            sessionId = doc.RootElement.GetProperty("sessionId").GetGuid();
        }
        catch
        {
            return (false, "The stop command arrived without a readable session id.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return (false, "No such session exists at this branch.");

        // Genuinely nothing left to do - not a failure. The operator may have stopped it
        // themselves in the time the command took to arrive, and that is the correct outcome
        // either way: the session is stopped, which is all Head Office actually asked for.
        if (session.State != SessionState.Active)
            return (true, $"Already {session.State.ToString().ToLowerInvariant()} - nothing to do.");

        try
        {
            var sessionService = scoped.GetRequiredService<ISessionService>();

            // deferPayment is NOT "skip billing for a remote stop" - it marks the bill paid on
            // the spot and writes the balance off as a CustomerCredit, as if the customer had
            // walked out owing money. A remote stop must never quietly forgive a debt nobody
            // has actually decided to forgive. False is the same outcome an operator's own
            // Stop button gives: if nothing is owed the PC frees immediately; if something is,
            // it goes to Awaiting Billing exactly as if stopped at the counter, and whoever is
            // standing there collects it normally.
            //
            // The operator who started the session is recorded as the one who stopped it,
            // because they are the one whose shift and till this affects - the same as if they
            // had pressed Stop themselves.
            var result = await sessionService.StopSessionAsync(
                session.BranchId, session.OperatorId, sessionId, deferPayment: false);

            return (true, $"Stopped. {result.PackageName}, billed {result.DurationMinutes} min, Rs {result.ExpectedAmount}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Installs the exact version Head Office named - downloads it, verifies it against the
    /// published hash, and runs it silently, the same three steps the desktop app's own
    /// UpdateService performs on its nightly check. The difference is what happens before
    /// those steps: UpdateService refuses anything that is not strictly newer than what is
    /// installed, because it is acting on its own, unsupervised, in the middle of the night.
    /// A command that reached this method already passed through a Super Admin naming this
    /// exact version on purpose - that decision is allowed to go backwards, and this trusts it
    /// without asking whether the number is bigger.
    ///
    /// Runs directly from inside this Windows service rather than handing off to the desktop
    /// app, and that is safe only because of how this service is registered: New-Service with
    /// no -Credential runs it as LocalSystem, which already has every right the installer
    /// needs. The desktop app has to request elevation with a UAC prompt because it normally
    /// runs as whoever is logged in; this has nothing to request permission from, and nothing
    /// to show the prompt on even if it needed one - a Windows service has no desktop session.
    ///
    /// What is worth naming plainly: the moment the installer stops this service to replace
    /// its files is also the moment Head Office's only channel to this branch goes quiet. A
    /// clean install restarts everything within moments and the next heartbeat confirms the
    /// new version. A failed one does not, and because the failure kills the very thing that
    /// would have reported it, there is nothing left here to send a "this went wrong" message
    /// with - recovering from that needs a person at the branch, or remote desktop to it. This
    /// is why the endpoint that queues this command is Super Admin only, one branch at a time,
    /// never a bulk push to the whole fleet at once.
    /// </summary>
    private static async Task<(bool, string)> RunInstallVersionAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        string version, sha256, downloadPath;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            version = root.GetProperty("version").GetString() ?? "";
            sha256 = root.GetProperty("sha256").GetString() ?? "";
            downloadPath = root.GetProperty("downloadPath").GetString() ?? "";

            if (version.Length == 0 || sha256.Length == 0 || downloadPath.Length == 0)
                throw new InvalidOperationException();
        }
        catch
        {
            return (false, "The install command arrived without a readable version, hash and download path.");
        }

        var configuration = scoped.GetRequiredService<IConfiguration>();
        var headOffice = configuration["Sync:HeadOfficeUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(headOffice))
            return (false, "This branch has no Head Office address configured.");

        var folder = Path.Combine(Path.GetTempPath(), "AppleEsportsUpdate");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"AppleEsports-Branch-Setup-{version}.exe");

        try
        {
            var httpClientFactory = scoped.GetRequiredService<IHttpClientFactory>();
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(15);   // installers are large

            using (var response = await client.GetAsync(
                $"{headOffice}{downloadPath}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    return (false, $"Head Office answered {(int)response.StatusCode} for the installer download.");

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination, ct);
            }

            string actual;
            await using (var stream = File.OpenRead(target))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            }

            // Corrupted in transit, or worse - either way this does not run.
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteInstaller(target);
                return (false, "The downloaded installer did not match the hash Head Office published. Nothing was run.");
            }
        }
        catch (Exception ex)
        {
            TryDeleteInstaller(target);
            return (false, $"Could not download or verify the installer: {ex.GetBaseException().Message}");
        }

        // An installer already running is the one thing that guarantees this cannot work, and it is
        // knowable before anything is launched.
        //
        // Inno Setup will not start a second install while one holds its mutex, so launching into
        // that does nothing at all - and this method used to report "verified and launched" anyway,
        // because launching a process is all it checked. A stuck installer from an earlier update
        // sat on a Citylight counter PC for an hour, and every attempt in that hour was downloaded
        // in full, refused, and reported as a success. The version on screen never moved and no
        // error existed anywhere to explain it.
        //
        // Refusing here, and saying why, is the difference between an hour of guessing and a
        // sentence that names the problem.
        var runningInstaller = Process.GetProcesses()
            .FirstOrDefault(p =>
            {
                try { return p.ProcessName.StartsWith("AppleEsports-Branch-Setup", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

        if (runningInstaller is not null)
        {
            TryDeleteInstaller(target);
            return (false,
                $"An installer is already running on this PC ({runningInstaller.ProcessName}, started " +
                $"{runningInstaller.StartTime:HH:mm}). Windows will not run two at once, so nothing was " +
                "installed. If it has been sitting there, end that process and send this again.");
        }

        try
        {
            // /LOG for the same reason the desktop app's own installs write one - without it a
            // failure this far into the process leaves nothing to read afterwards.
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Apple Esports", "logs", "remote-install.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);

            // No Verb = "runas" - this process is already LocalSystem, and a Windows service
            // has no desktop session to show a UAC prompt on even if it asked for one.
            Process.Start(new ProcessStartInfo(target)
            {
                // /NORESTARTAPPLICATIONS, not /RESTARTAPPLICATIONS. That flag hands the install to
                // Windows Restart Manager to close and reopen the user's applications - which is
                // meaningless from a service in session 0, where there is no user session to put
                // anything back, and is one more thing that can sit waiting on a desktop nobody
                // can reach. The app is started again by its own autostart entry regardless.
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NORESTARTAPPLICATIONS \"/LOG={log}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            TryDeleteInstaller(target);
            return (false, $"The installer was downloaded and verified but could not be started: {ex.GetBaseException().Message}");
        }

        return (true,
            $"Installer for {version} verified and launched. This branch's services restart shortly " +
            "running it - the next heartbeat confirms whether it took.");
    }

    private static void TryDeleteInstaller(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Takes a PC out of service, or puts it back, because Head Office asked - carrying the
    /// reason with it, so the branch's maintenance log reads the same as if the operator had
    /// typed it at the counter.
    ///
    /// Unlike RunSetPcStateAsync this does not refuse while the PC is busy. Head Office marking
    /// a machine faulty is a statement about the hardware, not about the booking, and a PC that
    /// has just started smoking should not stay bookable until someone remembers to stop the
    /// session first. The session is left alone deliberately: it is real, it is running, and the
    /// customer still owes for the minutes they played. Stopping it is a separate command Head
    /// Office can also send.
    ///
    /// The actor recorded is whoever is on shift here, because they are the person who will be
    /// asked about it. If nobody is on shift - a PC flagged overnight - it falls back to the
    /// operator who last used the machine, and only refuses when there is nobody at all to name.
    /// </summary>
    private static async Task<(bool, string)> RunSetMaintenanceAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid pcId;
        bool enable;
        string? reason;
        string? notes;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            pcId = root.GetProperty("pcId").GetGuid();
            enable = root.GetProperty("enable").GetBoolean();
            reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
        }
        catch
        {
            return (false, "The maintenance command arrived without a readable PC and setting.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null) return (false, "No such PC exists at this branch.");

        var alreadyThere = enable
            ? pc.State == PcState.UnderMaintenance
            : pc.State != PcState.UnderMaintenance;

        if (alreadyThere)
            return (true, enable
                ? $"{pc.PcNumber} is already under maintenance - nothing to do."
                : $"{pc.PcNumber} is not under maintenance - nothing to do.");

        var actorId = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == pc.BranchId && s.Status == ShiftStatus.Active)
            .OrderByDescending(s => s.LoginTime)
            .Select(s => (Guid?)s.OperatorId)
            .FirstOrDefaultAsync(ct)
            ?? pc.LastOperatorId;

        if (actorId is null)
            return (false,
                "Nobody is on shift and this PC has no last operator, so there is nobody at " +
                "this branch to record the maintenance against.");

        try
        {
            var pcManagement = scoped.GetRequiredService<IPcManagementService>();
            var maintenanceLogs = scoped.GetRequiredService<IMaintenanceLogService>();

            if (enable)
            {
                await maintenanceLogs.LogMaintenanceAsync(
                    pcId, pc.BranchId, actorId.Value, Roles.Operator,
                    string.IsNullOrWhiteSpace(reason) ? "Marked from Head Office" : reason);
            }
            else
            {
                var active = await maintenanceLogs.GetActiveMaintenanceAsync(pcId);
                if (active is not null)
                {
                    await maintenanceLogs.ResolveMaintenanceAsync(
                        active.Id, actorId.Value, Roles.Operator, notes);
                }
            }

            await pcManagement.MarkMaintenanceAsync(pcId, actorId.Value, Roles.Operator, enable);

            return (true, enable
                ? $"{pc.PcNumber} is now under maintenance."
                : $"{pc.PcNumber} is back in service.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Collects a payment because Head Office asked for it on the customer's behalf.
    ///
    /// The branch, not Head Office, owns the till and the register this money actually lands
    /// in - a payment marked "paid" only in Head Office's synced copy leaves the counter still
    /// showing the bill open and the PC still locked on Billing, because nothing here actually
    /// changed. Amounts and payment type travel in the command exactly as the person at Head
    /// Office entered them; the branch, operator and shift the money is credited to are read
    /// off the bill's own row, never trusted from the payload, so this can only ever pay the
    /// bill it names and nothing else.
    /// </summary>
    private static async Task<(bool, string)> RunProcessPaymentAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid billId;
        Application.DTOs.Billing.ProcessPaymentDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            billId = doc.RootElement.GetProperty("billId").GetGuid();
            dto = JsonSerializer.Deserialize<Application.DTOs.Billing.ProcessPaymentDto>(
                payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Empty payment payload.");
        }
        catch
        {
            return (false, "The payment command arrived without a readable bill id or amounts.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var bill = await db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, ct);

        if (bill is null)
            return (false, "No such bill exists at this branch.");

        // Genuinely nothing left to do - the operator at the counter may well have collected
        // this payment themselves in the time the command took to arrive, and that is the
        // correct outcome either way: the bill is paid, which is all Head Office actually asked.
        if (bill.Status == BillStatus.Completed)
            return (true, "Already paid - nothing to do.");

        try
        {
            var billingService = scoped.GetRequiredService<IBillingService>();
            var result = await billingService.ProcessPaymentAsync(
                bill.BranchId, bill.OperatorId, bill.ShiftId ?? Guid.Empty, billId, dto);

            return (true, $"Paid. {dto.PaymentType}, Rs {result.TotalAmount}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Applies a discount decided at Head Office to the branch's own copy of the bill.
    ///
    /// The actor is read straight from the payload rather than looked up from the shift on
    /// duty, unlike every other command here - a discount is a specific, accountable decision
    /// by whoever pressed the button (the Super Admin, or an Admin with the discount
    /// permission, both already verified by BillingController before the command was ever
    /// sent), and crediting it to whichever operator happens to be on shift when it arrives
    /// would misattribute the one action on a bill most likely to be questioned later.
    /// </summary>
    private static async Task<(bool, string)> RunApplyDiscountAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid billId, actorId;
        string actorRole;
        Application.DTOs.Billing.ApplyDiscountDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            billId = root.GetProperty("billId").GetGuid();
            actorId = root.GetProperty("actorId").GetGuid();
            actorRole = root.GetProperty("actorRole").GetString() ?? Roles.SuperAdmin;
            dto = JsonSerializer.Deserialize<Application.DTOs.Billing.ApplyDiscountDto>(
                payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Empty discount payload.");
        }
        catch
        {
            return (false, "The discount command arrived without a readable bill id and amount.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var bill = await db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, ct);

        if (bill is null)
            return (false, "No such bill exists at this branch.");

        if (bill.Status == BillStatus.Completed)
            return (false, "This bill has already been paid, so a discount can no longer be applied to it.");

        try
        {
            var billingService = scoped.GetRequiredService<IBillingService>();
            var result = await billingService.ApplyDiscountAsync(
                bill.BranchId, actorId, actorRole, billId, dto);

            return (true, $"Discount applied. New total Rs {result.TotalAmount}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Corrects a bill's payment method at the branch that actually holds it - same reasoning
    /// as RunApplyDiscountAsync, actor carried explicitly for the same accountability reason.
    /// </summary>
    private static async Task<(bool, string)> RunEditPaymentMethodAsync(
        IServiceProvider scoped, string payload, CancellationToken ct)
    {
        Guid billId, actorId;
        string actorRole;
        Application.DTOs.Billing.EditPaymentMethodDto dto;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            billId = root.GetProperty("billId").GetGuid();
            actorId = root.GetProperty("actorId").GetGuid();
            actorRole = root.GetProperty("actorRole").GetString() ?? Roles.SuperAdmin;
            dto = JsonSerializer.Deserialize<Application.DTOs.Billing.EditPaymentMethodDto>(
                payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Empty payment-correction payload.");
        }
        catch
        {
            return (false, "The payment-correction command arrived without a readable bill id and amounts.");
        }

        var db = scoped.GetRequiredService<AppDbContext>();
        var bill = await db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, ct);

        if (bill is null) return (false, "No such bill exists at this branch.");

        try
        {
            var billingService = scoped.GetRequiredService<IBillingService>();
            await billingService.EditPaymentMethodAsync(
                bill.BranchId, actorId, actorRole, billId, dto);

            return (true, $"Payment method corrected to {dto.NewPaymentType}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Takes whatever settings Head Office sent back and makes this branch match them.
    ///
    /// This is the direction that never existed. A super admin could grant an operator End of
    /// Day, watch the server save it, and the counter would never hear - every permission
    /// screen on the server was decoration, and an operator hired at Head Office could not log
    /// in at the shop they had been hired for.
    ///
    /// Head Office sends nothing at all when this branch is already correct, so the usual case
    /// is a few hundred bytes and this method does nothing.
    /// </summary>
    private async Task ApplyConfigFromReplyAsync(string body, CancellationToken ct)
    {
        BranchConfigDto? config;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("config", out var node) || node.ValueKind == JsonValueKind.Null) return;

            config = JsonSerializer.Deserialize<BranchConfigDto>(node.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the settings Head Office sent ({Reason}).",
                ex.GetBaseException().Message);
            return;
        }

        if (config is null) return;
        if (config.Operators.Count == 0 && config.MenuItems.Count == 0 && config.Members.Count == 0
            && config.PricingProfiles.Count == 0 && config.Admins.Count == 0) return;

        // Every row gets its own scope and its own SaveChangesAsync - deliberately, and this is
        // the fix for a real, silent, permanent failure mode found on Citylight. All of an
        // update's operators, menu items and members used to be tracked on ONE shared DbContext
        // and flushed with ONE SaveChangesAsync at the end. A single colliding row - a leftover,
        // orphaned Operator sharing a Username with a brand new one Head Office was pushing down,
        // exactly the kind of debris an old, heavily-used branch accumulates over years - made
        // that one call throw, which meant NOTHING in the batch was applied: not the colliding
        // operator, not the other nine perfectly fine ones, not that beat's menu or member
        // changes either. And because the fingerprint this branch reports is only updated after
        // a successful save (below), Head Office kept resending the exact same batch every beat,
        // which kept failing on the exact same row, forever - a branch permanently stuck unable
        // to learn of ANY new operator, permission change, menu item or member, with nothing
        // anywhere telling the owner why, because the only trace was one log line on that one
        // branch's own machine. Isolating each row means the one that keeps colliding keeps
        // failing on its own, loudly, while everything else lands normally on every beat.
        var anyFailed = false;
        var opsAdded = 0;
        var menuAdded = 0;
        var membersAdded = 0;
        var pricingAdded = 0;
        var adminsAdded = 0;

        foreach (var incoming in config.Operators)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await ApplyOneOperatorAsync(db, _branchId, incoming, ct)) opsAdded++;
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                LogConfigRowFailure("operator", incoming.Username, incoming.Id, ex);
            }
        }

        foreach (var item in config.MenuItems)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await ApplyOneMenuItemAsync(db, _branchId, item, ct)) menuAdded++;
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                LogConfigRowFailure("menu item", item.ItemName, item.Id, ex);
            }
        }

        foreach (var item in config.Members)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await ApplyOneMemberAsync(db, item, ct)) membersAdded++;
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                LogConfigRowFailure("member", item.FullName, item.Id, ex);
            }
        }

        foreach (var item in config.PricingProfiles)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await ApplyOnePricingProfileAsync(db, _branchId, item, ct)) pricingAdded++;
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                LogConfigRowFailure("pricing profile", item.Name, item.Id, ex);
            }
        }

        foreach (var item in config.Admins)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await ApplyOneAdminAsync(db, item, ct)) adminsAdded++;
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                anyFailed = true;
                LogConfigRowFailure("admin", item.FullName, item.Id, ex);
            }
        }

        if (opsAdded + menuAdded + membersAdded + pricingAdded + adminsAdded > 0)
        {
            _logger.LogInformation(
                "Settings updated from Head Office: {OpCount} operator(s), {OpNew} new; " +
                "{MenuCount} menu item(s), {MenuNew} new; {MemberCount} member(s), {MemberNew} new; " +
                "{PricingCount} pricing profile(s), {PricingNew} new; {AdminCount} admin(s), {AdminNew} new. " +
                "Version {Version}.",
                config.Operators.Count, opsAdded,
                config.MenuItems.Count, menuAdded,
                config.Members.Count, membersAdded,
                config.PricingProfiles.Count, pricingAdded,
                config.Admins.Count, adminsAdded,
                config.Version);
        }

        // Advanced only when every row landed cleanly. A row that keeps failing means Head
        // Office keeps resending this same batch next beat too - which is exactly what should
        // happen, since that row still needs fixing - but every OTHER row in it lands again
        // regardless (harmlessly; every apply here is an idempotent upsert), so a single bad
        // row no longer holds the rest of the branch's settings hostage.
        if (!anyFailed) _configVersion = config.Version;
    }

    /// <summary>Logged at most every <see cref="ComplainAtMost"/> - the same row fails on the same schedule as the beat itself otherwise, which is every three seconds forever.</summary>
    private void LogConfigRowFailure(string kind, string? label, Guid id, Exception ex)
    {
        if (DateTimeOffset.UtcNow - _lastConfigRowComplaint <= ComplainAtMost) return;
        _lastConfigRowComplaint = DateTimeOffset.UtcNow;

        _logger.LogError(ex,
            "Could not apply the {Kind} Head Office sent ({Label}, {Id}). Every other operator, " +
            "menu item and member in this update is applied independently and is not affected - " +
            "only this one row is stuck, and will keep being retried every beat until whatever is " +
            "colliding with it locally (most likely a leftover row sharing the same name/username) " +
            "is found and removed.",
            kind, label, id);
    }

    /// <summary>
    /// One operator, one row. Returns true when it was newly created here.
    ///
    /// Somebody hired at Head Office who has never existed here is created with the same id, so
    /// everything they later do lines up on both sides rather than arriving as a stranger.
    /// </summary>
    private static async Task<bool> ApplyOneOperatorAsync(
        AppDbContext db, Guid branchId, BranchOperatorConfigDto incoming, CancellationToken ct)
    {
        var op = await db.Operators.FirstOrDefaultAsync(o => o.Id == incoming.Id, ct);
        var isNew = op is null;

        if (op is null)
        {
            op = new Operator
            {
                Id = incoming.Id,
                BranchId = branchId,
                Status = OperatorStatus.LoggedOut,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Operators.Add(op);
        }

        op.FullName = incoming.FullName;
        op.Username = incoming.Username;
        op.Email = incoming.Email;
        op.PasswordHash = incoming.PasswordHash;
        op.MobileNumber = incoming.MobileNumber;
        op.AccessPin = incoming.AccessPin;
        op.IsGlobalAdmin = incoming.IsGlobalAdmin;
        op.DashboardPermissions = incoming.DashboardPermissions;
        op.UpdatedAt = DateTimeOffset.UtcNow;

        // Only the barred/not-barred decision comes down. Active and LoggedOut say whether
        // somebody is standing at this counter, which Head Office cannot know and must
        // never overwrite - doing so would sign out the operator halfway through a shift.
        if (incoming.IsBlocked)
        {
            if (op.Status is not (OperatorStatus.Suspended or OperatorStatus.Disabled))
                op.Status = OperatorStatus.Suspended;
        }
        else if (op.Status is OperatorStatus.Suspended or OperatorStatus.Disabled)
        {
            op.Status = OperatorStatus.LoggedOut;   // unbarred; duty is decided here
        }

        return isNew;
    }

    /// <summary>
    /// Makes this branch's menu match Head Office's catalog for it, one item at a time.
    ///
    /// This is the fix for a super admin adding a food item at Head Office and it never
    /// appearing at the counter - the Menu Editor is branch-scoped storage, and an item added
    /// "for Adajan" while working at Head Office was written into Head Office's own copy of
    /// Adajan's table, which the physical Adajan counter - a completely separate database -
    /// was never told about.
    ///
    /// Only catalog fields are touched: name, category, price, image, whether Head Office has
    /// withdrawn it from sale. CurrentStock and SoldQty are never written here, for the same
    /// reason a PC's busy/idle state is never written from config - they are this branch's own
    /// trading state and change at the counter, not at Head Office. A shop that just sold its
    /// last plate of fries must not have Head Office silently restock it on the next beat.
    /// </summary>
    private static async Task<bool> ApplyOneMenuItemAsync(
        AppDbContext db, Guid branchId, BranchMenuItemConfigDto item, CancellationToken ct)
    {
        var row = await db.Set<InventoryItem>().FirstOrDefaultAsync(i => i.Id == item.Id, ct);
        var isNew = row is null;

        if (row is null)
        {
            row = new InventoryItem
            {
                Id = item.Id,
                BranchId = branchId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(row);
        }

        // Compared before writing, and this matters more than it looks now that the menu
        // also travels upward. Assigning the same value still marks the row Modified, which
        // SyncCapture would faithfully record as a change and send back to Head Office -
        // every item, every time a config arrived, describing nothing that had happened.
        var differs =
            row.ItemName != item.ItemName
            || row.Category != item.Category
            || row.Price != item.Price
            || row.ImageUrl != item.ImageUrl;

        if (differs)
        {
            row.ItemName = item.ItemName;
            row.Category = item.Category;
            row.Price = item.Price;
            row.ImageUrl = item.ImageUrl;
        }

        // A branch marking something Out of Stock is its own call and stays exactly as it
        // is; only Head Office's Disabled/not-Disabled decision moves this needle, and only
        // when it actually says something - "disabled" pulls an item from sale everywhere,
        // "not disabled" must not silently un-hide something the branch itself paused.
        if (item.IsDisabled && row.Status != FoodAvailability.Disabled)
        {
            row.Status = FoodAvailability.Disabled;
            differs = true;
        }
        else if (!item.IsDisabled && row.Status == FoodAvailability.Disabled)
        {
            row.Status = FoodAvailability.Available;
            differs = true;
        }

        if (differs) row.UpdatedAt = DateTimeOffset.UtcNow;

        return isNew;
    }

    /// <summary>
    /// Makes this branch's pricing match Head Office's for one profile, packages included.
    ///
    /// This is the fix for the same class of bug as the menu editor, one layer further down: a
    /// branch running the full local install (its own database, not just a thin agent) has its
    /// own separate copy of PricingProfiles, written once at adoption and never touched again.
    /// A rate changed, or a custom package added, at Head Office's own dashboard was invisible
    /// at the counter - confirmed live at Citylight 144Hz, where new packages showed correctly
    /// on Head Office's own screen and the never-updated 1/2/3-hour multiples kept showing at
    /// the actual PC, because the two were reading two different databases.
    ///
    /// Every package Head Office currently has on this profile is sent, active or not (a
    /// deactivated package still needs to arrive with IsActive=false so the branch stops
    /// offering it - it does not simply vanish from the payload), so there is nothing extra to
    /// reconcile here beyond an ordinary upsert.
    /// </summary>
    private static async Task<bool> ApplyOnePricingProfileAsync(
        AppDbContext db, Guid branchId, BranchPricingProfileConfigDto profile, CancellationToken ct)
    {
        var row = await db.Set<PricingProfile>()
            .Include(p => p.Packages)
            .FirstOrDefaultAsync(p => p.Id == profile.Id, ct);
        var isNew = row is null;

        if (row is null)
        {
            row = new PricingProfile
            {
                Id = profile.Id,
                BranchId = branchId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(row);
        }

        if (row.Name != profile.Name) row.Name = profile.Name;
        if (row.BaseHourlyRate != profile.BaseHourlyRate) row.BaseHourlyRate = profile.BaseHourlyRate;
        if (row.BufferMinutes != profile.BufferMinutes) row.BufferMinutes = profile.BufferMinutes;
        if (row.IsActive != profile.IsActive) row.IsActive = profile.IsActive;
        if (row.RefreshRate != profile.RefreshRate) row.RefreshRate = profile.RefreshRate;
        if (row.SystemSpecs != profile.SystemSpecs) row.SystemSpecs = profile.SystemSpecs;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        var existingPackages = row.Packages?.ToDictionary(pkg => pkg.Id) ?? new Dictionary<Guid, PricingPackage>();

        foreach (var incoming in profile.Packages)
        {
            if (existingPackages.TryGetValue(incoming.Id, out var pkgRow))
            {
                pkgRow.Name = incoming.Name;
                pkgRow.DurationMinutes = incoming.DurationMinutes;
                pkgRow.Price = incoming.Price;
                pkgRow.SortOrder = incoming.SortOrder;
                pkgRow.IsActive = incoming.IsActive;
                pkgRow.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.Add(new PricingPackage
                {
                    Id = incoming.Id,
                    PricingProfileId = profile.Id,
                    Name = incoming.Name,
                    DurationMinutes = incoming.DurationMinutes,
                    Price = incoming.Price,
                    SortOrder = incoming.SortOrder,
                    IsActive = incoming.IsActive,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        return isNew;
    }

    /// <summary>
    /// Makes this branch recognise one member Head Office knows about, with the wallet balance
    /// Head Office currently holds for them.
    ///
    /// This is what lets someone who joined at Adajan spend their wallet at Katargam: without
    /// it, a branch that has never seen a member locally has no row for them at all. Balance is
    /// only ever accepted from Head Office when it is not older than what this branch already
    /// has - the same "newest wins" rule used for a PC's state, applied to money instead. A
    /// branch that just took a top-up of its own must not have that top-up erased because Head
    /// Office's reply, built moments earlier, has not caught up yet.
    /// </summary>
    private static async Task<bool> ApplyOneMemberAsync(
        AppDbContext db, BranchMemberConfigDto item, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == item.Id, ct);
        var isNew = member is null;

        if (member is null)
        {
            member = new Member
            {
                Id = item.Id,
                HomeBranchId = item.HomeBranchId,
                PasswordHash = item.PasswordHash,
                PasswordChangedAt = item.PasswordChangedAt,
                GamingBalance = item.GamingBalance,
                FoodBalance = item.FoodBalance,
                BalanceAsOf = item.BalanceAsOf,
                Status = item.IsBlocked ? MemberStatus.Suspended : MemberStatus.Active,
                JoinDate = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Members.Add(member);
        }
        else if (item.BalanceAsOf is { } incomingAsOf
                 && (member.BalanceAsOf is not { } localAsOf || incomingAsOf > localAsOf))
        {
            member.GamingBalance = item.GamingBalance;
            member.FoodBalance = item.FoodBalance;
            member.BalanceAsOf = incomingAsOf;
        }

        member.FullName = item.FullName;
        member.MemberNumber = item.MemberNumber;
        member.MobileNumber = item.MobileNumber;
        member.Email = item.Email;
        member.Username = item.Username;

        // Head Office's own, stable fact about this member - never contested by what a visiting
        // branch thinks, so always overwritten, unlike password/balance below. Also backfills
        // every branch's already-existing copy of a member on the very next heartbeat, not just
        // new ones - confirmed live at Adajan: this was null on a member whose home branch is
        // Citylight-144Hz, which silently broke their password reset at Adajan (see
        // AuthService.ShareMemberResetTokenAsync) with no error anywhere to explain why.
        if (item.HomeBranchId is { } homeBranchId)
            member.HomeBranchId = homeBranchId;

        // Never store a blank hash - BCrypt.Verify against "" throws rather than returning
        // false, so a member this branch already knew how to log in would suddenly be unable
        // to at all, the moment a beat happened to carry an empty value. Head Office should
        // never send one, but this stays the same "only ever move forward" rule Head Office's
        // own RunSetMemberPasswordAsync already applies for the exact same reason.
        //
        // Also only ever moves forward in TIME now (Member.IsPasswordNewer), same as BalanceAsOf
        // just above: Head Office's own copy of a password can itself be the stale one (an
        // operator resetting it at the counter never used to reach Head Office at all - see
        // MemberService.UpdateMemberAsync's own note), and without this a branch that had just
        // been given the real, current password could have it silently overwritten by Head
        // Office's older one on the very next heartbeat.
        if (!string.IsNullOrWhiteSpace(item.PasswordHash) && member.IsPasswordNewer(item.PasswordChangedAt))
        {
            member.PasswordHash = item.PasswordHash;
            member.PasswordChangedAt = item.PasswordChangedAt;
        }
        member.UpdatedAt = DateTimeOffset.UtcNow;

        // Same rule as operators: only the barred decision comes down. There is no local
        // "on shift" equivalent for a member to protect, but Active/Vip is still this
        // branch's own read on a member's standing and is left alone either way.
        if (item.IsBlocked && member.Status is not MemberStatus.Suspended)
            member.Status = MemberStatus.Suspended;
        else if (!item.IsBlocked && member.Status is MemberStatus.Suspended)
            member.Status = MemberStatus.Active;

        return isNew;
    }

    /// <summary>
    /// Creates or updates one Admin-level Users-table account locally, so Quick Admin Switch at
    /// this branch can actually find someone made "the right way" at Head Office - see
    /// BranchConfigDto.Admins for why nothing here ever arrived before.
    ///
    /// Never touches Role - a row this loop creates is always an Admin, and a row it finds
    /// already local stays whatever it already is, so this can never turn a branch's own local
    /// SuperAdmin (created at that branch's own first-time setup, entirely unrelated to Head
    /// Office's copy) into anything else.
    /// </summary>
    private static async Task<bool> ApplyOneAdminAsync(
        AppDbContext db, BranchAdminConfigDto item, CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Id == item.Id, ct);
        var isNew = admin is null;

        if (admin is null)
        {
            admin = new User
            {
                Id = item.Id,
                Role = Roles.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(admin);
        }

        admin.FullName = item.FullName;
        admin.Email = item.Email;
        admin.PasswordHash = item.PasswordHash;
        admin.AccessPin = item.AccessPin;
        admin.DashboardPermissions = item.DashboardPermissions;
        admin.UpdatedAt = DateTimeOffset.UtcNow;

        // Same rule as operators and members: only the barred decision comes down, and only in
        // the direction that actually enforces it - never used to quietly reinstate someone
        // whatever this branch's own local record already says about them.
        if (item.IsBlocked && admin.Status is not UserStatus.Suspended)
            admin.Status = UserStatus.Suspended;
        else if (!item.IsBlocked && admin.Status is UserStatus.Suspended)
            admin.Status = UserStatus.Active;

        return isNew;
    }

    /// <summary>
    /// The settings fingerprint this branch is running on, sent with every beat so Head Office
    /// can stay silent while it matches. Held in memory only - after a restart the branch
    /// simply asks once more and is told once more, which costs one message.
    /// </summary>
    private string? _configVersion;

    /// <summary>This branch's own id, remembered from the beat that was just built.</summary>
    private Guid _branchId;
}
