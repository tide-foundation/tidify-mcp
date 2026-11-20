using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

var builder = WebApplication.CreateBuilder(args);


var vendorId = builder.Configuration["Keycloak:realm"]
	?? throw new InvalidOperationException("No Keycloak realm configuration found.");

// the public client the browser logs in with, e.g. dotnet run -- --BrowserClientId=browser-login-page
var browserClientId = builder.Configuration["BrowserClientId"]
	?? throw new InvalidOperationException("Missing the browser client id. Pass it as --BrowserClientId=<client id>.");


var status = VaultStatus.AdaptorRequired;

builder.Services.AddControllers();
if (builder.Configuration.GetSection("Keycloak:jwk").Exists())
{
	builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local server
 		.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // accepts DPoP-bound user tokens, and creates + enrolls this backend's mTLS keys
}

builder.Services.AddProblemDetails(); // required by UseExceptionHandler

var app = builder.Build();

app.UseExceptionHandler(); // Asgard reports its errors through it
app.UseDefaultFiles(); // "/" -> wwwroot/index.html
app.UseStaticFiles(); // serves wwwroot/ publicly, before any auth middleware runs
app.UseTide_over_DPoP(builder.Configuration, browserClientId);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

string? adaptor = null;
app.MapGet("/adaptor", (string data) =>
{
	JsonNode? keycloak;
	try
	{
		adaptor = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data));
		keycloak = JsonNode.Parse(adaptor);
	}
	catch (Exception e) when (e is FormatException or JsonException) { return Results.BadRequest("data must be base64-encoded JSON."); }
	Console.WriteLine(adaptor);

	var path = Path.Combine(app.Environment.ContentRootPath, "appsettings.json");
	var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
	settings["Keycloak"] = keycloak;
	File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

	// Exit code 3 asks the bridge to restart us, so the new config is loaded. Graceful shutdown
	// finishes this response first.
	Environment.ExitCode = 3;
	app.Lifetime.StopApplication();
	return Results.Ok();
});

// Always this address, whatever --urls or ASPNETCORE_URLS say: the bridge opens the sign-in page
// here, and the Tidecloak client allows it as a redirect URI. 127.0.0.1 only, so it isn't reachable
// from other machines.
// The bridge waits for this exact stdout line to know *this* process holds the port. ApplicationStarted
// fires only after Kestrel has bound it; if the port is taken, Run throws and the process exits instead.
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine("VAULT_STATUS:" + status));
app.Run("http://127.0.0.1:18433");

// [Authorize] // a valid DPoP-bound user token
// [RequireExchangeApproval] // and the user's delegation to this backend - answers 401 + challenge until tidecloak-js signs it
// [ApiController]
// [Route("exchange")]
// public class ExchangeController(IAspAsgardService asgard) : ControllerBase
// {
// 	// exchanges the user's token with TideCloak over mTLS - the new token is bound to this backend's certificate (cnf.x5t#S256)
// 	[HttpGet]
// 	public Task<string> Get() => asgard.ExchangeContextTokenForApplicationToken();
// }
