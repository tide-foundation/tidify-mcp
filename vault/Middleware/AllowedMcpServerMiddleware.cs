namespace Asgard.Vault.Middleware;

/// <summary>
/// Proxied requests only go to the MCP servers listed in the AllowedMcpServers config, exact domains such as
/// "amb.tideprotocol.com". Anything else is refused before it can reach a session or the proxy, so a server someone
/// else runs (one that accepts any token) can't fill the session cache.
/// Set it in appsettings.json as "AllowedMcpServers": ["amb.tideprotocol.com"], or as --AllowedMcpServers:0=amb.tideprotocol.com.
/// </summary>
public class AllowedMcpServerMiddleware(RequestDelegate next, IConfiguration config)
{
	private readonly string proxyDomain = McpProxyMiddleware.GetProxyDomain(config);

	private readonly HashSet<string> allowedServers = config.GetSection("AllowedMcpServers").Get<string[]>()?
		.Select(server => server.Trim().TrimEnd('.'))
		.Where(server => server.Length > 0)
		.ToHashSet(StringComparer.OrdinalIgnoreCase) is { Count: > 0 } servers
			? servers
			: throw new InvalidOperationException("No AllowedMcpServers in the configuration: list the MCP server domains the proxy may forward to.");

	public async Task InvokeAsync(HttpContext context)
	{
		var server = McpProxyMiddleware.GetTargetDomain(context.Request.Host.Host, proxyDomain);
		if (!allowedServers.Contains(server))
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			await context.Response.WriteAsync($"This proxy doesn't forward to '{server}': it isn't one of its allowed MCP servers.");
			return;
		}

		await next(context);
	}
}
