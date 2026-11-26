// A temporary host for the vault's McpProxyMiddleware on its own: no Asgard, no auth, no static files.
// {flattened mcp server domain}.{ProxyDomain} (amb-tideprotocol-com.agentproxy...) goes to that mcp server; any other host gets a 404.
var builder = WebApplication.CreateBuilder(args);
var proxyDomain = builder.Configuration["ProxyDomain"] is { Length: > 0 } domain ? domain
	: throw new InvalidOperationException("Missing the proxy domain. Pass it as --ProxyDomain=<domain>.");
var app = builder.Build();
app.UseMiddleware<McpProxyMiddleware>(proxyDomain);
app.Run("http://127.0.0.1:18433");
