using TermSquared.App;

namespace TermSquared.App.Tests;

public sealed class SftpNavigationStateTests
{
    [Fact]
    public void NavigateBackForwardAndUpPreserveDirectoryHistory()
    {
        var navigation = new SftpNavigationState();

        Assert.True(navigation.NavigateTo("/home"));
        Assert.True(navigation.NavigateTo("/home/user"));
        Assert.Equal("/home/user", navigation.CurrentPath);

        Assert.True(navigation.GoBack());
        Assert.Equal("/home", navigation.CurrentPath);
        Assert.True(navigation.GoForward());
        Assert.Equal("/home/user", navigation.CurrentPath);
        Assert.True(navigation.GoUp());
        Assert.Equal("/home", navigation.CurrentPath);
    }

    [Fact]
    public void NavigationTargetsCanBeInspectedWithoutMutatingCurrentPath()
    {
        var navigation = new SftpNavigationState();
        navigation.NavigateTo("/home");
        navigation.NavigateTo("/home/user");

        Assert.Equal("/home", navigation.BackPath);
        Assert.Equal("/home", navigation.ParentPath);
        Assert.Equal("/home/user", navigation.CurrentPath);
    }
}
