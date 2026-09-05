using Square.CSS.Engine;
using Square.Controls;
using Square.UI;
using TermSquared.App;
using TermSquared.App.Components;

namespace TermSquared.App.Tests;

public sealed class WorkspaceLayoutTests
{
    [Fact]
    public void WorkspaceExposesSessionToolTabsSplitHostsAndContextStatusBar()
    {
        var page = new WorkspacePage();
        page.BuildElementTree();

        Assert.Equal("终端", page.TerminalToolButton.TextContent);
        Assert.Equal("SFTP 文件", page.SftpToolButton.TextContent);
        Assert.Equal("映射端口", page.PortForwardingToolButton.TextContent);
        Assert.Equal("会话", page.SessionInfoToolButton.TextContent);
        Assert.IsType<Splitter>(page.ToolSplit);
        Assert.NotNull(page.PrimaryToolHost);
        Assert.NotNull(page.SecondaryToolHost);
        Assert.NotNull(page.SessionStatus);
        Assert.IsType<InspectorSidebar>(page.RightSidebarRoot);
        Assert.IsType<SessionInfoPanel>(page.SessionInfoRoot);
        Assert.Equal("/", page.SftpPathInput.Value);
        Assert.IsType<VirtualList>(page.SftpFileList);
        Assert.NotNull(page.SftpBackButton);
        Assert.NotNull(page.SftpForwardButton);
        Assert.NotNull(page.SftpUpButton);
        Assert.Contains(Descendants(page), element => element.ClassList.Contains("sftp-toolbar"));
        Assert.Contains(Descendants(page), element => element.ClassList.Contains("sftp-list-header"));
        Assert.DoesNotContain(Descendants(page.RightSidebarRoot), element => element is Tree);
        Assert.True(page.RefreshSftpButton.ClassList.Contains("sftp-icon-button"));
        Assert.Equal("多行命令", page.CommandEntryButton.TextContent);
        Assert.DoesNotContain(Descendants(page), element => element.ClassList.Contains("status-chip"));
    }

    [Fact]
    public void MovingSftpToolPreservesItsScopedLayoutStyles()
    {
        var page = new WorkspacePage();
        page.BuildElementTree();
        try
        {
            var tool = page.RightSidebarRoot;
            if (tool.ParentNode is Element parent) parent.Children.Remove(tool);
            page.PrimaryToolHost.Children.Clear();
            page.PrimaryToolHost.Children.Add(tool);

            CssStyleReconciler.Flush();

            Assert.Equal("row", page.RefreshSftpButton.Parent!.Style.Get("flex-direction"));
            Assert.Equal("26px", page.RefreshSftpButton.Style.Get("width"));
            Assert.Equal("flex", page.SftpPathInput.Parent!.Style.Get("display"));
        }
        finally
        {
            CssStyleReconciler.UnregisterScopesForTree(page);
        }
    }

    private static IEnumerable<Element> Descendants(Element root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
