using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Abstractions;
using Notifications.Application.Models;

namespace Notifications.Infrastructure.Firebase;

/// <summary>
/// <see cref="IPushNotificationSender"/> backed by the Firebase Admin SDK.
/// Registered as a singleton; <see cref="FirebaseMessaging"/> is thread-safe.
/// </summary>
internal sealed partial class FirebaseMessagingSender : IPushNotificationSender, IDisposable
{
    /// <summary>
    /// Prefix of this sender's FirebaseApp. Each sender instance gets its own app (FirebaseApp instances are
    /// process-wide), so it never collides with the host's default app or reuses another instance's credentials.
    /// </summary>
    internal const string AppNamePrefix = "notifications-";

    // SendEachAsync sends every message of a batch as its own concurrent HTTP request, so batch size times
    // concurrent batches is the number of requests in flight. 2 x 100 keeps it at 200, enough throughput
    // without exhausting connections or tripping FCM's quota on its own.
    private const int SendBatchSize = 100;
    private const int MaxConcurrentSendBatches = 2;

    // Topic management sends one HTTP request per batch of up to 1,000 tokens.
    private const int TopicManagementBatchSize = 1000;
    private const int MaxConcurrentTopicBatches = 4;

    /// <summary>Messages are validated by a dry run to this topic, which delivers nothing.</summary>
    private const string ValidationTopic = "payload-validation";

    private readonly Lock _appCreationLock = new();
    private readonly string _appName = AppNamePrefix + Guid.NewGuid().ToString("N");
    private FirebaseApp? _app;

    private readonly Lazy<FirebaseMessaging> _messaging;
    private readonly FcmMessageFactory _messageFactory;
    private readonly ILogger<FirebaseMessagingSender> _logger;

    public FirebaseMessagingSender(
        IOptions<FirebaseOptions> options,
        FcmMessageFactory messageFactory,
        ILogger<FirebaseMessagingSender> logger)
    {
        _messageFactory = messageFactory;
        _logger = logger;
        // PublicationOnly does not cache a failed initialization (e.g. a brief credential outage),
        // so the next send tries again instead of failing until the process restarts. Concurrent first
        // calls may both run the factory, which is why GetOrCreateApp is synchronized.
        _messaging = new Lazy<FirebaseMessaging>(
            () => FirebaseMessaging.GetMessaging(GetOrCreateApp(options.Value)),
            LazyThreadSafetyMode.PublicationOnly);
    }

    public async Task<SendResult> SendToTokenAsync(
        string token, PushNotification notification, CancellationToken cancellationToken)
    {
        var message = _messageFactory.CreateForToken(token, notification);
        return await SendAsync(message, target: "device", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes this sender's FirebaseApp when the host shuts down.</summary>
    public void Dispose()
    {
        lock (_appCreationLock)
        {
            _app?.Delete();
            _app = null;
        }
    }

    public async Task<SendResult> SendToTopicAsync(
        string topic, PushNotification notification, CancellationToken cancellationToken)
    {
        var message = _messageFactory.CreateForTopic(topic, notification);
        return await SendAsync(message, target: $"topic '{topic}'", cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TokenSendResult>> SendToTokensAsync(
        IReadOnlyList<string> tokens, PushNotification notification, CancellationToken cancellationToken) =>
        FcmBatching.SendAsync(
            tokens,
            SendBatchSize,
            MaxConcurrentSendBatches,
            async batch =>
            {
                // Failures up to here mean nothing was sent.
                var messages = _messageFactory.CreateForTokens(batch, notification);
                var messaging = _messaging.Value;

                return await WhileInFlightAsync(async () =>
                {
                    // Not the caller's token: a started send always completes (see FcmBatching).
                    var response = await messaging.SendEachAsync(messages, CancellationToken.None).ConfigureAwait(false);

                    // Responses are in the same order as the tokens in the request.
                    return (IReadOnlyList<SendResult>)response.Responses.Select(ToSendResult).ToList();
                }).ConfigureAwait(false);
            },
            ClassifyAndLog,
            cancellationToken);

    public async Task<SendResult> ValidateAsync(PushNotification notification, CancellationToken cancellationToken)
    {
        var message = _messageFactory.CreateForTopic(ValidationTopic, notification);
        return await SendAsync(message, target: "validation dry run", cancellationToken, dryRun: true).ConfigureAwait(false);
    }

    public Task<TopicSubscriptionResult> SubscribeToTopicAsync(
        IReadOnlyList<string> tokens, string topic, CancellationToken cancellationToken) =>
        ManageTopicAsync(tokens, (batch, messaging) => messaging.SubscribeToTopicAsync(batch, topic), cancellationToken);

    public Task<TopicSubscriptionResult> UnsubscribeFromTopicAsync(
        IReadOnlyList<string> tokens, string topic, CancellationToken cancellationToken) =>
        ManageTopicAsync(tokens, (batch, messaging) => messaging.UnsubscribeFromTopicAsync(batch, topic), cancellationToken);

    private async Task<SendResult> SendAsync(
        Message message, string target, CancellationToken cancellationToken, bool dryRun = false)
    {
        // Same rule as batches (see FcmBatching): cancellation counts only before the request starts. Once FCM
        // may have accepted the message, the outcome is always reported, so callers never retry into a duplicate.
        cancellationToken.ThrowIfCancellationRequested();
        var messaging = _messaging.Value; // A failure here (e.g. unreadable credentials) means nothing was sent.

        try
        {
            var messageId = await messaging.SendAsync(message, dryRun, CancellationToken.None).ConfigureAwait(false);
            return SendResult.Delivered(messageId);
        }
        catch (Exception ex)
        {
            if (FcmErrorMapper.Classify(ex) is { } failure)
            {
                LogSendFailed(ex, target, failure);
                return SendResult.Failed(failure, ex.Message);
            }

            // A dry run delivers nothing, so there an unexpected exception surfaces as the bug it is.
            if (dryRun)
            {
                throw;
            }

            // Unexpected, but the request was in flight: the message may have been delivered.
            LogSendFailedUnexpectedly(ex, target);
            return SendResult.Failed(DeliveryFailure.Unknown, "The provider request failed unexpectedly.");
        }
    }

    private Task<TopicSubscriptionResult> ManageTopicAsync(
        IReadOnlyList<string> tokens,
        Func<IReadOnlyList<string>, FirebaseMessaging, Task<TopicManagementResponse>> operation,
        CancellationToken cancellationToken) =>
        FcmBatching.ManageTopicAsync(
            tokens,
            TopicManagementBatchSize,
            MaxConcurrentTopicBatches,
            async batch =>
            {
                var messaging = _messaging.Value;
                return await WhileInFlightAsync(async () =>
                {
                    var response = await operation(batch, messaging).ConfigureAwait(false);

                    // e.Reason is the SDK's reason code (e.g. "registration-token-not-registered"), safe to return.
                    return (response.SuccessCount, response.Errors
                        .Select(e => new TopicSubscriptionFailure(batch[e.Index], e.Reason, FcmErrorMapper.MapTopicReason(e.Reason)))
                        .ToList()
                        .AsEnumerable());
                }).ConfigureAwait(false);
            },
            ClassifyTopicFailure,
            cancellationToken);

    /// <summary>
    /// Subscribing or unsubscribing twice is harmless, so a request that got no answer is simply retryable.
    /// </summary>
    private DeliveryFailure? ClassifyTopicFailure(Exception exception) =>
        ClassifyAndLog(exception) switch
        {
            DeliveryFailure.Unknown => DeliveryFailure.Unavailable,
            var failure => failure,
        };

    /// <summary>
    /// Runs a provider request. An unexpected exception from it is wrapped in
    /// <see cref="DeliveryStateUnknownException"/>: the request may have been processed, so it must not be
    /// reported as "not sent". Known provider and network failures are passed through to be classified.
    /// </summary>
    private static async Task<T> WhileInFlightAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (Exception ex) when (FcmErrorMapper.Classify(ex) is null)
        {
            throw new DeliveryStateUnknownException("The provider request failed unexpectedly.", ex);
        }
    }

    private DeliveryFailure? ClassifyAndLog(Exception exception)
    {
        var failure = FcmErrorMapper.Classify(exception);
        if (failure is null || exception is DeliveryStateUnknownException)
        {
            LogBatchFailedUnexpectedly(exception);
        }
        else
        {
            LogBatchFailed(exception, failure.Value);
        }

        return failure;
    }

    private static SendResult ToSendResult(SendResponse response) =>
        response.IsSuccess
            ? SendResult.Delivered(response.MessageId)
            : SendResult.Failed(FcmErrorMapper.Map(response.Exception), response.Exception.Message);

    private FirebaseApp GetOrCreateApp(FirebaseOptions options)
    {
        // The lock stops concurrent first requests from both creating the app, which would throw for the second.
        lock (_appCreationLock)
        {
            return _app ??= FirebaseApp.Create(
                new AppOptions
                {
                    Credential = CreateCredential(options),
                    ProjectId = options.ProjectId,
                },
                _appName);
        }
    }

    private static GoogleCredential CreateCredential(FirebaseOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CredentialsPath))
        {
            return CredentialFactory.FromFile<ServiceAccountCredential>(options.CredentialsPath).ToGoogleCredential();
        }

        if (!string.IsNullOrWhiteSpace(options.CredentialsJson))
        {
            return CredentialFactory.FromJson<ServiceAccountCredential>(options.CredentialsJson).ToGoogleCredential();
        }

        return GoogleCredential.GetApplicationDefault();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push notification to {Target} failed ({Failure})")]
    private partial void LogSendFailed(Exception exception, string target, DeliveryFailure failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push provider failed a whole batch ({Failure}); remaining tokens were not processed")]
    private partial void LogBatchFailed(Exception exception, DeliveryFailure failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push notification to {Target} failed unexpectedly while in flight; reported as unconfirmed")]
    private partial void LogSendFailedUnexpectedly(Exception exception, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "A push batch failed with an unexpected error; batches not yet started were skipped")]
    private partial void LogBatchFailedUnexpectedly(Exception exception);
}
