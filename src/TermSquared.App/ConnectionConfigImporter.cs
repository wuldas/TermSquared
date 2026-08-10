using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.App;

internal sealed record ImportedConfiguration(IReadOnlyList<ConnectionProfile> Profiles, InMemorySecretStore VolatileSecrets) : IDisposable
{
    public void Dispose() => VolatileSecrets.Dispose();
}

internal static class ConnectionConfigImporter
{
    public static async Task<ImportedConfiguration> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        var secrets = new InMemorySecretStore();
        if (!File.Exists(filePath)) return new ImportedConfiguration([], secrets);
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var profiles = new List<ConnectionProfile>();
            foreach (var (item, configuredAlias) in EnumerateProfiles(document.RootElement))
            {
                var host = GetString(item, "host", "hostname");
                var username = GetString(item, "username", "user");
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username)) continue;
                var name = GetString(item, "alias", "name", "id") ?? configuredAlias ?? host;
                var port = GetInt32(item, "port") ?? 22;
                SecretReference? secret = null;
                var password = WebUtility.HtmlDecode(GetString(item, "password"));
                var publishedRoots = GetStringArray(item, "allowedRemotePaths");
                if (!string.IsNullOrEmpty(password))
                    secret = await secrets.StoreAsync("ssh-password", password.AsMemory(), cancellationToken).ConfigureAwait(false);
                profiles.Add(new ConnectionProfile(
                    CreateStableId(configuredAlias ?? name, host, port, username), name, ConnectionProtocol.Ssh, host, port, username,
                    secret is null ? AuthenticationKind.None : AuthenticationKind.Password,
                    secret, ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
                    publishedRoots.Length > 0 ? PublishedMcpScope.ReadOnly : PublishedMcpScope.None,
                    publishedRoots));
            }
            return new ImportedConfiguration(profiles, secrets);
        }
        catch
        {
            secrets.Dispose();
            throw;
        }
    }

    private static Guid CreateStableId(string name, string host, int port, string username)
    {
        var identity = $"ssh\n{name.Trim()}\n{host.Trim().ToLowerInvariant()}\n{port}\n{username.Trim()}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(identity), hash);
        return new Guid(hash[..16]);
    }

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

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }

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
