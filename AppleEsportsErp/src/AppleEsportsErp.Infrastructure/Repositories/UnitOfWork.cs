using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Infrastructure.Repositories;

/// <summary>
/// UnitOfWork implementation — manages transactions and repository lifecycle.
/// SOP: login+shift, bill+payment, wallet+audit all must be atomic.
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;
    private readonly ConcurrentDictionary<Type, object> _repositories = new();
    private IDbContextTransaction? _transaction;
    private bool _disposed;

    public UnitOfWork(AppDbContext db) => _db = db;

    public IRepository<T> Repository<T>() where T : class
    {
        return (IRepository<T>)_repositories.GetOrAdd(typeof(T), _ => new Repository<T>(_db));
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _transaction = await _db.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction == null)
        {
            await _db.SaveChangesAsync(ct);
            return;
        }
        await _db.SaveChangesAsync(ct);
        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    /// <summary>
    /// hashtextextended, not Guid's own GetHashCode - that is only guaranteed stable within one
    /// process's lifetime, and a second server instance (or this one, restarted) hashing the
    /// same GUID differently would just never contend with the first at all, silently
    /// defeating the whole lock. Postgres's own hash is stable everywhere this query runs.
    /// </summary>
    public async Task LockAsync(Guid key, CancellationToken ct = default)
        => await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key.ToString()}, 0))", ct);

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction == null) return;
        await _transaction.RollbackAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _transaction?.Dispose();
        _db.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
