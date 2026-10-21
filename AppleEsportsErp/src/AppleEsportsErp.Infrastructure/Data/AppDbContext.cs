using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Domain.Entities;

namespace AppleEsportsErp.Infrastructure.Data;

/// <summary>
/// Gaming Café ERP — EF Core DbContext
/// Maps all 23 tables from schema.sql with full relational integrity.
/// SOP §23: Database Architecture
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── 23 DbSets matching schema.sql ──
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<PricingProfile> PricingProfiles => Set<PricingProfile>();
    public DbSet<PricingPackage> PricingPackages => Set<PricingPackage>();
    public DbSet<Pc> Pcs => Set<Pc>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionActivity> SessionActivities => Set<SessionActivity>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillItem> BillItems => Set<BillItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CustomerCredit> CustomerCredits => Set<CustomerCredit>();
    public DbSet<MemberTopUpCredit> MemberTopUpCredits => Set<MemberTopUpCredit>();
    public DbSet<CashRegister> CashRegisters => Set<CashRegister>();
    public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
    public DbSet<DenominationCount> DenominationCounts => Set<DenominationCount>();
    public DbSet<ShiftHandover> ShiftHandovers => Set<ShiftHandover>();
    public DbSet<BranchHeartbeat> BranchHeartbeats => Set<BranchHeartbeat>();
    public DbSet<BranchCommand> BranchCommands => Set<BranchCommand>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryLog> InventoryLogs => Set<InventoryLog>();
    public DbSet<FoodOrder> FoodOrders => Set<FoodOrder>();
    public DbSet<FoodOrderItem> FoodOrderItems => Set<FoodOrderItem>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<LoyaltyPoint> LoyaltyPoints => Set<LoyaltyPoint>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    // Decentralized LAN Offline Architecture sync tables
    public DbSet<OfflineSyncSession> OfflineSyncSessions => Set<OfflineSyncSession>();
    public DbSet<OfflineSyncBilling> OfflineSyncBillings => Set<OfflineSyncBilling>();

    // HR Module
    public DbSet<Employee> Employees => Set<Employee>();

    // Maintenance Logs
    public DbSet<MaintenanceLog> MaintenanceLogs => Set<MaintenanceLog>();

    // Version tracking & updates
    public DbSet<VersionInfo> VersionInfos => Set<VersionInfo>();
    public DbSet<BranchVersionStatus> BranchVersionStatuses => Set<BranchVersionStatus>();

    // Sync engine — outbox is what a branch owes Head Office, inbox is what Head Office
    // has been told by its branches.
    public DbSet<SyncOutboxEntry> SyncOutboxEntries => Set<SyncOutboxEntry>();
    public DbSet<SharedStockCount> SharedStockCounts => Set<SharedStockCount>();
    public DbSet<SharedStockRequest> SharedStockRequests => Set<SharedStockRequest>();
    public DbSet<SharedStockPending> SharedStockPending => Set<SharedStockPending>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<SyncInboxEntry> SyncInboxEntries => Set<SyncInboxEntry>();

    // Power cuts and lost connections, for the EOD and printed reports
    public DbSet<DowntimeEvent> DowntimeEvents => Set<DowntimeEvent>();

    /// <summary>
    /// Records anything a branch did that Head Office needs, in the same breath as doing it.
    ///
    /// Both overloads are here on purpose. Some of this codebase saves synchronously and some
    /// asynchronously, and instrumenting only one is precisely the sort of half-coverage that
    /// produced the bugs SyncCapture exists to end.
    ///
    /// The outbox entries are added before the save runs, so they are part of the same
    /// transaction as the shift, till or credit that caused them. A crash between the two is
    /// therefore not possible: either the branch took the money and has a record queued to
    /// report it, or neither happened.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Shared-fridge stock is asked of Head Office first - see SharedStockGate.
        var applied = await SharedStockGate.ApplyAsync(this, cancellationToken);
        try
        {
            CaptureForSync();
            var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            HoldOrForget(applied);
            return written;
        }
        catch
        {
            await SharedStockGate.UndoAsync(applied);
            throw;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var applied = SharedStockGate.ApplyAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            CaptureForSync();
            var written = base.SaveChanges(acceptAllChangesOnSuccess);
            HoldOrForget(applied);
            return written;
        }
        catch
        {
            SharedStockGate.UndoAsync(applied).GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// Shared-stock changes Head Office applied for a save inside a transaction that has not
    /// committed yet. A rollback must take them back at Head Office too (UnitOfWork).
    /// </summary>
    private readonly List<SharedStockGate.AppliedOp> _sharedStockUncommitted = new();

    private void HoldOrForget(List<SharedStockGate.AppliedOp> applied)
    {
        if (Database.CurrentTransaction is not null) _sharedStockUncommitted.AddRange(applied);
    }

    public void SharedStockCommitted() => _sharedStockUncommitted.Clear();

    public Task SharedStockRolledBackAsync() => SharedStockGate.UndoAsync(_sharedStockUncommitted);

    private void CaptureForSync()
    {
        AssignMissingIds();

        var entries = SyncCapture.Collect(ChangeTracker);
        if (entries.Count > 0) SyncOutboxEntries.AddRange(entries);
    }

    /// <summary>
    /// Gives every new row its id here, rather than letting PostgreSQL invent one on insert.
    ///
    /// Almost every table in this schema declares its key as uuid_generate_v4(), so a newly
    /// added entity carries an all-zero id right up until the insert comes back. Anything that
    /// reads that id before the save therefore reads nothing - and a distributed system reads
    /// it constantly, because the id is how the two sides agree they are talking about the
    /// same thing.
    ///
    /// This was not theoretical. A member created at Citylight was reported to Head Office as
    /// "00000000-0000-0000-0000-000000000000", created in the year 0001. Head Office refused
    /// it, and then refused all four of that member's wallet top-ups in turn - "Head Office has
    /// no member ed63a9f5..." - so Rs 2,200 of real top-ups sat at the branch belonging to
    /// nobody. It looked exactly like one PC being treated differently from another; it was
    /// simply whoever happened to create a new member.
    ///
    /// It would also have quietly crippled SyncCapture, which cannot record a row whose id is
    /// still empty and would have skipped every insert while faithfully syncing updates - the
    /// worst kind of half-working, because most of a day's shifts and tills would have arrived
    /// and nobody could have said which were missing.
    ///
    /// The database defaults stay exactly as they are. They remain the right answer for
    /// anything inserted outside EF; setting a value here simply means the default is never
    /// reached.
    /// </summary>
    private void AssignMissingIds()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;

            var key = entry.Metadata.FindPrimaryKey();
            if (key is null) continue;

            foreach (var property in key.Properties)
            {
                if (property.ClrType != typeof(Guid)) continue;

                // IsTemporary too, not just an empty value: for a key with a database default EF
                // keeps the property temporary even after a value is set, then leaves it out of
                // the INSERT - so Postgres made a second id and SyncCapture had already sent this
                // one. Every later edit then reached Head Office under the other id, as a second
                // row (duplicate payments after "Edit payment method", proven on a real database).
                var tracked = entry.Property(property.Name);
                if (tracked.IsTemporary || (tracked.CurrentValue is Guid g && g == Guid.Empty))
                {
                    tracked.CurrentValue = Guid.NewGuid();
                    tracked.IsTemporary = false;
                }
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // PostgreSQL extensions
        modelBuilder.HasPostgresExtension("uuid-ossp");
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.HasSequence<long>("shared_stock_version_seq");

        // Hardening C.1: Optimistic Concurrency via PostgreSQL xmin
        modelBuilder.Entity<Session>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<Reservation>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<Bill>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<BillItem>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<Payment>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<CashRegister>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<CashTransaction>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<WalletTransaction>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<FoodOrder>().UseXminAsConcurrencyToken();
        modelBuilder.Entity<InventoryItem>().UseXminAsConcurrencyToken();

        // Apply all IEntityTypeConfiguration<T> from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
