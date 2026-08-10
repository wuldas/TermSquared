using System.Text.Json;

namespace TermSquared.App;

internal sealed class WorkspaceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly WorkspaceSettings _settings;

    private WorkspaceSettingsStore(string path, WorkspaceSettings settings)
    {
        _path = path;
        _settings = settings;
    }

    public IReadOnlyList<ConnectionFolderSettings> Folders => _settings.Folders;
    public IReadOnlyDictionary<string, ConnectionItemSettings> Connections => _settings.Connections;

    public static WorkspaceSettingsStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<WorkspaceSettings>(json, JsonOptions);
                if (settings is not null) return new WorkspaceSettingsStore(path, settings);
            }
        }
        catch
        {
        }

        return new WorkspaceSettingsStore(path, new WorkspaceSettings());
    }

    public void AddFolder(string name, string? parentId = null)
    {
        var siblings = _settings.Folders.Where(folder => folder.ParentId == parentId).ToArray();
        _settings.Folders.Add(new ConnectionFolderSettings(Guid.NewGuid().ToString("N"), name, parentId, siblings.Length));
        Save();
    }

    public void EnsureConnections(IReadOnlyList<Guid> profileIds)
    {
        var changed = false;
        for (var index = 0; index < profileIds.Count; index++)
        {
            var key = profileIds[index].ToString("D");
            if (_settings.Connections.ContainsKey(key)) continue;
            _settings.Connections[key] = new ConnectionItemSettings { Order = index };
            changed = true;
        }
        if (changed) Save();
    }

    public void RenameFolder(string id, string name)
    {
        var folder = _settings.Folders.FirstOrDefault(item => item.Id == id);
        if (folder is null) return;
        folder.Name = name;
        Save();
    }

    public void DeleteFolder(string id)
    {
        var descendants = GetDescendantFolderIds(id);
        descendants.Add(id);
        _settings.Folders.RemoveAll(folder => descendants.Contains(folder.Id));
        foreach (var connection in _settings.Connections.Values)
            if (connection.FolderId is not null && descendants.Contains(connection.FolderId))
                connection.FolderId = null;
        Save();
    }

    public void RenameConnection(string workspaceId, string? displayName)
    {
        GetConnection(workspaceId).DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Save();
    }

    public void MoveConnection(string workspaceId, string? folderId)
    {
        var targetOrder = _settings.Connections.Values.Count(item => item.FolderId == folderId);
        var connection = GetConnection(workspaceId);
        connection.FolderId = folderId;
        connection.Order = targetOrder;
        Save();
    }

    public void MoveConnectionBy(string workspaceId, int delta)
    {
        var connection = GetConnection(workspaceId);
        var siblings = _settings.Connections
            .Where(item => item.Value.FolderId == connection.FolderId)
            .OrderBy(item => item.Value.Order)
            .Select(item => item.Value)
            .ToList();
        var index = siblings.IndexOf(connection);
        var target = Math.Clamp(index + delta, 0, siblings.Count - 1);
        if (index < 0 || index == target) return;
        (siblings[index].Order, siblings[target].Order) = (siblings[target].Order, siblings[index].Order);
        Save();
    }

    public void HideConnection(string workspaceId)
    {
        GetConnection(workspaceId).Hidden = true;
        Save();
    }

    public string DuplicateConnection(Guid sourceProfileId, string displayName, string? folderId)
    {
        var id = Guid.NewGuid().ToString("D");
        _settings.Connections[id] = new ConnectionItemSettings
        {
            SourceProfileId = sourceProfileId.ToString("D"),
            DisplayName = displayName,
            FolderId = folderId,
            Order = _settings.Connections.Values.Count(item => item.FolderId == folderId)
        };
        Save();
        return id;
    }

    public void RestoreHiddenConnections()
    {
        foreach (var connection in _settings.Connections.Values) connection.Hidden = false;
        Save();
    }

    public string GetDisplayName(string workspaceId, string fallback) =>
        _settings.Connections.TryGetValue(workspaceId, out var settings) &&
        !string.IsNullOrWhiteSpace(settings.DisplayName)
            ? settings.DisplayName
            : fallback;

    private ConnectionItemSettings GetConnection(string workspaceId)
    {
        if (_settings.Connections.TryGetValue(workspaceId, out var settings)) return settings;
        settings = new ConnectionItemSettings();
        _settings.Connections[workspaceId] = settings;
        return settings;
    }

    private HashSet<string> GetDescendantFolderIds(string parentId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(parentId);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var child in _settings.Folders.Where(folder => folder.ParentId == parent))
                if (result.Add(child.Id)) pending.Enqueue(child.Id);
        }
        return result;
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_settings, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class WorkspaceSettings
    {
        public List<ConnectionFolderSettings> Folders { get; set; } = [];
        public Dictionary<string, ConnectionItemSettings> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class ConnectionFolderSettings(string id, string name, string? parentId, int order)
{
    public string Id { get; set; } = id;
    public string Name { get; set; } = name;
    public string? ParentId { get; set; } = parentId;
    public int Order { get; set; } = order;
}

internal sealed class ConnectionItemSettings
{
    public string? SourceProfileId { get; set; }
    public string? DisplayName { get; set; }
    public string? FolderId { get; set; }
    public int Order { get; set; }
    public bool Hidden { get; set; }
}
