namespace AppleEsportsErp.Application.Interfaces;

/// <summary>
/// Unit of Work pattern — coordinates transactions across multiple repositories.
/// SOP: All multi-table operations (login+shift, bill+payment) must be atomic.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    IRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Blocks until no other transaction holds this same key, then holds it itself until this
    /// transaction commits or rolls back - a Postgres advisory lock (pg_advisory_xact_lock),
    /// scoped to whatever caller-chosen key names the thing being protected (a bill id, say).
    ///
    /// For closing a "read it, it still says X, now act on X" race - two requests for the same
    /// bill both reading "not paid yet" before either one's payment has actually committed, so
    /// both record one. Confirmed live: two Payment rows, same bill, identical timestamp,
    /// twice. Bill already carries an xmin concurrency token, which should refuse the second
    /// writer's UPDATE - but a lock the second request has to wait behind closes the race at
    /// its source (the two reads can no longer interleave at all) rather than relying on being
    /// caught after the fact.
    /// </summary>
    Task LockAsync(Guid key, CancellationToken ct = default);
}
