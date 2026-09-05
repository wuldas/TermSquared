using Square.Events;
using TermSquared.App.Components;

namespace TermSquared.App.Tests;

public sealed class ComponentEventContractTests
{
    [Fact]
    public void TopBarEmitsConnectRequestedWhenItsMenuCommandIsClicked()
    {
        var topBar = new TopBar();
        topBar.BuildElementTree();
        var emitted = 0;
        using var subscription = topBar.Listen(TopBar.ConnectRequestedEvent, () => emitted++);

        topBar.ConnectMenuItem.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(1, emitted);
    }

    [Fact]
    public void TerminalToolbarEmitsRefreshRequestedWhenItsRefreshButtonIsClicked()
    {
        var toolbar = new TerminalToolbar();
        toolbar.BuildElementTree();
        var emitted = 0;
        using var subscription = toolbar.Listen(TerminalToolbar.RefreshRequestedEvent, () => emitted++);

        toolbar.RefreshFilesButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(1, emitted);
    }

    [Fact]
    public void TerminalToolbarCanPublishAProtocolToolRequest()
    {
        var toolbar = new TerminalToolbar();
        toolbar.BuildElementTree();
        string? action = null;
        using var subscription = toolbar.Listen(TerminalToolbar.ProtocolActionRequestedEvent, e => action = e.Detail);

        toolbar.RequestProtocolAction("sftp");

        Assert.Equal("sftp", action);
    }

    [Fact]
    public void CommandPanelEmitsTheCurrentDraftWhenSendIsClicked()
    {
        var panel = new CommandPanel();
        panel.BuildElementTree();
        string? draft = null;
        using var subscription = panel.Listen(CommandPanel.SendRequestedEvent, e => draft = e.Detail);
        panel.CommandEditor.Value = "uname -a";

        panel.SendButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal("uname -a", draft);
    }

    [Fact]
    public void SftpBrowserEmitsRefreshRequestedWhenItsRefreshButtonIsClicked()
    {
        var browser = new InspectorSidebar();
        browser.BuildElementTree();
        var emitted = 0;
        using var subscription = browser.Listen(InspectorSidebar.RefreshRequestedEvent, () => emitted++);

        browser.RefreshSftpButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(1, emitted);
    }

    [Fact]
    public void SessionInfoEmitsTrustOnceWhenItsApprovalButtonIsClicked()
    {
        var panel = new SessionInfoPanel();
        panel.BuildElementTree();
        var emitted = 0;
        using var subscription = panel.Listen(SessionInfoPanel.TrustOnceRequestedEvent, () => emitted++);

        panel.TrustOnceButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(1, emitted);
    }

    [Fact]
    public void WorkspaceCenterEmitsTheSelectedTool()
    {
        var center = new WorkspaceCenter();
        center.BuildElementTree();
        SessionToolKind? selected = null;
        using var subscription = center.Listen(WorkspaceCenter.ToolSelectedEvent, e => selected = e.Detail);

        center.SftpToolButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(SessionToolKind.Sftp, selected);
    }

    [Fact]
    public void WorkspaceCenterEmitsToolSplitSizeChanges()
    {
        var center = new WorkspaceCenter();
        center.BuildElementTree();
        float? width = null;
        using var subscription = center.Listen(WorkspaceCenter.ToolSplitChangedEvent, e => width = e.Detail);
        center.ToolSplit.Value = 420;

        center.ToolSplit.DispatchEvent(StandardEvents.CreateInput());

        Assert.Equal(420, width);
    }

    [Fact]
    public void ConnectionSidebarEmitsAddRequestedWhenItsAddButtonIsClicked()
    {
        var sidebar = new ConnectionSidebar();
        sidebar.BuildElementTree();
        var emitted = 0;
        using var subscription = sidebar.Listen(ConnectionSidebar.AddConnectionRequestedEvent, () => emitted++);

        sidebar.AddConnectionButton.DispatchEvent(StandardEvents.CreateClick());

        Assert.Equal(1, emitted);
    }

    [Fact]
    public void SessionTabsCanPublishASelectedSessionRequest()
    {
        var tabs = new SessionTabs();
        tabs.BuildElementTree();
        var sessionId = Guid.NewGuid();
        Guid? selected = null;
        using var subscription = tabs.Listen(SessionTabs.SessionSelectedEvent, e => selected = e.Detail);

        tabs.RequestSessionSelected(sessionId);

        Assert.Equal(sessionId, selected);
    }
}
