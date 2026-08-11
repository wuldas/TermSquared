using Square.Controls;
using Square.UI;
using TermSquared.App;

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
        Assert.Equal("多行命令", page.CommandEntryButton.TextContent);
        Assert.DoesNotContain(Descendants(page), element => element.ClassList.Contains("status-chip"));
    }

    private static IEnumerable<Element> Descendants(Element root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
