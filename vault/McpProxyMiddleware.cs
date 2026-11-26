using System.Net;
using Microsoft.AspNetCore.Http.Features;

/// <summary>
/// Forwards a request for {mcp server domain}.{proxy domain} to the MCP server:
///   https://amb.tideprotocol.com.vault.example.com/mcp?x=1 -> https://amb.tideprotocol.com/mcp?x=1
/// The proxy domain (here vault.example.com) comes from the ProxyDomain argument, so the proxy knows where the MCP
/// server's domain ends. The client's headers go along, token included, and the server's answer streams back as it
/// arrives, so SSE works. Requests for any other host (e.g. the proxy domain itself) go on to the rest of the app.
/// </summary>
public class McpProxyMiddleware(RequestDelegate next, string proxyDomain)
{
	// Headers about this one connection (or meant for the proxy itself): not passed on in either direction
	private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
		{ "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Upgrade", "Proxy-Connection", "Proxy-Authorization" };

	// ".vault.example.com": what a proxied host ends with
	private readonly string hostSuffix = "." + proxyDomain.Trim().Trim('.');

	// One client for every server (the middleware is created once). No timeout, because SSE streams stay open.
	// No redirects, cookies or decompression, so the client gets the server's answer exactly as it was sent.
	private readonly HttpClient client = new(new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		UseCookies = false,
		AutomaticDecompression = DecompressionMethods.None,
		PooledConnectionLifetime = TimeSpan.FromMinutes(5) // picks up DNS changes
	}) { Timeout = Timeout.InfiniteTimeSpan };

	public async Task InvokeAsync(HttpContext context)
	{
		// DEBUG: every request's headers (Authorization included, so tokens end up in the console) - remove when done
		Console.WriteLine($"{context.Request.Method} {context.Request.Host}{context.Request.Path}{context.Request.QueryString}");
		foreach (var (name, values) in context.Request.Headers)
			Console.WriteLine($"  {name}: {values}");

		// "amb.tideprotocol.com.vault.example.com" -> "amb.tideprotocol.com". The port the client used is the proxy's, not the server's.
		var host = context.Request.Host.Host;
		if (!host.EndsWith(hostSuffix, StringComparison.OrdinalIgnoreCase) || host.Length == hostSuffix.Length)
		{
			await next(context);
			return;
		}
		var serverName = host[..^hostSuffix.Length];

		// The server name must be just a host, so it can't smuggle in a port, path, user info or another scheme.
		if (Uri.CheckHostName(serverName) != UriHostNameType.Dns
			|| !Uri.TryCreate($"https://{serverName}{context.Request.Path.ToUriComponent()}{context.Request.QueryString}", UriKind.Absolute, out var target)
			|| !target.Host.Equals(serverName, StringComparison.OrdinalIgnoreCase) || !target.IsDefaultPort)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync($"Expected {{mcp server domain}}{hostSuffix} as the host.");
			return;
		}

		using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
		if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
			request.Content = new StreamContent(context.Request.Body);
		foreach (var (name, values) in context.Request.Headers)
		{
			if (HopByHop.Contains(name)) continue;
			// Authorization, Mcp-Session-Id, Accept... go on the request. Content-Type and Content-Length go on its body.
			if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
				request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
		}

		HttpResponseMessage response;
		try
		{
			// Returns as soon as the headers arrive, so an SSE body is streamed instead of waited for
			response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
		}
		catch (HttpRequestException error)
		{
			context.Response.StatusCode = StatusCodes.Status502BadGateway;
			await context.Response.WriteAsync($"Couldn't reach {target.Host}: {error.Message}");
			return;
		}

		using (response)
		{
			context.Response.StatusCode = (int)response.StatusCode;
			// WWW-Authenticate, Mcp-Session-Id, Content-Type... all come back to the client
			foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
				if (!HopByHop.Contains(name)) context.Response.Headers[name] = values.ToArray();

			// Send each chunk to the client the moment it arrives, which is what SSE needs
			context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
			await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
		}
	}
}
