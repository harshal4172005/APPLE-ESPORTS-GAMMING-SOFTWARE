using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Api.Controllers;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Services;

/// <summary>
/// Lets Head Office drive any branch, by asking rather than by writing.
///
/// The requirement is simple and was asked for plainly: from Head Office, do anything - stop a
/// session at any shop, start one, take a PC out of service. What made it hard was that the
/// obvious implementation is wrong in a way that looks right for about three seconds.
///
/// Head Office holds a synced copy of every branch's rows. Writing to that copy is easy, and
/// it updates the Head Office screen immediately, so it demos perfectly. It also does nothing
/// whatsoever. The shop's own database is a different machine in a different city; it has not
/// been told, so the counter still shows the PC as busy, the operator still cannot bill it, and
/// three seconds later that shop's next heartbeat reports the truth and Head Office's screen
/// flips back. That is the "sometimes it works, sometimes it doesn't" everybody has been
/// chasing, and it is not a race condition to be tightened up - the write was never real.
///
/// So Head Office states an intent and the branch carries it out, through the identical code
/// path an operator's own click uses. The session is stopped by the shop that has the customer,
/// the cash and the till. It is billed normally, it lands in that shift's takings, and it syncs
/// back up on its own. Head Office and the branch agree afterwards because only one of them
/// ever acted - and the one that acted is the one that could.
///
/// The cost is that a command is not instant: it is queued, collected on the next beat, and
/// confirmed. In practice that is a few seconds, and it is honest about them. An instant lie
/// was what we had before.
/// </summary>
public interface IRemoteBranchControl
{
    /// <summary>
    /// True when this instance is Head Office, so an action aimed at a branch has to travel.
    /// False at a branch, where the shop is right here and acts directly.
    /// </summary>
    bool MustTravel { get; }

    /// <summary>
    /// Puts a command in the branch's queue and returns what to tell whoever pressed the
    /// button. Refuses politely rather than throwing when the branch is not reachable at all,
    /// because "this shop has never reported in" is a useful thing to be told immediately
    /// instead of after a command sits unclaimed for a day.
    /// </summary>
    Task<RemoteCommandReceipt> SendAsync(
        Guid branchId, string commandType, object payload, Guid requestedByUserId, CancellationToken ct);
}

/// <summary>What Head Office can tell the person who pressed the button, right now.</summary>
public record RemoteCommandReceipt(Guid CommandId, string Message, bool BranchIsReporting);

public class RemoteBranchControl : IRemoteBranchControl
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _audit;
    private readonly ILogger<RemoteBranchControl> _logger;

    public RemoteBranchControl(
        AppDbContext db, IConfiguration configuration, IAuditService audit, ILogger<RemoteBranchControl> logger)
    {
        _db = db;
        _configuration = configuration;
        _audit = audit;
        _logger = logger;
    }

    public bool MustTravel => _configuration.IsHeadOffice();

    public async Task<RemoteCommandReceipt> SendAsync(
        Guid branchId, string commandType, object payload, Guid requestedByUserId, CancellationToken ct)
    {
        var command = new BranchCommand
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            CommandType = commandType,
            Payload = JsonSerializer.Serialize(payload),
            Status = BranchCommandStatus.Pending,
            RequestedByUserId = requestedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Add(command);
        await _db.SaveChangesAsync(ct);

        // Whether the shop is actually listening, checked against the same silence window the
        // overview screen uses. A command for a branch whose counter PC is switched off is not
        // an error - it will be collected whenever that PC comes back - but saying so now is
        // the difference between "nothing happened" and "nothing happened yet".
        var beat = await _db.Set<BranchHeartbeat>().AsNoTracking()
            .FirstOrDefaultAsync(h => h.BranchId == branchId, ct);

        var reporting = beat is not null
            && DateTimeOffset.UtcNow - beat.LastSeenAt < BranchHeartbeatController.SilentAfter;

        _logger.LogInformation(
            "Queued {CommandType} for branch {BranchId}, requested by {UserId}. Branch reporting: {Reporting}.",
            commandType, branchId, requestedByUserId, reporting);

        // The half of this story that belongs to the person who asked, not the branch that
        // carried it out. Once the branch actually runs the command it logs its own entry the
        // normal way - StartSessionAsync's SessionStart, StopSessionAsync's SessionStop, and so
        // on - exactly as if it had happened at the counter, because it is the same code doing
        // it. Neither entry alone answers both questions a real incident needs answered: this
        // one says who decided and when; the branch's own says what actually happened and
        // whether it worked. Logged even for a branch that is not currently reporting - the
        // decision was still made the moment the button was pressed, whatever happens after.
        var requestedBy = await _db.Set<Domain.Entities.User>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == requestedByUserId, ct);

        var branchName = await _db.Branches.AsNoTracking()
            .Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(ct);

        await _audit.LogAsync(new AuditEntry
        {
            UserId = requestedByUserId,
            UserRole = requestedBy?.Role ?? "super_admin",
            UserName = requestedBy?.FullName ?? "Head Office",
            Action = AuditActions.RemoteCommandIssued,
            TargetType = "branch_command",
            TargetId = command.Id,
            BranchId = branchId,
            BranchName = branchName,
            Details = new { commandType, branchReporting = reporting, payload },
        });

        var message = reporting
            ? "Sent to the branch. It carries this out within a few seconds and reports back."
            : "Queued. This branch is not reporting at the moment, so it will be carried out as " +
              "soon as its counter PC is back online.";

        return new RemoteCommandReceipt(command.Id, message, reporting);
    }
}

/// <summary>
/// The command words, in one place so Head Office and the branch cannot drift apart on
/// spelling. A branch that meets a word it does not recognise says so and leaves the command
/// alone, so adding one here does not break shops running an older build.
/// </summary>
public static class BranchCommands
{
    public const string StopSession = "stop_session";
    public const string StartSession = "start_session";
    public const string SetPcState = "set_pc_state";
    public const string AddPc = "add_pc";
    public const string UpdatePc = "update_pc";
    public const string DeletePc = "delete_pc";
    public const string TransferSession = "transfer_session";

    /// <summary>
    /// A member's new password, after they set it from the link in their email.
    ///
    /// This is the leg that makes a reset usable at all. A branch mints the token into its own
    /// local database, so the link used to have to point at the shop LAN - which is dead in the
    /// recipient's inbox the moment they are not standing in the shop, and that is where a
    /// customer reads their mail. Sending them to Head Office instead means the link opens from
    /// anywhere, but the password is then set in Head Office's copy of the member, and the
    /// branch's copy is the one the gaming PCs actually check.
    ///
    /// So it rides back down here. The hash travels, never the password - Head Office does the
    /// hashing and the branch stores the result, exactly as it would have if the reset had
    /// happened locally. Storing it locally is also what keeps member login working with the
    /// internet down, which pointing every login at Head Office would have quietly broken.
    /// </summary>
    public const string SetMemberPassword = "set_member_password";

    /// <summary>
    /// What RunSetMemberPasswordAsync/RunAdminEditMemberValuesAsync report back when the member
    /// simply has not reached this branch's own database yet - a brand-new member, or a
    /// password reset requested within moments of signing up, before the ordinary heartbeat
    /// config push (BranchConfigDto.Members) has had its next cycle to deliver them here.
    ///
    /// This is the exact fix for "the member got a success message but cannot log in": the old
    /// code treated "member not found here" as Succeeded outright, which closed the command for
    /// good the instant it was checked. Head Office had genuinely reset the password in its own
    /// copy, told the member so, and then never told the branch anything at all - the command
    /// was marked done, not pending, so nothing ever tried again once the member did arrive a
    /// few seconds later.
    ///
    /// BranchHeartbeatController.CommandResult checks for this exact string and, only for it,
    /// leaves the command Pending instead of closing it Failed - so it rides the next heartbeat
    /// and every one after that, same as a genuine delivery failure would, until either it
    /// actually succeeds or the ordinary 48-hour give-up window closes it for real.
    /// </summary>
    public const string MemberNotYetSyncedMessage =
        "This member has not synced to this branch yet - will retry once it has.";

    /// <summary>
    /// Money collected at Head Office on the customer's behalf, credited to the branch's own
    /// till. The bill lives in both databases, but only the branch's copy is the one its
    /// counter reads and its cash register reconciles against - so paying Head Office's copy
    /// settles nothing, leaves the PC locked on Billing, and invites the operator to take the
    /// money a second time. See BranchHeartbeatService.RunProcessPaymentAsync.
    /// </summary>
    public const string ProcessPayment = "process_payment";

    /// <summary>
    /// A PC taken out of service, or put back, WITH the reason and resolution notes that make
    /// the maintenance log worth reading.
    ///
    /// Distinct from <see cref="SetPcState"/> on purpose. That one carries a bare state and
    /// refuses outright while a PC is Active or AwaitingBilling; this one is what the
    /// maintenance screens actually use, and it has to carry who said what and why, because a
    /// MaintenanceLog row without a reason is no better than the flag itself.
    /// </summary>
    public const string SetMaintenance = "set_maintenance";

    /// <summary>
    /// Installs the exact version named, older or newer than what is currently running - a
    /// person at Head Office has already decided, so this bypasses the branch's own "only
    /// ever go forward" rule (desktop-client's UpdateService.CheckAsync), which exists for the
    /// unattended nightly check, not for an explicit instruction. See
    /// BranchHeartbeatService.RunInstallVersionAsync for why this is the one command capable
    /// of cutting off the very channel that carries it, if it goes wrong.
    /// </summary>
    public const string InstallVersion = "install_version";

    /// <summary>
    /// A stock delivery, told to the branch rather than typed into a number that would only
    /// ever be overwritten. See InventoryController.AddStock for why this exists at all: Head
    /// Office cannot know a shelf's real count, only the branch standing in front of it can -
    /// so this carries how many units arrived, not what the total should now read, and the
    /// branch adds that to whatever it already honestly has.
    /// </summary>
    public const string AdjustStock = "adjust_stock";

    /// <summary>
    /// A booking, told to the branch instead of written into Head Office's own copy of it.
    /// Reservation was never even synced upward until now (see SyncCapture.Watched), so a
    /// booking made at Head Office had nothing at the branch backing it - no PC held, nothing
    /// on the counter's own list, nobody expecting the customer when they walked in.
    /// </summary>
    public const string CreateReservation = "create_reservation";

    /// <summary>Cancels a booking - see CreateReservation for why this has to travel too.</summary>
    public const string CancelReservation = "cancel_reservation";
    public const string DeleteReservation = "delete_reservation";

    /// <summary>
    /// Converts a booking into a running session, at the counter that actually has the PC.
    /// </summary>
    public const string StartReservation = "start_reservation";

    /// <summary>
    /// Seats a walk-in on a machine somebody else booked, overriding the hold - a judgement
    /// call about who is standing at the counter right now, which only the branch can make.
    /// </summary>
    public const string OverrideReservation = "override_reservation";

    /// <summary>
    /// A discount decided at Head Office, applied to the branch's own copy of the bill - the
    /// one its counter reads and its register reconciles against. Unlike the other commands
    /// here, the actor is carried explicitly in the payload rather than resolved from whoever
    /// is on shift: a discount is a specific person's accountable decision (Super Admin, or an
    /// Admin with the discount permission), not a routine counter action, and the audit trail
    /// needs to say who actually authorised it, not whoever happened to be on duty when the
    /// command arrived.
    /// </summary>
    public const string ApplyDiscount = "apply_discount";

    /// <summary>
    /// Corrects an already-completed bill's payment method at the branch that actually holds
    /// the bill, its register, and its cash - same reasoning as ApplyDiscount and ProcessPayment.
    /// The actor is carried explicitly, for the same accountability reason ApplyDiscount does.
    /// </summary>
    public const string EditPaymentMethod = "edit_payment_method";

    /// <summary>
    /// Removes a menu item from the branch's own catalogue - permanently deletes it there too
    /// if nothing local references it, deactivates it otherwise. Without this, a delete at Head
    /// Office only ever removed Head Office's copy: the branch's row sat there untouched, and
    /// the next time anything about it changed - a price edit, a delivery, even its own next
    /// stock reconciliation - that untouched row synced straight back up and undid the delete.
    /// "Permanently deleted" was never actually true for anything requested from Head Office.
    /// </summary>
    public const string DeleteInventoryItem = "delete_inventory_item";

    /// <summary>
    /// A Super Admin's direct override of a member's wallet balance or lifetime stats, told to
    /// the branch that actually holds this member's row instead of just written into Head
    /// Office's own copy.
    ///
    /// Same shape as every other command here: Head Office's dashboard reads its own copy and
    /// shows the new number immediately, which is exactly the trap this file's own class
    /// comment describes - it demos perfectly and does nothing, because the gaming PC at the
    /// counter checks the branch's row, not Head Office's. An operator watching that PC would
    /// see the old balance forever, no matter what Head Office's screen said.
    /// </summary>
    public const string AdminEditMemberValues = "admin_edit_member_values";

    /// <summary>A free bonus added to a member's wallet - never money received, so it carries
    /// no cash/online and never touches a drawer or EOD. See WalletService.GiveBonusAsync.</summary>
    public const string GiveMemberBonus = "give_member_bonus";

    /// <summary>
    /// A shared food/snacks item's stock moving by some amount at a sibling branch, relayed by
    /// Head Office so this branch's own count moves by the same amount. Carries a delta, not a
    /// fresh total - see SharedStockCapture and SyncInboxController.RelaySharedStockDeltaAsync
    /// for why. Clamped to zero on arrival (BranchHeartbeatService.RunRelaySharedStockDeltaAsync)
    /// - two branches occasionally selling "the last one" within the same few seconds of each
    /// other can still happen, but the shared count itself never persists below what's honestly
    /// left.
    /// </summary>
    public const string RelaySharedStockDelta = "relay_shared_stock_delta";
}
