using Square.Controls;
using Square.Extensions.Terminal;
using Square.Graphics;
using TermSquared.Core;
using TermSquared.Protocols.Ssh;
using TermSquared.Security;

namespace TermSquared.App;

internal enum SessionToolKind
{
    Terminal,
    Sftp,
    PortForwarding,
    SessionInfo
}

internal sealed record OpenSessionSettings(
    Guid SessionId,
    string WorkspaceId,
    string DisplayName,
    SessionToolKind ActiveTool,
    SessionToolKind? SplitTool,
    float SplitWidth);

internal sealed record WorkspaceRestoreEntry(OpenSessionSettings Settings, bool ShouldConnect);

internal static class WorkspaceRestorePlanner
{
    public static IReadOnlyList<WorkspaceRestoreEntry> Create(
        IReadOnlyList<OpenSessionSettings> sessions,
        Guid? activeSessionId)
    {
        var seen = new HashSet<Guid>();
        var normalized = sessions
            .Where(settings => settings.SessionId != Guid.Empty && seen.Add(settings.SessionId))
            .Select(Normalize)
            .ToArray();
        var effectiveActiveId = activeSessionId is Guid active &&
                                normalized.Any(entry => entry.SessionId == active)
            ? active
            : normalized.LastOrDefault()?.SessionId;
        return normalized
            .Select(settings => new WorkspaceRestoreEntry(
                settings,
                effectiveActiveId is Guid selected && settings.SessionId == selected))
            .ToArray();
    }

    private static OpenSessionSettings Normalize(OpenSessionSettings settings)
    {
        var activeTool = Enum.IsDefined(settings.ActiveTool)
            ? settings.ActiveTool
            : SessionToolKind.Terminal;
        SessionToolKind? splitTool = settings.SplitTool is { } split &&
                                     Enum.IsDefined(split) &&
                                     split != activeTool
            ? split
            : null;
        return settings with
        {
            ActiveTool = activeTool,
            SplitTool = splitTool,
            SplitWidth = Math.Clamp(settings.SplitWidth, 280, 700)
        };
    }
}

internal static class SessionItemScope
{
    public static bool Matches(Guid? activeSessionId, Guid itemSessionId) =>
        activeSessionId is Guid active && active == itemSessionId;
}

internal static class SftpSelection
{
    public static RemoteEntry? GetSelectedEntry(VirtualList list) => list.SelectedValue as RemoteEntry;
}

internal sealed class SftpNavigationState
{
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();

    public string CurrentPath { get; private set; } = "/";
    public bool CanGoBack => _backHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;
    public string? BackPath => _backHistory.TryPeek(out var path) ? path : null;
    public string? ForwardPath => _forwardHistory.TryPeek(out var path) ? path : null;
    public string? ParentPath
    {
        get
        {
            if (CurrentPath == "/") return null;
            var separator = CurrentPath.LastIndexOf('/');
            return separator <= 0 ? "/" : CurrentPath[..separator];
        }
    }

    public bool NavigateTo(string path)
    {
        if (!PathRootPolicy.TryCanonicalizePosixPath(path, out var canonicalPath) ||
            string.Equals(CurrentPath, canonicalPath, StringComparison.Ordinal))
            return false;
        _backHistory.Push(CurrentPath);
        _forwardHistory.Clear();
        CurrentPath = canonicalPath;
        return true;
    }

    public bool GoBack()
    {
        if (!_backHistory.TryPop(out var path)) return false;
        _forwardHistory.Push(CurrentPath);
        CurrentPath = path;
        return true;
    }

    public bool GoForward()
    {
        if (!_forwardHistory.TryPop(out var path)) return false;
        _backHistory.Push(CurrentPath);
        CurrentPath = path;
        return true;
    }

    public bool GoUp()
    {
        return ParentPath is { } path && NavigateTo(path);
    }
}

internal sealed class WorkspaceSession(
    ConnectionProfile profile,
    string displayName,
    TerminalView terminal,
    Guid? sessionId = null,
    string? workspaceId = null)
{
    public Guid Id { get; } = sessionId ?? Guid.NewGuid();
    public string WorkspaceId { get; } = workspaceId ?? profile.Id.ToString("D");
    public ConnectionProfile Profile { get; } = profile;
    public string DisplayName { get; set; } = displayName;
    public TerminalView Terminal { get; } = terminal;
    public View? TabContainer { get; set; }
    public Button? TabButton { get; set; }
    public Button? TabCloseButton { get; set; }
    public SessionState State { get; set; } = SessionState.Created;
    public SshSession? Transport { get; set; }
    public SshShellSession? Shell { get; set; }
    public Task ReaderTask { get; set; } = Task.CompletedTask;
    public CancellationTokenSource? ConnectionLifetime { get; set; }
    public HostKeyCheck? PendingHostKey { get; set; }
    public Guid? PendingHostKeyPromptId { get; set; }
    public long Generation { get; set; }
    public long SftpRequestVersion { get; set; }
    public string StatusText { get; set; } = "尚未连接";
    public string StatusColor { get; set; } = "#8290a3";
    public string DetailsText { get; set; } = "";
    public string CommandDraft { get; set; } = "";
    public bool CommandPanelExpanded { get; set; }
    public bool CommandPanelVisible { get; set; }
    public SessionToolKind ActiveTool { get; set; } = SessionToolKind.Terminal;
    public SessionToolKind? SplitTool { get; set; }
    public float SplitWidth { get; set; } = 360;
    public View? PortForwardingPanel { get; set; }
    public bool IsClosing { get; set; }
    public SftpNavigationState SftpNavigation { get; } = new();
    public IReadOnlyList<RemoteEntry> SftpEntries { get; set; } = [];
    public string CurrentRemotePath => SftpNavigation.CurrentPath;
    public RemoteEntry? SelectedRemoteEntry { get; set; }
    public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
    public SemaphoreSlim OutboundGate { get; } = new(1, 1);
}

internal sealed class ConnectionProfileTreeItem(string workspaceId, ConnectionProfile profile, string displayName) : TreeItem(displayName)
{
    public string WorkspaceId { get; } = workspaceId;
    public ConnectionProfile Profile { get; } = profile;
    public double LastClickTime { get; set; }
}

internal sealed class ConnectionFolderTreeItem(ConnectionFolderSettings settings) : TreeItem(settings.Name)
{
    public ConnectionFolderSettings Settings { get; } = settings;
}

internal sealed class SftpListItem(Guid sessionId, RemoteEntry entry, int index) : ListItem
{
    public Guid SessionId { get; } = sessionId;
    public RemoteEntry Entry { get; } = entry;
    public int Index { get; } = index;
    public bool IsDirectory => Entry.Kind == RemoteEntryKind.Directory;
    public double LastClickTime { get; set; }
}

internal sealed record SftpContextMenuRequest(RemoteEntry Entry, Point? Position);

internal sealed record ToolSplitRequest(SessionToolKind Tool, Point Position);

internal sealed record ConnectionContextMenuRequest(Point? Position);

internal sealed record RemoteClipboardItem(Guid SessionId, string Path, string Name, bool IsDirectory, bool Cut);
