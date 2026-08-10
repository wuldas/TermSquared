using System.Text.Json;
using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.App;

internal sealed record ConnectionEditorValue(
    string Name,
    string Host,
    int Port,
    string Username,
    string Password);

internal sealed class ConnectionProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly List<StoredConnectionProfile> _profiles;

    private ConnectionProfileStore(string path, List<StoredConnectionProfile> profiles)
    {
        _path = path;
        _profiles = profiles;
    }

    public static ConnectionProfileStore Load(string path)
    {
        if (!File.Exists(path)) return new ConnectionProfileStore(path, []);
        var document = JsonSerializer.Deserialize<ConnectionProfileDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The connection profile store is empty.");
        if (document.Version != 1) throw new InvalidDataException("Unsupported connection profile store version.");
        foreach (var profile in document.Profiles)
        {
            if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
                string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username) ||
                profile.Port is < 1 or > 65535)
                throw new InvalidDataException("The connection profile store contains an invalid profile.");
            if (profile.Secret is not null && !string.Equals(profile.Secret.Store, "dpapi-file", StringComparison.Ordinal))
                throw new InvalidDataException("Persisted connections may only reference protected local secrets.");
            if (profile.Secret is not null && profile.InheritImportedSecret)
                throw new InvalidDataException("A connection cannot contain and inherit a password at the same time.");
        }
        if (document.Profiles.Select(profile => profile.Id).Distinct().Count() != document.Profiles.Count)
            throw new InvalidDataException("The connection profile store contains duplicate IDs.");
        return new ConnectionProfileStore(path, document.Profiles);
    }

    public IReadOnlyList<ConnectionProfile> Merge(IReadOnlyList<ConnectionProfile> imported)
    {
        var importedById = imported.ToDictionary(profile => profile.Id);
        var merged = imported.ToDictionary(profile => profile.Id);
        foreach (var stored in _profiles)
        {
            importedById.TryGetValue(stored.Id, out var importedProfile);
            var secret = stored.Secret ?? (stored.InheritImportedSecret ? importedProfile?.Secret : null);
            if (stored.InheritImportedSecret && importedProfile is null) continue;
            merged[stored.Id] = new ConnectionProfile(
                stored.Id,
                stored.Name,
                ConnectionProtocol.Ssh,
                stored.Host,
                stored.Port,
                stored.Username,
                secret is null ? AuthenticationKind.None : AuthenticationKind.Password,
                secret,
                ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
                stored.PublishedScope,
                stored.PublishedRoots);
        }
        return merged.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ConnectionProfile> CreateAsync(
        ConnectionEditorValue value,
        ISecretStore secrets,
        CancellationToken cancellationToken)
    {
        Validate(value, passwordRequired: true);
        var reference = await secrets.StoreAsync("ssh-password", value.Password.AsMemory(), cancellationToken).ConfigureAwait(false);
        var stored = new StoredConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = value.Name.Trim(),
            Host = value.Host.Trim(),
            Port = value.Port,
            Username = value.Username.Trim(),
            Secret = reference
        };
        _profiles.Add(stored);
        try
        {
            Save();
        }
        catch
        {
            _profiles.Remove(stored);
            await secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            throw;
        }
        return ToProfile(stored, reference);
    }

    public async Task<ConnectionProfile> UpdateAsync(
        ConnectionProfile current,
        ConnectionEditorValue value,
        IReadOnlyList<ConnectionProfile> imported,
        ISecretStore secrets,
        CancellationToken cancellationToken)
    {
        var endpointChanged = !string.Equals(current.Host, value.Host.Trim(), StringComparison.OrdinalIgnoreCase) ||
                              current.Port != value.Port ||
                              !string.Equals(current.Username, value.Username.Trim(), StringComparison.Ordinal);
        Validate(value, passwordRequired: current.Secret is null || endpointChanged);
        var existing = _profiles.FirstOrDefault(profile => profile.Id == current.Id);
        var importedProfile = imported.FirstOrDefault(profile => profile.Id == current.Id);
        if (endpointChanged && string.IsNullOrWhiteSpace(value.Password))
            throw new ArgumentException("修改连接目标时必须重新输入密码。");
        SecretReference? newReference = null;
        if (!string.IsNullOrWhiteSpace(value.Password))
            newReference = await secrets.StoreAsync("ssh-password", value.Password.AsMemory(), cancellationToken).ConfigureAwait(false);

        var stored = existing ?? new StoredConnectionProfile { Id = current.Id };
        var oldSnapshot = Clone(stored);
        var oldReference = stored.Secret;
        stored.Name = value.Name.Trim();
        stored.Host = value.Host.Trim();
        stored.Port = value.Port;
        stored.Username = value.Username.Trim();
        stored.Secret = newReference ?? existing?.Secret;
        stored.InheritImportedSecret = stored.Secret is null && !endpointChanged && importedProfile?.Secret is not null;
        if (endpointChanged)
        {
            stored.PublishedScope = PublishedMcpScope.None;
            stored.PublishedRoots = [];
        }
        else if (existing is null)
        {
            stored.PublishedScope = current.PublishedScope;
            stored.PublishedRoots = current.PublishedRoots?.ToArray() ?? [];
        }
        if (existing is null) _profiles.Add(stored);
        try
        {
            Save();
        }
        catch
        {
            if (existing is null) _profiles.Remove(stored);
            else Restore(stored, oldSnapshot);
            if (newReference is not null) await secrets.DeleteAsync(newReference, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (newReference is not null && oldReference is not null)
            await secrets.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        var effectiveSecret = stored.Secret ?? (stored.InheritImportedSecret ? importedProfile?.Secret : null);
        return ToProfile(stored, effectiveSecret);
    }

    private static void Validate(ConnectionEditorValue value, bool passwordRequired)
    {
        if (string.IsNullOrWhiteSpace(value.Name)) throw new ArgumentException("连接名称不能为空。");
        if (string.IsNullOrWhiteSpace(value.Host) || value.Host.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("主机地址无效。");
        if (value.Port is < 1 or > 65535) throw new ArgumentException("端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(value.Username) || value.Username.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("用户名无效。");
        if (passwordRequired && string.IsNullOrWhiteSpace(value.Password))
            throw new ArgumentException("新连接或目标发生变化时必须输入密码。");
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ConnectionProfileDocument { Profiles = _profiles }, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static ConnectionProfile ToProfile(StoredConnectionProfile stored, SecretReference? secret) =>
        new(stored.Id, stored.Name, ConnectionProtocol.Ssh, stored.Host, stored.Port, stored.Username,
            secret is null ? AuthenticationKind.None : AuthenticationKind.Password, secret,
            ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser,
            stored.PublishedScope, stored.PublishedRoots);

    private static StoredConnectionProfile Clone(StoredConnectionProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Host = source.Host,
        Port = source.Port,
        Username = source.Username,
        Secret = source.Secret,
        InheritImportedSecret = source.InheritImportedSecret,
        PublishedScope = source.PublishedScope,
        PublishedRoots = source.PublishedRoots.ToArray()
    };

    private static void Restore(StoredConnectionProfile target, StoredConnectionProfile source)
    {
        target.Name = source.Name;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Secret = source.Secret;
        target.InheritImportedSecret = source.InheritImportedSecret;
        target.PublishedScope = source.PublishedScope;
        target.PublishedRoots = source.PublishedRoots;
    }

    private sealed class ConnectionProfileDocument
    {
        public int Version { get; set; } = 1;
        public List<StoredConnectionProfile> Profiles { get; set; } = [];
    }

    private sealed class StoredConnectionProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public SecretReference? Secret { get; set; }
        public bool InheritImportedSecret { get; set; }
        public PublishedMcpScope PublishedScope { get; set; }
        public string[] PublishedRoots { get; set; } = [];
    }
}
