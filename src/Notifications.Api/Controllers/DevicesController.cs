using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Api.Contracts;
using Notifications.Api.Extensions;
using Notifications.Application.Services;

namespace Notifications.Api.Controllers;

/// <summary>Called by the mobile app to register its FCM token for the signed-in user.</summary>
[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController(IDeviceRegistrationService registrations) : ControllerBase
{
    /// <remarks>Returns 409 when the token belongs to another user: delete the token on the device and register the new one.</remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var result = await registrations.RegisterAsync(User.GetUserId(), request.Token, request.Platform, cancellationToken);
        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error);
    }

    /// <summary>Call on sign-out so the user stops receiving notifications on this device.</summary>
    /// <remarks>A POST with a body rather than a DELETE with a query string, so the token stays out of URLs.</remarks>
    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unregister(UnregisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var result = await registrations.UnregisterAsync(User.GetUserId(), request.Token, cancellationToken);
        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error);
    }
}
