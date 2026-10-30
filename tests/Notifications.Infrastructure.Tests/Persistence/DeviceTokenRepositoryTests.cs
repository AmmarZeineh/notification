using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Notifications.Application.Abstractions;
using Notifications.Domain.DeviceTokens;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Persistence.Configurations;
using Notifications.Tests.Shared;

namespace Notifications.Infrastructure.Tests.Persistence;

public sealed class DeviceTokenRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private NotificationsDbContext _context = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);
        _connection.UseSqlServerCaseSensitiveCollation();
        _context = CreateContext();
        await _context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Adds_and_finds_token()
    {
        var repository = new DeviceTokenRepository(_context);
        repository.Add(DeviceToken.Create("user-1", "token-1", DevicePlatform.Ios, Now));
        await repository.SaveChangesAsync(Ct);

        await using var readContext = CreateContext();
        var found = await new DeviceTokenRepository(readContext).GetByTokenAsync("token-1", Ct);

        Assert.NotNull(found);
        Assert.Equal("user-1", found.UserId);
        Assert.Equal(DevicePlatform.Ios, found.Platform);
    }

    [Fact]
    public async Task Returns_only_the_users_tokens()
    {
        await SeedAsync(("user-1", "a"), ("user-1", "b"), ("user-2", "c"));

        var tokens = await new DeviceTokenRepository(_context).GetTokensForUserAsync("user-1", 100, Ct);

        Assert.Equal(["a", "b"], tokens.Order());
    }

    [Fact]
    public async Task Removes_tokens_in_bulk()
    {
        await SeedAsync(("user-1", "a"), ("user-1", "b"), ("user-2", "c"));

        var removed = await new DeviceTokenRepository(_context).RemoveTokensAsync(["a", "c", "unknown"], Ct);

        Assert.Equal(2, removed);
        Assert.Equal(["b"], await _context.Set<DeviceToken>().Select(d => d.Token).ToListAsync(Ct));
    }

    [Fact]
    public async Task Removes_a_users_token_only_while_that_user_owns_it()
    {
        await SeedAsync(("user-b", "shared"));
        var repository = new DeviceTokenRepository(_context);

        // User A's sign-out arrives after user B re-registered the same device.
        Assert.False(await repository.RemoveUserTokenAsync("user-a", "shared", Ct));
        Assert.True(await repository.RemoveUserTokenAsync("user-b", "shared", Ct));
        Assert.Empty(await _context.Set<DeviceToken>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Reports_concurrent_duplicate_and_stays_usable()
    {
        await SeedAsync(("user-1", "a"));

        var repository = new DeviceTokenRepository(_context);
        repository.Add(DeviceToken.Create("user-2", "a", DevicePlatform.Android, Now));

        await Assert.ThrowsAsync<DeviceTokenConflictException>(() => repository.SaveChangesAsync(Ct));

        var existing = await repository.GetByTokenAsync("a", Ct);
        existing!.Refresh(DevicePlatform.Ios, Now.AddMinutes(1));
        await repository.SaveChangesAsync(Ct);

        await using var readContext = CreateContext();
        var saved = await readContext.Set<DeviceToken>().SingleAsync(Ct);
        Assert.Equal(DevicePlatform.Ios, saved.Platform);
    }

    [Fact]
    public async Task Reports_update_of_a_concurrently_deleted_token_and_stays_usable()
    {
        await SeedAsync(("user-1", "a"));
        var repository = new DeviceTokenRepository(_context);
        var existing = await repository.GetByTokenAsync("a", Ct);

        await using (var other = CreateContext())
        {
            await other.Set<DeviceToken>().ExecuteDeleteAsync(Ct);
        }

        existing!.Refresh(DevicePlatform.Ios, Now.AddMinutes(1));
        await Assert.ThrowsAsync<DeviceTokenConflictException>(() => repository.SaveChangesAsync(Ct));

        repository.Add(DeviceToken.Create("user-1", "a", DevicePlatform.Ios, Now.AddMinutes(1)));
        await repository.SaveChangesAsync(Ct);
        Assert.Equal(DevicePlatform.Ios, (await repository.GetByTokenAsync("a", Ct))?.Platform);
    }

    [Fact]
    public async Task Bulk_deletes_detach_tracked_entities_so_a_later_save_does_not_fail()
    {
        await SeedAsync(("user-1", "a"), ("user-1", "b"));
        var repository = new DeviceTokenRepository(_context);
        var tracked = await repository.GetByTokenAsync("a", Ct);
        tracked!.Refresh(DevicePlatform.Ios, Now.AddMinutes(1));

        await repository.RemoveTokensAsync(["a"], Ct);
        await repository.RemoveUserTokenAsync("user-1", "b", Ct);

        Assert.Empty(_context.ChangeTracker.Entries<DeviceToken>());
        await _context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Returns_the_most_recently_seen_tokens_first_up_to_the_limit()
    {
        await using (var seed = CreateContext())
        {
            seed.AddRange(
                DeviceToken.Create("user-1", "old", DevicePlatform.Android, Now.AddDays(-2)),
                DeviceToken.Create("user-1", "new", DevicePlatform.Android, Now),
                DeviceToken.Create("user-1", "mid", DevicePlatform.Android, Now.AddDays(-1)));
            await seed.SaveChangesAsync(Ct);
        }

        var tokens = await new DeviceTokenRepository(_context).GetTokensForUserAsync("user-1", 2, Ct);

        Assert.Equal(["new", "mid"], tokens);
    }

    [Fact]
    public async Task Removes_more_tokens_than_one_statement_can_hold()
    {
        var tokens = Enumerable.Range(0, DeviceTokenRepository.DeleteBatchSize + 5).Select(i => $"t{i}").ToArray();
        await SeedAsync(tokens.Select(t => ("user-1", t)).ToArray());

        var removed = await new DeviceTokenRepository(_context).RemoveTokensAsync([.. tokens, "unknown"], Ct);

        Assert.Equal(tokens.Length, removed);
        Assert.Empty(await _context.Set<DeviceToken>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Treats_tokens_that_differ_only_in_case_as_different_devices()
    {
        await SeedAsync(("user-1", "AbC"), ("user-2", "abc"));
        var repository = new DeviceTokenRepository(_context);

        var found = await repository.GetByTokenAsync("abc", Ct);
        var removed = await repository.RemoveTokensAsync(["abc"], Ct);

        Assert.Equal("user-2", found?.UserId);
        Assert.Equal(1, removed);
        Assert.Equal(["AbC"], await _context.Set<DeviceToken>().Select(d => d.Token).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(nameof(DeviceToken.Token))]
    [InlineData(nameof(DeviceToken.UserId))]
    public void Maps_column_with_a_case_sensitive_collation(string property)
    {
        // Collations live only in the design-time model, which is what migrations are generated from.
        var model = _context.GetService<IDesignTimeModel>().Model;
        var column = model.FindEntityType(typeof(DeviceToken))!.FindProperty(property)!;

        Assert.Equal(DeviceTokenConfiguration.CaseSensitiveCollation, column.GetCollation());
    }

    [Fact]
    public async Task Treats_user_ids_that_differ_only_in_case_as_different_users()
    {
        await SeedAsync(("alice", "a"), ("Alice", "b"));
        var repository = new DeviceTokenRepository(_context);

        Assert.Equal(["a"], await repository.GetTokensForUserAsync("alice", 100, Ct));
        Assert.False(await repository.RemoveUserTokenAsync("Alice", "a", Ct));
    }

    [Fact]
    public async Task Rethrows_insert_failures_that_are_not_duplicates()
    {
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE DeviceTokens", Ct);

        var repository = new DeviceTokenRepository(_context);
        repository.Add(DeviceToken.Create("user-1", "a", DevicePlatform.Android, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.SaveChangesAsync(Ct));

        // The failed insert must not linger in the (shared) context and be retried by the host's next save.
        Assert.Empty(_context.ChangeTracker.Entries<DeviceToken>());
    }

    [Fact]
    public async Task Removes_only_tokens_last_seen_before_the_cutoff()
    {
        await using (var seed = CreateContext())
        {
            seed.AddRange(
                DeviceToken.Create("user-1", "stale", DevicePlatform.Android, Now.AddDays(-300)),
                DeviceToken.Create("user-1", "fresh", DevicePlatform.Ios, Now.AddDays(-10)));
            await seed.SaveChangesAsync(Ct);
        }

        var removed = await new DeviceTokenRepository(_context).RemoveTokensLastSeenBeforeAsync(Now.AddDays(-270), Ct);

        Assert.Equal(1, removed);
        Assert.Equal(["fresh"], await new DeviceTokenRepository(_context).GetTokensForUserAsync("user-1", 10, Ct));
    }

    private NotificationsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>().UseSqliteAsSqlServer(_connection).Options);

    private async Task SeedAsync(params (string UserId, string Token)[] tokens)
    {
        await using var context = CreateContext();
        context.AddRange(tokens.Select(t => DeviceToken.Create(t.UserId, t.Token, DevicePlatform.Android, Now)));
        await context.SaveChangesAsync(Ct);
    }
}
