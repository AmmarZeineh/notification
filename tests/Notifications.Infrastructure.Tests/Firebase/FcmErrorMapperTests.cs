using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Notifications.Application.Models;
using Notifications.Infrastructure.Firebase;

namespace Notifications.Infrastructure.Tests.Firebase;

public sealed class FcmErrorMapperTests
{
    [Theory]
    [InlineData(MessagingErrorCode.Unregistered, DeliveryFailure.InvalidToken)]
    [InlineData(MessagingErrorCode.SenderIdMismatch, DeliveryFailure.Rejected)]
    [InlineData(MessagingErrorCode.QuotaExceeded, DeliveryFailure.Unavailable)]
    [InlineData(MessagingErrorCode.Unavailable, DeliveryFailure.Unavailable)]
    [InlineData(MessagingErrorCode.Internal, DeliveryFailure.Unavailable)]
    [InlineData(MessagingErrorCode.ThirdPartyAuthError, DeliveryFailure.Rejected)]
    public void Maps_messaging_error_codes(MessagingErrorCode code, DeliveryFailure expected) =>
        Assert.Equal(expected, FcmErrorMapper.Map(code, ErrorCode.Unknown));

    [Fact]
    public void Invalid_argument_is_an_invalid_request_not_an_invalid_token()
    {
        // INVALID_ARGUMENT also covers oversized payloads; deleting tokens for it would lose valid devices.
        Assert.Equal(
            DeliveryFailure.InvalidRequest,
            FcmErrorMapper.Map(MessagingErrorCode.InvalidArgument, ErrorCode.InvalidArgument));
    }

    [Theory]
    [InlineData(ErrorCode.Unavailable, DeliveryFailure.Unavailable)]
    [InlineData(ErrorCode.ResourceExhausted, DeliveryFailure.Unavailable)]
    [InlineData(ErrorCode.InvalidArgument, DeliveryFailure.InvalidRequest)]
    [InlineData(ErrorCode.PermissionDenied, DeliveryFailure.Rejected)]
    [InlineData(ErrorCode.Unauthenticated, DeliveryFailure.Rejected)]
    public void Falls_back_to_platform_error_code(ErrorCode code, DeliveryFailure expected) =>
        Assert.Equal(expected, FcmErrorMapper.Map(null, code));

    [Theory]
    [InlineData("registration-token-not-registered", DeliveryFailure.InvalidToken)]
    [InlineData("invalid-argument", DeliveryFailure.InvalidRequest)]
    [InlineData("internal-error", DeliveryFailure.Unavailable)]
    [InlineData("too-many-topics", DeliveryFailure.Rejected)]
    [InlineData("unknown-error", DeliveryFailure.Unavailable)]
    public void Maps_topic_management_reasons(string reason, DeliveryFailure expected) =>
        Assert.Equal(expected, FcmErrorMapper.MapTopicReason(reason));

    public static TheoryData<Exception> NoAnswerExceptions =>
    [
        new HttpRequestException("connection reset"),
        new TimeoutException(),
        new TaskCanceledException("HTTP timeout"),
    ];

    [Theory]
    [MemberData(nameof(NoAnswerExceptions))]
    public void A_send_without_an_answer_has_an_unknown_outcome(Exception exception) =>
        Assert.Equal(DeliveryFailure.Unknown, FcmErrorMapper.Classify(exception));

    [Fact]
    public void Local_io_errors_are_configuration_faults_not_outages()
    {
        Assert.Null(FcmErrorMapper.Classify(new FileNotFoundException("key.json")));
        Assert.Null(FcmErrorMapper.Classify(new IOException("disk")));
    }

    [Fact]
    public void Deadline_exceeded_has_an_unknown_outcome() =>
        Assert.Equal(DeliveryFailure.Unknown, FcmErrorMapper.Map(null, ErrorCode.DeadlineExceeded));

    [Fact]
    public void Unexpected_exceptions_are_not_classified() =>
        Assert.Null(FcmErrorMapper.Classify(new InvalidOperationException()));

    [Fact]
    public void An_unexpected_failure_while_in_flight_has_an_unknown_outcome() =>
        Assert.Equal(
            DeliveryFailure.Unknown,
            FcmErrorMapper.Classify(new DeliveryStateUnknownException("in flight", new InvalidOperationException())));
}
