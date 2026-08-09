using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using ModelContextProtocol.Server;
using TermSquared.Core;
using TermSquared.Mcp;
using TermSquared.Mcp.HttpHost;
using TermSquared.Security;

var builder = WebApplication.CreateBuilder(args);
var settings = HttpHostSettings.Load();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, settings.Port);
    options.Limits.MaxRequestBodySize = settings.MaximumRequestBodyBytes;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new OperationDeduplicator(TimeSpan.FromHours(24)));
builder.Services.AddSingleton(new NamedPipeBrokerOptions
{
    PipeName = Environment.GetEnvironmentVariable("TERMSQUARED_BROKER_PIPE")
        ?? NamedPipeBrokerOptions.DefaultPipeName
});
builder.Services.AddSingleton<IRemoteOperations, NamedPipeRemoteOperations>();
builder.Services.AddSingleton<IPublishedConnectionRegistry, PublishedConnectionRegistry>();
builder.Services.AddSingleton<IOperationAuthorizer, UnavailableOperationAuthorizer>();
builder.Services.AddSingleton(new McpCallerIdentity("mcp-http-client"));
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .AddAuthorizationFilters()
    .WithTools<RemoteMcpTools>();

var app = builder.Build();
app.UseMiddleware<McpRequestSecurityMiddleware>();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization();
await app.RunAsync().ConfigureAwait(false);

namespace TermSquared.Mcp.HttpHost
{
    public sealed record HttpHostSettings(
        string Token,
        int Port,
        long MaximumRequestBodyBytes,
        int MaximumConcurrentRequests,
        IReadOnlySet<string> AllowedOrigins)
    {
        public static HttpHostSettings Load()
        {
            var token = Environment.GetEnvironmentVariable("TERMSQUARED_MCP_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("TERMSQUARED_MCP_TOKEN is required and must be delivered through a secure channel.");
            if (Encoding.UTF8.GetByteCount(token) < 32)
            {
                throw new InvalidOperationException("TERMSQUARED_MCP_TOKEN must contain at least 32 bytes.");
            }

            var origins = (Environment.GetEnvironmentVariable("TERMSQUARED_MCP_ORIGINS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new HttpHostSettings(
                token,
                ReadInt32("TERMSQUARED_MCP_PORT", 5088, 1, 65_535),
                ReadInt32("TERMSQUARED_MCP_MAX_BODY_BYTES", 1_048_576, 1_024, 16 * 1_048_576),
                ReadInt32("TERMSQUARED_MCP_MAX_CONCURRENCY", 32, 1, 256),
                origins);
        }

        private static int ReadInt32(string name, int defaultValue, int minimum, int maximum)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
                throw new InvalidOperationException($"{name} is outside the allowed range.");
            return parsed;
        }
    }

    public sealed class McpRequestSecurityMiddleware : IDisposable
    {
        private static readonly ClaimsPrincipal AuthenticatedPrincipal = new(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "mcp-client")], "Bearer"));
        private readonly RequestDelegate _next;
        private readonly HttpHostSettings _settings;
        private readonly SemaphoreSlim _concurrency;
        private readonly byte[] _expectedTokenHash;

        public McpRequestSecurityMiddleware(RequestDelegate next, HttpHostSettings settings)
        {
            _next = next;
            _settings = settings;
            _concurrency = new SemaphoreSlim(settings.MaximumConcurrentRequests, settings.MaximumConcurrentRequests);
            _expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsOriginAllowed(context.Request.Headers.Origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.ContentLength > _settings.MaximumRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false })
                bodySizeFeature.MaxRequestBodySize = _settings.MaximumRequestBodyBytes;

            if (!TryAuthenticate(context.Request.Headers.Authorization.ToString(), out var principal))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!await _concurrency.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            try
            {
                context.User = principal;
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                _concurrency.Release();
            }
        }

        public void Dispose() => _concurrency.Dispose();

        private bool IsOriginAllowed(string origin) =>
            string.IsNullOrEmpty(origin) || _settings.AllowedOrigins.Contains(origin);

        private bool TryAuthenticate(string authorization, out ClaimsPrincipal principal)
        {
            const string prefix = "Bearer ";
            principal = new ClaimsPrincipal(new ClaimsIdentity());
            if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var token = authorization[prefix.Length..].Trim();
            var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            if (!CryptographicOperations.FixedTimeEquals(_expectedTokenHash, tokenHash))
                return false;

            principal = AuthenticatedPrincipal;
            return true;
        }
    }
}
