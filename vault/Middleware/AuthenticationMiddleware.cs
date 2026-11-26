using System.Buffers.Text;
using System.Text.Json;

namespace Asgard.Vault.Middleware;

public class AuthenticationMiddleware(RequestDelegate next, McpSessionCache cache)
{
	public async Task InvokeAsync(HttpContext context)
	{
		// bearer token for the mcp server - basically just a symmetric credential
		var token = GetBearerToken(context);
        if(token is null)
        {
            // throw error, can only go through proxy for authenticated clients
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
		// server (the proxied host, one per server). Null for no token, a token that isn't a JWT, or no entry yet.
		var claims = ReadUnverifiedClaims(token);
		var session = cache.Get(new McpSessionKey(claims.Issuer, claims.Subject, context.Request.Host.Host.ToLowerInvariant()));

        if(session is null)
        {
            // Start login process
            return;
        }

		// Is this exact token one the session has seen? False for a new session, or a known sub with a new (likely
		// refreshed) token
		var tokenHash = McpSessionCache.HashToken(token);

        if (session.HasToken(tokenHash) is false)
        {
            // unknown token, wait until response comes back with 200 to add them
            
        }

        if (session.HasDelegation() is false)
        {
            // known token but no delegation - VERY WEIRD
            // there should always be a known token with a delegation
            // get user to log in again
        }

		await next(context);

		// After the rest of the pipeline
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

	// The scheme is case-insensitive (RFC 9110), so "bearer" counts too
	private static string? GetBearerToken(HttpContext context)
	{
		var header = context.Request.Headers.Authorization.ToString();
		const string scheme = "Bearer ";
		if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
		var token = header[scheme.Length..].Trim();
		return token.Length > 0 ? token : null;
	}
}
