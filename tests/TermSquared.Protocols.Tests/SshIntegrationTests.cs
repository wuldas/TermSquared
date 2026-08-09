using System.Net;
using System.Text.Json;
using Renci.SshNet.Common;
using TermSquared.Core;
using TermSquared.Protocols.Ssh;
using TermSquared.Security;
using Xunit.Abstractions;

namespace TermSquared.Protocols.Tests;

public sealed class SshIntegrationTests(ITestOutputHelper output)
{
    private const string ConfigurationPath = @"C:\Users\wulda\.ssh\ssh-config.json";

    [IntegrationFact]
    public async Task J4125TrustsAndStoresHostKeyThenReconnectsAsTrusted()
    {
        using var configuration = await TestSshConfiguration.LoadAsync(ConfigurationPath, default);
        await VerifySuccessfulHostAsync("J4125", configuration);
    }

    [IntegrationFact]
    public async Task RockTrustsAndStoresHostKeyThenReconnectsAsTrusted()
    {
        using var configuration = await TestSshConfiguration.LoadAsync(ConfigurationPath, default);
        await VerifySuccessfulHostAsync("rock", configuration);
    }

    [IntegrationFact]
    public async Task J4125InteractiveShellAcceptsInputAndReturnsOutput()
    {
        using var configuration = await TestSshConfiguration.LoadAsync(ConfigurationPath, default);
        var profile = configuration.GetRequiredProfile("J4125");
        await using var session = await SshSession.CreateFromProfileAsync(
            profile,
            configuration.Secrets,
            new EphemeralKnownHostStore(),
            static (_, _) => Task.FromResult(HostKeyDecision.TrustOnce),
            timeout: TimeSpan.FromSeconds(15));
        await session.ConnectAsync(default);
        await using var shell = session.CreateShellSession(100, 30);
        await shell.WriteAsync("printf 'TERMSQUARED_PTY_OK\\n'\r"u8.ToArray(), default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[4096];
        var outputText = new System.Text.StringBuilder();
        while (!outputText.ToString().Contains("TERMSQUARED_PTY_OK", StringComparison.Ordinal))
        {
            var read = await shell.ReadAsync(buffer, timeout.Token);
            Assert.NotEqual(0, read);
            outputText.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }

        output.WriteLine("SSH alias=J4125 outcome=interactive-shell-success");
    }

    [IntegrationFact]
    public async Task Yc8gAuthenticationFailureIsKnownInteroperabilityFact()
    {
        using var configuration = await TestSshConfiguration.LoadAsync(ConfigurationPath, default);
        var profile = configuration.GetRequiredProfile("YC8G");
        KnownHost? hostKey = null;
        await using var session = await SshSession.CreateFromProfileAsync(
            profile,
            configuration.Secrets,
            new EphemeralKnownHostStore(),
            (check, _) =>
            {
                hostKey = check.Presented;
                return Task.FromResult(HostKeyDecision.TrustOnce);
            },
            timeout: TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<SshAuthenticationException>(() => session.ConnectAsync(default));
        Assert.NotNull(hostKey);
        output.WriteLine(
            $"SSH alias=YC8G outcome=authentication-failed hostKeyAlgorithm={hostKey.Algorithm} hostKeyFingerprint={hostKey.Sha256Fingerprint}");
    }

    private async Task VerifySuccessfulHostAsync(string alias, TestSshConfiguration configuration)
    {
        var profile = configuration.GetRequiredProfile(alias);
        var directory = Path.Combine(Path.GetTempPath(), "TermSquared.Integration", Guid.NewGuid().ToString("N"));
        var knownHostsPath = Path.Combine(directory, "known-hosts.json");
        try
        {
            using var knownHosts = new JsonKnownHostStore(knownHostsPath);
            KnownHost? firstHostKey = null;
            await using (var firstSession = await SshSession.CreateFromProfileAsync(
                             profile,
                             configuration.Secrets,
                             knownHosts,
                             (check, _) =>
                             {
                                 Assert.NotEqual(HostKeyStatus.Changed, check.Status);
                                 if (firstHostKey is null)
                                 {
                                     Assert.Equal(HostKeyStatus.Unknown, check.Status);
                                     firstHostKey = check.Presented;
                                     output.WriteLine(
                                         $"SSH alias={alias} phase=first status=unknown hostKeyAlgorithm={check.Presented.Algorithm} hostKeyFingerprint={check.Presented.Sha256Fingerprint}");
                                     return Task.FromResult(HostKeyDecision.TrustAndStore);
                                 }

                                 AssertTrustedHostKey(firstHostKey, check);
                                 return Task.FromResult(HostKeyDecision.TrustOnce);
                             },
                             timeout: TimeSpan.FromSeconds(15)))
            {
                await firstSession.ConnectAsync(default);
            }

            Assert.NotNull(firstHostKey);
            var trustedChecks = 0;
            await using (var secondSession = await SshSession.CreateFromProfileAsync(
                             profile,
                             configuration.Secrets,
                             knownHosts,
                             (check, _) =>
                             {
                                 AssertTrustedHostKey(firstHostKey, check);
                                 trustedChecks++;
                                 output.WriteLine(
                                     $"SSH alias={alias} phase=second status=trusted hostKeyAlgorithm={check.Presented.Algorithm} hostKeyFingerprint={check.Presented.Sha256Fingerprint}");
                                 return Task.FromResult(HostKeyDecision.TrustOnce);
                             },
                             timeout: TimeSpan.FromSeconds(15)))
            {
                await secondSession.ConnectAsync(default);
                var result = await secondSession.ExecAsync("echo TERMSQUARED_OK", default);
                Assert.Equal(0, result.ExitStatus);
                Assert.Equal("TERMSQUARED_OK", result.StandardOutput.Trim());
                var entries = await secondSession.ListAsync("/", default);
                Assert.NotEmpty(entries);
                output.WriteLine($"SSH alias={alias} outcome=success");
            }

            Assert.True(trustedChecks >= 2, "Both SSH and SFTP connections must validate the stored host key.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertTrustedHostKey(KnownHost expected, HostKeyCheck check)
    {
        Assert.Equal(HostKeyStatus.Trusted, check.Status);
        Assert.Equal(expected.Algorithm, check.Presented.Algorithm);
        Assert.Equal(expected.Sha256Fingerprint, check.Presented.Sha256Fingerprint);
    }

    private sealed class EphemeralKnownHostStore : IKnownHostStore
    {
        public Task<HostKeyCheck> CheckAsync(
            string host,
            int port,
            string algorithm,
            ReadOnlyMemory<byte> hostKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HostKeyCheck(
                HostKeyStatus.Unknown,
                null,
                new KnownHost(host, port, algorithm, JsonKnownHostStore.Fingerprint(hostKey.Span), DateTimeOffset.UtcNow)));

        public Task TrustAsync(KnownHost host, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestSshConfiguration(
        IReadOnlyDictionary<string, ConnectionProfile> profiles,
        InMemorySecretStore secrets) : IDisposable
    {
        public InMemorySecretStore Secrets { get; } = secrets;

        public ConnectionProfile GetRequiredProfile(string alias) =>
            profiles.TryGetValue(alias, out var profile)
                ? profile
                : throw new InvalidDataException($"SSH integration configuration does not contain alias '{alias}'.");

        public static async Task<TestSshConfiguration> LoadAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("SSH integration configuration was not found.", path);
            var secrets = new InMemorySecretStore();
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var profiles = new Dictionary<string, ConnectionProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var (element, configuredAlias) in EnumerateProfiles(document.RootElement))
                {
                    var alias = GetString(element, "alias", "name", "id") ?? configuredAlias;
                    var host = GetString(element, "host", "hostname");
                    var username = GetString(element, "username", "user");
                    var encodedPassword = GetString(element, "password");
                    if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(host) ||
                        string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(encodedPassword))
                        continue;

                    var password = WebUtility.HtmlDecode(encodedPassword);
                    var reference = await secrets.StoreAsync("ssh-integration-password", password.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    profiles.Add(alias, ConnectionProfile.CreateSsh(alias, host, GetInt32(element, "port") ?? 22, username, reference));
                }
                return new TestSshConfiguration(profiles, secrets);
            }
            catch
            {
                secrets.Dispose();
                throw;
            }
        }

        public void Dispose() => Secrets.Dispose();

        private static IEnumerable<(JsonElement Profile, string? Alias)> EnumerateProfiles(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) yield return (item, null);
                yield break;
            }
            if (root.ValueKind != JsonValueKind.Object) yield break;
            foreach (var propertyName in new[] { "connections", "hosts", "profiles" })
            {
                if (!TryGetProperty(root, propertyName, out var collection) || collection.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in collection.EnumerateArray()) yield return (item, null);
                yield break;
            }
            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object) yield return (property.Value, property.Name);
        }

        private static string? GetString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
                if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            return null;
        }

        private static int? GetInt32(JsonElement element, string name) =>
            TryGetProperty(element, name, out var value) && value.TryGetInt32(out var result) ? result : null;

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
            value = default;
            return false;
        }
    }
}
