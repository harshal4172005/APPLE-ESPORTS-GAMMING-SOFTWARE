using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using AppleEsportsErp.Domain.Entities;

namespace AppleEsportsErp.Infrastructure.Data;

/// <summary>
/// Records what a branch did, automatically, as it is being saved.
///
/// Every sync bug in this system has had the same shape. Somebody added a feature, wired an
/// outbox event into the one service that happened to change that record, and missed another
/// place that changed the same record. Sessions were remembered. Bills were remembered. PC
/// state was not, so Head Office showed early August for four days. Operator status was not,
/// so a branch trading all evening showed its staff as logged out. Wallet spending was not,
/// so Head Office's balances could only ever climb.
///
/// Shifts, cash registers and customer credits are the same story again, and worse: they are
/// written from at least ten different places between AuthService, CashDeskService,
/// CashRegisterService, BillingService, SessionService, CreditService and ShiftTakeoverService.
/// Hand-wiring ten call sites means missing one, and a missed one is silent - the figures at
/// Head Office are simply wrong, with nothing anywhere saying so. That is exactly the
/// "EOD and Credits do not match" report this exists to answer.
///
/// So this is not wired per service. It watches the change tracker, and anything on the list
/// below is recorded no matter which code path wrote it - including code written next year by
/// somebody who has never heard of sync. Missing a record now requires deleting a line from
/// this file rather than forgetting to add one somewhere else.
///
/// It runs inside the same SaveChanges as the data, so the record and the fact that it
/// happened commit together or not at all. A branch cannot end up having taken money it has
/// no intention of ever reporting.
/// </summary>
public static class SyncCapture
{
    /// <summary>
    /// False at Head Office, which has nowhere to report to and would otherwise queue an
    /// outbox entry for every row it ever wrote - including the rows it just received from a
    /// branch, which would then be sent back to itself for ever.
    ///
    /// Set once at startup from configuration. A deployment does not change role while it is
    /// running, so this is genuinely constant for the life of the process.
    /// </summary>
    public static bool IsEnabled { get; set; }

    /// <summary>
    /// What a branch must tell Head Office about, and the event name each one travels under.
    ///
    /// Deliberately a short, explicit list rather than "everything". Most tables here are
    /// either local bookkeeping (audit logs, sync plumbing), already covered by their own
    /// richer events (sessions, bills, payments carry more than a row snapshot), or state
    /// rather than history and therefore the heartbeat's job (PCs, operators).
    ///
    /// These four are the ones End of Day is computed from, and the reason its figures could
    /// never match: Head Office simply did not have them.
    /// </summary>
    private static readonly Dictionary<Type, string> Watched = new()
    {
        [typeof(Shift)] = "shift",
        [typeof(CashRegister)] = "cash_register",
        [typeof(CashTransaction)] = "cash_transaction",
        [typeof(CustomerCredit)] = "customer_credit",
        [typeof(MemberTopUpCredit)] = "member_topup_credit",

        // The note/coin breakdown behind a register's counted total. Missing from this list
        // entirely until now - the count itself (register.PhysicalCashCounted, via CashRegister
        // above) always arrived, but the breakdown that explains it never did. A branch's own
        // Cash Register History showed it fine; Head Office's remote view of the same register
        // always read "No denomination count recorded" for every count ever taken, anywhere,
        // because this table was simply never queued for sync - not a save failure, a missing
        // line in this dictionary.
        [typeof(DenominationCount)] = "denomination_count",

        // The cash/online/wallet breakdown behind every completed bill. Watched here for the
        // same reason the four above are: "bill.paid" and "payment.recorded" exist as handlers
        // at Head Office's receiving end, but nothing anywhere in this codebase ever emits
        // either one - a payment recorded by clearing a credit, in particular, never travelled
        // by any path at all. EodService reads Payment directly for its Cash/Online/Wallet
        // columns, so without this those columns were never anything other than zero at Head
        // Office, no matter how much a branch had actually taken.
        [typeof(Payment)] = "payment",

        // Bills, because a bill only reached Head Office when somebody paid it.
        //
        // The bill.paid event creates the bill as a side effect of recording a payment, which
        // covers every bill that was settled and none of the ones that were not. But a customer
        // credit is raised precisely when a bill is NOT paid - the customer left owing money -
        // and it holds a required reference to that bill. So the one case that creates a credit
        // is the same case that guarantees its bill never arrives, and the credit could never
        // be filed: "insert or update on CustomerCredits violates foreign key
        // FK_CustomerCredits_bills_BillId", found by testing this before shipping it.
        //
        // Unpaid money is the half of the ledger an owner most needs to see, and it was the
        // half that structurally could not arrive.
        [typeof(Bill)] = "bill",

        // Who did what, where, and when - written already at every login, session, payment,
        // wallet change and member edit, and until now kept entirely to itself.
        //
        // This is written INSERT-only (AuditService never updates a row), so there is nothing
        // to reconcile on conflict and nothing a later write could clobber - the simplest shape
        // this file deals with. Head Office ends up with the same trail a branch would show at
        // its own counter, which is what makes it possible to answer "what actually happened at
        // Katargam this afternoon" without driving to Katargam.
        [typeof(AuditLog)] = "audit_log",

        // The menu, because it only ever travelled downward.
        //
        // Head Office could push a price or a new dish out to a shop, and that half works. But
        // an item created or repriced at the counter existed only at that counter: Head Office
        // showed the old price, so every report of what a branch sold was priced against a
        // menu that branch had stopped using. It also made the downward direction unsafe to
        // trust, because Head Office's copy was not a record of the branch's menu - it was a
        // record of the last thing Head Office had said about it.
        //
        // CurrentStock and SoldQty ride along in the snapshot, which is a bonus rather than a
        // risk: they let Head Office see what a shop has actually got left. They never travel
        // back down - the config reply carries catalogue fields only - so Head Office cannot
        // restock a branch that has just sold out.
        [typeof(InventoryItem)] = "inventory_item",

        // A shift's whole stock count, so Super Admin sees who counted what under each
        // register's note breakdown at Head Office too. Insert-only, like AuditLog.
        [typeof(StockCount)] = "stock_count",

        // Reservations, which had never travelled at all.
        //
        // This was reported as "reservations are broken on the Head Office panel", and the
        // suspicion was a wrong query. The query was fine. The table was empty: no reservation
        // taken at any branch, ever, had reached Head Office, because Reservation was simply
        // not in this list. Head Office was correctly reporting what it had, which was nothing.
        //
        // A booking is a promise made to a customer that occupies a machine and often carries a
        // deposit, so an owner who cannot see the day's bookings cannot see what the shop has
        // already committed to - or reconcile a deposit that was taken against a PC that was
        // held empty for it.
        //
        // Snapshot-shaped like the rest: a booking is created, then cancelled, started or
        // overridden, so it is the current row that matters and a late-arriving update is
        // resolved the same "newest wins" way as everything else here.
        [typeof(Reservation)] = "reservation",

        // An operator created (or edited) at a branch's own counter, rather than pushed down
        // from Head Office. The comment this replaces assumed operators were entirely "the
        // heartbeat's job" - true only in one direction. BranchHeartbeatController's config
        // reply pushes Head Office's operators DOWN to a branch; nothing ever carried a
        // branch-created operator back UP. Confirmed live at Citylight 144Hz: an operator
        // created locally had no row at Head Office, so every session, bill and payment they
        // ever touched sat permanently stuck in the sync inbox with "Head Office has no
        // operator X" - 12 sessions and 21 payments deep before anyone noticed, because the
        // branch's own screen showed all of it working. Sessions, cash desk, EOD, everything
        // that names an operator was silently invisible at Head Office for exactly this reason.
        [typeof(Operator)] = "operator",
    };

    /// <summary>
    /// Turns every watched insert or update in this save into outbox entries.
    ///
    /// Called before SaveChanges completes, so the entries join the same transaction. Returns
    /// them rather than adding them directly so the caller controls when they enter the
    /// change tracker - adding to a collection being enumerated is how this would otherwise
    /// throw halfway through somebody's shift close.
    /// </summary>
    public static List<SyncOutboxEntry> Collect(ChangeTracker tracker)
    {
        var entries = new List<SyncOutboxEntry>();
        if (!IsEnabled) return entries;

        foreach (var entity in tracker.Entries().ToList())
        {
            if (entity.State is not (EntityState.Added or EntityState.Modified)) continue;
            var entry = BuildEntryFor(entity);
            if (entry != null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Builds the same outbox entry <see cref="Collect"/> would for this row, for a caller that
    /// already has an <see cref="EntityEntry"/> in hand and wants a fresh delivery attempt
    /// regardless of the change tracker's own Added/Modified state - a still-open shift or
    /// drawer that a reconciliation sweep found sitting Unchanged with nothing queued for it.
    /// </summary>
    public static SyncOutboxEntry? BuildEntryFor(EntityEntry entity)
    {
        if (!Watched.TryGetValue(entity.Entity.GetType(), out var aggregate)) return null;

        var id = ReadGuid(entity, "Id");
        var branchId = ReadGuid(entity, "BranchId");

        // Without both, Head Office has nothing to file this against. Skipped rather than
        // thrown: a missing id is a bug worth finding, but not one worth refusing to close
        // somebody's till over.
        if (id is null || branchId is null) return null;

        return new SyncOutboxEntry
        {
            Id = Guid.NewGuid(),
            BranchId = branchId.Value,
            AggregateType = aggregate,
            AggregateId = id.Value,

            // "changed" rather than created/updated. Head Office applies these as an
            // upsert keyed on the row's own id, so it does not matter which it was - and
            // a branch that was offline may deliver an update before Head Office has ever
            // seen the insert.
            EventType = $"{aggregate}.changed",
            EventData = Snapshot(entity),
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// The whole row as JSON, every scalar column of it.
    ///
    /// Whole rather than just what changed, because Head Office may never have seen this row
    /// before - a branch that spent the evening offline delivers its shift close without the
    /// shift open having arrived first. A complete snapshot applies correctly either way,
    /// where a delta would need a history Head Office does not have.
    ///
    /// Navigation properties are skipped, so a cash register does not drag its branch, its
    /// operator and their entire shift along with it.
    /// </summary>
    private static string Snapshot(EntityEntry entity)
    {
        var values = new Dictionary<string, object?>();

        foreach (var property in entity.Properties)
        {
            // xmin and friends. Several of these tables use PostgreSQL's own row version as a
            // concurrency token, which is a system column belonging to whichever database the
            // row happens to live in - it means nothing at the other end and cannot be written
            // there. Sending it would only invite the receiver to try.
            if (property.Metadata.IsConcurrencyToken) continue;

            // Only genuinely computed columns are skipped - ones the database recalculates on
            // every write, which the sender has no authority over.
            //
            // Emphatically NOT "anything with a default". OpeningBalance, TotalCashSales and
            // ExpectedDrawerCash are all declared HasDefaultValue(0m), which marks them
            // ValueGenerated.OnAdd - and an earlier version of this skipped them on that basis,
            // so a till holding Rs 3,450 was faithfully reported as holding nothing. A default
            // says what to use when no value is given; it never means the value cannot be sent.
            if (property.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate)
                continue;

            var value = property.CurrentValue;

            values[property.Metadata.Name] = value switch
            {
                null => null,
                DateTimeOffset d => d.ToUniversalTime(),   // Npgsql refuses a non-UTC offset
                DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                Enum e => e.ToString(),
                _ => value,
            };
        }

        return JsonSerializer.Serialize(values);
    }

    private static Guid? ReadGuid(EntityEntry entity, string propertyName)
    {
        var property = entity.Properties.FirstOrDefault(p => p.Metadata.Name == propertyName);
        return property?.CurrentValue is Guid g && g != Guid.Empty ? g : null;
    }

    /// <summary>
    /// A short, stable fingerprint of a row's current snapshot - the same JSON <see cref="Collect"/>
    /// would send, hashed rather than sent whole. Lets two databases ask "do we still agree
    /// about this row" by exchanging a few bytes instead of the row itself; only a row that
    /// actually disagrees needs its full snapshot sent.
    /// </summary>
    public static string ComputeChecksum(EntityEntry entity)
    {
        var json = Snapshot(entity);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// The watched CLR type behind an aggregate's event-name string, for a caller that received
    /// only the string - over the wire, or read back off an outbox row - and needs to load the
    /// actual entity. Kept as the reverse of <see cref="Watched"/> rather than a second list of
    /// the same names, so the two can never go out of sync with each other.
    /// </summary>
    public static Type? TypeForAggregate(string aggregateType) =>
        Watched.FirstOrDefault(kv => kv.Value == aggregateType).Key;
}
