using System.Net.Http.Headers;
using ModelContextProtocol.Client;

namespace TermSquared.Mcp;

public enum McpTransportKind
{
    Stdio,
    Http
}

public sealed record McpClientProfile(
    Guid Id,
    string Name,
    McpTransportKind Transport,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    Uri? Endpoint = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    bool InheritEnvironmentVariables = false,
    TimeSpan? ShutdownTimeout = null,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    TimeSpan? HttpConnectionTimeout = null,
    TimeSpan? HttpRequestTimeout = null);

public interface IMcpTokenProvider
{
    ValueTask<string?> GetTokenAsync(Guid profileId, CancellationToken cancellationToken);
}

public static class McpClientFactory
{
    public static async Task<McpClient> CreateAsync(
        McpClientProfile profile,
        IMcpTokenProvider? tokenProvider = null,
        CancellationToken cancellationToken = default)
    {
        IClientTransport transport;
        if (profile.Transport == McpTransportKind.Stdio && !string.IsNullOrWhiteSpace(profile.Command))
        {
            transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = profile.Command,
                    Arguments = profile.Arguments?.ToArray() ?? [],
                    WorkingDirectory = profile.WorkingDirectory,
                    EnvironmentVariables = profile.EnvironmentVariables is null
                        ? null
                        : new Dictionary<string, string?>(profile.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
                    InheritEnvironmentVariables = profile.InheritEnvironmentVariables,
                    ShutdownTimeout = profile.ShutdownTimeout ?? TimeSpan.FromSeconds(10)
                });
        }
        else if (profile.Transport == McpTransportKind.Http && profile.Endpoint is not null)
        {
            ValidateHttpEndpoint(profile.Endpoint);
            var headers = profile.HttpHeaders is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.HttpHeaders, StringComparer.OrdinalIgnoreCase);
            if (headers.ContainsKey("Authorization"))
                throw new ArgumentException("Authorization headers must come from an external token provider.", nameof(profile));

            var token = tokenProvider is null
                ? null
                : await tokenProvider.GetTokenAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("An external MCP token provider is required for HTTP profiles.");
            headers["Authorization"] = new AuthenticationHeaderValue("Bearer", token).ToString();

            var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = profile.HttpRequestTimeout ?? TimeSpan.FromSeconds(60)
            };
            transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = profile.Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = headers,
                    ConnectionTimeout = profile.HttpConnectionTimeout ?? TimeSpan.FromSeconds(30)
                },
                httpClient,
                ownsHttpClient: true);
        }
        else
        {
            throw new ArgumentException("The MCP profile is incomplete.", nameof(profile));
        }

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateHttpEndpoint(Uri endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("MCP endpoints must not contain user information.", nameof(endpoint));
        if (endpoint.Scheme == Uri.UriSchemeHttps) return;
        if (endpoint.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(endpoint.Host)) return;
        throw new ArgumentException("MCP HTTP endpoints require HTTPS; plain HTTP is allowed only for loopback addresses.", nameof(endpoint));
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}
