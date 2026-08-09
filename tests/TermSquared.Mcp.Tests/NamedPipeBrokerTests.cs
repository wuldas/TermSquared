using TermSquared.Core;
using TermSquared.Mcp;

namespace TermSquared.Mcp.Tests;

public sealed class NamedPipeBrokerTests
{
    [Fact]
    public async Task RoundTripForwardsToRemoteOperations()
    {
        var options = CreateOptions();
        await using var server = new NamedPipeBrokerServer(new FakeRemoteOperations(), options);
        await server.StartAsync();
        var client = new NamedPipeRemoteOperations(options);

        var connections = await client.ListConnectionsAsync(default);
        var path = await client.ResolveReadPathAsync(connections[0].Id.ToString("D"), "/srv/data", ["/srv"], default);

        Assert.Single(connections);
        Assert.Equal("fake", connections[0].Name);
        Assert.Equal("", connections[0].Host);
        Assert.Null(connections[0].Username);
        Assert.Null(connections[0].Secret);
        Assert.Equal("/srv/data", path);
    }

    [Fact]
    public async Task BrokerUsesPublishedRootsInsteadOfClientRoots()
    {
        var operations = new FakeRemoteOperations();
        var options = CreateOptions();
        await using var server = new NamedPipeBrokerServer(operations, options);
        await server.StartAsync();
        var client = new NamedPipeRemoteOperations(options);
        var connection = Assert.Single(await client.ListConnectionsAsync(default));

        await client.ResolveReadPathAsync(connection.Id.ToString("D"), "/srv/data", ["/"], default);

        Assert.Equal(["/srv"], operations.LastAllowedRoots);
    }

    [Fact]
    public async Task UnavailableBrokerFailsWithoutTransportDetails()
    {
        var options = CreateOptions() with { ConnectTimeout = TimeSpan.FromMilliseconds(100) };
        var client = new NamedPipeRemoteOperations(options);

        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => client.ListConnectionsAsync(default));

        Assert.Equal("The TermSquared desktop broker is unavailable.", exception.Message);
    }

    [Fact]
    public async Task OversizedMessageIsRejectedBeforeItIsSent()
    {
        var options = CreateOptions() with { MaximumMessageBytes = 256 };
        await using var server = new NamedPipeBrokerServer(new FakeRemoteOperations(), options);
        await server.StartAsync();
        var client = new NamedPipeRemoteOperations(options);

        await Assert.ThrowsAsync<BrokerMessageTooLargeException>(
            () => client.ResolveReadPathAsync("connection", "/" + new string('x', 1_000), ["/"], default));
    }

    [Fact]
    public async Task BrokerErrorsDoNotExposeOperationDetails()
    {
        var options = CreateOptions();
        await using var server = new NamedPipeBrokerServer(new ThrowingRemoteOperations(), options);
        await server.StartAsync();
        var client = new NamedPipeRemoteOperations(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListDirectoryAsync("connection", "/srv/data", default));

        Assert.Equal("The remote operation failed.", exception.Message);
        Assert.DoesNotContain("credential-value", exception.ToString(), StringComparison.Ordinal);
    }

    private static NamedPipeBrokerOptions CreateOptions() => new()
    {
        PipeName = $"TermSquared.Tests.{Guid.NewGuid():N}",
        ConnectTimeout = TimeSpan.FromSeconds(2),
        OperationTimeout = TimeSpan.FromSeconds(2)
    };

    private sealed class FakeRemoteOperations : IRemoteOperations
    {
        private readonly Guid _connectionId = Guid.NewGuid();
        public IReadOnlyList<string>? LastAllowedRoots { get; private set; }

        public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(
                [new(
                    _connectionId,
                    "fake",
                    ConnectionProtocol.Ssh,
                    "private.example",
                    22,
                    "private-user",
                    AuthenticationKind.Password,
                    new SecretReference("store", "secret"),
                    ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
                    PublishedMcpScope.ReadOnly,
                    ["/srv"])]);

        public Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(SessionState.Connected);

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);

        public Task<string> ResolveReadPathAsync(string connectionId, string path, IReadOnlyList<string> allowedRoots, CancellationToken cancellationToken)
        {
            LastAllowedRoots = allowedRoots;
            return Task.FromResult(path);
        }

        public Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken) =>
            Task.FromResult($"{connectionId}:{command}");

        public Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingRemoteOperations : IRemoteOperations
    {
        private static InvalidOperationException Failure() => new("credential-value must not cross the broker boundary");

        public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<ConnectionProfile>>(Failure());

        public Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken) =>
            Task.FromException<SessionState>(Failure());

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<RemoteEntry>>(Failure());

        public Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken) =>
            Task.FromException<RemoteEntry?>(Failure());

        public Task<string> ResolveReadPathAsync(string connectionId, string path, IReadOnlyList<string> allowedRoots, CancellationToken cancellationToken) =>
            Task.FromException<string>(Failure());

        public Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(Failure());

        public Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken) =>
            Task.FromException<string>(Failure());

        public Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
            Task.FromException(Failure());
    }
}
