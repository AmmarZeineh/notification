using Notifications.Application.Models;
using Notifications.Infrastructure.Firebase;

namespace Notifications.Infrastructure.Tests.Firebase;

public sealed class FcmBatchingTests
{
    private sealed class ProviderOutageException : Exception;

    private sealed class TokenLikeException : Exception;

    private sealed class BadRequestException : Exception;

    private sealed class InFlightException : Exception;

    private static readonly string[] Tokens = ["a", "b", "c", "d", "e"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeliveryFailure? Classify(Exception ex) => ex switch
    {
        ProviderOutageException => DeliveryFailure.Unavailable,
        TokenLikeException => DeliveryFailure.InvalidToken,
        BadRequestException => DeliveryFailure.InvalidRequest,
        InFlightException => DeliveryFailure.Unknown,
        _ => null,
    };

    private static Task<IReadOnlyList<SendResult>> DeliverAll(string[] batch) =>
        Task.FromResult<IReadOnlyList<SendResult>>(batch.Select(t => SendResult.Delivered($"m-{t}")).ToList());

    [Fact]
    public async Task Send_keeps_results_of_batches_sent_before_a_provider_failure()
    {
        var calls = 0;

        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => ++calls == 2 ? throw new ProviderOutageException() : DeliverAll(batch),
            Classify,
            Ct);

        Assert.Equal(Tokens, results.Select(r => r.Token));
        Assert.All(results.Take(2), r => Assert.True(r.Result.IsSuccess));
        Assert.All(results.Skip(2), r => Assert.Equal(DeliveryFailure.Unavailable, r.Result.Failure));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Send_runs_batches_concurrently_and_keeps_token_order()
    {
        var tokens = Enumerable.Range(0, 10).Select(i => $"t{i}").ToArray();
        var running = 0;
        var maxRunning = 0;

        var results = await FcmBatching.SendAsync(
            tokens,
            batchSize: 2,
            maxConcurrency: 3,
            async batch =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(20, Ct);
                Interlocked.Decrement(ref running);
                return await DeliverAll(batch);
            },
            Classify,
            Ct);

        Assert.Equal(tokens, results.Select(r => r.Token));
        Assert.All(results, r => Assert.Equal($"m-{r.Token}", r.Result.MessageId));
        Assert.InRange(maxRunning, 2, 3);
    }

    [Fact]
    public async Task Send_never_reports_a_whole_batch_failure_as_a_token_or_message_problem()
    {
        var calls = 0;

        // The first batch delivers, so per-token deletion rules would otherwise kick in for the rest.
        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => ++calls switch
            {
                1 => DeliverAll(batch),
                2 => throw new BadRequestException(),
                _ => throw new TokenLikeException(),
            },
            Classify,
            Ct);

        // The failed batch is never reported as a token or message problem, and the batch skipped after it
        // inherits the rejection rather than being reported as retryable.
        Assert.All(results.Skip(2), r => Assert.Equal(DeliveryFailure.Rejected, r.Result.Failure));
    }

    [Fact]
    public async Task Send_reports_batches_skipped_after_a_rejection_as_rejected_not_retryable()
    {
        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            _ => throw new BadRequestException(),
            Classify,
            Ct);

        // Bad credentials fail every batch the same way; nothing here may look like a temporary outage.
        Assert.All(results, r => Assert.Equal(DeliveryFailure.Rejected, r.Result.Failure));
    }

    [Fact]
    public async Task Send_reports_batches_skipped_after_an_outage_as_retryable()
    {
        var calls = 0;

        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => ++calls == 1 ? throw new ProviderOutageException() : DeliverAll(batch),
            Classify,
            Ct);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(DeliveryFailure.Unavailable, r.Result.Failure));
    }

    [Fact]
    public async Task Send_honours_cancellation_only_before_anything_is_sent()
    {
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => { calls++; return DeliverAll(batch); },
            Classify,
            new CancellationToken(canceled: true)));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Send_finishes_and_reports_every_batch_when_the_caller_cancels_midway()
    {
        using var cts = new CancellationTokenSource();

        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            async batch =>
            {
                // The caller disconnects after the first batch reached devices.
                await cts.CancelAsync();
                return await DeliverAll(batch);
            },
            Classify,
            cts.Token);

        Assert.Equal(Tokens, results.Select(r => r.Token));
        Assert.All(results, r => Assert.True(r.Result.IsSuccess));
    }

    [Fact]
    public async Task Send_keeps_delivered_results_and_skips_the_rest_after_an_unexpected_error()
    {
        var calls = 0;

        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => ++calls == 1 ? DeliverAll(batch) : throw new InvalidOperationException("bug"),
            Classify,
            Ct);

        // An unclassified exception means batch 2 never started, because of a local fault a retry would hit
        // again: it and the skipped batch 3 are rejected, not reported as a temporary outage.
        Assert.All(results.Take(2), r => Assert.True(r.Result.IsSuccess));
        Assert.All(results.Skip(2), r => Assert.Equal(DeliveryFailure.Rejected, r.Result.Failure));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Send_rethrows_unexpected_exceptions()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 2,
            _ => throw new InvalidOperationException(),
            Classify,
            Ct));
    }

    [Fact]
    public async Task ManageTopic_reports_unprocessed_tokens_and_the_provider_failure()
    {
        var calls = 0;

        var result = await FcmBatching.ManageTopicAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => ++calls == 2
                ? throw new ProviderOutageException()
                : Task.FromResult((batch.Length, Enumerable.Empty<TopicSubscriptionFailure>())),
            Classify,
            Ct);

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(["c", "d", "e"], result.Failures.Select(f => f.Token));
        Assert.All(result.Failures, f => Assert.Equal(DeliveryFailure.Unavailable, f.Failure));
        Assert.Equal(DeliveryFailure.Unavailable, result.ProviderFailure);
        Assert.Equal(3, result.UnprocessedCount);
    }

    [Fact]
    public async Task ManageTopic_combines_all_batches_when_nothing_fails()
    {
        var result = await FcmBatching.ManageTopicAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            batch => Task.FromResult((batch.Length - 1,
                (IEnumerable<TopicSubscriptionFailure>)[new(batch[0], "invalid-argument", DeliveryFailure.InvalidToken)])),
            Classify,
            Ct);

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(["a", "c", "e"], result.Failures.Select(f => f.Token));
        Assert.Null(result.ProviderFailure);
        Assert.Equal(0, result.UnprocessedCount);
    }

    [Fact]
    public async Task ManageTopic_runs_batches_concurrently_and_keeps_failure_order()
    {
        var tokens = Enumerable.Range(0, 8).Select(i => $"t{i}").ToArray();

        var result = await FcmBatching.ManageTopicAsync(
            tokens,
            batchSize: 2,
            maxConcurrency: 4,
            async batch =>
            {
                // Later batches finish first, so the output order must not depend on completion order.
                await Task.Delay(40 - (int.Parse(batch[0][1..], System.Globalization.CultureInfo.InvariantCulture) * 5), Ct);
                return (1, (IEnumerable<TopicSubscriptionFailure>)[new(batch[1], "too-many-topics", DeliveryFailure.Rejected)]);
            },
            Classify,
            Ct);

        Assert.Equal(4, result.SuccessCount);
        Assert.Equal(["t1", "t3", "t5", "t7"], result.Failures.Select(f => f.Token));
    }

    [Fact]
    public async Task Send_keeps_possibly_delivered_results_instead_of_rethrowing_an_unexpected_error()
    {
        var calls = 0;

        var results = await FcmBatching.SendAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            _ => ++calls == 1 ? throw new InFlightException() : throw new InvalidOperationException("bug"),
            Classify,
            Ct);

        Assert.All(results.Take(2), r => Assert.Equal(DeliveryFailure.Unknown, r.Result.Failure));
    }

    [Fact]
    public async Task Whole_batch_failures_report_a_generic_reason_not_the_provider_text()
    {
        var result = await FcmBatching.ManageTopicAsync(
            Tokens,
            batchSize: 2,
            maxConcurrency: 1,
            _ => throw new ProviderOutageException(),
            Classify,
            Ct);

        Assert.All(result.Failures, f => Assert.DoesNotContain("ProviderOutageException", f.Reason, StringComparison.Ordinal));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
            Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
