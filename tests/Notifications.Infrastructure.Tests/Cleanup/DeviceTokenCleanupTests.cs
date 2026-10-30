using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Abstractions;
using Notifications.Infrastructure.Cleanup;

namespace Notifications.Infrastructure.Tests.Cleanup;

public sealed class DeviceTokenCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly IDeviceTokenRepository _repository = Substitute.For<IDeviceTokenRepository>();
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_removes_tokens_older_than_the_stale_period()
    {
        _repository.RemoveTokensLastSeenBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(3);
        using var service = CreateService(new DeviceTokenCleanupOptions { Enabled = true, StaleAfter = TimeSpan.FromDays(270) });

        var removed = await service.RunOnceAsync(Ct);

        Assert.Equal(3, removed);
        await _repository.Received(1).RemoveTokensLastSeenBeforeAsync(Now.AddDays(-270), Ct);
    }

    [Fact]
    public async Task Runs_after_each_interval_and_keeps_going_after_a_failure()
    {
        _repository.RemoveTokensLastSeenBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("database down"), _ => Task.FromResult(1));
        using var service = CreateService(new DeviceTokenCleanupOptions { Enabled = true, Interval = TimeSpan.FromHours(1) });

        await service.StartAsync(Ct);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensLastSeenBeforeAsync(default, Ct);

        // The second run proves the first run's failure did not stop the service.
        await AdvanceUntilCallsAsync(2, TimeSpan.FromHours(1));

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        using var service = CreateService(new DeviceTokenCleanupOptions { Enabled = false, Interval = TimeSpan.FromHours(1) });

        await service.StartAsync(Ct);
        _time.Advance(TimeSpan.FromDays(2));
        await service.StopAsync(Ct);

        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensLastSeenBeforeAsync(default, Ct);
    }

    [Theory]
    [InlineData(29, 24, false)]
    [InlineData(30, 0, false)]
    [InlineData(30, 24, true)]
    public void Validator_requires_a_safe_stale_period_and_a_positive_interval(int staleDays, int intervalHours, bool valid)
    {
        var options = new DeviceTokenCleanupOptions
        {
            Enabled = true,
            StaleAfter = TimeSpan.FromDays(staleDays),
            Interval = TimeSpan.FromHours(intervalHours),
        };

        Assert.Equal(valid, new DeviceTokenCleanupOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validator_ignores_settings_while_disabled() =>
        Assert.True(new DeviceTokenCleanupOptionsValidator()
            .Validate(null, new DeviceTokenCleanupOptions { StaleAfter = TimeSpan.Zero }).Succeeded);

    private DeviceTokenCleanupService CreateService(DeviceTokenCleanupOptions options)
    {
        var services = new ServiceCollection().AddScoped(_ => _repository).BuildServiceProvider();
        return new DeviceTokenCleanupService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            _time,
            NullLogger<DeviceTokenCleanupService>.Instance);
    }

    /// <remarks>
    /// The service starts its loop on the thread pool, so the timer may not exist yet when the clock first
    /// moves. The clock is advanced one interval at a time until the runs are seen.
    /// </remarks>
    private async Task AdvanceUntilCallsAsync(int count, TimeSpan interval)
    {
        for (var i = 0; i < 500 && _repository.ReceivedCalls().Count() < count; i++)
        {
            _time.Advance(interval);
            await Task.Delay(10, Ct);
        }

        Assert.True(_repository.ReceivedCalls().Count() >= count);
    }
}
