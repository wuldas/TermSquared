using System.Net.Sockets;
using System.Globalization;
using System.Text;
using Square.Controls;
using Square.Events;
using Square.Extensions.CodeEditor;
using Square.Extensions.Terminal;
using Square.Hosting;
using Square.Runtime;
using Square.Graphics;
using TermSquared.Core;
using TermSquared.Protocols.Rdp;
using TermSquared.Protocols.Ssh;
using TermSquared.Protocols.Vnc;
using TermSquared.Mcp;
using TermSquared.Security;
using Element = Square.UI.Element;
using UIElement = Square.UI.UIElement;

namespace TermSquared.App;

internal sealed class AppController : IDisposable
{
    private readonly ImportedConfiguration _configuration;
    private readonly string _configPath;
    private readonly ConnectionProfileStore _connectionProfiles;
    private readonly ISecretStore _secrets;
    private readonly JsonKnownHostStore _knownHosts;
    private readonly AppRemoteOperations _remoteOperations;
    private readonly NamedPipeBrokerServer _broker;
    private readonly RdpLauncher _rdpLauncher = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WorkspaceSettingsStore _workspaceSettings;
    private IReadOnlyList<ConnectionProfile> _profiles;
    private readonly Dictionary<Guid, WorkspaceSession> _sessions = [];
    private readonly List<Guid> _sessionOrder = [];
    private readonly Dictionary<string, ConnectionProfileTreeItem> _connectionItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConnectionFolderTreeItem> _connectionFolders = new(StringComparer.Ordinal);
    private View? _root;
    private AppWindow? _window;
    private Element? _leftSidebar;
    private Element? _sftpToolRoot;
    private Element? _sessionInfoRoot;
    private Element? _bottomPanel;
    private Splitter? _leftSplitter;
    private Splitter? _toolSplit;
    private Tree? _connectionTree;
    private Tree? _sftpTree;
    private View? _sessionTabsHost;
    private View? _sessionTabsRoot;
    private View? _sessionToolTabsRoot;
    private View? _sessionContextStatusRoot;
    private View? _sessionContentHost;
    private View? _secondaryToolHost;
    private View? _terminalToolbarRoot;
    private View? _protocolToolsHost;
    private Text? _connectionEmptyState;
    private View? _hostKeyApproval;
    private View? _historyPanel;
    private View? _historyItems;
    private View? _securityPanel;
    private View? _commandPanelBody;
    private CodeEditor? _commandEditor;
    private Text? _sessionStatus;
    private Text? _details;
    private Text? _sftpPathText;
    private Text? _rightPanelTitle;
    private Text? _rightPanelSubtitle;
    private FontIcon? _sessionStatusIcon;
    private FontIcon? _rightPanelIcon;
    private Input? _connectionFilter;
    private Button? _connectButton;
    private Button? _disconnectButton;
    private Button? _refreshFilesButton;
    private Button? _sendButton;
    private Button? _clearCommandsButton;
    private Button? _expandCommandsButton;
    private Button? _terminalToolButton;
    private Button? _sftpToolButton;
    private Button? _portForwardingToolButton;
    private Button? _sessionInfoToolButton;
    private Button? _commandEntryButton;
    private Button? _closeSplitButton;
    private Button? _trustOnceButton;
    private Button? _trustStoreButton;
    private ConnectionProfile? _selectedProfile;
    private Guid? _activeSessionId;
    private RemoteClipboardItem? _remoteClipboard;
    private bool _hasHistory;
    private string? _commandTextBeforeShortcut;
    private Guid? _commandShortcutSessionId;
    private bool _leftSidebarRequested = true;
    private bool _restoringWorkspace;
    private WorkspaceSession? _restoredActiveSession;
    private bool _disposed;

    public AppController(ImportedConfiguration configuration, string configPath)
    {
        _configuration = configuration;
        _configPath = configPath;
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermSquared");
        var protectedSecrets = new DpapiFileSecretStore(Path.Combine(dataRoot, "secrets"));
        _secrets = new RoutingSecretStore(protectedSecrets, new Dictionary<string, ISecretStore>(StringComparer.Ordinal)
        {
            ["memory"] = configuration.VolatileSecrets,
            ["dpapi-file"] = protectedSecrets
        });
        _connectionProfiles = ConnectionProfileStore.Load(Path.Combine(dataRoot, "connections.json"));
        _profiles = _connectionProfiles.Merge(configuration.Profiles);
        _knownHosts = new JsonKnownHostStore(Path.Combine(dataRoot, "known-hosts.json"));
        _workspaceSettings = WorkspaceSettingsStore.Load(Path.Combine(dataRoot, "workspace.json"));
        _workspaceSettings.EnsureConnections(_profiles.Select(profile => profile.Id).ToArray());
        _remoteOperations = new AppRemoteOperations(_profiles, _secrets, _knownHosts);
        _broker = new NamedPipeBrokerServer(_remoteOperations, new NamedPipeBrokerOptions());
    }

    public void Attach(AppWindow window)
    {
        _window = window;
        window.Closed += Dispose;
        window.SizeChanged += ApplyResponsiveLayout;
        _broker.StartAsync(_lifetime.Token).GetAwaiter().GetResult();
    }

    public Element BuildWorkspace()
    {
        var page = new WorkspacePage();
        page.BuildElementTree();

        _root = page.Root;
        _leftSidebar = page.LeftSidebar;
        _sftpToolRoot = page.RightSidebarRoot;
        _sessionInfoRoot = page.SessionInfoRoot;
        _bottomPanel = page.BottomPanel;
        _leftSplitter = page.LeftSplitter;
        _toolSplit = page.ToolSplit;
        _connectionTree = page.ConnectionTree;
        _sftpTree = page.SftpTree;
        _sessionTabsHost = page.SessionTabsHost;
        _sessionTabsRoot = page.SessionTabsRoot;
        _sessionToolTabsRoot = page.SessionToolTabsRoot;
        _sessionContextStatusRoot = page.SessionContextStatusRoot;
        _sessionContentHost = page.SessionContentHost;
        _secondaryToolHost = page.SecondaryToolHost;
        _terminalToolbarRoot = page.TerminalToolbarRoot;
        _protocolToolsHost = page.ProtocolToolsHost;
        _connectionEmptyState = page.ConnectionEmptyState;
        _hostKeyApproval = page.HostKeyApproval;
        _historyPanel = page.HistoryPanel;
        _historyItems = page.HistoryItems;
        _securityPanel = page.SecurityPanel;
        _commandPanelBody = page.CommandPanelBody;
        _commandEditor = page.CommandEditor;
        _sessionStatus = page.SessionStatus;
        _details = page.Details;
        _sftpPathText = page.SftpPathText;
        _rightPanelTitle = page.RightPanelTitle;
        _rightPanelSubtitle = page.RightPanelSubtitle;
        _sessionStatusIcon = page.SessionStatusIcon;
        _rightPanelIcon = page.RightPanelIcon;
        _connectionFilter = page.ConnectionFilter;
        _connectButton = page.ConnectButton;
        _disconnectButton = page.DisconnectButton;
        _refreshFilesButton = page.RefreshFilesButton;
        _sendButton = page.SendButton;
        _clearCommandsButton = page.ClearCommandsButton;
        _expandCommandsButton = page.ExpandCommandsButton;
        _terminalToolButton = page.TerminalToolButton;
        _sftpToolButton = page.SftpToolButton;
        _portForwardingToolButton = page.PortForwardingToolButton;
        _sessionInfoToolButton = page.SessionInfoToolButton;
        _commandEntryButton = page.CommandEntryButton;
        _closeSplitButton = page.CloseSplitButton;
        _trustOnceButton = page.TrustOnceButton;
        _trustStoreButton = page.TrustStoreButton;

        ConfigureWorkspacePage(page);
        RebuildConnectionTree();
        RestoreOpenSessions();
        RenderActiveSession();
        if (_window is not null) ApplyResponsiveLayout(_window.ClientSize);
        return page;
    }

    private void ConfigureWorkspacePage(WorkspacePage page)
    {
        ConfigureFontIcon(page.ConnectionHeaderIcon, FluentGlyphs.Connect, "#8fb6ff", 16);
        ConfigureFontIcon(page.CommandPanelIcon, FluentGlyphs.CommandPrompt, "#8fb6ff", 16);
        ConfigureFontIcon(page.SessionStatusIcon, FluentGlyphs.Connect, "#718096", 14);
        ConfigureFontIcon(page.SftpPanelIcon, FluentGlyphs.Folder, "#8fb6ff", 16);
        ConfigureFontIcon(page.RightPanelIcon, FluentGlyphs.History, "#8fb6ff", 16);
        page.LeftSplitter.ZIndex = 1000;
        page.ToolSplit.ZIndex = 1000;

        page.ConnectMenuItem.Command = _ => RequestConnect();
        page.DisconnectMenuItem.Command = _ => RequestDisconnect();
        page.ExitMenuItem.Command = _ => _window?.Close();
        page.ToggleLeftMenuItem.Command = _ => ToggleLeftSidebar();
        page.ToggleRightMenuItem.Command = _ => ToggleSftpSplit();
        page.ToggleBottomMenuItem.Command = _ => ToggleCommandPanelVisible();
        page.RefreshRemoteMenuItem.Command = item => { _ = RefreshSftpAsync(); };
        page.RestoreConnectionsMenuItem.Command = _ => RestoreHiddenConnections();
        page.ProbeVncMenuItem.Command = item => { _ = ProbeVncAsync(); };
        page.LaunchRdpMenuItem.Command = _ => LaunchRdp();
        page.AboutMenuItem.Command = item =>
        {
            _ = SetStatusAsync("TermSquared - 安全优先的远程连接工作台", "#a8c7ff");
        };

        page.ConnectionFilter.Tooltip = $"配置来源: {_configPath}";
        page.ConnectionFilter.AddEventListener(StandardEvents.Input, ApplyConnectionFilter);
        page.AddConnectionButton.AddEventListener(StandardEvents.Click, () => _ = CreateConnectionAsync());
        page.EditConnectionButton.AddEventListener(StandardEvents.Click, () => _ = EditSelectedConnectionAsync());
        ConfigureIconButton(page.NewFolderButton, FluentGlyphs.NewFolder, "新建连接文件夹",
            () => _ = CreateConnectionFolderAsync());
        ConfigureIconButton(page.ConnectionMoreButton, FluentGlyphs.More, "所选项菜单",
            OpenSelectedConnectionMenu);

        page.ConnectionTree.AddEventListener(StandardEvents.SelectionChange, SelectConnectionTreeItem);
        page.ConnectionTree.AddEventListener<PointerEvent>(StandardEvents.ContextMenu, OpenConnectionContextMenu);
        page.ConnectionTree.AddEventListener<KeyboardEvent>(StandardEvents.KeyDown, e =>
        {
            if (e.KeyCode == 13 && page.ConnectionTree.SelectedItem is ConnectionProfileTreeItem profileItem)
            {
                e.PreventDefault();
                _ = OpenSessionAsync(profileItem.Profile, workspaceId: profileItem.WorkspaceId);
            }
            else if (e.KeyCode == 93 || e.ShiftKey && e.KeyCode == 121)
            {
                e.PreventDefault();
                OpenSelectedConnectionMenu();
            }
        });

        ConfigureIconButton(page.ConnectButton, FluentGlyphs.Connect, "连接或重新连接当前选择的 SSH 配置",
            RequestConnect, "button-primary");
        ConfigureIconButton(page.DisconnectButton, FluentGlyphs.Disconnect, "安全关闭当前终端和 SFTP 会话",
            RequestDisconnect, "button-danger");
        ConfigureIconButton(page.RefreshFilesButton, FluentGlyphs.Refresh, "读取远程根目录",
            () => _ = RefreshSftpAsync());

        ConfigureIconButton(page.SendButton, FluentGlyphs.Send, "发送全部", SendCommands, "icon-button-primary");
        ConfigureIconButton(page.ClearCommandsButton, FluentGlyphs.Delete, "清空命令", () =>
        {
            page.CommandEditor.Value = "";
        });
        ConfigureIconButton(page.ExpandCommandsButton, FluentGlyphs.ChevronUp, "展开命令面板",
            ToggleCommandPanelExpanded);

        ConfigureSessionToolButton(page.TerminalToolButton, SessionToolKind.Terminal);
        ConfigureSessionToolButton(page.SftpToolButton, SessionToolKind.Sftp);
        ConfigureSessionToolButton(page.PortForwardingToolButton, SessionToolKind.PortForwarding);
        ConfigureSessionToolButton(page.SessionInfoToolButton, SessionToolKind.SessionInfo);
        page.CommandEntryButton.Tooltip = "显示或隐藏多行命令编辑器";
        ConfigureStatusActionButton(page.CommandEntryButton);
        page.CommandEntryButton.AddEventListener(StandardEvents.Click, ToggleCommandPanelVisible);
        page.CloseSplitButton.Tooltip = "关闭当前会话的右侧分屏";
        ConfigureStatusActionButton(page.CloseSplitButton);
        page.CloseSplitButton.AddEventListener(StandardEvents.Click, CloseToolSplit);

        page.CommandEditor.Placeholder = "输入一行或多行命令。每行将依次发送到当前 SSH Shell。";
        page.CommandEditor.Language = "plaintext";
        page.CommandEditor.ThemeId = "default-dark";
        page.CommandEditor.ShowLineNumbers = true;
        page.CommandEditor.ShowGlyphMargin = false;
        page.CommandEditor.ShowFolding = false;
        page.CommandEditor.ShowOverviewRuler = false;
        page.CommandEditor.ShowScrollBars = true;
        page.CommandEditor.WordWrap = true;
        page.CommandEditor.AddEventListener<KeyboardEvent>(StandardEvents.KeyDown, e =>
        {
            if (e.KeyCode != 13 || !e.ControlKey) return;
            _commandTextBeforeShortcut = page.CommandEditor.Value;
            _commandShortcutSessionId = _activeSessionId;
            e.PreventDefault();
            SendCommands();
        });
        page.CommandEditor.AddEventListener(StandardEvents.Input, () =>
        {
            if (_commandTextBeforeShortcut is null) return;
            if (_commandShortcutSessionId == _activeSessionId)
                page.CommandEditor.Value = _commandTextBeforeShortcut;
            _commandTextBeforeShortcut = null;
            _commandShortcutSessionId = null;
        });

        ConfigureSplitter(page.LeftSplitter, page.LeftSidebar, page.LeftSidebarRoot, 276, 230, 420);
        page.ToolSplit.Minimum = 280;
        page.ToolSplit.Maximum = 700;
        page.ToolSplit.Value = 360;
        page.ToolSplit.IsVertical = true;
        page.ToolSplit.IsReversed = true;
        page.ToolSplit.AddEventListener(StandardEvents.Input, () =>
        {
            if (ActiveSession is not { } session) return;
            session.SplitWidth = page.ToolSplit.Value;
            ApplyToolSplitWidth(session.SplitWidth);
        });
        page.ToolSplit.AddEventListener(StandardEvents.Change, SaveOpenSessions);

        ConfigureIconButton(page.NewRemoteFolderButton, FluentGlyphs.NewFolder, "新建远程目录",
            () => _ = CreateRemoteDirectoryAsync());
        ConfigureIconButton(page.UploadRemoteFileButton, FluentGlyphs.Upload, "上传本地文件",
            () => _ = UploadRemoteFileAsync());
        ConfigureIconButton(page.SftpMoreButton, FluentGlyphs.More, "所选文件菜单", OpenSelectedSftpMenu);
        ConfigureIconButton(page.RefreshSftpButton, FluentGlyphs.Refresh, "刷新 SFTP",
            () => _ = RefreshSftpAsync());
        page.SftpTree.AddEventListener(StandardEvents.SelectionChange, SelectSftpTreeItem);
        page.SftpTree.AddEventListener("expand", e =>
        {
            if (e.Target is SftpTreeItem item) _ = EnsureSftpChildrenAsync(item);
        });
        page.SftpTree.AddEventListener<PointerEvent>(StandardEvents.ContextMenu, OpenSftpContextMenu);
        page.SftpTree.AddEventListener<KeyboardEvent>(StandardEvents.KeyDown, e =>
        {
            if (e.KeyCode == 93 || e.ShiftKey && e.KeyCode == 121)
            {
                e.PreventDefault();
                OpenSelectedSftpMenu();
            }
        });

        page.TrustOnceButton.AddEventListener(StandardEvents.Click,
            () => _ = ConnectPendingAsync(HostKeyDecision.TrustOnce));
        page.TrustStoreButton.AddEventListener(StandardEvents.Click,
            () => _ = ConnectPendingAsync(HostKeyDecision.TrustAndStore));
        page.BottomPanel.IsVisible = false;
        page.ToolSplit.IsVisible = false;
        page.SecondaryToolHost.IsVisible = false;
        page.CloseSplitButton.IsVisible = false;
        page.CommandEntryButton.IsVisible = false;
        page.SessionTabsRoot.IsVisible = false;
        page.SessionToolTabsRoot.IsVisible = false;
        page.SessionContextStatusRoot.IsVisible = false;
        SetCommandPanelExpanded(false);
        SetTrustButtons(false);
        ShowHistoryContext();
    }

    private static void ConfigureSplitter(
        Splitter splitter,
        Element panel,
        View panelContent,
        float value,
        float minimum,
        float maximum,
        bool reversed = false)
    {
        splitter.Minimum = minimum;
        splitter.Maximum = maximum;
        splitter.Value = value;
        splitter.IsVertical = true;
        splitter.IsReversed = reversed;
        void ApplyWidth(float widthValue)
        {
            var width = widthValue.ToString("0", CultureInfo.InvariantCulture) + "px";
            panel.Style.Set("width", width);
            panelContent.Style.Set("width", width);
            var overlayOffset = (widthValue - 2).ToString("0", CultureInfo.InvariantCulture) + "px";
            splitter.Style.Set(reversed ? "right" : "left", overlayOffset);
        }
        ApplyWidth(value);
        splitter.AddEventListener(StandardEvents.Input, () => ApplyWidth(splitter.Value));
    }

    private static void ConfigureIconButton(
        Button button,
        string glyph,
        string tooltip,
        Action action,
        string? className = null)
    {
        button.TextContent = glyph;
        button.ClassList.Add("icon-button");
        if (!string.IsNullOrWhiteSpace(className)) button.ClassList.Add(className);
        button.Style.Set("font-family", "'Segoe Fluent Icons', 'Segoe MDL2 Assets'");
        button.Style.Set("font-size", "16px");
        button.Tooltip = tooltip;
        button.AddEventListener(StandardEvents.Click, action);
    }

    private static void ConfigureFontIcon(FontIcon icon, string glyph, string color, float size)
    {
        icon.Glyph = glyph;
        icon.FontFamily = "Segoe Fluent Icons";
        icon.FontSize = size;
        icon.Style.Set("color", color);
        icon.Style.Set("width", size.ToString("0", CultureInfo.InvariantCulture) + "px");
        icon.Style.Set("height", size.ToString("0", CultureInfo.InvariantCulture) + "px");
    }

    private void ConfigureSessionToolButton(Button button, SessionToolKind tool)
    {
        button.Tooltip = "左键切换；右键在右侧分屏打开";
        button.AddEventListener(StandardEvents.Click, () => ActivateSessionTool(tool));
        button.AddEventListener<PointerEvent>(StandardEvents.ContextMenu, e =>
        {
            var menu = new ContextMenu();
            menu.ClassList.Add("context-menu");
            menu.Children.Add(MenuCommand("在右侧分屏打开", () => OpenToolSplit(tool)));
            OpenContextMenu(menu, new Point(e.ClientX, e.ClientY));
            e.PreventDefault();
        });
    }

    private static void ConfigureStatusActionButton(Button button)
    {
        button.Style.Set("background", "transparent");
        button.Style.Set("color", "#8fb6ff");
        button.Style.Set("border", "1px solid #2a3748");
        button.Style.Set("border-radius", "5px");
    }

    private void ActivateSessionTool(SessionToolKind tool)
    {
        var session = ActiveSession;
        if (session is null) return;
        if (session.SplitTool == tool)
            session.SplitTool = session.ActiveTool;
        session.ActiveTool = tool;
        if (session.SplitTool == session.ActiveTool) session.SplitTool = null;
        session.CommandPanelVisible &= tool == SessionToolKind.Terminal;
        RenderToolLayout(session);
        SaveOpenSessions();
    }

    private void OpenToolSplit(SessionToolKind tool)
    {
        var session = ActiveSession;
        if (session is null) return;
        if (session.ActiveTool == tool)
        {
            _ = SetStatusAsync("当前工具已在主区域显示", "#9aa7b8");
            return;
        }
        session.SplitTool = tool;
        RenderToolLayout(session);
        SaveOpenSessions();
    }

    private void ToggleSftpSplit()
    {
        if (ActiveSession is not { } session) return;
        if (session.SplitTool == SessionToolKind.Sftp) CloseToolSplit();
        else OpenToolSplit(SessionToolKind.Sftp);
    }

    private void CloseToolSplit()
    {
        if (ActiveSession is not { } session) return;
        session.SplitTool = null;
        RenderToolLayout(session);
        SaveOpenSessions();
    }

    private void ToggleCommandPanelVisible()
    {
        if (ActiveSession is not { } session || session.ActiveTool != SessionToolKind.Terminal) return;
        session.CommandPanelVisible = !session.CommandPanelVisible;
        if (_bottomPanel is not null) _bottomPanel.IsVisible = session.CommandPanelVisible;
        if (_commandEntryButton is not null)
            _commandEntryButton.TextContent = session.CommandPanelVisible ? "收起命令" : "多行命令";
        SaveOpenSessions();
    }

    private void ApplyToolSplitWidth(float widthValue)
    {
        if (_secondaryToolHost is null) return;
        _secondaryToolHost.Style.Set("width", widthValue.ToString("0", CultureInfo.InvariantCulture) + "px");
    }

    private static View BuildEmptyState(string title, string description)
    {
        var empty = Panel("", "column", "100%", "112px");
        empty.ClassList.Add("subtle-surface");
        empty.Style.Set("padding", "16px 14px");
        empty.Style.Set("gap", "7px");
        var heading = Caption(title, "#c8d2df");
        heading.Style.Set("font-weight", "700");
        empty.Children.Add(heading);
        var body = Caption(description, "#718096");
        body.Style.Set("white-space", "pre-wrap");
        empty.Children.Add(body);
        return empty;
    }

    private void RebuildConnectionTree()
    {
        if (_connectionTree is null) return;
        _connectionTree.Children.Clear();
        _connectionItems.Clear();
        _connectionFolders.Clear();

        foreach (var folder in _workspaceSettings.Folders.OrderBy(item => item.Order))
        {
            var item = new ConnectionFolderTreeItem(folder) { IsExpanded = true };
            item.ClassList.Add("connection-folder");
            _connectionFolders[folder.Id] = item;
        }
        foreach (var folder in _workspaceSettings.Folders.OrderBy(item => item.Order))
        {
            var item = _connectionFolders[folder.Id];
            if (folder.ParentId is not null && _connectionFolders.TryGetValue(folder.ParentId, out var parent))
                parent.Children.Add(item);
            else
                _connectionTree.Children.Add(item);
        }

        var profilesById = _profiles.ToDictionary(profile => profile.Id.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var connections = new List<(string WorkspaceId, ConnectionProfile Profile, ConnectionItemSettings Settings)>();
        foreach (var profile in _profiles)
        {
            var workspaceId = profile.Id.ToString("D");
            var settings = _workspaceSettings.Connections.GetValueOrDefault(workspaceId) ?? new ConnectionItemSettings();
            if (!settings.Hidden) connections.Add((workspaceId, profile, settings));
        }
        foreach (var entry in _workspaceSettings.Connections)
        {
            if (profilesById.ContainsKey(entry.Key) || entry.Value.Hidden ||
                entry.Value.SourceProfileId is null || !profilesById.TryGetValue(entry.Value.SourceProfileId, out var source))
                continue;
            connections.Add((entry.Key, source, entry.Value));
        }

        foreach (var entry in connections.OrderBy(item => item.Settings.Order).ThenBy(item => item.Profile.Name))
        {
            var displayName = _workspaceSettings.GetDisplayName(entry.WorkspaceId, entry.Profile.Name);
            var item = new ConnectionProfileTreeItem(entry.WorkspaceId, entry.Profile, displayName);
            item.ClassList.Add("connection-item");
            item.Tooltip = $"{entry.Profile.Protocol.ToString().ToUpperInvariant()}  {entry.Profile.Username}@{entry.Profile.Host}:{entry.Profile.Port}";
            item.AddEventListener(StandardEvents.Click, e =>
            {
                if (e.TimeStamp - item.LastClickTime <= 500)
                {
                    item.LastClickTime = 0;
                    _ = OpenSessionAsync(item.Profile, displayName: item.TextContent, forceNew: true, workspaceId: item.WorkspaceId);
                }
                else item.LastClickTime = e.TimeStamp;
            });
            _connectionItems[entry.WorkspaceId] = item;
            if (entry.Settings.FolderId is not null && _connectionFolders.TryGetValue(entry.Settings.FolderId, out var folder))
                folder.Children.Add(item);
            else
                _connectionTree.Children.Add(item);
        }
        ApplyConnectionFilter();
    }

    private void SelectConnectionTreeItem()
    {
        if (_connectionTree?.SelectedItem is not ConnectionProfileTreeItem item) return;
        _selectedProfile = item.Profile;
        SetText(_details,
            $"目标: {item.Profile.Username}@{item.Profile.Host}:{item.Profile.Port}\n协议: {item.Profile.Protocol}\n双击打开新会话。",
            "#c6d0df");
        if (ActiveSession is null) RenderActiveSession();
    }

    private async Task CreateConnectionFolderAsync()
    {
        if (_root is null) return;
        var name = await WorkspaceDialogs.PromptAsync(_root, "新建连接文件夹", placeholder: "文件夹名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        var parentId = (_connectionTree?.SelectedItem as ConnectionFolderTreeItem)?.Settings.Id;
        _workspaceSettings.AddFolder(name, parentId);
        RebuildConnectionTree();
    }

    private async Task CreateConnectionAsync()
    {
        if (_root is null) return;
        var value = await WorkspaceDialogs.EditConnectionAsync(_root);
        if (value is null) return;
        try
        {
            var profile = await _connectionProfiles.CreateAsync(value, _secrets, _lifetime.Token).ConfigureAwait(false);
            await RefreshConnectionCatalogAsync(profile.Id).ConfigureAwait(false);
            await SetStatusAsync($"已新增连接 {profile.Name}", "#7ee2ae").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"新增连接失败: {SafeError(exception)}", "#ff9baa").ConfigureAwait(false);
        }
    }

    private async Task EditSelectedConnectionAsync()
    {
        if (_root is null || _connectionTree?.SelectedItem is not ConnectionProfileTreeItem selected)
        {
            await SetStatusAsync("请先选择要编辑的连接", "#f6c66b").ConfigureAwait(false);
            return;
        }
        var current = selected.Profile;
        var value = await WorkspaceDialogs.EditConnectionAsync(_root, current);
        if (value is null) return;
        var endpointChanged = !string.Equals(current.Host, value.Host, StringComparison.OrdinalIgnoreCase) ||
                              current.Port != value.Port ||
                              !string.Equals(current.Username, value.Username, StringComparison.Ordinal);
        if (endpointChanged && string.IsNullOrWhiteSpace(value.Password))
        {
            await SetStatusAsync("修改主机、端口或用户名时必须重新输入密码", "#f6c66b").ConfigureAwait(false);
            return;
        }

        var sessions = _sessions.Values.Where(session => session.Profile.Id == current.Id).ToArray();
        if (sessions.Length > 0 && !await WorkspaceDialogs.ConfirmAsync(
                _root,
                "编辑连接",
                $"保存“{current.Name}”前将关闭其 {sessions.Length} 个已打开会话。是否继续？"))
            return;
        foreach (var session in sessions) await CloseSessionAsync(session).ConfigureAwait(false);

        try
        {
            var profile = await _connectionProfiles.UpdateAsync(
                current, value, _configuration.Profiles, _secrets, _lifetime.Token).ConfigureAwait(false);
            await RefreshConnectionCatalogAsync(profile.Id, selected.WorkspaceId).ConfigureAwait(false);
            await SetStatusAsync($"已更新连接 {profile.Name}", "#7ee2ae").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"保存连接失败: {SafeError(exception)}", "#ff9baa").ConfigureAwait(false);
        }
    }

    private async Task RefreshConnectionCatalogAsync(Guid selectedProfileId, string? workspaceId = null)
    {
        _profiles = _connectionProfiles.Merge(_configuration.Profiles);
        _workspaceSettings.EnsureConnections(_profiles.Select(profile => profile.Id).ToArray());
        await _remoteOperations.ReplaceProfilesAsync(_profiles, _lifetime.Token).ConfigureAwait(false);
        await InvokeUiAsync(() =>
        {
            RebuildConnectionTree();
            var key = workspaceId ?? selectedProfileId.ToString("D");
            if (_connectionItems.TryGetValue(key, out var item)) _connectionTree?.SelectItem(item);
            _selectedProfile = _profiles.FirstOrDefault(profile => profile.Id == selectedProfileId);
            RenderActiveSession();
        }).ConfigureAwait(false);
    }

    private async Task RenameSelectedConnectionNodeAsync()
    {
        if (_root is null || _connectionTree?.SelectedItem is not { } selected) return;
        var name = await WorkspaceDialogs.PromptAsync(_root, "重命名", selected.TextContent, "显示名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        switch (selected)
        {
            case ConnectionFolderTreeItem folder:
                _workspaceSettings.RenameFolder(folder.Settings.Id, name);
                break;
            case ConnectionProfileTreeItem profile:
                _workspaceSettings.RenameConnection(profile.WorkspaceId, name);
                break;
        }
        RebuildConnectionTree();
    }

    private void OpenConnectionContextMenu(PointerEvent e)
    {
        if (_connectionTree is null) return;
        if (FindAncestor<ConnectionProfileTreeItem>(e.Target as Element) is { } profile) _connectionTree.SelectItem(profile);
        else if (FindAncestor<ConnectionFolderTreeItem>(e.Target as Element) is { } folder) _connectionTree.SelectItem(folder);
        OpenSelectedConnectionMenu(new Point(e.ClientX, e.ClientY));
        e.PreventDefault();
    }

    private void OpenSelectedConnectionMenu() => OpenSelectedConnectionMenu(null);

    private void OpenSelectedConnectionMenu(Point? position)
    {
        if (_root is null || _connectionTree?.SelectedItem is not { } selected) return;
        var menu = new ContextMenu();
        menu.ClassList.Add("context-menu");
        if (selected is ConnectionProfileTreeItem profile)
        {
            menu.Children.Add(MenuCommand("打开新会话", () => _ = OpenSessionAsync(profile.Profile, displayName: profile.TextContent, forceNew: true, workspaceId: profile.WorkspaceId)));
            menu.Children.Add(MenuCommand("编辑连接信息", () => _ = EditSelectedConnectionAsync()));
            menu.Children.Add(MenuCommand("重命名显示名称", () => _ = RenameSelectedConnectionNodeAsync()));
            menu.Children.Add(MenuCommand("复制连接快捷方式", () => DuplicateConnectionItem(profile)));
            menu.Children.Add(BuildMoveConnectionMenu(profile));
            menu.Children.Add(new MenuSeparator());
            menu.Children.Add(MenuCommand("上移", () => MoveConnectionItem(profile, -1)));
            menu.Children.Add(MenuCommand("下移", () => MoveConnectionItem(profile, 1)));
            menu.Children.Add(MenuCommand("从工作区移除", () => _ = RemoveConnectionItemAsync(profile)));
        }
        else if (selected is ConnectionFolderTreeItem folder)
        {
            menu.Children.Add(MenuCommand("在此新建文件夹", () => _ = CreateConnectionFolderAsync()));
            menu.Children.Add(MenuCommand("重命名文件夹", () => _ = RenameSelectedConnectionNodeAsync()));
            menu.Children.Add(MenuCommand("删除文件夹", () => _ = DeleteConnectionFolderAsync(folder)));
        }
        OpenContextMenu(menu, position ?? MenuPointFor(selected));
    }

    private MenuItem BuildMoveConnectionMenu(ConnectionProfileTreeItem item)
    {
        var root = new MenuItem { TextContent = "移动到文件夹" };
        var submenu = new Menu();
        submenu.Children.Add(MenuCommand("工作区根目录", () => MoveConnectionTo(item, null)));
        foreach (var folder in _workspaceSettings.Folders.OrderBy(folder => folder.Name))
            submenu.Children.Add(MenuCommand(folder.Name, () => MoveConnectionTo(item, folder.Id)));
        root.Children.Add(submenu);
        return root;
    }

    private void DuplicateConnectionItem(ConnectionProfileTreeItem item)
    {
        var settings = _workspaceSettings.Connections.GetValueOrDefault(item.WorkspaceId);
        _workspaceSettings.DuplicateConnection(item.Profile.Id, item.TextContent + " 副本", settings?.FolderId);
        RebuildConnectionTree();
    }

    private void MoveConnectionItem(ConnectionProfileTreeItem item, int delta)
    {
        _workspaceSettings.MoveConnectionBy(item.WorkspaceId, delta);
        RebuildConnectionTree();
    }

    private void MoveConnectionTo(ConnectionProfileTreeItem item, string? folderId)
    {
        _workspaceSettings.MoveConnection(item.WorkspaceId, folderId);
        RebuildConnectionTree();
    }

    private async Task RemoveConnectionItemAsync(ConnectionProfileTreeItem item)
    {
        if (_root is null || !await WorkspaceDialogs.ConfirmAsync(_root, "移除连接", $"从 TermSquared 工作区移除“{item.TextContent}”？\n原始 SSH 配置不会被删除。")) return;
        _workspaceSettings.HideConnection(item.WorkspaceId);
        RebuildConnectionTree();
    }

    private async Task DeleteConnectionFolderAsync(ConnectionFolderTreeItem folder)
    {
        if (_root is null || !await WorkspaceDialogs.ConfirmAsync(_root, "删除文件夹", $"删除“{folder.TextContent}”及其子文件夹？\n其中连接将移回工作区根目录。")) return;
        _workspaceSettings.DeleteFolder(folder.Settings.Id);
        RebuildConnectionTree();
    }

    private void OpenContextMenu(ContextMenu menu, Point position)
    {
        if (_root is null) return;
        menu.AddEventListener("close", () =>
        {
            if (menu.ParentNode is Element parent) parent.Children.Remove(menu);
        }, new AddEventListenerOptions { Once = true });
        _root.Children.Add(menu);
        menu.OpenAt(position);
    }

    private static Point MenuPointFor(Element element)
    {
        var point = new Point(element.Geometry.X + 20, element.Geometry.Y + 26);
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollViewer scroll) continue;
            point = new Point(point.X - scroll.HorizontalOffset, point.Y - scroll.VerticalOffset);
        }
        return point;
    }

    private static T? FindAncestor<T>(Element? element) where T : Element
    {
        for (var current = element; current is not null; current = current.Parent)
            if (current is T typed) return typed;
        return null;
    }

    private void SendCommands()
    {
        var text = _commandEditor?.Value;
        if (string.IsNullOrWhiteSpace(text)) return;
        var session = ActiveSession;
        if (session?.Shell is null)
        {
            _ = SetStatusAsync("请先连接 SSH 会话再发送文本", "#f6c66b");
            return;
        }
        session.CommandDraft = text;
        var commands = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        _ = WriteTerminalInputAsync(session, commands.Replace("\n", "\r", StringComparison.Ordinal) + "\r");
        UpdateSessionStatus(session, "已将命令发送到当前会话", "#7ee2ae");
    }

    private void ToggleCommandPanelExpanded()
    {
        var session = ActiveSession;
        if (session is null) return;
        SetCommandPanelExpanded(!session.CommandPanelExpanded);
    }

    private void SetCommandPanelExpanded(bool expanded)
    {
        if (ActiveSession is { } session) session.CommandPanelExpanded = expanded;
        if (_commandPanelBody is not null)
            _commandPanelBody.Style.Set("height", expanded ? "270px" : "150px");
        if (_expandCommandsButton is not null)
        {
            _expandCommandsButton.TextContent = expanded ? FluentGlyphs.ChevronDown : FluentGlyphs.ChevronUp;
            _expandCommandsButton.Tooltip = expanded ? "收起命令面板" : "展开命令面板";
        }
    }

    private Task ConnectPendingAsync(HostKeyDecision decision)
    {
        var session = ActiveSession;
        return session?.PendingHostKey is null ? Task.CompletedTask : ConnectAsync(session, decision);
    }

    private async Task ConnectAsync(WorkspaceSession workspaceSession, HostKeyDecision requestedDecision)
    {
        await workspaceSession.LifecycleGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (workspaceSession.State == SessionState.Connecting) return;
            await DisconnectSessionCoreAsync(workspaceSession, preparingConnection: true).ConfigureAwait(false);
            var profile = workspaceSession.Profile;
            var generation = ++workspaceSession.Generation;
            var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            workspaceSession.ConnectionLifetime = connectionLifetime;
            workspaceSession.State = SessionState.Connecting;
            workspaceSession.StatusText = $"正在连接 {profile.Username}@{profile.Host}:{profile.Port}";
            workspaceSession.StatusColor = "#f6c66b";
            await InvokeUiAsync(() => RenderSession(workspaceSession)).ConfigureAwait(false);
        HostKeyCheck? observed = null;
        SshSession? session = null;
        try
        {
            session = await SshSession.CreateFromProfileAsync(
                profile,
                _secrets,
                _knownHosts,
                (check, _) =>
                {
                    observed = check;
                    var decision = check.Status switch
                    {
                        HostKeyStatus.Trusted => HostKeyDecision.TrustOnce,
                        HostKeyStatus.Unknown when workspaceSession.PendingHostKey?.Presented.Sha256Fingerprint == check.Presented.Sha256Fingerprint => requestedDecision,
                        _ => HostKeyDecision.Reject
                    };
                    return Task.FromResult(decision);
                },
                cancellationToken: connectionLifetime.Token).ConfigureAwait(false);
            await session.ConnectAsync(connectionLifetime.Token).ConfigureAwait(false);
            var shell = session.CreateShellSession(
                (uint)workspaceSession.Terminal.Columns,
                (uint)workspaceSession.Terminal.Rows);
            if (workspaceSession.Generation != generation) throw new OperationCanceledException();
            workspaceSession.Transport = session;
            workspaceSession.Shell = shell;
            session = null;
            workspaceSession.PendingHostKey = null;
            workspaceSession.PendingHostKeyPromptId = null;
            workspaceSession.State = SessionState.Connected;
            workspaceSession.StatusText = $"已连接 {profile.Username}@{profile.Host}:{profile.Port}";
            workspaceSession.StatusColor = "#7ee2ae";
            workspaceSession.DetailsText = $"目标: {profile.Username}@{profile.Host}:{profile.Port}\n主机密钥: {observed?.Presented.Algorithm}\n{observed?.Presented.Sha256Fingerprint}";
            await InvokeUiAsync(() =>
            {
                workspaceSession.Terminal.Feed($"\r\n\x1b[32mConnected to {workspaceSession.DisplayName}\x1b[0m\r\n");
                AddHistoryEntry(profile, "已连接", "#7ee2ae");
                RenderSession(workspaceSession);
            }).ConfigureAwait(false);
            workspaceSession.ReaderTask = ReadShellAsync(workspaceSession, shell, generation, connectionLifetime.Token);
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception.GetType().Name == "SshAuthenticationException")
        {
            await InvokeUiAsync(() =>
            {
                workspaceSession.State = SessionState.Failed;
                workspaceSession.StatusText = "认证失败，请检查连接凭据";
                workspaceSession.StatusColor = "#ff9baa";
                workspaceSession.DetailsText = $"{workspaceSession.DisplayName}: 认证失败。\n请检查用户名和受保护的凭据配置。";
                AddHistoryEntry(profile, "认证失败", "#ff9baa");
                RenderSession(workspaceSession);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (observed?.Status == HostKeyStatus.Unknown)
        {
            workspaceSession.PendingHostKey = observed;
            workspaceSession.PendingHostKeyPromptId = Guid.NewGuid();
            await InvokeUiAsync(() =>
            {
                workspaceSession.State = SessionState.Failed;
                workspaceSession.StatusText = "需要确认未知主机密钥";
                workspaceSession.StatusColor = "#f6c66b";
                workspaceSession.DetailsText = $"{workspaceSession.DisplayName} 提供了未知主机密钥\n算法: {observed.Presented.Algorithm}\n{observed.Presented.Sha256Fingerprint}\n确认指纹无误后再继续。";
                RenderSession(workspaceSession);
            }).ConfigureAwait(false);
            _ = exception;
        }
        catch (Exception) when (observed?.Status == HostKeyStatus.Changed)
        {
            await InvokeUiAsync(() =>
            {
                workspaceSession.State = SessionState.Failed;
                workspaceSession.StatusText = "主机密钥已变化，连接被拒绝";
                workspaceSession.StatusColor = "#ff7f8f";
                workspaceSession.DetailsText = $"{workspaceSession.DisplayName} 的主机密钥与已保存记录不一致。\n为防止中间人攻击，TermSquared 已拒绝连接。";
                AddHistoryEntry(profile, "主机密钥变化，已拒绝", "#ff7f8f");
                RenderSession(workspaceSession);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = SafeError(exception);
            await InvokeUiAsync(() =>
            {
                workspaceSession.State = SessionState.Failed;
                workspaceSession.StatusText = $"连接失败: {error}";
                workspaceSession.StatusColor = "#ff9baa";
                workspaceSession.DetailsText = $"{workspaceSession.DisplayName}: {error}";
                AddHistoryEntry(profile, $"失败: {error}", "#ff9baa");
                RenderSession(workspaceSession);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        if (workspaceSession.Transport is null && ReferenceEquals(workspaceSession.ConnectionLifetime, connectionLifetime))
        {
            workspaceSession.ConnectionLifetime = null;
            await connectionLifetime.CancelAsync().ConfigureAwait(false);
            connectionLifetime.Dispose();
        }
        }
        finally
        {
            workspaceSession.LifecycleGate.Release();
        }
    }

    private async Task ReadShellAsync(
        WorkspaceSession workspaceSession,
        SshShellSession shell,
        long generation,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (charCount == 0) continue;
                var batch = new string(chars, 0, charCount);
                await InvokeUiAsync(() =>
                {
                    if (workspaceSession.Generation == generation && ReferenceEquals(workspaceSession.Shell, shell))
                        workspaceSession.Terminal.Feed(batch);
                }).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested && workspaceSession.Generation == generation)
            {
                workspaceSession.State = SessionState.Disconnected;
                UpdateSessionStatus(workspaceSession, "远程 Shell 已断开", "#fda4af");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (workspaceSession.Generation == generation)
            {
                workspaceSession.State = SessionState.Failed;
                UpdateSessionStatus(workspaceSession, $"Shell disconnected: {SafeError(exception)}", "#fda4af");
            }
        }
    }

    private async Task WriteTerminalInputAsync(WorkspaceSession workspaceSession, string data)
    {
        var shell = workspaceSession.Shell;
        if (shell is null) return;
        var entered = false;
        try
        {
            await workspaceSession.OutboundGate.WaitAsync(
                workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            entered = true;
            if (ReferenceEquals(workspaceSession.Shell, shell))
                await shell.WriteAsync(Encoding.UTF8.GetBytes(data), workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateSessionStatus(workspaceSession, $"Terminal write failed: {SafeError(exception)}", "#fda4af");
        }
        finally
        {
            if (entered) workspaceSession.OutboundGate.Release();
        }
    }

    private WorkspaceSession? ActiveSession =>
        _activeSessionId is Guid id && _sessions.TryGetValue(id, out var session) ? session : null;

    private async Task<WorkspaceSession?> OpenSessionAsync(
        ConnectionProfile profile,
        bool connect = true,
        string? displayName = null,
        bool forceNew = false,
        string? workspaceId = null)
    {
        var existing = forceNew ? null : _sessions.Values.FirstOrDefault(session =>
            workspaceId is not null
                ? string.Equals(session.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                : session.Profile.Id == profile.Id);
        if (existing is not null)
        {
            await InvokeUiAsync(() => ActivateSession(existing.Id)).ConfigureAwait(false);
            return existing;
        }

        WorkspaceSession? created = null;
        await InvokeUiAsync(() => created = CreateWorkspaceSession(
            profile,
            displayName ?? _workspaceSettings.GetDisplayName(workspaceId ?? profile.Id.ToString("D"), profile.Name),
            workspaceId: workspaceId)).ConfigureAwait(false);

        if (connect && created is not null)
            await ConnectAsync(created, HostKeyDecision.Reject).ConfigureAwait(false);
        return created;
    }

    private WorkspaceSession CreateWorkspaceSession(
        ConnectionProfile profile,
        string displayName,
        Guid? sessionId = null,
        string? workspaceId = null,
        SessionToolKind activeTool = SessionToolKind.Terminal,
        SessionToolKind? splitTool = null,
        float splitWidth = 360)
    {
        var terminal = new TerminalView(100, 30, 5000);
        terminal.ClassList.Add("terminal-frame");
        terminal.Style.Set("flex", "1");
        terminal.Style.Set("width", "100%");
        terminal.Style.Set("min-height", "260px");
        terminal.Style.Set("font-family", "Cascadia Mono, Consolas, monospace");
        terminal.Style.Set("font-size", "15px");
        terminal.Style.Set("border", "0");
        var session = new WorkspaceSession(profile, displayName, terminal, sessionId, workspaceId)
        {
            DetailsText = $"目标: {profile.Username}@{profile.Host}:{profile.Port}\n状态: 尚未建立安全会话",
            ActiveTool = activeTool,
            SplitTool = splitTool == activeTool ? null : splitTool,
            SplitWidth = Math.Clamp(splitWidth, 280, 700)
        };
        var crlf = string.Concat((char)13, (char)10);
        terminal.Feed("\x1b[1;36mTermSquared secure remote workspace\x1b[0m" + crlf);
        terminal.Feed($"Session: {displayName}  {profile.Username}@{profile.Host}:{profile.Port}" + crlf + crlf);
        terminal.Input += (_, input) => _ = WriteTerminalInputAsync(session, input.Data);
        terminal.GridSizeChanged += (_, size) => _ = ResizeRemoteTerminalAsync(session, size.Columns, size.Rows);
        _sessions.Add(session.Id, session);
        _sessionOrder.Add(session.Id);
        BuildSessionTab(session);
        ActivateSession(session.Id);
        return session;
    }

    private void RestoreOpenSessions()
    {
        var plan = WorkspaceRestorePlanner.Create(_workspaceSettings.OpenSessions, _workspaceSettings.ActiveSessionId);
        if (plan.Count == 0)
        {
            if (_workspaceSettings.OpenSessions.Count > 0)
                _workspaceSettings.SaveOpenSessions([], null);
            return;
        }
        WorkspaceSession? preferred = null;
        WorkspaceSession? fallback = null;
        _restoringWorkspace = true;
        try
        {
            foreach (var entry in plan)
            {
                if (!TryResolveWorkspaceProfile(entry.Settings.WorkspaceId, out var profile)) continue;
                var session = CreateWorkspaceSession(
                    profile,
                    entry.Settings.DisplayName,
                    entry.Settings.SessionId,
                    entry.Settings.WorkspaceId,
                    entry.Settings.ActiveTool,
                    entry.Settings.SplitTool,
                    entry.Settings.SplitWidth);
                fallback = session;
                if (entry.ShouldConnect) preferred = session;
            }
            var selected = preferred ?? fallback;
            if (selected is not null)
            {
                ActivateSession(selected.Id);
                _restoredActiveSession = selected;
            }
        }
        finally
        {
            _restoringWorkspace = false;
        }
        SaveOpenSessions();
    }

    public void ConnectRestoredActiveSession()
    {
        var session = _restoredActiveSession;
        _restoredActiveSession = null;
        if (session is not null) _ = ConnectAsync(session, HostKeyDecision.Reject);
    }

    private bool TryResolveWorkspaceProfile(string workspaceId, out ConnectionProfile profile)
    {
        var profileId = workspaceId;
        if (_workspaceSettings.Connections.TryGetValue(workspaceId, out var settings) &&
            !string.IsNullOrWhiteSpace(settings.SourceProfileId))
            profileId = settings.SourceProfileId;
        profile = _profiles.FirstOrDefault(item =>
            string.Equals(item.Id.ToString("D"), profileId, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    private void SaveOpenSessions()
    {
        if (_restoringWorkspace) return;
        _workspaceSettings.SaveOpenSessions(
            _sessionOrder.Where(_sessions.ContainsKey).Select(id => _sessions[id]).Select(session => new OpenSessionSettings(
                session.Id,
                session.WorkspaceId,
                session.DisplayName,
                session.ActiveTool,
                session.SplitTool,
                session.SplitWidth)),
            _activeSessionId);
    }

    private void BuildSessionTab(WorkspaceSession session)
    {
        if (_sessionTabsHost is null) return;
        var container = Panel("", "row", "auto", "38px");
        container.ClassList.Add("session-tab-container");
        container.Style.Set("gap", "0");
        var tab = ActionButton(session.DisplayName, () => ActivateSession(session.Id), compact: true, className: "session-tab");
        tab.Style.Set("height", "38px");
        tab.Style.Set("min-width", "124px");
        tab.Style.Set("border-radius", "7px 0 0 0");
        var close = ActionButton(FluentGlyphs.Cancel, () => _ = CloseSessionAsync(session), compact: true, className: "session-tab-close");
        close.Style.Set("width", "30px");
        close.Style.Set("height", "38px");
        close.Style.Set("padding", "0");
        close.Style.Set("font-family", "'Segoe Fluent Icons', 'Segoe MDL2 Assets'");
        close.Tooltip = "关闭会话";
        container.Children.Add(tab);
        container.Children.Add(close);
        _sessionTabsHost.Children.Add(container);
        session.TabContainer = container;
        session.TabButton = tab;
        UpdateSessionTab(session);
    }

    private void ActivateSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (ActiveSession is { } previous && !ReferenceEquals(previous, session))
        {
            if (_commandEditor is not null) previous.CommandDraft = _commandEditor.Value;
            previous.Terminal.Unfocus();
        }
        _activeSessionId = sessionId;
        if (_commandEditor is not null) _commandEditor.Value = session.CommandDraft;
        SetCommandPanelExpanded(session.CommandPanelExpanded);
        RenderToolLayout(session);
        RenderActiveSession();
        if (!_restoringWorkspace) SaveOpenSessions();
    }

    private void RenderToolLayout(WorkspaceSession session)
    {
        if (_sessionTabsRoot is not null) _sessionTabsRoot.IsVisible = true;
        if (_sessionToolTabsRoot is not null) _sessionToolTabsRoot.IsVisible = true;
        if (_sessionContextStatusRoot is not null) _sessionContextStatusRoot.IsVisible = true;
        SetToolTabState(_terminalToolButton, session.ActiveTool == SessionToolKind.Terminal);
        SetToolTabState(_sftpToolButton, session.ActiveTool == SessionToolKind.Sftp);
        SetToolTabState(_portForwardingToolButton, session.ActiveTool == SessionToolKind.PortForwarding);
        SetToolTabState(_sessionInfoToolButton, session.ActiveTool == SessionToolKind.SessionInfo);

        if (_terminalToolbarRoot is not null)
            _terminalToolbarRoot.IsVisible = session.ActiveTool == SessionToolKind.Terminal;
        if (_commandEntryButton is not null)
        {
            _commandEntryButton.IsVisible = session.ActiveTool == SessionToolKind.Terminal;
            _commandEntryButton.TextContent = session.CommandPanelVisible ? "收起命令" : "多行命令";
        }
        if (_bottomPanel is not null)
            _bottomPanel.IsVisible = session.ActiveTool == SessionToolKind.Terminal && session.CommandPanelVisible;

        RenderToolInHost(session, session.ActiveTool, _sessionContentHost);

        var splitVisible = session.SplitTool is not null;
        if (_toolSplit is not null)
        {
            _toolSplit.IsVisible = splitVisible;
            _toolSplit.Value = session.SplitWidth;
        }
        if (_secondaryToolHost is not null)
        {
            _secondaryToolHost.IsVisible = splitVisible;
            _secondaryToolHost.Children.Clear();
            if (session.SplitTool is { } splitTool)
            {
                ApplyToolSplitWidth(session.SplitWidth);
                RenderToolInHost(session, splitTool, _secondaryToolHost);
            }
        }
        if (_closeSplitButton is not null) _closeSplitButton.IsVisible = splitVisible;

        SetText(_sessionStatus, BuildToolStatus(session), session.StatusColor);
        if (_sessionStatusIcon is not null)
        {
            _sessionStatusIcon.Glyph = session.State == SessionState.Connected ? FluentGlyphs.CheckMark :
                session.State == SessionState.Connecting ? FluentGlyphs.Sync :
                session.State == SessionState.Failed ? FluentGlyphs.Error : FluentGlyphs.Connect;
            _sessionStatusIcon.Style.Set("color", session.StatusColor);
        }
    }

    private static void SetToolTabState(Button? button, bool active)
    {
        if (button is null) return;
        button.ClassList.Toggle("active", active);
        button.Style.Set("background", active ? "#172130" : "#0d131a");
        button.Style.Set("color", active ? "#ffffff" : "#8d9aae");
        button.Style.Set("border", "0");
        button.Style.Set("border-bottom", active ? "2px solid #4f8cff" : "2px solid transparent");
        button.Style.Set("border-radius", "5px 5px 0 0");
    }

    private void RenderToolInHost(WorkspaceSession session, SessionToolKind tool, View? host)
    {
        if (host is null) return;
        Element content = tool switch
        {
            SessionToolKind.Terminal => session.Terminal,
            SessionToolKind.Sftp => _sftpToolRoot ?? BuildEmptyState("SFTP 不可用", "文件浏览器尚未初始化。"),
            SessionToolKind.PortForwarding => session.PortForwardingPanel ??= BuildPortForwardingPanel(),
            SessionToolKind.SessionInfo => _sessionInfoRoot ?? BuildEmptyState("会话信息不可用", "会话检查器尚未初始化。"),
            _ => session.Terminal
        };

        if (tool == SessionToolKind.SessionInfo)
        {
            if (session.PendingHostKey is not null) ShowSecurityContext();
            else ShowHistoryContext();
        }
        if (content.ParentNode is Element parent) parent.Children.Remove(content);
        host.Children.Clear();
        host.Children.Add(content);
    }

    private static View BuildPortForwardingPanel()
    {
        var panel = Panel("#10161e", "column", "100%", "100%");
        panel.Style.Set("padding", "12px");
        panel.Children.Add(BuildEmptyState("映射端口", "当前会话尚未配置本地、远程或动态端口映射。"));
        return panel;
    }

    private static string BuildToolStatus(WorkspaceSession session) => session.ActiveTool switch
    {
        SessionToolKind.Terminal when session.State == SessionState.Connected =>
            $"已连接 {session.Profile.Username}@{session.Profile.Host}:{session.Profile.Port}  |  UTF-8  |  {session.Terminal.Columns}x{session.Terminal.Rows}",
        SessionToolKind.Terminal => session.StatusText,
        SessionToolKind.Sftp when session.State == SessionState.Connected =>
            $"SFTP 已连接  |  {session.CurrentRemotePath}  |  当前无传输任务",
        SessionToolKind.Sftp => $"SFTP 未连接  |  {session.StatusText}",
        SessionToolKind.PortForwarding when session.State == SessionState.Connected =>
            "端口映射  |  当前无活动映射",
        SessionToolKind.PortForwarding => $"端口映射不可用  |  {session.StatusText}",
        _ => session.StatusText
    };

    private async Task CloseSessionAsync(WorkspaceSession session)
    {
        if (session.IsClosing) return;
        session.IsClosing = true;
        try
        {
            await DisconnectSessionAsync(session).ConfigureAwait(false);
            await InvokeUiAsync(() =>
            {
                if (session.TabContainer?.ParentNode is Element parent) parent.Children.Remove(session.TabContainer);
                if (session.Terminal.ParentNode is Element terminalParent) terminalParent.Children.Remove(session.Terminal);
                _sessions.Remove(session.Id);
                _sessionOrder.Remove(session.Id);
                session.LifecycleGate.Dispose();
                session.OutboundGate.Dispose();
                if (_activeSessionId == session.Id)
                {
                    _activeSessionId = _sessionOrder.LastOrDefault();
                    if (_activeSessionId is Guid next && next != Guid.Empty) ActivateSession(next);
                    else
                    {
                        _activeSessionId = null;
                        if (_sessionContentHost is not null)
                        {
                            _sessionContentHost.Children.Clear();
                            _sessionContentHost.Children.Add(BuildEmptyState("没有打开的会话", "双击左侧连接以创建新会话。"));
                        }
                        RenderActiveSession();
                    }
                }
                SaveOpenSessions();
            }).ConfigureAwait(false);
        }
        catch
        {
            session.IsClosing = false;
            throw;
        }
    }

    private async Task ResizeRemoteTerminalAsync(WorkspaceSession session, int columns, int rows)
    {
        var shell = session.Shell;
        if (shell is null) return;
        var entered = false;
        try
        {
            await session.OutboundGate.WaitAsync(session.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            entered = true;
            if (ReferenceEquals(session.Shell, shell)) shell.Resize((uint)columns, (uint)rows);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (entered) session.OutboundGate.Release();
        }
    }

    private void UpdateSessionStatus(WorkspaceSession session, string status, string color)
    {
        _ = InvokeUiAsync(() =>
        {
            session.StatusText = status;
            session.StatusColor = color;
            RenderSession(session);
        });
    }

    private void RenderActiveSession()
    {
        var session = ActiveSession;
        var connected = session?.State == SessionState.Connected;
        var connecting = session?.State == SessionState.Connecting;
        if (_connectButton is not null)
        {
            _connectButton.TextContent = connecting ? FluentGlyphs.Sync : connected ? FluentGlyphs.Refresh : FluentGlyphs.Connect;
            _connectButton.Tooltip = connecting ? "正在连接" : connected ? "重新连接当前会话" : "连接所选会话";
            _connectButton.ClassList.Toggle("not-ready", session is null && _selectedProfile is null || connecting);
        }
        _disconnectButton?.ClassList.Toggle("not-ready", !connected && !connecting);
        _refreshFilesButton?.ClassList.Toggle("not-ready", !connected);
        _sendButton?.ClassList.Toggle("not-ready", !connected);
        if (_commandEditor is not null && session is null) _commandEditor.Value = "";
        RenderProtocolTools(session);
        if (session is null)
        {
            SetText(_sessionStatus, "就绪 - 请选择连接", "#9aa7b8");
            if (_sessionTabsRoot is not null) _sessionTabsRoot.IsVisible = false;
            if (_sessionToolTabsRoot is not null) _sessionToolTabsRoot.IsVisible = false;
            if (_sessionContextStatusRoot is not null) _sessionContextStatusRoot.IsVisible = false;
            if (_terminalToolbarRoot is not null) _terminalToolbarRoot.IsVisible = false;
            if (_commandEntryButton is not null) _commandEntryButton.IsVisible = false;
            if (_closeSplitButton is not null) _closeSplitButton.IsVisible = false;
            if (_bottomPanel is not null) _bottomPanel.IsVisible = false;
            if (_toolSplit is not null) _toolSplit.IsVisible = false;
            if (_secondaryToolHost is not null) _secondaryToolHost.IsVisible = false;
            if (_sftpTree is not null)
            {
                _sftpTree.ClearSelection();
                _sftpTree.Children.Clear();
            }
            if (_sftpPathText is not null) _sftpPathText.TextContent = "/";
            SetTrustButtons(false);
            ShowHistoryContext();
            return;
        }
        RenderSession(session);
    }

    private void RenderSession(WorkspaceSession session)
    {
        UpdateSessionTab(session);
        if (_activeSessionId != session.Id) return;
        var connected = session.State == SessionState.Connected;
        SetText(_details, session.DetailsText, session.PendingHostKey is null ? "#c6d0df" : "#f6c66b");
        SetTrustButtons(session.PendingHostKey is not null);
        if (session.PendingHostKey is not null)
        {
            ShowSecurityContext();
            if (session.ActiveTool != SessionToolKind.SessionInfo && session.SplitTool != SessionToolKind.SessionInfo)
                session.ActiveTool = SessionToolKind.SessionInfo;
        }
        else ShowHistoryContext();
        RenderSftpTree(session);
        RenderToolLayout(session);
        RenderActiveSessionControls(session);
    }

    private void RenderActiveSessionControls(WorkspaceSession session)
    {
        var connected = session.State == SessionState.Connected;
        var connecting = session.State == SessionState.Connecting;
        _connectButton?.ClassList.Toggle("not-ready", connecting);
        _disconnectButton?.ClassList.Toggle("not-ready", !connected && !connecting);
        _refreshFilesButton?.ClassList.Toggle("not-ready", !connected);
        _sendButton?.ClassList.Toggle("not-ready", !connected);
        if (_bottomPanel is not null)
            _bottomPanel.IsVisible = session.ActiveTool == SessionToolKind.Terminal && session.CommandPanelVisible;
        RenderProtocolTools(session);
    }

    private void UpdateSessionTab(WorkspaceSession session)
    {
        if (session.TabButton is null) return;
        session.TabButton.TextContent = session.DisplayName;
        var active = _activeSessionId == session.Id;
        session.TabButton.ClassList.Toggle("active", active);
        session.TabButton.ClassList.Toggle("connected", session.State == SessionState.Connected);
        session.TabButton.ClassList.Toggle("connecting", session.State == SessionState.Connecting);
        session.TabButton.ClassList.Toggle("failed", session.State == SessionState.Failed);
        session.TabButton.Style.Set("background", active ? "#20324b" : "#182230");
        session.TabButton.Style.Set("color", active ? "#ffffff" : "#dfe7f2");
        session.TabButton.Style.Set("border-bottom", active ? "2px solid #4f8cff" : "2px solid transparent");
        session.TabButton.Tooltip = session.StatusText;
    }

    private void RenderProtocolTools(WorkspaceSession? session)
    {
        if (_protocolToolsHost is null) return;
        _protocolToolsHost.Children.Clear();
        var profile = session?.Profile ?? _selectedProfile;
        if (profile is null) return;
        if ((profile.Capabilities & ConnectionCapabilities.FileBrowser) != 0)
            _protocolToolsHost.Children.Add(IconButton(FluentGlyphs.Folder, "打开 SFTP 文件", () =>
            {
                if (session is not null) ActivateSessionTool(SessionToolKind.Sftp);
            }));
        if (profile.Protocol == ConnectionProtocol.Rdp ||
            (profile.Capabilities & ConnectionCapabilities.RemoteDesktop) != 0 && profile.Port == 3389)
            _protocolToolsHost.Children.Add(IconButton(FluentGlyphs.Desktop, "启动 RDP", LaunchRdp));
        if (profile.Protocol == ConnectionProtocol.Vnc)
            _protocolToolsHost.Children.Add(IconButton(FluentGlyphs.Screen, "检测 VNC", () => _ = ProbeVncAsync()));
    }

    private Task RefreshSftpAsync() => ActiveSession is { } session
        ? RefreshSftpAsync(session)
        : Task.CompletedTask;

    private async Task RefreshSftpAsync(WorkspaceSession workspaceSession)
    {
        if (workspaceSession.Transport is null)
        {
            UpdateSessionStatus(workspaceSession, "请先建立 SSH 连接。", "#f6c66b");
            return;
        }
        var requestVersion = ++workspaceSession.SftpRequestVersion;
        workspaceSession.CurrentRemotePath = "/";
        workspaceSession.SelectedRemoteEntry = null;
        SftpTreeItem? root = null;
        await InvokeUiAsync(() =>
        {
            root = CreateSftpItem(workspaceSession, null, "/", "/");
            workspaceSession.SftpRoot = root;
            RenderSftpTree(workspaceSession);
        }).ConfigureAwait(false);
        if (root is null) return;
        await EnsureSftpChildrenAsync(root, requestVersion).ConfigureAwait(false);
        await InvokeUiAsync(() => root.Expand()).ConfigureAwait(false);
    }

    private bool TryGetActiveSftpSession(SftpTreeItem item, out WorkspaceSession workspaceSession)
    {
        if (SessionItemScope.Matches(_activeSessionId, item.SessionId) &&
            _sessions.TryGetValue(item.SessionId, out var session) &&
            ReferenceEquals(ActiveSession, session))
        {
            workspaceSession = session;
            return true;
        }
        workspaceSession = null!;
        return false;
    }

    private void RenderSftpTree(WorkspaceSession workspaceSession)
    {
        if (_activeSessionId != workspaceSession.Id || _sftpTree is null) return;
        var root = workspaceSession.State == SessionState.Connected ? workspaceSession.SftpRoot : null;
        if (!ReferenceEquals(root?.ParentNode, _sftpTree) || root is null && _sftpTree.Children.Count > 0)
        {
            _sftpTree.ClearSelection();
            _sftpTree.Children.Clear();
            if (root is not null) _sftpTree.Children.Add(root);
        }
        if (_sftpPathText is not null)
            _sftpPathText.TextContent = root is null ? "/" : workspaceSession.CurrentRemotePath;
    }

    private static SftpTreeItem CreateSftpItem(WorkspaceSession workspaceSession, RemoteEntry? entry, string path, string label)
    {
        var item = new SftpTreeItem(workspaceSession.Id, entry, path, label);
        item.ClassList.Add(entry is null || entry.Kind == RemoteEntryKind.Directory ? "sftp-directory" : "sftp-file");
        var (icon, color) = ResolveSftpIcon(entry);
        item.LeadingIcon = icon;
        item.LeadingIconFontFamily = "Segoe Fluent Icons";
        item.LeadingIconColor = color;
        if (item.IsDirectory)
        {
            var placeholder = new TreeItem("正在加载...") { IsEnabled = false };
            placeholder.ClassList.Add("tree-placeholder");
            item.Placeholder = placeholder;
            item.Children.Add(placeholder);
        }
        item.Tooltip = path;
        return item;
    }

    private static (string Glyph, Color Color) ResolveSftpIcon(RemoteEntry? entry)
    {
        if (entry is null || entry.Kind == RemoteEntryKind.Directory)
            return (FluentGlyphs.Folder, Color.FromRgb(92, 164, 255));
        if (entry.Kind == RemoteEntryKind.SymbolicLink)
            return (FluentGlyphs.Link, Color.FromRgb(166, 139, 250));

        var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico")
            return (FluentGlyphs.ImageFile, Color.FromRgb(83, 201, 158));
        if (extension is ".zip" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".7z" or ".rar")
            return (FluentGlyphs.ArchiveFile, Color.FromRgb(231, 176, 74));
        if (extension is ".cs" or ".fs" or ".vb" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".go" or ".rs" or ".java" or ".c" or ".h" or ".cpp" or ".hpp" or ".sh" or ".ps1")
            return (FluentGlyphs.CodeFile, Color.FromRgb(107, 174, 255));
        if (extension is ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".conf" or ".config" or ".env")
            return (FluentGlyphs.SettingsFile, Color.FromRgb(142, 153, 172));
        if (extension is ".pem" or ".key" or ".pub" or ".crt" or ".cer" or ".pfx")
            return (FluentGlyphs.KeyFile, Color.FromRgb(239, 129, 136));
        if (extension is ".exe" or ".msi" or ".bin" or ".app" or ".deb" or ".rpm" ||
            string.IsNullOrEmpty(extension) && entry.Name is "bin" or "bash" or "sh" or "zsh")
            return (FluentGlyphs.ExecutableFile, Color.FromRgb(130, 214, 153));
        return (FluentGlyphs.File, Color.FromRgb(177, 190, 207));
    }

    private Task EnsureSftpChildrenAsync(SftpTreeItem item) => EnsureSftpChildrenAsync(item, null);

    private async Task EnsureSftpChildrenAsync(SftpTreeItem item, long? expectedRequestVersion)
    {
        if (item.ChildrenLoaded || item.IsLoading || !item.IsDirectory ||
            !TryGetActiveSftpSession(item, out var workspaceSession) || workspaceSession.Transport is not { } transport)
            return;
        item.IsLoading = true;
        var requestVersion = expectedRequestVersion ?? workspaceSession.SftpRequestVersion;
        try
        {
            var entries = await transport.ListAsync(item.Path, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            if (workspaceSession.SftpRequestVersion != requestVersion)
            {
                item.IsLoading = false;
                return;
            }
            await InvokeUiAsync(() =>
            {
                if (!SessionItemScope.Matches(_activeSessionId, item.SessionId) ||
                    workspaceSession.SftpRequestVersion != requestVersion)
                {
                    item.IsLoading = false;
                    return;
                }
                foreach (var entry in entries
                             .OrderByDescending(entry => entry.Kind == RemoteEntryKind.Directory)
                             .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var label = entry.Kind == RemoteEntryKind.Directory ? entry.Name + "/" : entry.Name;
                    item.Children.Add(CreateSftpItem(workspaceSession, entry, entry.FullPath, label));
                }
                if (item.Placeholder is not null) item.Children.Remove(item.Placeholder);
                item.Placeholder = null;
                item.ChildrenLoaded = true;
                item.IsLoading = false;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            item.IsLoading = false;
            UpdateSessionStatus(workspaceSession, $"SFTP: {SafeError(exception)}", "#fda4af");
        }
    }

    private void SelectSftpTreeItem()
    {
        if (_sftpTree?.SelectedItem is not SftpTreeItem item || !TryGetActiveSftpSession(item, out var workspaceSession)) return;
        workspaceSession.SelectedRemoteEntry = item.Entry;
        workspaceSession.CurrentRemotePath = item.IsDirectory ? item.Path : RemoteParent(item.Path);
        if (_sftpPathText is not null) _sftpPathText.TextContent = workspaceSession.CurrentRemotePath;
    }

    private void OpenSftpContextMenu(PointerEvent e)
    {
        if (_sftpTree is null) return;
        if (FindAncestor<SftpTreeItem>(e.Target as Element) is not { } item ||
            !SessionItemScope.Matches(_activeSessionId, item.SessionId)) return;
        _sftpTree.SelectItem(item);
        OpenSelectedSftpMenu(new Point(e.ClientX, e.ClientY));
        e.PreventDefault();
    }

    private void OpenSelectedSftpMenu() => OpenSelectedSftpMenu(null);

    private void OpenSelectedSftpMenu(Point? position)
    {
        if (_sftpTree?.SelectedItem is not SftpTreeItem item || !TryGetActiveSftpSession(item, out var workspaceSession)) return;
        var menu = new ContextMenu();
        menu.ClassList.Add("context-menu");
        if (item.IsDirectory)
        {
            menu.Children.Add(MenuCommand("刷新目录", () => _ = ReloadSftpDirectoryAsync(item)));
            menu.Children.Add(MenuCommand("新建文件夹", () => _ = CreateRemoteDirectoryAsync(item.Path)));
            menu.Children.Add(MenuCommand("上传文件到这里", () => _ = UploadRemoteFileAsync(item.Path)));
            menu.Children.Add(MenuCommand("粘贴", () => _ = PasteRemoteItemAsync(item.Path)));
            menu.Children.Add(new MenuSeparator());
        }
        if (item.Entry is not null)
        {
            if (!item.IsDirectory) menu.Children.Add(MenuCommand("下载到本地", () => _ = DownloadRemoteFileAsync(item)));
            menu.Children.Add(MenuCommand("重命名 / 移动", () => _ = RenameRemoteItemAsync(item)));
            menu.Children.Add(MenuCommand("复制", () => CopyRemoteItem(workspaceSession, item, cut: false)));
            menu.Children.Add(MenuCommand("剪切", () => CopyRemoteItem(workspaceSession, item, cut: true)));
            menu.Children.Add(MenuCommand("删除", () => _ = DeleteRemoteItemAsync(item)));
            menu.Children.Add(new MenuSeparator());
        }
        menu.Children.Add(MenuCommand("复制路径", () => _ = CopyRemotePathAsync(item.Path)));
        menu.Children.Add(MenuCommand("发送路径到终端", () => _ = WriteTerminalInputAsync(workspaceSession, QuoteShellPath(item.Path))));
        OpenContextMenu(menu, position ?? MenuPointFor(item));
    }

    private async Task ReloadSftpDirectoryAsync(SftpTreeItem item)
    {
        if (!SessionItemScope.Matches(_activeSessionId, item.SessionId)) return;
        item.ChildrenLoaded = false;
        item.Children.Clear();
        item.Placeholder = new TreeItem("正在加载...") { IsEnabled = false };
        item.Children.Add(item.Placeholder);
        await EnsureSftpChildrenAsync(item).ConfigureAwait(false);
        await InvokeUiAsync(() => item.Expand()).ConfigureAwait(false);
    }

    private Task CreateRemoteDirectoryAsync() => CreateRemoteDirectoryAsync(GetRemoteTargetDirectory(ActiveSession));

    private async Task CreateRemoteDirectoryAsync(string? directory)
    {
        var workspaceSession = ActiveSession;
        if (_root is null || workspaceSession?.Transport is not { } transport || directory is null) return;
        var name = await WorkspaceDialogs.PromptAsync(_root, "新建远程目录", placeholder: "目录名称");
        if (!IsValidRemoteName(name)) return;
        try
        {
            await transport.CreateDirectoryAsync(RemoteChild(directory, name!), workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            UpdateSessionStatus(workspaceSession, $"新建目录失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private Task UploadRemoteFileAsync() => UploadRemoteFileAsync(GetRemoteTargetDirectory(ActiveSession));

    private async Task UploadRemoteFileAsync(string? directory)
    {
        var workspaceSession = ActiveSession;
        if (_root is null || workspaceSession?.Transport is not { } transport || directory is null) return;
        var localPath = await WorkspaceDialogs.PromptAsync(_root, "上传本地文件", placeholder: "本地文件完整路径");
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            UpdateSessionStatus(workspaceSession, "本地文件不存在。", "#f6c66b");
            return;
        }
        try
        {
            await using var source = File.OpenRead(localPath);
            await transport.UploadAsync(source, RemoteChild(directory, Path.GetFileName(localPath)), overwrite: false,
                workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            UpdateSessionStatus(workspaceSession, $"上传失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private async Task DownloadRemoteFileAsync(SftpTreeItem item)
    {
        if (_root is null || !TryGetActiveSftpSession(item, out var workspaceSession) ||
            workspaceSession.Transport is not { } transport || item.Entry is null) return;
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", item.Entry.Name);
        var localPath = await WorkspaceDialogs.PromptAsync(_root, "下载远程文件", defaultPath, "本地保存完整路径");
        if (string.IsNullOrWhiteSpace(localPath)) return;
        var temporaryPath = localPath + ".termsquared-part";
        try
        {
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using (var destination = File.Create(temporaryPath))
                await transport.DownloadAsync(item.Path, destination, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            File.Move(temporaryPath, localPath, overwrite: true);
            UpdateSessionStatus(workspaceSession, $"已下载到 {localPath}", "#7ee2ae");
        }
        catch (Exception exception)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            UpdateSessionStatus(workspaceSession, $"下载失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private async Task RenameRemoteItemAsync(SftpTreeItem item)
    {
        if (_root is null || item.Entry is null || !TryGetActiveSftpSession(item, out var workspaceSession) ||
            workspaceSession.Transport is not { } transport) return;
        var destination = await WorkspaceDialogs.PromptAsync(_root, "重命名或移动", item.Path, "远程完整路径");
        if (string.IsNullOrWhiteSpace(destination) || destination == item.Path || !destination.StartsWith('/')) return;
        try
        {
            await transport.RenameAsync(item.Path, destination, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            UpdateSessionStatus(workspaceSession, $"移动失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private void CopyRemoteItem(WorkspaceSession workspaceSession, SftpTreeItem item, bool cut)
    {
        if (item.Entry is null || !SessionItemScope.Matches(_activeSessionId, item.SessionId) ||
            workspaceSession.Id != item.SessionId) return;
        _remoteClipboard = new RemoteClipboardItem(workspaceSession.Id, item.Path, item.Entry.Name, item.IsDirectory, cut);
        UpdateSessionStatus(workspaceSession, cut ? "已剪切远程项目，选择目标目录后粘贴。" : "已复制远程项目，选择目标目录后粘贴。", "#8fb6ff");
    }

    private async Task PasteRemoteItemAsync(string destinationDirectory)
    {
        var clipboard = _remoteClipboard;
        if (clipboard is null || !_sessions.TryGetValue(clipboard.SessionId, out var workspaceSession) ||
            workspaceSession.Transport is not { } transport || ActiveSession?.Id != clipboard.SessionId) return;
        var destination = RemoteChild(destinationDirectory, clipboard.Name);
        if (destination == clipboard.Path || destination.StartsWith(clipboard.Path.TrimEnd('/') + '/', StringComparison.Ordinal))
        {
            UpdateSessionStatus(workspaceSession, "目标路径不能位于源目录内部。", "#f6c66b");
            return;
        }
        try
        {
            if (clipboard.Cut)
                await transport.RenameAsync(clipboard.Path, destination, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            else if (clipboard.IsDirectory)
                await CopyRemoteDirectoryAsync(transport, clipboard.Path, destination, 0, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            else
                await transport.CopyFileAsync(clipboard.Path, destination, overwrite: false,
                    workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            if (clipboard.Cut) _remoteClipboard = null;
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            UpdateSessionStatus(workspaceSession, $"粘贴失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private static async Task CopyRemoteDirectoryAsync(
        SshSession transport,
        string source,
        string destination,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= 64) throw new InvalidDataException("Remote directory nesting is too deep.");
        await transport.CreateDirectoryAsync(destination, cancellationToken).ConfigureAwait(false);
        foreach (var entry in await transport.ListAsync(source, cancellationToken).ConfigureAwait(false))
        {
            var target = RemoteChild(destination, entry.Name);
            if (entry.Kind == RemoteEntryKind.Directory)
                await CopyRemoteDirectoryAsync(transport, entry.FullPath, target, depth + 1, cancellationToken).ConfigureAwait(false);
            else if (entry.Kind == RemoteEntryKind.File)
                await transport.CopyFileAsync(entry.FullPath, target, overwrite: false, cancellationToken).ConfigureAwait(false);
            else
                throw new NotSupportedException($"Remote entry type '{entry.Kind}' cannot be copied safely.");
        }
    }

    private async Task DeleteRemoteItemAsync(SftpTreeItem item)
    {
        if (_root is null || item.Entry is null || !TryGetActiveSftpSession(item, out var workspaceSession) ||
            workspaceSession.Transport is not { } transport ||
            !await WorkspaceDialogs.ConfirmAsync(_root, "删除远程项目", $"永久删除 {item.Path}？\n目录将递归删除，此操作无法撤销。")) return;
        try
        {
            if (item.IsDirectory)
                await DeleteRemoteDirectoryAsync(transport, item.Path, 0, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            else
                await transport.RemoveFileAsync(item.Path, workspaceSession.ConnectionLifetime?.Token ?? _lifetime.Token).ConfigureAwait(false);
            await RefreshSftpAsync(workspaceSession).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            UpdateSessionStatus(workspaceSession, $"删除失败: {SafeError(exception)}", "#fda4af");
        }
    }

    private static async Task DeleteRemoteDirectoryAsync(
        SshSession transport,
        string path,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= 64) throw new InvalidDataException("Remote directory nesting is too deep.");
        foreach (var entry in await transport.ListAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == RemoteEntryKind.Directory)
                await DeleteRemoteDirectoryAsync(transport, entry.FullPath, depth + 1, cancellationToken).ConfigureAwait(false);
            else
                await transport.RemoveFileAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
        }
        await transport.RemoveDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyRemotePathAsync(string path)
    {
        if (_window is not null) await _window.SetClipboardTextAsync(path).ConfigureAwait(false);
    }

    private static string? GetRemoteTargetDirectory(WorkspaceSession? workspaceSession)
    {
        if (workspaceSession is null) return null;
        return workspaceSession.SelectedRemoteEntry is { Kind: RemoteEntryKind.Directory } directory
            ? directory.FullPath
            : workspaceSession.CurrentRemotePath;
    }

    private static string RemoteChild(string parent, string name) =>
        parent == "/" ? "/" + name : parent.TrimEnd('/') + "/" + name;

    private static string RemoteParent(string path)
    {
        var normalized = path.TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    private static bool IsValidRemoteName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && !name.Contains('/');

    private static string QuoteShellPath(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private async Task ProbeVncAsync()
    {
        var profile = ActiveSession?.Profile ?? _selectedProfile;
        if (profile is null)
        {
            await SetStatusAsync("请先选择一个连接，再从工具菜单检测 VNC", "#f6c66b").ConfigureAwait(false);
            return;
        }
        var host = profile.Host;
        const int port = 5900;
        await SetStatusAsync($"正在检测 VNC {host}:{port}...", "#f6c66b").ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var client = new RfbClient(tcp.GetStream(), leaveOpen: true);
            var info = await client.HandshakeAsync(timeout.Token).ConfigureAwait(false);
            await client.SetEncodingsAsync([0], timeout.Token).ConfigureAwait(false);
            await SetStatusAsync($"VNC 可用: {info.Name}  {info.Width}x{info.Height}  Raw", "#7ee2ae").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"VNC 检测失败: {SafeError(exception)}", "#ff9baa").ConfigureAwait(false);
        }
    }

    private void LaunchRdp()
    {
        var profile = ActiveSession?.Profile ?? _selectedProfile;
        if (profile is null)
        {
            _ = SetStatusAsync("请先选择一个连接，再从工具菜单启动 RDP", "#f6c66b");
            return;
        }
        var host = profile.Host;
        var username = profile.Username;
        const int port = 3389;
        try
        {
            using var process = _rdpLauncher.Launch(new RdpConnectionOptions(host, port,
                string.IsNullOrWhiteSpace(username) ? null : username));
            _ = SetStatusAsync($"已启动 {host}:{port} 的 RDP，凭据将由 Windows 安全提示输入", "#7ee2ae");
        }
        catch (Exception exception)
        {
            _ = SetStatusAsync($"RDP 启动失败: {SafeError(exception)}", "#ff9baa");
        }
    }

    private async Task DisconnectSessionAsync(WorkspaceSession workspaceSession)
    {
        await workspaceSession.LifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectSessionCoreAsync(workspaceSession, preparingConnection: false).ConfigureAwait(false);
        }
        finally
        {
            workspaceSession.LifecycleGate.Release();
        }
    }

    private async Task DisconnectSessionCoreAsync(WorkspaceSession workspaceSession, bool preparingConnection)
    {
        var shell = workspaceSession.Shell;
        var transport = workspaceSession.Transport;
        var reader = workspaceSession.ReaderTask;
        var hadSession = shell is not null || transport is not null;
        workspaceSession.Generation++;
        workspaceSession.Shell = null;
        workspaceSession.Transport = null;
        workspaceSession.ReaderTask = Task.CompletedTask;
        var connectionLifetime = workspaceSession.ConnectionLifetime;
        workspaceSession.ConnectionLifetime = null;
        if (connectionLifetime is not null)
        {
            await connectionLifetime.CancelAsync().ConfigureAwait(false);
            connectionLifetime.Dispose();
        }
        if (shell is not null) await shell.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(reader, Task.CompletedTask))
        {
            try { await reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
        if (preparingConnection) return;
        workspaceSession.State = SessionState.Disconnected;
        workspaceSession.StatusText = $"已断开 {workspaceSession.Profile.Host}:{workspaceSession.Profile.Port}";
        workspaceSession.StatusColor = "#9aa7b8";
        workspaceSession.PendingHostKey = null;
        workspaceSession.PendingHostKeyPromptId = null;
        await InvokeUiAsync(() =>
        {
            if (hadSession) AddHistoryEntry(workspaceSession.Profile, "已断开", "#9aa7b8");
            RenderSession(workspaceSession);
        }).ConfigureAwait(false);
    }

    private Task SetStatusAsync(string status, string color) =>
        InvokeUiAsync(() => SetText(_sessionStatus, status, color));

    private Task InvokeUiAsync(Action action)
    {
        var window = _window;
        return window is null || window.IsClosed ? Task.CompletedTask : window.Dispatcher.InvokeAsync(action);
    }

    private void SetTrustButtons(bool enabled)
    {
        if (_trustOnceButton is not null) _trustOnceButton.IsEnabled = enabled;
        if (_trustStoreButton is not null) _trustStoreButton.IsEnabled = enabled;
        if (_hostKeyApproval is not null) _hostKeyApproval.IsVisible = enabled;
    }

    private void AddHistoryEntry(ConnectionProfile profile, string result, string color)
    {
        if (_historyItems is null) return;
        if (!_hasHistory)
        {
            _historyItems.Children.Clear();
            _hasHistory = true;
        }

        var item = Panel("", "column", "100%", "72px");
        item.ClassList.Add("history-item");
        item.Style.Set("padding", "9px 10px");
        item.Style.Set("gap", "4px");
        var title = Caption(profile.Name, "#dfe7f2");
        title.Style.Set("font-weight", "700");
        item.Children.Add(title);
        item.Children.Add(Caption($"{profile.Username}@{profile.Host}:{profile.Port}", "#8290a3"));
        item.Children.Add(Caption($"{DateTime.Now:HH:mm:ss}  {result}", color));
        _historyItems.Children.Insert(0, item);
    }

    private void ShowHistoryContext()
    {
        SetRightContext(FluentGlyphs.History, "历史记录", "本次运行", history: true);
    }

    private void ShowSecurityContext()
    {
        SetRightContext(FluentGlyphs.Lock, "安全确认", "主机密钥", security: true);
    }

    private void SetRightContext(
        string glyph,
        string title,
        string subtitle,
        bool history = false,
        bool security = false)
    {
        if (_rightPanelIcon is not null) _rightPanelIcon.Glyph = glyph;
        SetText(_rightPanelTitle, title, "#e7edf5");
        SetText(_rightPanelSubtitle, subtitle, "#718096");
        if (_historyPanel is not null) _historyPanel.IsVisible = history;
        if (_securityPanel is not null) _securityPanel.IsVisible = security;
    }

    private void ApplyConnectionFilter()
    {
        var query = _connectionFilter?.Value.Trim() ?? "";
        var visible = 0;
        foreach (var entry in _connectionItems)
        {
            var profile = entry.Value.Profile;
            var matches = query.Length == 0 ||
                          entry.Value.TextContent.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          (profile.Username?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            entry.Value.IsVisible = matches;
            if (matches) visible++;
        }
        if (_connectionEmptyState is not null)
        {
            _connectionEmptyState.TextContent = visible == 0
                ? (_profiles.Count == 0 ? "未发现连接配置。" : "没有匹配的连接。")
                : "";
            _connectionEmptyState.IsVisible = visible == 0;
        }
    }

    private void RequestDisconnect()
    {
        var session = ActiveSession;
        if (session is null || session.State is not SessionState.Connected and not SessionState.Connecting)
        {
            _ = SetStatusAsync("当前没有活动会话", "#9aa7b8");
            return;
        }
        _ = DisconnectSessionAsync(session);
    }

    private void RestoreHiddenConnections()
    {
        _workspaceSettings.RestoreHiddenConnections();
        RebuildConnectionTree();
        _ = SetStatusAsync("已恢复从工作区移除的连接", "#7ee2ae");
    }

    private void RequestConnect()
    {
        if (ActiveSession is { } session)
        {
            if (session.State == SessionState.Connecting) return;
            _ = ConnectAsync(session, HostKeyDecision.Reject);
            return;
        }
        if (_selectedProfile is null)
        {
            _ = SetStatusAsync("请先从左侧选择一个连接", "#f6c66b");
            return;
        }
        if (_connectionTree?.SelectedItem is ConnectionProfileTreeItem item)
            _ = OpenSessionAsync(item.Profile, displayName: item.TextContent,
                forceNew: item.WorkspaceId != item.Profile.Id.ToString("D"), workspaceId: item.WorkspaceId);
        else
            _ = OpenSessionAsync(_selectedProfile);
    }


    private void ToggleLeftSidebar()
    {
        if (_window?.ClientSize.Width < 760)
        {
            _ = SetStatusAsync("当前窗口过窄，放大后可显示连接侧栏", "#f6c66b");
            return;
        }
        _leftSidebarRequested = !_leftSidebarRequested;
        ApplyResponsiveLayout(_window!.ClientSize);
    }

    private void ApplyResponsiveLayout(Square.Graphics.Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var showLeft = _leftSidebarRequested && size.Width >= 760;
        if (_leftSidebar is not null) _leftSidebar.IsVisible = showLeft;
        if (_leftSplitter is not null) _leftSplitter.IsVisible = showLeft;
    }

    private static MenuItem MenuCommand(string title, Action action) => new()
    {
        TextContent = title,
        Command = _ => action()
    };

    private static void SetText(Text? text, string value, string color)
    {
        if (text is null) return;
        text.TextContent = value;
        text.Style.Set("color", color);
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "operation cancelled",
        SocketException => "network connection failed",
        ArgumentException => exception.Message,
        InvalidDataException => exception.Message,
        _ when exception.GetType().Name == "SshAuthenticationException" => "authentication failed",
        _ => exception.GetType().Name
    };

    private static Button ActionButton(string text, Action action, bool compact = false, string? className = null)
    {
        var button = new Button(text);
        button.Style.Set("width", compact ? "auto" : "100%");
        button.Style.Set("height", compact ? "30px" : "36px");
        button.Style.Set("padding", compact ? "3px 10px" : "6px 12px");
        if (!string.IsNullOrWhiteSpace(className)) button.ClassList.Add(className);
        button.AddEventListener(StandardEvents.Click, action);
        return button;
    }

    private static Button IconButton(string glyph, string tooltip, Action action, string? className = null)
    {
        var button = ActionButton(glyph, action, compact: true, className: className);
        button.ClassList.Add("icon-button");
        button.Style.Set("width", "34px");
        button.Style.Set("height", "30px");
        button.Style.Set("padding", "0");
        button.Style.Set("font-family", "'Segoe Fluent Icons', 'Segoe MDL2 Assets'");
        button.Style.Set("font-size", "16px");
        button.Tooltip = tooltip;
        return button;
    }

    private static View Panel(string background, string direction, string width, string height)
    {
        var panel = new View();
        panel.Style.Set("display", "flex");
        panel.Style.Set("flex-direction", direction);
        panel.Style.Set("width", width);
        panel.Style.Set("height", height);
        if (!string.IsNullOrWhiteSpace(background)) panel.Style.Set("background", background);
        panel.Style.Set("gap", "12px");
        return panel;
    }

    private static Text Caption(string value, string color, string height = "auto", string padding = "0")
    {
        var text = new Text(value) { FontSize = 13 };
        text.Style.Set("color", color);
        text.Style.Set("height", height);
        text.Style.Set("padding", padding);
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var session in _sessions.Values.ToArray())
        {
            try
            {
                DisconnectSessionAsync(session).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
        try
        {
            _broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _remoteOperations.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }
        _rdpLauncher.Dispose();
        _knownHosts.Dispose();
        _lifetime.Dispose();
    }
}
