using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Abstractions;
using Notifications.Application.Common;
using Notifications.Application.Models;
using Notifications.Application.Services;

namespace Notifications.Application.Tests.Services;

public sealed class PushNotificationServiceTests
{
    private static readonly PushNotification Notification = new("Title", "Body");

    private readonly IPushNotificationSender _sender = Substitute.For<IPushNotificationSender>();
    private readonly IDeviceTokenRepository _repository = Substitute.For<IDeviceTokenRepository>();
    private readonly PushNotificationService _service;

    public PushNotificationServiceTests()
    {
        _service = new PushNotificationService(_sender, _repository, NullLogger<PushNotificationService>.Instance);
        _sender.ValidateAsync(Arg.Any<PushNotification>(), Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "message too big"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendToDevice_returns_message_id_when_delivered()
    {
        _sender.SendToTokenAsync("token-1", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Delivered("msg-1"));

        var result = await _service.SendToDeviceAsync("token-1", Notification, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(SendReceipt.Confirmed("msg-1"), result.Value);
    }

    [Fact]
    public async Task SendToDevice_removes_token_when_provider_reports_it_invalid()
    {
        _sender.SendToTokenAsync("stale", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered"));

        var result = await _service.SendToDeviceAsync("stale", Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _repository.Received(1).RemoveTokensAsync(
            Arg.Is<IReadOnlyCollection<string>>(t => t.SequenceEqual(new[] { "stale" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToDevice_keeps_token_when_provider_is_unavailable()
    {
        _sender.SendToTokenAsync("token-1", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unavailable, "quota"));

        var result = await _service.SendToDeviceAsync("token-1", Notification, Ct);

        Assert.Equal(NotificationErrors.ProviderUnavailable, result.Error);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensAsync(default!, Ct);
    }

    [Fact]
    public async Task SendToDevice_rejects_empty_notification_without_calling_provider()
    {
        var result = await _service.SendToDeviceAsync("token-1", new PushNotification(null, " "), Ct);

        Assert.Equal(NotificationErrors.EmptyNotification, result.Error);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokenAsync(default!, default!, Ct);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendToDevice_rejects_a_blank_token_without_calling_provider(string token)
    {
        var result = await _service.SendToDeviceAsync(token, Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokenAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SendToDevice_allows_data_only_notification()
    {
        var dataOnly = new PushNotification(null, null, new Dictionary<string, string> { ["screen"] = "home" });
        _sender.SendToTokenAsync("token-1", dataOnly, Arg.Any<CancellationToken>())
            .Returns(SendResult.Delivered("msg-1"));

        var result = await _service.SendToDeviceAsync("token-1", dataOnly, Ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SendToDevices_deduplicates_tokens_and_ignores_blank_ones()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<string>>()
                .Select(t => new TokenSendResult(t, SendResult.Delivered($"msg-{t}")))
                .ToList());

        var result = await _service.SendToDevicesAsync(["a", "b", "a", " ", ""], Notification, Ct);

        Assert.Equal(new BatchSendSummary(SuccessCount: 2, FailureCount: 0, RemovedInvalidTokenCount: 0), result.Value);
        await _sender.Received(1).SendToTokensAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "a", "b" })), Notification, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToDevices_returns_error_when_no_usable_tokens()
    {
        var result = await _service.SendToDevicesAsync([" "], Notification, Ct);

        Assert.Equal(NotificationErrors.NoTokens, result.Error);
    }

    [Fact]
    public async Task SendToUser_sends_to_all_user_devices_and_removes_invalid_ones()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["ok", "stale", "busy"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ok", SendResult.Delivered("msg-1")),
                new("stale", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
                new("busy", SendResult.Failed(DeliveryFailure.Unavailable, "quota")),
            });
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(1);

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal((1, 2, 1), (result.Value.SuccessCount, result.Value.FailureCount, result.Value.RemovedInvalidTokenCount));
        Assert.Equal(["busy"], result.Value.RetryableTokens);
        await _repository.Received(1).RemoveTokensAsync(
            Arg.Is<IReadOnlyCollection<string>>(t => t.SequenceEqual(new[] { "stale" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToUser_returns_not_found_when_user_has_no_devices()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(NotificationErrors.UserHasNoDevices, result.Error);
    }

    [Fact]
    public async Task SendToUser_returns_unavailable_when_nothing_was_delivered_because_provider_is_down()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["a"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult> { new("a", SendResult.Failed(DeliveryFailure.Unavailable, "down")) });

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(NotificationErrors.ProviderUnavailable, result.Error);
    }

    [Theory]
    [InlineData("news")]
    [InlineData("fund-1024-updates")]
    [InlineData("lang_ar.v1~%")]
    public async Task SendToTopic_sends_valid_topic(string topic)
    {
        _sender.SendToTopicAsync(topic, Notification, Arg.Any<CancellationToken>()).Returns(SendResult.Delivered("msg-1"));

        var result = await _service.SendToTopicAsync(topic, Notification, Ct);

        Assert.Equal(SendReceipt.Confirmed("msg-1"), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/topics/news")]
    [InlineData("has space")]
    [InlineData("emoji-🔥")]
    [InlineData("news\n")]
    public async Task SendToTopic_rejects_invalid_topic(string topic)
    {
        var result = await _service.SendToTopicAsync(topic, Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidTopic, result.Error);
        await _sender.DidNotReceiveWithAnyArgs().SendToTopicAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SendToTopic_maps_rejected_failure_to_provider_rejected_error()
    {
        _sender.SendToTopicAsync("news", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Rejected, "payload too large"));

        var result = await _service.SendToTopicAsync("news", Notification, Ct);

        Assert.Equal(ErrorType.Failure, result.Error!.Type);
        Assert.Equal(NotificationErrors.ProviderRejected, result.Error);
        Assert.DoesNotContain("payload too large", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeToTopic_forwards_distinct_tokens_and_returns_provider_result()
    {
        var providerResult = new TopicSubscriptionResult(1, [new TopicSubscriptionFailure("bad", "too-many-topics", DeliveryFailure.Rejected)]);
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(providerResult);

        var result = await _service.SubscribeToTopicAsync(["good", "bad", "good"], "news", Ct);

        Assert.Equal(providerResult, result.Value);
        await _sender.Received(1).SubscribeToTopicAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "good", "bad" })), "news", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnsubscribeFromTopic_rejects_invalid_topic()
    {
        var result = await _service.UnsubscribeFromTopicAsync(["a"], "bad topic", Ct);

        Assert.Equal(NotificationErrors.InvalidTopic, result.Error);
    }

    [Theory]
    [InlineData("banner.png")]
    [InlineData("ftp://cdn.example.com/banner.png")]
    [InlineData("http://cdn.example.com/banner.png")]
    public async Task Send_rejects_image_urls_providers_cannot_download(string imageUrl)
    {
        var notification = new PushNotification("Title", "Body", ImageUrl: new Uri(imageUrl, UriKind.RelativeOrAbsolute));

        var result = await _service.SendToDeviceAsync("token-1", notification, Ct);

        Assert.Equal(NotificationErrors.InvalidImageUrl, result.Error);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokenAsync(default!, default!, Ct);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("Message_Type")]
    [InlineData("google.c.a.e")]
    [InlineData("gcm.notification")]
    [InlineData("googleUserId")]
    [InlineData("gcmKey")]
    public async Task Send_rejects_data_keys_reserved_by_firebase(string key)
    {
        var notification = new PushNotification("Title", "Body", new Dictionary<string, string> { [key] = "x" });

        var result = await _service.SendToTopicAsync("news", notification, Ct);

        Assert.Equal(NotificationErrors.ReservedDataKey(key), result.Error);
    }

    [Fact]
    public async Task SendToDevice_maps_invalid_message_to_validation_error()
    {
        _sender.SendToTokenAsync("token-1", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "message too big"));

        var result = await _service.SendToDeviceAsync("token-1", Notification, Ct);

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains("message too big", result.Error.Message, StringComparison.Ordinal);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensAsync(default!, Ct);
    }

    [Fact]
    public async Task SendToDevices_returns_validation_error_when_every_token_failed_on_an_invalid_message()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("a", SendResult.Failed(DeliveryFailure.InvalidRequest, "message too big")),
                new("b", SendResult.Failed(DeliveryFailure.InvalidRequest, "message too big")),
            });

        var result = await _service.SendToDevicesAsync(["a", "b"], Notification, Ct);

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
    }

    [Fact]
    public async Task SubscribeToTopic_returns_unavailable_when_the_provider_applied_nothing()
    {
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(new TopicSubscriptionResult(0, [new("a", "unavailable", DeliveryFailure.Unavailable)], DeliveryFailure.Unavailable, 1));

        var result = await _service.SubscribeToTopicAsync(["a"], "news", Ct);

        Assert.Equal(NotificationErrors.ProviderUnavailable, result.Error);
    }

    [Fact]
    public async Task SubscribeToTopic_returns_partial_result_when_a_later_batch_failed()
    {
        var partial = new TopicSubscriptionResult(1, [new("late", "unavailable", DeliveryFailure.Unavailable)], DeliveryFailure.Unavailable, 1);
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(partial);

        var result = await _service.SubscribeToTopicAsync(["early", "late"], "news", Ct);

        Assert.Equal(partial, result.Value);
    }

    [Fact]
    public async Task UnsubscribeFromTopic_calls_provider_unsubscribe()
    {
        _sender.UnsubscribeFromTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(new TopicSubscriptionResult(1, []));

        var result = await _service.UnsubscribeFromTopicAsync(["a"], "news", Ct);

        Assert.Equal(1, result.Value.SuccessCount);
        await _sender.DidNotReceiveWithAnyArgs().SubscribeToTopicAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SendToDevices_keeps_tokens_when_the_message_reached_other_devices_but_fails_validation()
    {
        // e.g. Android delivered, but the iOS part of the message is what FCM rejected for the iOS tokens.
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("android", SendResult.Delivered("msg-1")),
                new("ios", SendResult.Failed(DeliveryFailure.InvalidRequest, "invalid APNs payload")),
            });

        var result = await _service.SendToDevicesAsync(["android", "ios"], Notification, Ct);

        Assert.Equal(new BatchSendSummary(SuccessCount: 1, FailureCount: 1, RemovedInvalidTokenCount: 0), result.Value);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensAsync(default!, Ct);
    }

    [Fact]
    public async Task SendToUser_returns_provider_rejected_when_every_device_was_rejected()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["a", "b"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("a", SendResult.Failed(DeliveryFailure.Rejected, "APNs auth key missing")),
                new("b", SendResult.Failed(DeliveryFailure.Rejected, "APNs auth key missing")),
            });

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(ErrorType.Failure, result.Error!.Type);
        Assert.Equal(NotificationErrors.ProviderRejected, result.Error);
    }

    [Fact]
    public async Task SendToUser_still_reports_delivery_when_removing_invalid_tokens_fails()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["ok", "stale"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ok", SendResult.Delivered("msg-1")),
                new("stale", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
            });
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new TimeoutException("database timeout"));

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(new BatchSendSummary(SuccessCount: 1, FailureCount: 1, RemovedInvalidTokenCount: 0), result.Value);
    }

    [Fact]
    public async Task SendToUser_cleans_up_without_the_request_cancellation_after_delivery()
    {
        using var cts = new CancellationTokenSource();
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["ok", "stale"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The caller disconnects right after the provider accepted the send.
                cts.Cancel();
                return new List<TokenSendResult>
                {
                    new("ok", SendResult.Delivered("msg-1")),
                    new("stale", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
                };
            });
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(1);

        var result = await _service.SendToUserAsync("user-1", Notification, cts.Token);

        Assert.Equal(new BatchSendSummary(SuccessCount: 1, FailureCount: 1, RemovedInvalidTokenCount: 1), result.Value);
        await _repository.Received(1).RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), CancellationToken.None);
    }

    [Fact]
    public async Task SubscribeToTopic_removes_tokens_the_provider_reports_as_not_registered()
    {
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(new TopicSubscriptionResult(1, [
                new("stale", "registration-token-not-registered", DeliveryFailure.InvalidToken),
                new("busy", "internal-error", DeliveryFailure.Unavailable),
            ]));
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(1);

        var result = await _service.SubscribeToTopicAsync(["ok", "stale", "busy"], "news", Ct);

        Assert.Equal(1, result.Value.RemovedInvalidTokenCount);
        await _repository.Received(1).RemoveTokensAsync(
            Arg.Is<IReadOnlyCollection<string>>(t => t.SequenceEqual(new[] { "stale" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscribeToTopic_keeps_per_token_failures_when_a_later_batch_hit_an_outage()
    {
        var partial = new TopicSubscriptionResult(0, [
            new("bad", "too-many-topics", DeliveryFailure.Rejected),
            new("late", "unavailable", DeliveryFailure.Unavailable),
        ], DeliveryFailure.Unavailable, 1);
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(partial);

        var result = await _service.SubscribeToTopicAsync(["bad", "late"], "news", Ct);

        Assert.Equal(["bad", "late"], result.Value.Failures.Select(f => f.Token));
    }

    [Fact]
    public async Task SubscribeToTopic_returns_provider_rejected_when_nothing_was_processed()
    {
        _sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(new TopicSubscriptionResult(0, [new("a", "permission denied", DeliveryFailure.Rejected)], DeliveryFailure.Rejected, 1));

        var result = await _service.SubscribeToTopicAsync(["a"], "news", Ct);

        Assert.Equal(ErrorType.Failure, result.Error!.Type);
    }

    [Fact]
    public async Task SendToDevice_returns_unavailable_when_the_validation_check_hits_an_outage()
    {
        _sender.SendToTokenAsync("abc", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "invalid argument"));
        _sender.ValidateAsync(Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unavailable, "503"));

        var result = await _service.SendToDeviceAsync("abc", Notification, Ct);

        Assert.Equal(NotificationErrors.ProviderUnavailable, result.Error);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensAsync(default!, Ct);
    }

    [Fact]
    public async Task SendToDevices_lists_only_temporarily_failed_tokens_as_retryable()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ok", SendResult.Delivered("msg-1")),
                new("quota", SendResult.Failed(DeliveryFailure.Unavailable, "quota exceeded")),
                new("stale", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
            });

        var result = await _service.SendToDevicesAsync(["ok", "quota", "stale"], Notification, Ct);

        Assert.Equal(["quota"], result.Value.RetryableTokens);
    }

    [Fact]
    public async Task SendToDevice_reports_a_malformed_token_but_keeps_it()
    {
        // The message passes validation, so the token is at fault; "invalid argument" is still not proof enough to delete.
        _sender.SendToTokenAsync("abc", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "not a valid FCM registration token"));
        _sender.ValidateAsync(Notification, Arg.Any<CancellationToken>()).Returns(SendResult.Delivered("dry-run"));

        var result = await _service.SendToDeviceAsync("abc", Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _repository.DidNotReceiveWithAnyArgs().RemoveTokensAsync(default!, Ct);
    }

    [Fact]
    public async Task Validation_after_a_send_is_not_cancelled_by_the_caller()
    {
        using var cts = new CancellationTokenSource();
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return new List<TokenSendResult> { new("abc", SendResult.Failed(DeliveryFailure.InvalidRequest, "too big")) };
            });
        _sender.ValidateAsync(Notification, CancellationToken.None)
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "too big"));

        var result = await _service.SendToDevicesAsync(["abc"], Notification, cts.Token);

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
    }

    [Fact]
    public async Task Send_rejects_an_image_on_a_data_only_message()
    {
        var notification = new PushNotification(
            null, null, new Dictionary<string, string> { ["screen"] = "home" }, new Uri("https://cdn.example.com/a.png"));

        var result = await _service.SendToTopicAsync("news", notification, Ct);

        Assert.Equal(NotificationErrors.ImageWithoutVisibleContent, result.Error);
    }

    [Fact]
    public async Task SendToDevices_trims_tokens_before_deduplicating()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<string>>().Select(t => new TokenSendResult(t, SendResult.Delivered("m"))).ToList());

        await _service.SendToDevicesAsync([" abc", "abc", "abc\n"], Notification, Ct);

        await _sender.Received(1).SendToTokensAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "abc" })), Notification, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToDevices_returns_the_summary_when_nothing_was_delivered_but_not_only_because_of_an_outage()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("stale-1", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
                new("stale-2", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
                new("quota", SendResult.Failed(DeliveryFailure.Unavailable, "quota exceeded")),
            });
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(2);

        var result = await _service.SendToDevicesAsync(["stale-1", "stale-2", "quota"], Notification, Ct);

        Assert.Equal(2, result.Value.RemovedInvalidTokenCount);
        Assert.Equal(["quota"], result.Value.RetryableTokens);
    }

    [Fact]
    public async Task SendToUser_returns_provider_rejected_when_a_rejected_device_sits_next_to_a_retryable_one()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["ios", "android"]);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ios", SendResult.Failed(DeliveryFailure.Rejected, "APNs auth key missing")),
                new("android", SendResult.Failed(DeliveryFailure.Unavailable, "quota exceeded")),
            });

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(NotificationErrors.ProviderRejected, result.Error);
    }

    [Fact]
    public async Task SendToUser_returns_provider_rejected_even_when_a_stale_device_was_removed()
    {
        _repository.GetTokensForUserAsync("user-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(["ios", "stale"]);
        _repository.RemoveTokensAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(1);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ios", SendResult.Failed(DeliveryFailure.Rejected, "APNs auth key missing")),
                new("stale", SendResult.Failed(DeliveryFailure.InvalidToken, "unregistered")),
            });

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        // The stale token is still cleaned up, but the configuration fault is what the caller must see.
        Assert.Equal(NotificationErrors.ProviderRejected, result.Error);
        await _repository.Received(1).RemoveTokensAsync(
            Arg.Is<IReadOnlyCollection<string>>(t => t.SequenceEqual(new[] { "stale" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_rejects_null_data_values_and_empty_keys()
    {
        var nullValue = new PushNotification("Title", "Body", new Dictionary<string, string> { ["screen"] = null! });
        var emptyKey = new PushNotification("Title", "Body", new Dictionary<string, string> { [" "] = "x" });

        Assert.Equal(NotificationErrors.InvalidData, (await _service.SendToTopicAsync("news", nullValue, Ct)).Error);
        Assert.Equal(NotificationErrors.InvalidData, (await _service.SendToTopicAsync("news", emptyKey, Ct)).Error);
    }

    [Fact]
    public async Task SendToDevices_rejects_requests_over_the_token_limit()
    {
        var tokens = Enumerable.Range(0, PushNotificationService.MaxTokensPerRequest + 1).Select(i => $"t{i}").ToList();

        var result = await _service.SendToDevicesAsync(tokens, Notification, Ct);

        Assert.Equal("Notifications.TooManyTokens", result.Error!.Code);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokensAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SendToDevices_applies_the_token_limit_after_dropping_blanks_and_duplicates()
    {
        var distinct = Enumerable.Range(0, PushNotificationService.MaxTokensPerRequest).Select(i => $"t{i}").ToList();
        var tokens = distinct.Concat(["t0", " ", ""]).ToList();
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<string>>().Select(t => new TokenSendResult(t, SendResult.Delivered($"m-{t}"))).ToList());

        var result = await _service.SendToDevicesAsync(tokens, Notification, Ct);

        Assert.Equal(PushNotificationService.MaxTokensPerRequest, result.Value.SuccessCount);
    }

    [Fact]
    public async Task SendToDevices_rejects_a_malformed_token_by_position_without_calling_the_provider()
    {
        var result = await _service.SendToDevicesAsync(["ok", "has space"], Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidTokenAt(2), result.Error);
        Assert.DoesNotContain("has space", result.Error!.Message, StringComparison.Ordinal);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokensAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SendToDevice_rejects_a_malformed_token_without_calling_the_provider()
    {
        var result = await _service.SendToDeviceAsync(new string('x', 600), Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidToken, result.Error);
        await _sender.DidNotReceiveWithAnyArgs().SendToTokenAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_fault_during_validation_surfaces_instead_of_looking_like_an_outage()
    {
        _sender.SendToTokenAsync("abc", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.InvalidRequest, "invalid argument"));
        _sender.ValidateAsync(Notification, Arg.Any<CancellationToken>())
            .Returns<SendResult>(_ => throw new InvalidOperationException("credentials are broken"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendToDeviceAsync("abc", Notification, Ct));
    }

    [Fact]
    public async Task SendToDevice_never_reports_an_unanswered_send_as_safe_to_retry()
    {
        _sender.SendToTokenAsync("token-1", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unknown, "timeout"));

        var result = await _service.SendToDeviceAsync("token-1", Notification, Ct);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsConfirmed);
    }

    [Fact]
    public async Task SendToTopic_reports_an_unanswered_send_as_unconfirmed()
    {
        _sender.SendToTopicAsync("news", Notification, Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unknown, "timeout"));

        var result = await _service.SendToTopicAsync("news", Notification, Ct);

        Assert.Equal(SendReceipt.Unconfirmed, result.Value);
    }

    [Fact]
    public async Task Possibly_delivered_tokens_keep_the_summary_even_when_validation_would_fail()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("bad", SendResult.Failed(DeliveryFailure.InvalidRequest, "invalid argument")),
                new("slow", SendResult.Failed(DeliveryFailure.Unknown, "timeout")),
            });
        _sender.ValidateAsync(Notification, Arg.Any<CancellationToken>())
            .Returns<SendResult>(_ => throw new InvalidOperationException("must not be called"));

        var result = await _service.SendToDevicesAsync(["bad", "slow"], Notification, Ct);

        Assert.Equal(["slow"], result.Value.UnconfirmedTokens);
        await _sender.DidNotReceiveWithAnyArgs().ValidateAsync(default!, Ct);
    }

    [Fact]
    public async Task SendToDevices_lists_unanswered_tokens_as_unconfirmed_not_retryable()
    {
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(new List<TokenSendResult>
            {
                new("ok", SendResult.Delivered("msg-1")),
                new("quota", SendResult.Failed(DeliveryFailure.Unavailable, "quota exceeded")),
                new("timeout", SendResult.Failed(DeliveryFailure.Unknown, "timeout")),
            });

        var result = await _service.SendToDevicesAsync(["ok", "quota", "timeout"], Notification, Ct);

        Assert.Equal(["quota"], result.Value.RetryableTokens);
        Assert.Equal(["timeout"], result.Value.UnconfirmedTokens);
    }

    [Fact]
    public async Task SendToUser_rejects_an_overlong_user_id()
    {
        var result = await _service.SendToUserAsync(new string('u', 451), Notification, Ct);

        Assert.Equal(NotificationErrors.InvalidUserId, result.Error);
    }

    [Fact]
    public async Task SendToUser_reads_at_most_the_per_request_limit()
    {
        var many = Enumerable.Range(0, PushNotificationService.MaxTokensPerRequest).Select(i => $"t{i}").ToList();
        _repository.GetTokensForUserAsync("user-1", PushNotificationService.MaxTokensPerRequest, Arg.Any<CancellationToken>())
            .Returns(many);
        _sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Notification, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<string>>().Select(t => new TokenSendResult(t, SendResult.Delivered("m"))).ToList());

        var result = await _service.SendToUserAsync("user-1", Notification, Ct);

        Assert.Equal(PushNotificationService.MaxTokensPerRequest, result.Value.SuccessCount);
    }
}
