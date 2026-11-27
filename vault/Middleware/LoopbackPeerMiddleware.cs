using System.Net;

namespace Asgard.Vault.Middleware;

/// <summary>
/// The first step of a proxied request: it must come from a loopback peer, the nginx in front on this machine.
/// This is the TCP peer, not X-Forwarded-For, so a client can't fake it.
/// </summary>
public class LoopbackPeerMiddleware(RequestDelegate next)
{
	public async Task InvokeAsync(HttpContext context)
	{
		if (context.Connection.RemoteIpAddress is not { } peer || !EnsureProxyAvailability.IsLoopback(peer))
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			await context.Response.WriteAsync("Proxied requests are only accepted from a loopback peer.");
			return;
		}

		await next(context);
	}
}
