using System.Runtime.ExceptionServices;
using Notifications.Application.Models;

namespace Notifications.Infrastructure.Firebase;

/// <summary>
/// Splits large requests into provider-sized batches and runs up to a fixed number at a time.
/// When the provider fails a whole batch, the results already collected are kept and the unsent
/// tokens are reported as failed, instead of losing everything to an exception.
/// </summary>
/// <remarks>
/// <para>
/// A request is all or nothing for the caller: cancellation is honoured only before the first batch
/// starts. Once a batch may have reached devices, the rest is sent and every result is returned, so
/// a disconnecting caller never loses the record of what was delivered (and never retries into duplicates).
/// </para>
/// <para>
/// <c>classifyFailure</c> returns the failure for a provider exception, or null for an unexpected one.
/// The batch delegate must report an unexpected failure during the request itself as a classified
/// <see cref="DeliveryFailure.Unknown"/> (the request may have been processed); an unclassified exception
/// therefore means the batch was never sent, and is reported as <see cref="DeliveryFailure.Rejected"/>: a local
/// fault (e.g. unreadable credentials) that a retry would hit again. After any batch failure, batches that have
/// not started are skipped. They inherit a <see cref="DeliveryFailure.Rejected"/> failure, since they would be
/// rejected too; after any other failure they are reported as <see cref="DeliveryFailure.Unavailable"/>: never
/// sent, so safe to resend. An unexpected exception is rethrown only if nothing was, or may have been, delivered; otherwise every
/// result is kept. Reasons for whole-batch failures are generic: raw provider text stays in the logs.
/// </para>
/// </remarks>
internal static class FcmBatching
{
    /// <remarks><c>sendBatch</c> returns one result per token, in order.</remarks>
    public static async Task<IReadOnlyList<TokenSendResult>> SendAsync(
        IReadOnlyList<string> tokens,
        int batchSize,
        int maxConcurrency,
        Func<string[], Task<IReadOnlyList<SendResult>>> sendBatch,
        Func<Exception, DeliveryFailure?> classifyFailure,
        CancellationToken cancellationToken)
    {
        var batches = tokens.Chunk(batchSize).ToArray();
        var outcomes = await RunAsync(batches, maxConcurrency, sendBatch, classifyFailure, cancellationToken).ConfigureAwait(false);

        return batches
            .SelectMany((batch, b) => batch.Select((token, i) => new TokenSendResult(
                token,
                outcomes[b].Failure is { } failure ? SendResult.Failed(failure, outcomes[b].Reason!) : outcomes[b].Result![i])))
            .ToList();
    }

    /// <remarks><c>manageBatch</c> returns the success count and the failed tokens of one batch.</remarks>
    public static async Task<TopicSubscriptionResult> ManageTopicAsync(
        IReadOnlyList<string> tokens,
        int batchSize,
        int maxConcurrency,
        Func<string[], Task<(int SuccessCount, IEnumerable<TopicSubscriptionFailure> Failures)>> manageBatch,
        Func<Exception, DeliveryFailure?> classifyFailure,
        CancellationToken cancellationToken)
    {
        var batches = tokens.Chunk(batchSize).ToArray();
        var outcomes = await RunAsync(batches, maxConcurrency, manageBatch, classifyFailure, cancellationToken).ConfigureAwait(false);

        var successCount = 0;
        var unprocessedCount = 0;
        DeliveryFailure? providerFailure = null;
        var failures = new List<TopicSubscriptionFailure>();

        for (var b = 0; b < batches.Length; b++)
        {
            if (outcomes[b].Failure is { } failure)
            {
                providerFailure ??= failure;
                unprocessedCount += batches[b].Length;
                failures.AddRange(batches[b].Select(token => new TopicSubscriptionFailure(token, outcomes[b].Reason!, failure)));
            }
            else
            {
                successCount += outcomes[b].Result.SuccessCount;
                failures.AddRange(outcomes[b].Result.Failures);
            }
        }

        return new TopicSubscriptionResult(successCount, failures, providerFailure, unprocessedCount);
    }

    private static async Task<BatchOutcome<T>[]> RunAsync<T>(
        string[][] batches,
        int maxConcurrency,
        Func<string[], Task<T>> runBatch,
        Func<Exception, DeliveryFailure?> classifyFailure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcomes = new BatchOutcome<T>?[batches.Length];
        BatchOutcome<T>? stopReason = null;
        Exception? unexpected = null;
        var stopLock = new Lock();

        // Deliberately not linked to the caller's token: see the class remarks.
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency };

        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length), parallelOptions, async (b, _) =>
        {
            lock (stopLock)
            {
                // The provider rejected a whole request; the batches not yet sent would fail the same way.
                if (stopReason is { } earlier)
                {
                    outcomes[b] = earlier;
                    return;
                }
            }

            try
            {
                outcomes[b] = new BatchOutcome<T>(await runBatch(batches[b]).ConfigureAwait(false), null, null);
            }
            catch (Exception ex)
            {
                var failure = ClassifyBatchFailure(classifyFailure, ex);

                // Unclassified means it failed before the request started (see the class remarks): not sent, and
                // a local fault such as unreadable credentials, which a retry would hit again.
                var failed = failure is { } known
                    ? new BatchOutcome<T>(default, known, $"The provider failed the whole request ({known}).")
                    : new BatchOutcome<T>(default, DeliveryFailure.Rejected, "Not sent: the request could not be started.");

                // Skipped batches would fail the same way, so a permanent failure is passed on rather than
                // hidden behind a retryable one. Anything else (an outage, no answer) leaves them merely unsent.
                var skipped = failed.Failure == DeliveryFailure.Rejected
                    ? new BatchOutcome<T>(default, DeliveryFailure.Rejected, "Not sent: an earlier batch was rejected.")
                    : new BatchOutcome<T>(default, DeliveryFailure.Unavailable, "Not sent: an earlier batch failed.");
                lock (stopLock)
                {
                    stopReason ??= skipped;
                    if (failure is null)
                    {
                        unexpected ??= ex;
                    }
                }

                outcomes[b] = failed;
            }
        }).ConfigureAwait(false);

        if (unexpected is not null &&
            outcomes.All(o => o!.Value.Failure is { } failure && failure != DeliveryFailure.Unknown))
        {
            // Nothing was, or may have been, delivered, so there is no result to protect: surface the bug.
            ExceptionDispatchInfo.Throw(unexpected);
        }

        return outcomes.Select(o => o!.Value).ToArray();
    }

    /// <summary>
    /// A whole request never fails because of its tokens or its content as seen per device, so a failure
    /// that looks like an invalid token or message is reported as rejected. Otherwise the caller would
    /// treat every token in the batch as malformed and delete it.
    /// </summary>
    private static DeliveryFailure? ClassifyBatchFailure(Func<Exception, DeliveryFailure?> classifyFailure, Exception exception) =>
        classifyFailure(exception) switch
        {
            DeliveryFailure.InvalidToken or DeliveryFailure.InvalidRequest => DeliveryFailure.Rejected,
            var failure => failure,
        };

    /// <summary>Either the batch's result, or the failure that stopped the whole batch.</summary>
    private readonly record struct BatchOutcome<T>(T? Result, DeliveryFailure? Failure, string? Reason);
}
