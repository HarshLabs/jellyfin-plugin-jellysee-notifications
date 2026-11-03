using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellySeeNotifications;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
public sealed record BindRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("registration")] string Registration,
    [property: System.Text.Json.Serialization.JsonPropertyName("profile")] string Profile);
public sealed record HealthResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("health")] string Health,
    [property: System.Text.Json.Serialization.JsonPropertyName("setupId")] string SetupId);

[ApiController]
[Authorize]
[Route("JellySeeNotifications")]
public sealed class NotificationsController(NotificationService service, IServerApplicationHost host) : ControllerBase
{
    // Jellyfin's user claim is populated only from its authenticated session.
    private Guid SessionUser => User.Identity?.IsAuthenticated == true && Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    [HttpPost("Bind")]
    public async Task<IActionResult> Bind([FromBody] BindRequest request, CancellationToken ct)
    {
        if (SessionUser == Guid.Empty) return Unauthorized();
        if (!Identity.IsId(request.Registration) || !Binding.ValidProfile(request.Profile)) return BadRequest();
        if (!await service.Bind(request.Registration, request.Profile, Identity.UserHash(Identity.ServerKey(host.SystemId), SessionUser), ct)) return StatusCode(503);
        return Ok();
    }

    [HttpGet("Health")]
    public ActionResult<HealthResponse> Health()
    {
        if (SessionUser == Guid.Empty) return Unauthorized();
        return service.Health();
    }

    [HttpGet("Diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Diagnostics() => Ok(service.Diagnostics());
}
