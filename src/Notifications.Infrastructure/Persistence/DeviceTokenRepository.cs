using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Abstractions;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Infrastructure.Persistence;

/// <summary>
/// Works against any DbContext whose model includes <see cref="DeviceToken"/>, on SQL Server: duplicate
/// registrations are recognised by SQL Server error numbers, and the model uses a SQL Server collation.
/// </summary>
internal sealed class DeviceTokenRepository(DbContext dbContext) : IDeviceTokenRepository
{
    /// <summary>
    /// SQL Server allows 2,100 parameters per statement, and EF Core may send one per value of a
    /// <c>Contains</c> list, so bulk deletes are split well below that.
    /// </summary>
    internal const int DeleteBatchSize = 1000;

    // SQL Server error numbers for a unique index or unique constraint violation.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private DbSet<DeviceToken> DeviceTokens => dbContext.Set<DeviceToken>();

    public Task<DeviceToken?> GetByTokenAsync(string token, CancellationToken cancellationToken) =>
        DeviceTokens.SingleOrDefaultAsync(d => d.Token == token, cancellationToken);

    public async Task<IReadOnlyList<string>> GetTokensForUserAsync(string userId, int limit, CancellationToken cancellationToken) =>
        await DeviceTokens
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAt)
            .Take(limit)
            .Select(d => d.Token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(DeviceToken deviceToken) => DeviceTokens.Add(deviceToken);

    public async Task<bool> RemoveUserTokenAsync(string userId, string token, CancellationToken cancellationToken)
    {
        var removed = await DeviceTokens
            .Where(d => d.Token == token && d.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        DetachTracked(d => d.Token == token && d.UserId == userId);
        return removed > 0;
    }

    public async Task<int> RemoveTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var batch in tokens.Chunk(DeleteBatchSize))
        {
            removed += await DeviceTokens
                .Where(d => batch.Contains(d.Token))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var deleted = tokens.ToHashSet(StringComparer.Ordinal);
        DetachTracked(d => deleted.Contains(d.Token));
        return removed;
    }

    public async Task<int> RemoveTokensLastSeenBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var removed = await DeviceTokens
            .Where(d => d.LastSeenAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        DetachTracked(d => d.LastSeenAt < cutoff);
        return removed;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex) when (OnlyDeviceTokensFailed(ex))
        {
            // The row was deleted by a concurrent request (e.g. unregister) before this update ran.
            Detach(ex);
            throw new DeviceTokenConflictException("The device token was removed by a concurrent request.", ex);
        }
        catch (DbUpdateException ex) when (FailedToInsertDeviceTokens(ex))
        {
            // Only a unique violation, or a token that now exists, means a concurrent duplicate. Anything else
            // (deadlock, timeout, missing table) is rethrown unchanged so the real cause is not hidden.
            // The violation is checked too because the competing row may already be deleted again.
            var tokens = ex.Entries.Select(e => ((DeviceToken)e.Entity).Token).ToList();

            // Whatever happens next (including cancellation during the check), the rejected insert is
            // discarded first, so a later save of the host's own changes in this (shared) context does not
            // retry it behind the caller's back.
            Detach(ex);

            bool alreadyStored;
            try
            {
                alreadyStored = await DeviceTokens
                    .AsNoTracking()
                    .AnyAsync(d => tokens.Contains(d.Token), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception checkFailure) when (checkFailure is not OperationCanceledException)
            {
                // The database itself is failing; report the original error, not this follow-up one.
                alreadyStored = false;
            }

            if (!alreadyStored && !IsUniqueViolation(ex))
            {
                throw;
            }

            throw new DeviceTokenConflictException("The device token was registered by a concurrent request.", ex);
        }
    }

    /// <summary>
    /// ExecuteDelete bypasses the change tracker. Entities of deleted rows that this (shared) context already
    /// tracks are detached, so a later save by the host does not try to update rows that no longer exist.
    /// </summary>
    private void DetachTracked(Func<DeviceToken, bool> wasDeleted)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<DeviceToken>().Where(e => wasDeleted(e.Entity)).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation };

    private static bool OnlyDeviceTokensFailed(DbUpdateException exception) =>
        exception.Entries.Count > 0 && exception.Entries.All(e => e.Entity is DeviceToken);

    private static bool FailedToInsertDeviceTokens(DbUpdateException exception) =>
        OnlyDeviceTokensFailed(exception) && exception.Entries.All(e => e.State == EntityState.Added);

    /// <summary>Discards the rejected changes so the context can be reused for a retry from a fresh read.</summary>
    private static void Detach(DbUpdateException exception)
    {
        foreach (var entry in exception.Entries)
        {
            entry.State = EntityState.Detached;
        }
    }
}
