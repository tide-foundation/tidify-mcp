using Asgard.Vault;

// A temporary host for the vault's McpProxyMiddleware on its own: no Asgard, no auth, no static files.
// {flattened mcp server domain}.{ProxyDomain} (amb-tideprotocol-com.agentproxy...) goes to that mcp server; any other host gets a 404.
// Needs --ProxyDomain=<domain>. Proxied requests are only accepted from a loopback peer.
var app = WebApplication.CreateBuilder(args).Build();
app.UseMiddleware<McpProxyMiddleware>(); // reads ProxyDomain from the configuration itself
app.Run("http://127.0.0.1:18433");
