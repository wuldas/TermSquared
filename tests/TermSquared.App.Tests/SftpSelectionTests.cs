using Square.Controls;
using TermSquared.App;
using TermSquared.Core;

namespace TermSquared.App.Tests;

public sealed class SftpSelectionTests
{
    [Fact]
    public void SelectedEntryRemainsAvailableWhenItsVirtualRowIsNotRealized()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(index => new RemoteEntry(
                $"file-{index}.txt",
                $"/file-{index}.txt",
                RemoteEntryKind.File,
                index,
                DateTimeOffset.UnixEpoch))
            .ToArray();
        var list = new VirtualList();
        list.SetItemsSource(entries, static (entry, index) =>
            new SftpListItem(Guid.Empty, entry, index) { Marker = "" });

        list.SelectIndex(entries.Length - 1);

        Assert.Null(list.SelectedItem);
        Assert.Same(entries[^1], SftpSelection.GetSelectedEntry(list));
    }
}
