using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Common;

namespace Notifications.Api.Extensions;

internal static class ResultExtensions
{
    private const string RetryAfterSeconds = "30";

    /// <summary>Maps an application <see cref="Error"/> to an RFC 9457 problem details response.</summary>
    public static IActionResult ToProblem(this ControllerBase controller, Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status502BadGateway,
        };

        if (error == NotificationErrors.ProviderUnavailable)
        {
            // Tells clients to back off instead of retrying in a tight loop (often FCM quota throttling).
            // Only for this error: other errors are either not retryable or, like the 409 registration race,
            // can be retried immediately.
            controller.Response.Headers.RetryAfter = RetryAfterSeconds;
        }

        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext, statusCode, title: error.Code, detail: error.Message);

        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
