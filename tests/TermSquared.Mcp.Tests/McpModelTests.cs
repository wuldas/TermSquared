using System.Text.Json;
using TermSquared.Core;
using TermSquared.Mcp;
using TermSquared.Security;

namespace TermSquared.Mcp.Tests;

public sealed class McpModelTests
{
    [Fact]
    public void ClientProfileKeepsTransportExplicit()
    {
        var profile = new McpClientProfile(Guid.NewGuid(), "local", McpTransportKind.Stdio, "server.exe", ["--stdio"]);
        Assert.Equal(McpTransportKind.Stdio, profile.Transport);
        Assert.Null(profile.Endpoint);
    }

    [Fact]
    public async Task HttpClientRequiresExternalTokenProvider()
    {
        var profile = new McpClientProfile(
            Guid.NewGuid(),
            "http",
            McpTransportKind.Http,
            Endpoint: new Uri("http://127.0.0.1:5088/mcp"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => McpClientFactory.CreateAsync(profile));
    }

    [Fact]
    public async Task HttpClientRejectsAuthorizationStoredInProfile()
    {
        var profile = new McpClientProfile(
            Guid.NewGuid(),
            "http",
            McpTransportKind.Http,
            Endpoint: new Uri("http://127.0.0.1:5088/mcp"),
            HttpHeaders: new Dictionary<string, string> { ["Authorization"] = "Bearer persisted-secret" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            McpClientFactory.CreateAsync(profile, new StaticTokenProvider()));
    }

    [Fact]
    public async Task HttpClientRejectsPlainHttpOutsideLoopback()
    {
        var profile = new McpClientProfile(
            Guid.NewGuid(),
            "insecure-http",
            McpTransportKind.Http,
            Endpoint: new Uri("http://192.0.2.10/mcp"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            McpClientFactory.CreateAsync(profile, new StaticTokenProvider()));
    }

    [Fact]
    public void StdioClientDoesNotInheritEnvironmentByDefault()
    {
        var profile = new McpClientProfile(Guid.NewGuid(), "local", McpTransportKind.Stdio, "server.exe");
        Assert.False(profile.InheritEnvironmentVariables);
    }

    [Fact]
    public async Task ConnectionDtoDoesNotSerializeProfileOrSecretData()
    {
        var publishedId = Guid.NewGuid();
        var secret = new SecretReference("secret-store", "secret-id");
        var operations = new FakeRemoteOperations([
            new ConnectionProfile(
                publishedId,
                "published-alias",
                ConnectionProtocol.Ssh,
                "private.example",
                22,
                "private-user",
                AuthenticationKind.Password,
                secret,
                ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
                PublishedMcpScope.ReadOnly)
        ]);
        var tools = CreateTools(operations, [new PublishedConnectionAccess(publishedId, ["/srv/data"])]);

        var dto = Assert.Single(await tools.ListConnectionsAsync(default));
        var json = JsonSerializer.Serialize(dto);

        Assert.Equal(publishedId.ToString("D"), dto.ConnectionId);
        Assert.DoesNotContain("private.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-store", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", json, StringComparison.Ordinal);
        Assert.Null(typeof(PublishedConnectionDto).GetProperty("Secret"));
        Assert.Null(typeof(PublishedConnectionDto).GetProperty("ReadableRoots"));
        Assert.Null(typeof(PublishedConnectionDto).GetProperty("PublishedScope"));
    }

    [Fact]
    public async Task UnpublishedOrRootlessConnectionsAreNotVisible()
    {
        var publishedId = Guid.NewGuid();
        var unpublishedId = Guid.NewGuid();
        var rootlessId = Guid.NewGuid();
        var operations = new FakeRemoteOperations([
            CreateProfile(publishedId, PublishedMcpScope.ReadOnly),
            CreateProfile(unpublishedId, PublishedMcpScope.None),
            CreateProfile(rootlessId, PublishedMcpScope.ReadOnly)
        ]);
        var tools = CreateTools(operations, [
            new PublishedConnectionAccess(publishedId, ["/srv/data"]),
            new PublishedConnectionAccess(unpublishedId, ["/srv/data"])
        ]);

        var connection = Assert.Single(await tools.ListConnectionsAsync(default));
        Assert.Equal(publishedId.ToString("D"), connection.ConnectionId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.GetConnectionStatusAsync(unpublishedId.ToString("D"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.GetConnectionStatusAsync(rootlessId.ToString("D"), default));
    }

    [Fact]
    public async Task SftpReadAcceptsAnyPublishedRootAndRejectsTraversal()
    {
        var connectionId = Guid.NewGuid();
        var operations = new FakeRemoteOperations([CreateProfile(connectionId, PublishedMcpScope.ReadOnly)]);
        var tools = CreateTools(operations, [new PublishedConnectionAccess(connectionId, ["/srv/data", "/opt/shared"])]);

        await tools.ReadAsync(connectionId.ToString("D"), "/opt/shared/team/../file.txt", 100, default);
        Assert.Equal("/opt/shared/file.txt", operations.LastReadPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.ReadAsync(connectionId.ToString("D"), "/srv/data/../../etc/passwd", 100, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.StatAsync(connectionId.ToString("D"), "/srv/database/file.txt", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.ListDirectoryAsync(connectionId.ToString("D"), "srv/data", default));
    }

    [Fact]
    public async Task ExecRejectsReusedOperationIdWithDifferentArguments()
    {
        var connectionId = Guid.NewGuid();
        var operations = new FakeRemoteOperations([CreateProfile(connectionId, PublishedMcpScope.ApprovedCommands)]);
        var approvals = new PreauthorizedOperationAuthorizer();
        var tools = CreateTools(
            operations,
            [new PublishedConnectionAccess(connectionId, ["/srv/data"])],
            approvals,
            new OperationDeduplicator(TimeSpan.FromMinutes(5)));

        Assert.Equal("ok", await tools.ExecAsync("op-1", connectionId.ToString("D"), "whoami", default));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.ExecAsync("op-1", connectionId.ToString("D"), "hostname", default));
        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighRiskOperationIsDeniedWithoutAuthorizer()
    {
        var connectionId = Guid.NewGuid();
        var operations = new FakeRemoteOperations([CreateProfile(connectionId, PublishedMcpScope.FullSession)]);
        var tools = CreateTools(
            operations,
            [new PublishedConnectionAccess(connectionId, ["/srv/data"])],
            new UnavailableOperationAuthorizer());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.ExecAsync("op-1", connectionId.ToString("D"), "whoami", default));
        Assert.Equal(0, operations.ExecCount);
    }

    [Fact]
    public async Task WriteAndCommandScopesAreIndependent()
    {
        var writeId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var operations = new FakeRemoteOperations([
            CreateProfile(writeId, PublishedMcpScope.ApprovedWrites),
            CreateProfile(commandId, PublishedMcpScope.ApprovedCommands)
        ]);
        var tools = CreateTools(operations, [
            new PublishedConnectionAccess(writeId, ["/srv/data"]),
            new PublishedConnectionAccess(commandId, ["/srv/data"])
        ]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.ExecAsync("op-command", writeId.ToString("D"), "whoami", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.WriteAsync("op-write", commandId.ToString("D"), "upload", "/srv/data/file", default));
    }

    [Fact]
    public void HighRiskToolApiDoesNotAcceptApprovalTokens()
    {
        var execParameters = typeof(RemoteMcpTools).GetMethod(nameof(RemoteMcpTools.ExecAsync))!.GetParameters();
        var writeParameters = typeof(RemoteMcpTools).GetMethod(nameof(RemoteMcpTools.WriteAsync))!.GetParameters();

        Assert.DoesNotContain(execParameters, parameter =>
            string.Equals(parameter.Name, "approvalToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(writeParameters, parameter =>
            string.Equals(parameter.Name, "approvalToken", StringComparison.OrdinalIgnoreCase));
    }

    private static RemoteMcpTools CreateTools(
        FakeRemoteOperations operations,
        IReadOnlyList<PublishedConnectionAccess> access,
        IOperationAuthorizer? authorizer = null,
        OperationDeduplicator? deduplicator = null)
    {
        var registry = new PublishedConnectionRegistry(operations, access);
        return new RemoteMcpTools(
            operations,
            registry,
            authorizer ?? new PreauthorizedOperationAuthorizer(),
            new McpCallerIdentity("test-caller"),
            deduplicator ?? new OperationDeduplicator(TimeSpan.FromMinutes(5)));
    }

    private static ConnectionProfile CreateProfile(Guid id, PublishedMcpScope scope) =>
        new(
            id,
            "connection",
            ConnectionProtocol.Ssh,
            "host",
            22,
            "user",
            AuthenticationKind.None,
            null,
            ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
            scope);

    private sealed class PreauthorizedOperationAuthorizer : IOperationAuthorizer
    {
        public ValueTask AuthorizeAsync(OperationAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticTokenProvider : IMcpTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid profileId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("external-token");
    }

    private sealed class FakeRemoteOperations(IReadOnlyList<ConnectionProfile> profiles) : IRemoteOperations
    {
        public string? LastReadPath { get; private set; }
        public int ExecCount { get; private set; }

        public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(profiles);

        public Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(SessionState.Connected);

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);

        public Task<string> ResolveReadPathAsync(string connectionId, string path, IReadOnlyList<string> allowedRoots, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken)
        {
            LastReadPath = path;
            return Task.FromResult(Array.Empty<byte>());
        }

        public Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken)
        {
            ExecCount++;
            return Task.FromResult("ok");
        }

        public Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
