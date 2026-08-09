using TermSquared.Core;
using TermSquared.Protocols.Ssh;
using TermSquared.Security;

namespace TermSquared.Protocols.Tests;

public sealed class SshSessionTests
{
    [Fact]
    public async Task CreatesPasswordSessionFromOpaqueSecretReferenceWithoutConnecting()
    {
        using var secrets = new InMemorySecretStore();
        var reference = await secrets.StoreAsync("ssh-password", "temporary-password".AsMemory(), default);
        var profile = ConnectionProfile.CreateSsh("test", "localhost", 22, "user", reference);
        await using var session = await SshSession.CreateFromProfileAsync(profile, secrets, new RejectKnownHostStore());
        Assert.False(session.IsConnected);
    }

    private sealed class RejectKnownHostStore : IKnownHostStore
    {
        public Task<HostKeyCheck> CheckAsync(string host, int port, string algorithm, ReadOnlyMemory<byte> hostKey, CancellationToken cancellationToken) =>
            Task.FromResult(new HostKeyCheck(
                HostKeyStatus.Unknown,
                null,
                new KnownHost(host, port, algorithm, JsonKnownHostStore.Fingerprint(hostKey.Span), DateTimeOffset.UtcNow)));

        public Task TrustAsync(KnownHost host, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
