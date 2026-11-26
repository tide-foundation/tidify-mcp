using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

/// <summary>
/// Forwards a request for {flattened mcp server domain}.{proxy domain} to the MCP server:
///   https://amb-tideprotocol-com.agentproxy.tideprotocol.com/mcp?x=1 -> https://amb.tideprotocol.com/mcp?x=1
/// The MCP server's domain is flattened into one label: each "." becomes "-", and a real "-" is written "--"
/// (my-server.com -> my--server-com). The proxy domain (here agentproxy.tideprotocol.com) comes from the ProxyDomain
/// argument, so the proxy knows where the flattened label ends. The client's headers go along, token included, and the server's answer streams back as it
/// arrives, so SSE works.
/// OAuth discovery is rewritten to name the proxied address instead of the server's own (WWW-Authenticate's
/// resource_metadata, and "resource" in /.well-known/oauth-protected-resource), because MCP clients reject metadata whose
/// resource isn't the URL they connected to. The login itself still goes to the server's authorization server.
/// Requests for any other host (e.g. the proxy domain itself) go on to the rest of the app.
/// </summary>
public class McpProxyMiddleware(RequestDelegate next, string proxyDomain)
{
	// Headers about this one connection (or meant for the proxy itself): not passed on in either direction
	private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
		{ "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Upgrade", "Proxy-Connection", "Proxy-Authorization" };

	// ".agentproxy.tideprotocol.com": what a proxied host ends with
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

		// "amb-tideprotocol-com.agentproxy.tideprotocol.com" -> "amb-tideprotocol-com". The port the client used is the proxy's, not the server's.
		var host = context.Request.Host.Host;
		if (!host.EndsWith(hostSuffix, StringComparison.OrdinalIgnoreCase) || host.Length == hostSuffix.Length)
		{
			await next(context);
			return;
		}
		var label = host[..^hostSuffix.Length];

		// "amb-tideprotocol-com" -> "amb.tideprotocol.com", "my--server-com" -> "my-server.com".
		// A label with a "." in it isn't a flattened domain, so it's left for the check below to turn away.
		var serverName = label.Contains('.') ? "" : label.Replace("--", "\0").Replace('-', '.').Replace("\0", "-");

		// The server name must be just a host, so it can't smuggle in a port, path, user info or another scheme.
		if (Uri.CheckHostName(serverName) != UriHostNameType.Dns
			|| !Uri.TryCreate($"https://{serverName}{context.Request.Path.ToUriComponent()}{context.Request.QueryString}", UriKind.Absolute, out var target)
			|| !target.Host.Equals(serverName, StringComparison.OrdinalIgnoreCase) || !target.IsDefaultPort)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync($"Expected {{mcp server domain, with \".\" as \"-\" and \"-\" as \"--\"}}{hostSuffix} as the host.");
			return;
		}

		// "https://amb.tideprotocol.com" -> "https://amb-tideprotocol-com.agentproxy.tideprotocol.com", as the client sees it.
		// A TLS-terminating front (nginx, cloudflare) says the scheme in X-Forwarded-Proto; without one, assume https.
		var serverOrigin = $"https://{target.Authority}";
		var publicOrigin = $"{context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https"}://{context.Request.Host}";
		string ToPublic(string value) => value.Replace(serverOrigin, publicOrigin, StringComparison.OrdinalIgnoreCase);

		// "/.well-known/oauth-protected-resource" and "/.well-known/oauth-protected-resource/mcp"
		var isResourceMetadata = context.Request.Path.StartsWithSegments("/.well-known/oauth-protected-resource");

		using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
		if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
			request.Content = new StreamContent(context.Request.Body);
		foreach (var (name, values) in context.Request.Headers)
		{
			if (HopByHop.Contains(name)) continue;
			// The resource metadata is read and rewritten here, so ask for it uncompressed
			if (isResourceMetadata && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
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
			{
				if (HopByHop.Contains(name)) continue;
				// Bearer resource_metadata="https://amb.tideprotocol.com/..." -> the proxied address, so discovery comes back through here
				context.Response.Headers[name] = name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
					? values.Select(ToPublic).ToArray() : values.ToArray();
			}

			// {"resource":"https://amb.tideprotocol.com/mcp", "authorization_servers":[...]} -> resource on the proxied address.
			// authorization_servers is left alone: the login still happens at the server's own authorization server.
			if (isResourceMetadata && response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
				try
				{
					if (JsonNode.Parse(body) is JsonObject metadata
						&& metadata["resource"] is JsonValue resource && resource.TryGetValue<string>(out var resourceUrl))
					{
						metadata["resource"] = ToPublic(resourceUrl);
						body = metadata.ToJsonString();
					}
				}
				catch (System.Text.Json.JsonException) { } // not JSON: passed on as it came
				context.Response.Headers.ContentLength = null; // the rewritten body has its own length
				await context.Response.WriteAsync(body, context.RequestAborted);
				return;
			}

			// Send each chunk to the client the moment it arrives, which is what SSE needs
			context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
			await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
		}
	}
}
