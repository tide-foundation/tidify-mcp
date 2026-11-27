using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Asgard.Vault.Middleware;

// One agent's session with one MCP server. A sub is only unique within its issuer.
public sealed record McpSessionKey(string Issuer, string Subject, string McpServer);

/// <summary>
/// A session: the access tokens seen for it (by hash, never the token itself) and, once the user has logged in to the
/// proxy, their delegation token. Several token hashes at once, as each client (Claude Code, claude.ai...) has its own
/// token and a refreshed token's predecessor stays valid until it expires.
/// </summary>
public sealed class McpSession(McpSessionKey key, string loginCode)
{
	private readonly Dictionary<string, DateTimeOffset> tokenHashes = []; // hash -> the token's exp
	private readonly Lock gate = new();

	public McpSessionKey Key { get; } = key;

	// Links the login page back to this session, so the bearer token never goes in a URL
	public string LoginCode { get; } = loginCode;

	public string? DelegationToken { get; private set; }
	public DateTimeOffset? DelegationExpiresAt { get; private set; }

	public bool HasToken(string tokenHash)
	{
		lock (gate)
		{
			RemoveExpiredTokens();
			return tokenHashes.ContainsKey(tokenHash);
		}
	}

	public void AddToken(string tokenHash, DateTimeOffset expiresAt)
	{
		lock (gate)
		{
			RemoveExpiredTokens();
			tokenHashes[tokenHash] = expiresAt;
		}
	}

	// The user has logged in to the proxy, and their delegation token hasn't expired
	public bool HasDelegation()
	{
		lock (gate)
			return DelegationToken is not null && DelegationExpiresAt > DateTimeOffset.UtcNow;
	}

	internal void SetDelegation(string delegationToken, DateTimeOffset expiresAt)
	{
		lock (gate)
		{
			DelegationToken = delegationToken;
			DelegationExpiresAt = expiresAt;
		}
	}

	private void RemoveExpiredTokens()
	{
		var now = DateTimeOffset.UtcNow;
		foreach (var (hash, expiresAt) in tokenHashes)
			if (expiresAt <= now) tokenHashes.Remove(hash);
	}
}

/// <summary>
/// In-memory sessions, in two bounded caches so unauthenticated requests can't crowd out logged-in users:
///   pending: waiting for the user to log in. Small, and short-lived.
///   sessions: logged in. Kept until the delegation token expires.
/// Everything is lost on restart, which means logging in again.
/// </summary>
public sealed class McpSessionCache : IDisposable
{
	private const int MaxPendingSessions = 1_000;
	private const int MaxSessions = 1_000;
	private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

	// Holds McpSessionKey -> McpSession, and (for pending) "code:" + login code -> McpSessionKey
	private readonly MemoryCache pending = new(new MemoryCacheOptions { SizeLimit = MaxPendingSessions * 2 });
	private readonly MemoryCache sessions = new(new MemoryCacheOptions { SizeLimit = MaxSessions });

	// SHA-256, hex. Enough for a long random token; the token itself is never stored.
	public static string HashToken(string token)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

	// The logged-in session, else the pending one, else null
	public McpSession? Get(McpSessionKey key)
		=> sessions.Get<McpSession>(key) ?? pending.Get<McpSession>(key);

	// No room for another pending session (each takes two entries: the session and its login code)
	public bool PendingIsFull => pending.Count >= MaxPendingSessions * 2;

	public McpSession? GetByLoginCode(string loginCode)
		=> pending.Get<McpSessionKey>(LoginCodeKey(loginCode)) is { } key ? pending.Get<McpSession>(key) : null;

	// A new session waiting for login, with its first token. Null when the pending cache is full.
	public McpSession? CreatePending(McpSessionKey key, string tokenHash, DateTimeOffset tokenExpiresAt)
	{
		var session = new McpSession(key, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
		session.AddToken(tokenHash, tokenExpiresAt);

		var options = new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = PendingLifetime };
		pending.Set(key, session, options);
		pending.Set(LoginCodeKey(session.LoginCode), key, options);

		// A full cache drops new entries instead of storing them
		return pending.TryGetValue(key, out _) && pending.TryGetValue(LoginCodeKey(session.LoginCode), out _) ? session : null;
	}

	// The user logged in: the session moves from pending to sessions until the delegation token expires.
	// When sessions is full, the least recently used 5% are evicted and it's tried once more; false if that fails too.
	public bool CompleteLogin(McpSession session, string delegationToken, DateTimeOffset expiresAt)
	{
		session.SetDelegation(delegationToken, expiresAt);
		var options = new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = expiresAt };
		if (!TrySet(sessions, session.Key, session, options))
		{
			sessions.Compact(0.05);
			if (!TrySet(sessions, session.Key, session, options)) return false;
		}

		pending.Remove(session.Key);
		pending.Remove(LoginCodeKey(session.LoginCode));
		return true;
	}

	// A full cache drops a new entry instead of storing it, without saying so
	private static bool TrySet(MemoryCache cache, object key, object value, MemoryCacheEntryOptions options)
	{
		cache.Set(key, value, options);
		return cache.TryGetValue(key, out _);
	}

	private static string LoginCodeKey(string loginCode) => "code:" + loginCode;

	public void Dispose()
	{
		pending.Dispose();
		sessions.Dispose();
	}
}
