using TermSquared.App;

namespace TermSquared.App.Tests;

public sealed class WorkspaceSettingsStoreTests
{
    [Fact]
    public void OpenSessionsRoundTripInTabOrderWithPerSessionToolLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "termsquared-workspace-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "workspace.json");
        try
        {
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var store = WorkspaceSettingsStore.Load(path);
            store.SaveOpenSessions(
            [
                new OpenSessionSettings(firstId, "profile-a", "alpha", SessionToolKind.Terminal, SessionToolKind.Sftp, 410),
                new OpenSessionSettings(secondId, "profile-b", "beta", SessionToolKind.PortForwarding, null, 360)
            ], secondId);

            var restored = WorkspaceSettingsStore.Load(path);

            Assert.Equal(secondId, restored.ActiveSessionId);
            Assert.Equal(
            [
                new OpenSessionSettings(firstId, "profile-a", "alpha", SessionToolKind.Terminal, SessionToolKind.Sftp, 410),
                new OpenSessionSettings(secondId, "profile-b", "beta", SessionToolKind.PortForwarding, null, 360)
            ], restored.OpenSessions);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RestorePlanConnectsOnlyThePreviouslyActiveTab()
    {
        var first = new OpenSessionSettings(Guid.NewGuid(), "profile-a", "alpha", SessionToolKind.Terminal, null, 360);
        var second = new OpenSessionSettings(Guid.NewGuid(), "profile-b", "beta", SessionToolKind.Sftp, null, 360);

        var plan = WorkspaceRestorePlanner.Create([first, second], second.SessionId);

        Assert.Collection(
            plan,
            entry => Assert.False(entry.ShouldConnect),
            entry => Assert.True(entry.ShouldConnect));
        Assert.Equal(second.SessionId, plan.Single(entry => entry.ShouldConnect).Settings.SessionId);
    }

    [Fact]
    public void RestorePlanFallsBackToLastValidTabWhenSavedActiveIdIsMissing()
    {
        var first = new OpenSessionSettings(Guid.NewGuid(), "profile-a", "alpha", SessionToolKind.Terminal, null, 360);
        var second = new OpenSessionSettings(Guid.NewGuid(), "profile-b", "beta", SessionToolKind.Sftp, null, 360);

        var plan = WorkspaceRestorePlanner.Create([first, second], Guid.NewGuid());

        Assert.Equal(second.SessionId, plan.Single(entry => entry.ShouldConnect).Settings.SessionId);
    }

    [Fact]
    public void RestorePlanDropsDuplicateIdsAndNormalizesInvalidToolLayout()
    {
        var sessionId = Guid.NewGuid();
        var invalid = new OpenSessionSettings(
            sessionId,
            "profile-a",
            "alpha",
            (SessionToolKind)999,
            SessionToolKind.Terminal,
            360);

        var plan = WorkspaceRestorePlanner.Create([invalid, invalid], sessionId);

        var restored = Assert.Single(plan).Settings;
        Assert.Equal(SessionToolKind.Terminal, restored.ActiveTool);
        Assert.Null(restored.SplitTool);
    }

    [Fact]
    public void SessionItemScopeRejectsItemsFromInactiveSessions()
    {
        var active = Guid.NewGuid();

        Assert.True(SessionItemScope.Matches(active, active));
        Assert.False(SessionItemScope.Matches(active, Guid.NewGuid()));
        Assert.False(SessionItemScope.Matches(null, active));
    }
}
