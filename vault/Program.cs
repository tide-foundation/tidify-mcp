using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Asgard.Vault;
using Asgard.Vault.Middleware;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

var builder = WebApplication.CreateBuilder(args);

var status = new VaultStatusProvider(VaultStatus.AdaptorRequired);

var vendorId = builder.Configuration["Keycloak:realm"] ?? throw new InvalidOperationException("No Keycloak realm configuration found.");
var browserClientId = builder.Configuration["BrowserClientId"] ?? throw new InvalidOperationException("Missing the browser client id. Pass it as --BrowserClientId=<client id>.");

builder.Services.AddControllers();

if (builder.Configuration.GetSection("Keycloak:jwk").Exists())
{
	var pendingRequest = builder.Configuration["Asgard:certificate_request_path"] ?? "./resource.csr";
	status.Current = File.Exists(pendingRequest) ? VaultStatus.CertificateApprovalRequired : VaultStatus.Running;

	builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local server
 		.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP) // accepts DPoP-bound user tokens, and creates + enrolls this backend's mTLS keys
		.OnEvent(AsgardEvent.CertificateRequested, _ => SetStatus(VaultStatus.CertificateApprovalRequired))
		.OnEvent(AsgardEvent.CertificateApproved, _ => SetStatus(VaultStatus.Running))
		.OnEvent(AsgardEvent.CertificateRequestedError, StopOnError)
		.OnEvent(AsgardEvent.CertificateFetchError, StopOnError);
}

Task SetStatus(VaultStatus next)
{
	status.Current = next;
	return Task.CompletedTask;
}
static Task StopOnError(AsgardEventContext context)
{
	Console.Error.WriteLine($"Asgard raised {context.Event}, stopping the vault: {context.Exception}");
	Environment.ExitCode = 1;
	context.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
	return Task.CompletedTask;
}

builder.Services.AddSingleton(status); // the same instance as the local `status`
builder.Services.AddSingleton<McpSessionCache>(); // for AuthenticationMiddleware

builder.Services.AddProblemDetails(); // required by UseExceptionHandler

var app = builder.Build();

app.UseExceptionHandler(); // Asgard reports its errors through it
// {flattened mcp server domain}.{ProxyDomain} (amb-tideprotocol-com.agentproxy...) goes to that mcp server - before static files and the vault's own auth.
// Off unless the vault is started with --ProxyDomain=<domain>.
// Only from a loopback peer (the nginx in front).
// AuthenticationMiddleware runs only on this branch, in front of the proxy: it sees each request before it's forwarded,
// and the response status after.
if (builder.Configuration["ProxyDomain"] is { Length: > 0 })
{
	var proxyDomain = McpProxyMiddleware.GetProxyDomain(builder.Configuration);
	app.MapWhen(context => McpProxyMiddleware.IsProxiedHost(context.Request.Host.Host, proxyDomain), proxied => proxied
		.UseMiddleware<LoopbackPeerMiddleware>()
		.UseMiddleware<McpProxyAuthenticationMiddleware>()
		.UseMiddleware<McpProxyMiddleware>()); // reads ProxyDomain from the configuration itself
}
app.UseDefaultFiles(); // "/" -> wwwroot/index.html
// Serves wwwroot/ publicly, before any auth middleware runs. no-cache makes browsers check back every time (a cheap
// 304 when unchanged) instead of reusing an old copy, which ran a new app.js against an old index.html.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
app.UseTide_over_DPoP(builder.Configuration, browserClientId);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Lifetime.ApplicationStarted.Register(status.StartAnnouncing);

var stopping = app.Lifetime.ApplicationStopping; // read before Run: Run disposes the host before it throws
try
{
	app.Run("http://127.0.0.1:18433");
}
// An error event in Asgard's first enrollment attempt stops the app before Kestrel has bound the port, which
// cancels Kestrel's start and makes Run throw. StopOnError already printed why and set the exit code.
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }


[Route("Provide")]
public class ExchangeController(IWebHostEnvironment environment, IHostApplicationLifetime lifetime, VaultStatusProvider status) : ControllerBase
{
	// exchanges the user's token with TideCloak over mTLS - the new token is bound to this backend's certificate (cnf.x5t#S256)
	[Authorize]
	[RequireExchangeApproval(ApprovalRequirement.TideEnabled)]
	[HttpGet("Delegation")]
	public IActionResult Get() => Ok("Thanks for the delegation :)");

	// Saves the two adapter configs the sign-in page fetched (the JSON request body) into appsettings.json:
	// the vault's own client under "Keycloak", and the browser client the bridge logs in with under "BrowserClient".
	[HttpPost("Adaptor")]
	public IActionResult Adaptor([FromBody] AdaptorConfigs? configs)
	{
		// Once the vault has a config, refuse to replace it: that would re-point its trusted server and keys.
		var current = status.Current;
		if (current != VaultStatus.AdaptorRequired) return Conflict($"The vault already has its adapter config (status: {current}).");

		// configs is null when the body is empty or isn't JSON; a property is null when it's missing or isn't an object
		if (configs?.Vault is null || configs.Bridge is null)
			return BadRequest("The body must be { \"vault\": {...}, \"bridge\": {...} }, both adapter configs as JSON objects.");
		// Not the whole config: the vault's holds the one-time enrollment_token.
		Console.WriteLine($"Saved the adapter configs for clients '{configs.Vault["resource"]}' and '{configs.Bridge["resource"]}' in realm '{configs.Vault["realm"]}'.");

		var path = Path.Combine(environment.ContentRootPath, "appsettings.json");
		var settings = System.IO.File.Exists(path) ? JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject() : new JsonObject();
		settings["Keycloak"] = configs.Vault;
		settings["BrowserClient"] = configs.Bridge;
		System.IO.File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

		// Exit code 3 asks the bridge to restart us, so the new config is loaded. Graceful shutdown
		// finishes this response first.
		Environment.ExitCode = 3;
		lifetime.StopApplication();
		return Ok();
	}
}

// The /Provide/Adaptor body. MVC matches "vault" and "bridge" to these names regardless of case.
public record AdaptorConfigs(JsonObject? Vault, JsonObject? Bridge);
