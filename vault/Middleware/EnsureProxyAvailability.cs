using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace Asgard.Vault.Middleware;

/// <summary>
/// McpProxyMiddleware's security: who may use it, where it may go, and DoS limits.
/// Each Reject... method writes an error response and returns true when the request must stop.
/// Configurable: --MaxRequestBodyBytes (default 1MB) and --RequestsPerSecond (per MCP server, default 30).
/// </summary>
public class EnsureProxyAvailability(IConfiguration config)
{
	private readonly long maxRequestBodyBytes = Positive(config, "MaxRequestBodyBytes", 1024 * 1024);
	private const int MaxBufferedResponseBytes = 64 * 1024;
	private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromMinutes(5);

	public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
	// Covers only the response headers (requests use ResponseHeadersRead), so SSE bodies aren't cut off
	public static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(30);

	// RequestsPerSecond per MCP server, no queue
	private readonly PartitionedRateLimiter<string> rateLimiter = CreateRateLimiter((int)Positive(config, "RequestsPerSecond", 30));

	private static PartitionedRateLimiter<string> CreateRateLimiter(int requestsPerSecond)
		=> PartitionedRateLimiter.Create<string, string>(server => RateLimitPartition.GetTokenBucketLimiter(server,
			_ => new TokenBucketRateLimiterOptions
			{
				TokenLimit = requestsPerSecond,
				TokensPerPeriod = requestsPerSecond,
				ReplenishmentPeriod = TimeSpan.FromSeconds(1),
				QueueLimit = 0
			}));

	// The setting, or its default when unset; anything below 1 stops startup
	private static long Positive(IConfiguration config, string key, long defaultValue)
		=> config.GetValue<long?>(key) is not { } value ? defaultValue
			: value > 0 ? value : throw new InvalidOperationException($"{key} must be above 0, but is {value}.");

	private sealed class LoopbackTargetException(string host)
		: Exception($"{host} resolves to a loopback address, which the proxy won't forward to.");

	public Task<bool> RejectOverRateLimit(HttpContext context, string server)
	{
		using var lease = rateLimiter.AttemptAcquire(server.ToLowerInvariant());
		if (lease.IsAcquired) return Task.FromResult(false);
		context.Response.Headers.RetryAfter = "1";
		return Reject(context, StatusCodes.Status429TooManyRequests, $"Too many requests to {server}; try again in a second.");
	}

	// Rejects a Content-Length over the limit now; a chunked body is cut off by Kestrel and answered in RejectSendFailure
	public Task<bool> RejectOversizedRequestBody(HttpContext context)
	{
		if (context.Request.ContentLength > maxRequestBodyBytes)
			return Reject(context, StatusCodes.Status413PayloadTooLarge, $"The request body is over {maxRequestBodyBytes} bytes.");
		if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
			bodySize.MaxRequestBodySize = maxRequestBodyBytes;
		return Task.FromResult(false);
	}

	// Answers SendAsync failures caused by these guards; false for any other failure
	public Task<bool> RejectSendFailure(HttpContext context, Exception error, string server) => error switch
	{
		HttpRequestException { InnerException: LoopbackTargetException loopback }
			=> Reject(context, StatusCodes.Status403Forbidden, loopback.Message),
		HttpRequestException { InnerException: BadHttpRequestException tooLarge }
			=> Reject(context, tooLarge.StatusCode, $"The request body is over {maxRequestBodyBytes} bytes."),
		// A timeout, not the client leaving (which cancels RequestAborted)
		OperationCanceledException when !context.RequestAborted.IsCancellationRequested
			=> Reject(context, StatusCodes.Status504GatewayTimeout, $"{server} didn't answer in time."),
		_ => Task.FromResult(false)
	};

	// The body as text, or null after answering 502 when it's over MaxBufferedResponseBytes
	public static async Task<string?> ReadBodyOrReject(HttpContext context, HttpResponseMessage response)
	{
		if (response.Content.Headers.ContentLength is null or <= MaxBufferedResponseBytes)
		{
			await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
			var buffer = new byte[MaxBufferedResponseBytes + 1]; // one extra byte detects "over"
			var length = 0;
			int read;
			while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), context.RequestAborted)) > 0)
				length += read;
			if (length <= MaxBufferedResponseBytes) return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
		}

		context.Response.Headers.Clear(); // they described the server's body
		await Reject(context, StatusCodes.Status502BadGateway, $"The server's response is over {MaxBufferedResponseBytes} bytes.");
		return null;
	}

	// Aborts (so the client sees it was cut short) when a read or write stalls for BodyIdleTimeout
	public static async Task CopyWithIdleTimeout(Stream from, HttpContext context)
	{
		using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
		var buffer = new byte[16 * 1024];
		try
		{
			int read;
			while (true)
			{
				idle.CancelAfter(BodyIdleTimeout);
				if ((read = await from.ReadAsync(buffer, idle.Token)) == 0) return;
				await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), idle.Token);
			}
		}
		catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
		{
			context.Abort();
		}
	}

	// The HttpClient's ConnectCallback: refuses names resolving to loopback, then connects to exactly the checked
	// addresses so DNS rebinding can't slip past
	public static async ValueTask<Stream> ConnectToNonLoopback(SocketsHttpConnectionContext connection, CancellationToken cancellationToken)
	{
		var host = connection.DnsEndPoint.Host;
		var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
		if (addresses.Any(IsLoopback)) throw new LoopbackTargetException(host);

		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; // dual-mode: IPv4 and IPv6
		try
		{
			await socket.ConnectAsync(addresses, connection.DnsEndPoint.Port, cancellationToken);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	// Also counts "::ffff:127.0.0.1". Also used by LoopbackPeerMiddleware.
	public static bool IsLoopback(IPAddress address)
		=> IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

	private static async Task<bool> Reject(HttpContext context, int statusCode, string message)
	{
		context.Response.StatusCode = statusCode;
		await context.Response.WriteAsync(message);
		return true;
	}
}
