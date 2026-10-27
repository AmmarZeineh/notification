using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Abstractions;
using Notifications.Application.Common;
using Notifications.Application.Services;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Tests.Services;

public sealed class DeviceRegistrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly IDeviceTokenRepository _repository = Substitute.For<IDeviceTokenRepository>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly DeviceRegistrationService _service;

    public DeviceRegistrationServiceTests()
    {
        _service = new DeviceRegistrationService(_repository, _time, NullLogger<DeviceRegistrationService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_adds_new_token()
    {
        var result = await _service.RegisterAsync("user-1", "token-1", DevicePlatform.Android, Ct);

        Assert.True(result.IsSuccess);
        _repository.Received(1).Add(Arg.Is<DeviceToken>(d =>
            d.UserId == "user-1" && d.Token == "token-1" && d.Platform == DevicePlatform.Android && d.CreatedAt == Now));
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_refreshes_existing_token_instead_of_duplicating_it()
    {
        var existing = DeviceToken.Create("user-1", "token-1", DevicePlatform.Ios, Now.AddDays(-3));
        _repository.GetByTokenAsync("token-1", Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _service.RegisterAsync("user-1", "token-1", DevicePlatform.Ios, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, existing.LastSeenAt);
        _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Register_refuses_a_token_owned_by_another_user()
    {
        var existing = DeviceToken.Create("owner", "token-1", DevicePlatform.Android, Now.AddDays(-1));
        _repository.GetByTokenAsync("token-1", Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _service.RegisterAsync("intruder", "token-1", DevicePlatform.Android, Ct);

        Assert.Equal(NotificationErrors.DeviceOwnedByAnotherUser, result.Error);
        Assert.True(existing.IsOwnedBy("owner"));
        await _repository.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Register_trims_the_token()
    {
        await _service.RegisterAsync("user-1", "  token-1\n", DevicePlatform.Android, Ct);

        _repository.Received(1).Add(Arg.Is<DeviceToken>(d => d.Token == "token-1"));
    }

    [Fact]
    public async Task Register_applies_to_the_token_a_concurrent_request_inserted_first()
    {
        var insertedConcurrently = DeviceToken.Create("user-1", "token-1", DevicePlatform.Android, Now.AddSeconds(-1));
        _repository.GetByTokenAsync("token-1", Arg.Any<CancellationToken>())
            .Returns((DeviceToken?)null, insertedConcurrently);
        _repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DeviceTokenConflictException(), _ => Task.CompletedTask);

        var result = await _service.RegisterAsync("user-1", "token-1", DevicePlatform.Android, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, insertedConcurrently.LastSeenAt);
        await _repository.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_inserts_again_when_a_concurrent_unregister_deleted_the_token()
    {
        var existing = DeviceToken.Create("user-1", "token-1", DevicePlatform.Android, Now.AddDays(-1));
        _repository.GetByTokenAsync("token-1", Arg.Any<CancellationToken>())
            .Returns(existing, (DeviceToken?)null);
        _repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DeviceTokenConflictException(), _ => Task.CompletedTask);

        var result = await _service.RegisterAsync("user-1", "token-1", DevicePlatform.Android, Ct);

        Assert.True(result.IsSuccess);
        _repository.Received(1).Add(Arg.Is<DeviceToken>(d => d.Token == "token-1" && d.UserId == "user-1"));
    }

    [Fact]
    public async Task Register_gives_up_after_repeated_conflicts()
    {
        _repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DeviceTokenConflictException());

        var result = await _service.RegisterAsync("user-1", "token-1", DevicePlatform.Android, Ct);

        Assert.Equal(NotificationErrors.RegistrationConflict, result.Error);
        await _repository.Received(3).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "token")]
    [InlineData("user", "")]
    public async Task Register_rejects_missing_values(string userId, string token)
    {
        var result = await _service.RegisterAsync(userId, token, DevicePlatform.Android, Ct);

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        await _repository.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Register_rejects_oversized_token()
    {
        var token = new string('x', DeviceToken.TokenMaxLength + 1);

        var result = await _service.RegisterAsync("user-1", token, DevicePlatform.Android, Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
    }

    [Fact]
    public async Task Register_rejects_undefined_platform()
    {
        var result = await _service.RegisterAsync("user-1", "token-1", (DevicePlatform)99, Ct);

        Assert.Equal(NotificationErrors.InvalidPlatform, result.Error);
    }

    [Fact]
    public async Task Unregister_removes_own_token()
    {
        _repository.RemoveUserTokenAsync("user-1", "token-1", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.UnregisterAsync("user-1", "token-1", Ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Unregister_returns_not_found_for_unknown_or_foreign_token()
    {
        _repository.RemoveUserTokenAsync("intruder", "token-1", Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.UnregisterAsync("intruder", "token-1", Ct);

        Assert.Equal(NotificationErrors.DeviceNotFound, result.Error);
    }

    [Fact]
    public async Task Unregister_rejects_a_malformed_token_without_touching_storage()
    {
        var result = await _service.UnregisterAsync("user-1", "has space", Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _repository.DidNotReceiveWithAnyArgs().RemoveUserTokenAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Unregister_rejects_missing_token()
    {
        var result = await _service.UnregisterAsync("user-1", " ", Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _repository.DidNotReceiveWithAnyArgs().RemoveUserTokenAsync(default!, default!, Ct);
    }

    [Fact]
    public void A_lost_registration_race_is_a_conflict_not_an_outage() =>
        Assert.Equal(ErrorType.Conflict, NotificationErrors.RegistrationConflict.Type);
}
