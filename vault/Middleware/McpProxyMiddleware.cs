using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Asgard.Vault.Middleware;

/// <summary>
/// Forwards a request for to the MCP server:
///   https://amb-tideprotocol-com.agentproxy.tideprotocol.com/mcp -> https://amb.tideprotocol.com/mcp
/// The MCP server's domain is flattened into one label: each "." becomes "-", and a real "-" is written "--"
/// (my-server.com -> my--server-com). The proxy domain (here agentproxy.tideprotocol.com) comes from the ProxyDomain
/// argument, so the proxy knows where the flattened label ends.
/// Its security (who may use it, where it may go, and its limits) is in EnsureProxyAvailability.
/// </summary>
public class McpProxyMiddleware(RequestDelegate next, IConfiguration config)
{
	// Headers about this one connection (or meant for the proxy itself): not passed on in either direction
	private static readonly HashSet<string> SkippableHeaders = new(StringComparer.OrdinalIgnoreCase)
		{ "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Upgrade", "Proxy-Connection", "Proxy-Authorization" };

	private readonly string proxyDomain = GetProxyDomain(config);

	// The --ProxyDomain argument, without surrounding spaces or dots
	public static string GetProxyDomain(IConfiguration config) => (config.GetValue<string>("ProxyDomain")
		?? throw new InvalidOperationException("Missing the proxy domain. Pass it as --ProxyDomain=<domain>"))
		.Trim()
		.Trim('.');

	// "amb-tideprotocol-com.agentproxy.tideprotocol.com" is proxied; "agentproxy.tideprotocol.com" itself, and the
	// vault's own pages, aren't. Program.cs uses it to send only proxied requests down the proxy's pipeline.
	public static bool IsProxiedHost(string host, string proxyDomain)
		=> host.EndsWith("." + proxyDomain, StringComparison.OrdinalIgnoreCase);

	private readonly EnsureProxyAvailability availability = new(config);

	// One client for every server (the middleware is created once).
	// No redirects, cookies or decompression, so the client gets the server's answer exactly as it was sent.
	private readonly HttpClient client = new(new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		UseCookies = false,
		AutomaticDecompression = DecompressionMethods.None,
		PooledConnectionLifetime = TimeSpan.FromMinutes(5), // picks up DNS changes
		ConnectTimeout = EnsureProxyAvailability.ConnectTimeout,
		ConnectCallback = EnsureProxyAvailability.ConnectToNonLoopback
	}) { Timeout = EnsureProxyAvailability.ResponseHeadersTimeout };

	public async Task InvokeAsync(HttpContext context)
	{
		// DEBUG: every request's headers (Authorization included, so tokens end up in the console) - remove when done
		Console.WriteLine($"{context.Request.Method} {context.Request.Host}{context.Request.Path}{context.Request.QueryString}");
		foreach (var (name, values) in context.Request.Headers)
			Console.WriteLine($"  {name}: {values}");

		// "amb-tideprotocol-com.agentproxy.tideprotocol.com" -> "amb-tideprotocol-com". The port the client used is the proxy's, not the server's.
		var requestHost = context.Request.Host.Host;
		if (!IsProxiedHost(requestHost, proxyDomain))
		{
			await next(context);
			return;
		}
		var flattenedTargetDomain = requestHost[..^(proxyDomain.Length+1)];

		// "amb-tideprotocol-com" -> "amb.tideprotocol.com", "my--server-com" -> "my-server.com".
		var cleanTargetDomain = flattenedTargetDomain.Contains('.') ? "" : flattenedTargetDomain.Replace("--", "\0").Replace('-', '.').Replace("\0", "-");

		// The server name must be just a host, so it can't smuggle in a port, path, user info or another scheme.
		if (Uri.CheckHostName(cleanTargetDomain) != UriHostNameType.Dns
			|| !Uri.TryCreate($"https://{cleanTargetDomain}{context.Request.Path.ToUriComponent()}{context.Request.QueryString}", UriKind.Absolute, out var targetFullUri)
			|| !targetFullUri.Host.Equals(cleanTargetDomain, StringComparison.OrdinalIgnoreCase) || !targetFullUri.IsDefaultPort)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync($"An issue was encountered determining the mcp server domain.");
			return;
		}

		if (await availability.RejectOverRateLimit(context, targetFullUri.Host)) return;

		// "https://amb.tideprotocol.com" -> "https://amb-tideprotocol-com.agentproxy.tideprotocol.com", as the client sees it.
		// A TLS-terminating front (nginx, cloudflare) says the scheme in X-Forwarded-Proto; without one, assume https.
		var targetOrigin = $"https://{targetFullUri.Authority}";
		var proxiedTargetOrigin = $"{context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https"}://{context.Request.Host}";

		using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetFullUri);
		if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
			request.Content = new StreamContent(context.Request.Body);
		foreach (var (name, values) in context.Request.Headers)
		{
			if (SkippableHeaders.Contains(name)) continue;
			// The resource metadata is read and rewritten here, so ask for it uncompressed
			if (IsResourceMetadata(context) && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
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
		catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
		{
			if (await availability.RejectSendFailure(context, error, targetFullUri.Host)) return;
			if (error is not HttpRequestException) throw; // the client went away: nothing to answer
			context.Response.StatusCode = StatusCodes.Status502BadGateway;
			await context.Response.WriteAsync($"Couldn't reach {targetFullUri.Host}: {error.Message}");
			return;
		}

		using (response)
		{
			context.Response.StatusCode = (int)response.StatusCode;
			HandleResponseHeaders(context, response, targetOrigin, proxiedTargetOrigin);
			await HandleResourceMetadataOverwrite(context, response, targetOrigin, proxiedTargetOrigin);
		}
	}

	// WWW-Authenticate, Mcp-Session-Id, Content-Type... all come back to the client, except the hop-by-hop ones
	private static void HandleResponseHeaders(HttpContext context, HttpResponseMessage response, string targetOrigin, string proxiedTargetOrigin)
	{
		foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
		{
			if (SkippableHeaders.Contains(name)) continue;
			// Bearer resource_metadata="https://amb.tideprotocol.com/..." -> the proxied address, so discovery comes back through here
			context.Response.Headers[name] = name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
				? values.Select(value => TargetDomainToProxiedTargetDomain(value, targetOrigin, proxiedTargetOrigin)).ToArray() : values.ToArray();
		}
	}

	// "/.well-known/oauth-protected-resource" and "/.well-known/oauth-protected-resource/mcp"
	private static bool IsResourceMetadata(HttpContext context)
		=> context.Request.Path.StartsWithSegments("/.well-known/oauth-protected-resource");

	// Writes the server's body to the client. A successful resource metadata answer,
	// {"resource":"https://amb.tideprotocol.com/mcp", "authorization_servers":[...]}, gets its resource moved to the
	// proxied address; authorization_servers is left alone, as the login still happens at the server's own authorization
	// server. Anything else is streamed through as it arrives.
	private static async Task HandleResourceMetadataOverwrite(HttpContext context, HttpResponseMessage response, string targetOrigin, string proxiedTargetOrigin)
	{
		if (!IsResourceMetadata(context) || !response.IsSuccessStatusCode)
		{
			await StreamBody(context, response);
			return;
		}

		var body = await EnsureProxyAvailability.ReadBodyOrReject(context, response);
		if (body is null) return;
		try
		{
			if (JsonNode.Parse(body) is JsonObject metadata
				&& metadata["resource"] is JsonValue resource && resource.TryGetValue<string>(out var resourceUrl))
			{
				metadata["resource"] = TargetDomainToProxiedTargetDomain(resourceUrl, targetOrigin, proxiedTargetOrigin);
				body = metadata.ToJsonString();
			}
		}
		catch (System.Text.Json.JsonException) { } // not JSON: passed on as it came
		context.Response.Headers.ContentLength = null; // the rewritten body has its own length
		await context.Response.WriteAsync(body, context.RequestAborted);
	}

	// Sends each chunk to the client the moment it arrives, which is what SSE needs
	private static async Task StreamBody(HttpContext context, HttpResponseMessage response)
	{
		context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
		await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
		await EnsureProxyAvailability.CopyWithIdleTimeout(body, context);
	}

	// "...https://amb.tideprotocol.com/..." -> "...https://amb-tideprotocol-com.agentproxy.tideprotocol.com/..."
	private static string TargetDomainToProxiedTargetDomain(string value, string targetOrigin, string proxiedTargetOrigin)
		=> value.Replace(targetOrigin, proxiedTargetOrigin, StringComparison.OrdinalIgnoreCase);
}
