using System.Buffers.Text;
using System.Text.Json;
using Tide.Asgard.Core;

namespace Asgard.Vault.Middleware;

public class McpProxyAuthenticationMiddleware(RequestDelegate next, McpSessionCache cache, AsgardSettings asgardSettings, IConfiguration config)
{
	private readonly string loginAdaptorB64Encoded = Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(
		ToJson(config.GetSection("BrowserClient") is { } section && section.Exists() ? section
			: throw new InvalidOperationException("Missing the browser client's adapter config: no \"BrowserClient\" section in appsettings.json.")
		).ToJsonString()));

	// Only for its request body limit (--MaxRequestBodyBytes)
	private readonly EnsureProxyAvailability availability = new(config);

	public async Task InvokeAsync(HttpContext context)
	{
		// The body limit, before anything reads the body: RequireLogin here, or the proxy as it forwards it
		if (await availability.RejectOversizedRequestBody(context)) return;

		// bearer token for the mcp server - basically just a symmetric credential
		var token = GetBearerToken(context);
        if(token is null)
        {
            // No token yet: let it through, so the MCP server answers 401 and the client starts its OAuth login with
            // that server (discovery and the /.well-known metadata come through here too, without a token)
            await next(context);
            return;
        }

        // we can't, and SHOULDN'T verify the mcp bearer token
        // although - it can serve as a session id for a particular agent going through this proxy to an mcp server
        // store the "session id" alongside a mcp server domain and we've got an artifact to connect recurring requests
        // when a NEW session id appears, we MUST request that they authenticate to US, the proxy
        // we can do that my returning a MCP error stating that the proxy requires the user to login at X URL
        // that url be the page at wwwroot, which authenticates a user to tidecloak then performs a delegation to the proxy
        // we then need to store the delegation token aside the session id + mcp server domain
        // the next time that bearer token appears, we'll hash it, find it's entry in the cache, and if it's got a delegated token too
        // we'll allow it to pass, if not we do the login process again


		// Does this request have a session? Keyed by who (the token's iss + sub, read without verifying) and which mcp
		// server (the proxied host, one per server). Null when there's no entry yet.
		// The token must be a JWT with iss, sub and exp: without them there's no identity to tie a session to.
		TokenClaims claims;
		try
		{
			claims = ReadUnverifiedClaims(token);
		}
		catch (FormatException error)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync($"Only JWT bearer tokens (with iss, sub and exp) are allowed through this proxy. {error.Message}");
			return;
		}
		var key = new McpSessionKey(claims.Issuer, claims.Subject, context.Request.Host.Host.ToLowerInvariant());
		var session = cache.Get(key);

        if(session is null)
        {
			// get the user to log into this proxy by providing their delegation
 			await RequireLogin(context, key, token, claims, false);
            return;
        }

		if (session.HasDelegation() is false)
        {
            // get the user to log into this proxy by providing their delegation
 			await RequireLogin(context, key, token, claims, true);
            return;
        }

        if (session.HasToken(McpSessionCache.HashToken(token)) is false)
        {
            // unknown token, wait until response comes back with 200 to add them
            CaptureTargetMcpAuthentication(context, session, token, claims);
        }

		await next(context);
	}

	// Records the token in the session once the MCP server accepts it: a 2xx from its protected /mcp path. The proxy
	// can't verify the token, so the server's answer is the only proof it isn't forged. Runs as the response headers
	// go out, so an SSE stream counts as soon as it opens, and a request that fails before then counts for nothing.
	private static void CaptureTargetMcpAuthentication(HttpContext context, McpSession session, string token, TokenClaims claims)
	{
		if (!context.Request.Path.StartsWithSegments("/mcp")) return; // other paths may answer 2xx without checking the token

		var tokenHash = McpSessionCache.HashToken(token);
		context.Response.OnStarting(() =>
		{
			if (context.Response.StatusCode is >= 200 and < 300)
				session.AddToken(tokenHash, claims.ExpiresAt);
			return Task.CompletedTask;
		});
	}

	// Starts the login process: a pending session holding this first token, and an MCP error pointing the user at the login page
	private async Task RequireLogin(HttpContext context, McpSessionKey key, string token, TokenClaims claims, bool sessionAlreadyExists)
	{
		// The existing session keeps its login code; a new one is pending. Null when the pending cache is full (or the
		// session expired a moment ago).
		var session = sessionAlreadyExists
			? cache.Get(key)
			: cache.CreatePending(key, McpSessionCache.HashToken(token), claims.ExpiresAt);
		if (session is null)
		{
			context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			context.Response.Headers.RetryAfter = "60";
			await context.Response.WriteAsync("The proxy has too many logins in progress. Try again in a minute.");
			return;
		}

		var loginUrl = "http://127.0.0.1:18433/" + QueryString.Create(new Dictionary<string, string?>
		{
			["realm"] = asgardSettings.RealmName,
			["clientId"] = asgardSettings.ClientName,
			["fetchAdaptor"] = "false",
			["provideDelegation"] = "true",
			["loginAdaptor"] = loginAdaptorB64Encoded,
			["loginCode"] = session.LoginCode // links the login back to this session
		});

		// The JSON-RPC request's id, so the error answers it. Null when the body isn't a JSON-RPC request with an id
		// (a GET for the SSE stream, a notification, or not JSON).
		JsonElement? requestId = null;
		if (HttpMethods.IsPost(context.Request.Method))
		{
			try
			{
				using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
				if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("id", out var id))
					requestId = id.Clone();
			}
			catch (JsonException) { }
			catch (BadHttpRequestException tooLarge) // a chunked body over the limit, cut off by Kestrel
			{
				context.Response.StatusCode = tooLarge.StatusCode;
				await context.Response.WriteAsync("The request body is too large.");
				return;
			}
		}

		// An MCP (JSON-RPC) error telling the user to log in to the proxy at loginUrl.
		// -32042 is MCP's URLElicitationRequiredError: clients that support URL elicitation offer to open the url; for the
		// rest, the message carries the url too.
		var message = $"This MCP server is reached through a proxy that needs you to log in first. Log in at {loginUrl} and then try again.";
		var error = new
		{
			jsonrpc = "2.0",
			id = requestId,
			error = new
			{
				code = -32042,
				message,
				data = new { elicitations = new[] { new { mode = "url", elicitationId = Guid.NewGuid().ToString(), url = loginUrl, message } } }
			}
		};
		context.Response.StatusCode = StatusCodes.Status200OK; // a JSON-RPC error is still a successful HTTP exchange
		await context.Response.WriteAsJsonAsync(error);
	}
	// A config section back as a JSON object. Configuration keeps every value as a string, so 0 and true come back
	// as "0" and "true"; the page only reads string fields (realm, auth-server-url, resource).
	private static System.Text.Json.Nodes.JsonObject ToJson(IConfigurationSection section)
	{
		var json = new System.Text.Json.Nodes.JsonObject();
		foreach (var child in section.GetChildren())
			json[child.Key] = child.Value is { } value ? value : ToJson(child);
		return json;
	}
	// The scheme is case-insensitive (RFC 9110), so "bearer" counts too
	private static string? GetBearerToken(HttpContext context)
	{
		var header = context.Request.Headers.Authorization.ToString();
		const string scheme = "Bearer ";
		if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
		var token = header[scheme.Length..].Trim();
		return token.Length > 0 ? token : null;
	}
	private sealed record TokenClaims(string Issuer, string Subject, DateTimeOffset ExpiresAt);

	// iss, sub and exp from a JWT's payload. NOT verified: only for finding the session.
	// Throws FormatException when the token isn't a JWT or lacks any of them.
	private static TokenClaims ReadUnverifiedClaims(string token)
	{
		var parts = token.Split('.');
		if (parts.Length != 3) throw new FormatException("The bearer token isn't a JWT.");

		JsonDocument payload;
		try
		{
			payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
		}
		catch (Exception error) when (error is FormatException or JsonException)
		{
			throw new FormatException("The bearer token's payload isn't base64url JSON.", error);
		}

		using (payload)
		{
			var root = payload.RootElement;
			if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The bearer token's payload isn't a JSON object.");
			return new TokenClaims(GetString(root, "iss"), GetString(root, "sub"), GetExpiry(root));
		}
	}

	private static string GetString(JsonElement payload, string claim)
		=> payload.TryGetProperty(claim, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()!
			: throw new FormatException($"The bearer token has no '{claim}' string.");

	private static DateTimeOffset GetExpiry(JsonElement payload)
	{
		if (!payload.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var seconds))
			throw new FormatException("The bearer token has no 'exp' number.");
		try
		{
			return DateTimeOffset.FromUnixTimeSeconds(seconds);
		}
		catch (ArgumentOutOfRangeException error)
		{
			throw new FormatException("The bearer token's 'exp' is out of range.", error);
		}
	}
}
