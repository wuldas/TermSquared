using Square.Controls;
using Square.Extensions.Terminal;
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
        Guid? activeSessionId) =>
        sessions.Select(settings => new WorkspaceRestoreEntry(
            settings,
            activeSessionId is Guid active && settings.SessionId == active)).ToArray();
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
    public string CurrentRemotePath { get; set; } = "/";
    public SftpTreeItem? SftpRoot { get; set; }
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

internal sealed class SftpTreeItem(Guid sessionId, RemoteEntry? entry, string path, string label) : TreeItem(label)
{
    public Guid SessionId { get; } = sessionId;
    public RemoteEntry? Entry { get; } = entry;
    public string Path { get; } = path;
    public bool IsDirectory => Entry is null || Entry.Kind == RemoteEntryKind.Directory;
    public bool ChildrenLoaded { get; set; }
    public bool IsLoading { get; set; }
    public TreeItem? Placeholder { get; set; }
}

internal sealed record RemoteClipboardItem(Guid SessionId, string Path, string Name, bool IsDirectory, bool Cut);
