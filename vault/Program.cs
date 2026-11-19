using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

var builder = WebApplication.CreateBuilder(args);

// the public client the browser logs in with, e.g. dotnet run -- --BrowserClientId=browser-login-page
var browserClientId = builder.Configuration["BrowserClientId"]
	?? throw new InvalidOperationException("Missing the browser client id. Pass it as --BrowserClientId=<client id>.");

builder.Services.AddControllers();
// builder.AddAsgard(options => options.RequireHttpsMetadata = false) // local server
// 	.RequireTokenBinding(options => options.SupportBindings = Binding.DPoP); // accepts DPoP-bound user tokens, and creates + enrolls this backend's mTLS keys

builder.Services.AddProblemDetails(); // required by UseExceptionHandler

var app = builder.Build();

app.UseExceptionHandler(); // Asgard reports its errors through it
app.UseDefaultFiles(); // "/" -> wwwroot/index.html
app.UseStaticFiles(); // serves wwwroot/ publicly, before any auth middleware runs
app.UseTide_over_DPoP(builder.Configuration, browserClientId);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Always this address, whatever --urls or ASPNETCORE_URLS say: the bridge opens the sign-in page
// here, and the Tidecloak client allows it as a redirect URI. 127.0.0.1 only, so it isn't reachable
// from other machines.
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
