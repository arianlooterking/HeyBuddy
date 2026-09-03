using Clicky.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class PersistenceGuidanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HeyBuddyPersistenceTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void RestartPausesPendingRunsAndPreservesCompletedHistory()
    {
        var first = new AppStore(root);
        var now = DateTimeOffset.UtcNow;
        foreach (var state in new[] { RunStatus.Queued, RunStatus.Running, RunStatus.AwaitingApproval, RunStatus.Completed })
            first.SaveRun(new(state.ToString(), "Original prompt", state, now, now, Actions: 2, Result: "Previous result"));
        first.AddMessage("Completed", "chat", "user", "Persistent message");
        var restarted = new AppStore(root);
        Assert.Equal(3, restarted.GetRuns().Count(r => r.Status == RunStatus.Paused));
        Assert.Equal(RunStatus.Completed, restarted.GetRuns().Single(r => r.Id == "Completed").Status);
        Assert.All(restarted.GetRuns(), r => { Assert.Equal("Original prompt", r.Prompt); Assert.Equal(2, r.Actions); });
        Assert.Equal("Persistent message", restarted.GetHistory().Single().Text);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("' OR 1=1 --")]
    public void HistorySearchTreatsQueryAsLiteralText(string query)
    {
        var store = new AppStore(root);
        store.AddMessage("s1", "chat", "user", "Ordinary text");
        store.AddMessage("s2", "chat", "user", "Literal " + query);
        Assert.Single(store.GetHistory(query));
        Assert.Equal("s2", store.GetHistory(query)[0].SessionId);
    }

    [Fact]
    public void BackupRestoresRealRecords()
    {
        var store = new AppStore(root);
        store.AddMessage("backup", "chat", "user", "Backup record");
        var destination = Path.Combine(root, "backup.db");
        store.Backup(destination);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT text FROM history";
        Assert.Equal("Backup record", query.ExecuteScalar());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/name")]
    [InlineData("C:\\escape")]
    [InlineData("..")]
    public void KnowledgeRejectsTraversalNames(string name)
    {
        var store = new KnowledgeStore(root);
        Assert.Throws<ArgumentException>(() => store.SaveSkill(name, "No", true));
    }

    [Fact]
    public void ProfileAndSkillUpdatesKeepPreviousVersions()
    {
        var store = new KnowledgeStore(root);
        store.SaveProfile("First");
        store.SaveProfile("Second");
        Assert.Equal("First", File.ReadAllText(Path.Combine(root, "Memory", "PROFILE.md.bak")));
        store.SaveSkill("research", "First skill", true);
        store.SaveSkill("research", "Second skill", true);
        Assert.Equal("First skill", File.ReadAllText(Path.Combine(root, "Skills", "research.md.bak")));
        Assert.Contains("Second skill", store.Context());
    }

    [Fact]
    public void TogglingASkillPreservesItsPriorContentInABackup()
    {
        var store = new KnowledgeStore(root);
        store.SaveSkill("research", "Enabled original", true);
        store.SaveSkill("research", "Disabled edit", false);
        Assert.Equal("Enabled original", File.ReadAllText(Path.Combine(root, "Skills", "research.md.bak")));
        Assert.DoesNotContain("Disabled edit", store.Context());
        Assert.False(store.GetSkills().Single().Enabled);
        store.SaveSkill("research", "Reenabled edit", true);
        Assert.Equal("Disabled edit", File.ReadAllText(Path.Combine(root, "Skills", "research.disabled.md.bak")));
        Assert.True(store.GetSkills().Single().Enabled);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("not JSON")]
    [InlineData("{\"kind\":\"point\"}")]
    [InlineData("[{\"kind\":\"click\",\"x\":0.5,\"y\":0.5}]")]
    [InlineData("[{\"kind\":\"point\",\"x\":1.5,\"y\":0.5}]")]
    public void MalformedOrExecutableGuidanceNeverBecomesADrawing(string json)
    {
        var parsed = GuidanceParser.Parse("Helpful text\n```guidance\n" + json + "\n```");
        Assert.Equal("Helpful text", parsed.Text);
        Assert.Empty(parsed.Commands);
    }

    [Fact]
    public void GuidanceOnlyAcceptsBoundedDrawingPrimitives()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 40).Select(i => "{\"kind\":\"point\",\"x\":0.2,\"y\":0.3}")) + "]";
        var parsed = GuidanceParser.Parse("```guidance\n" + json + "\n```");
        Assert.Equal(30, parsed.Commands.Count);
        Assert.All(parsed.Commands, c => Assert.Equal("point", c.Kind));
    }

    [Fact]
    public void ValidDrawingInAJsonFenceSupportsSmallLocalModelsWithoutHidingOtherJson()
    {
        var drawing = GuidanceParser.Parse("Here it is.\n```json\n[{\"kind\":\"circle\",\"x\":0.19,\"y\":0.45,\"label\":\"Increment counter\"}]\n```");
        Assert.Equal("Here it is.", drawing.Text);
        Assert.Single(drawing.Commands);
        Assert.Equal("circle", drawing.Commands[0].Kind);

        var ordinary = GuidanceParser.Parse("```json\n[{\"kind\":\"click\",\"x\":0.19,\"y\":0.45}]\n```");
        Assert.Empty(ordinary.Commands);
        Assert.Contains("kind", ordinary.Text);
    }

    [Theory]
    [InlineData("Where is the export button?", ScreenTurnKind.Locate)]
    [InlineData("Show me where to click", ScreenTurnKind.Locate)]
    [InlineData("What do you see on my screen?", ScreenTurnKind.Inspect)]
    [InlineData("Walk me through this app step by step", ScreenTurnKind.Walkthrough)]
    [InlineData("کدام دکمه را بزنم؟", ScreenTurnKind.Locate)]
    [InlineData("مرحله به مرحله یادم بده", ScreenTurnKind.Walkthrough)]
    [InlineData("Bu ekranda ne görüyorsun?", ScreenTurnKind.Inspect)]
    [InlineData("Bana adım adım öğret", ScreenTurnKind.Walkthrough)]
    [InlineData("Explain photosynthesis", ScreenTurnKind.None)]
    public void ScreenIntentRecognizesOwnerRequestsInSupportedLanguages(string request, ScreenTurnKind expected)
        => Assert.Equal(expected, ScreenTurnIntent.Classify(request));
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
