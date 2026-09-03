using System.Text.Json;
using Clicky.Core;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class ContextBudgetTests
{
    [Fact]
    public void DropsOldExchangesAsWholeGroupsWithoutOrphaningToolResults()
    {
        var messages = new List<ChatMessage> { new("system", "Follow the user's intent."), new("user", "Find the requested information, do not send messages.") };
        for (var n = 0; n < 12; n++)
        {
            messages.Add(new("assistant", "Reading source " + n, ToolCalls: [new("call" + n, "files.read", "{\"path\":\"file" + n + "\"}")]));
            messages.Add(new("tool", new string('x', 6000), ToolCallId: "call" + n));
        }
        var fit = ContextBudget.Fit(new(messages, [ToolDiscovery.SearchDefinition]), 8192);
        Assert.True(ContextBudget.EstimateRequest(fit) <= 8192);
        Assert.Contains(fit.Messages, m => m.Role == "user" && m.Content == messages[1].Content);
        Assert.Contains(fit.Messages, m => m.Role == "tool" && m.ToolCallId == "call11");
        Assert.True(fit.Messages.Count < messages.Count);
        Assert.Equal("system", fit.Messages[0].Role);
        var system = Assert.Single(fit.Messages, m => m.Role == "system");
        Assert.StartsWith(messages[0].Content, system.Content);
        Assert.Contains("Earlier conversation rounds were omitted", system.Content);
        var calls = fit.Messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id).Order().ToArray();
        var results = fit.Messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId).Order().ToArray();
        Assert.Equal(calls, results);
    }

    [Fact]
    public void CompactionWithoutOriginalSystemAddsOnlyOneLeadingNotice()
    {
        var current = new ChatMessage("user", "Keep this request unchanged.");
        var fit = ContextBudget.Fit(new([new("user", new string('x', 24000)), new("assistant", "Old response"), current]));
        Assert.Equal("system", fit.Messages[0].Role);
        Assert.Single(fit.Messages, m => m.Role == "system");
        Assert.Contains("Earlier conversation rounds were omitted", fit.Messages[0].Content);
        Assert.Contains(current, fit.Messages);
        Assert.True(ContextBudget.EstimateRequest(fit) <= 8192);
    }

    [Fact]
    public void OversizedCurrentIntentFailsInsteadOfBeingSilentlyCut()
    {
        var current = "Keep every instruction intact. " + new string('x', 60000);
        var error = Assert.Throws<ContextBudgetExceededException>(() => ContextBudget.Fit(new([new("system", "Required"), new("user", current)])));
        Assert.Contains("Shorten the prompt", error.Message);
    }

    [Fact]
    public void CurrentImagesAndUserMessageRemainIntact()
    {
        var user = new ChatMessage("user", "Explain this screen without clicking.", [new("AA==")]);
        var fit = ContextBudget.Fit(new([new("system", "Guide only."), new("user", new string('z', 20000)), new("assistant", "Old"), user]));
        Assert.Contains(user, fit.Messages);
        Assert.Same(user.Images, fit.Messages.Last(m => m.Role == "user").Images);
        Assert.True(ContextBudget.EstimateRequest(fit) <= 8192);
    }

    [Fact]
    public void MissingToolResultsAndStandaloneResultsAreNotForwarded()
    {
        var fit = ContextBudget.Fit(new([
            new("system", "Required"),
            new("assistant", "", ToolCalls: [new("missing", "files.read", "{}")] ),
            new("tool", "orphan", ToolCallId: "different"),
            new("user", "Current task")
        ]));
        Assert.DoesNotContain(fit.Messages, m => m.Role == "tool" || m.ToolCalls?.Count > 0);
    }

    [Fact]
    public void NewestToolExchangeKeepsCallArgumentsAndCompressesOnlyResultText()
    {
        const string arguments = "{\"path\":\"report.txt\",\"offset\":12000}";
        var fit = ContextBudget.Fit(new([
            new("system", "Required"), new("user", "Summarize this report"),
            new("assistant", "Reading", ToolCalls: [new("latest", "files.read", arguments)]),
            new("tool", new string('a', 24000), ToolCallId: "latest")
        ], [ToolDiscovery.SearchDefinition]));
        Assert.Equal(arguments, fit.Messages.Single(m => m.ToolCalls?.Count > 0).ToolCalls![0].Arguments);
        Assert.Contains("Excerpt truncated", fit.Messages.Single(m => m.Role == "tool").Content);
        Assert.True(ContextBudget.EstimateRequest(fit) <= 8192);
    }

    [Fact]
    public void ToolExcerptPreservesSuccessAndValidJsonWithinSixThousandCharacters()
    {
        var excerpt = ContextBudget.ToolResultExcerpt(new(false, "Failure details", new
        {
            text = new string('"', 25000)
        }));
        Assert.True(excerpt.Length <= 6000);
        using var json = JsonDocument.Parse(excerpt);
        Assert.False(json.RootElement.GetProperty("Success").GetBoolean());
        Assert.True(json.RootElement.GetProperty("Truncated").GetBoolean());
    }

    [Fact]
    public void ToolSchemasShareTheSameBoundedRequestBudget()
    {
        var tools = Enumerable.Range(0, 150).Select(i => new ToolDefinition("tool" + i, new string('a', 500), JsonSchema.Parse("{\"type\":\"object\"}"))).ToArray();
        var fit = ContextBudget.Fit(new([new("system", "Required"), new("user", "Read something")], tools));
        Assert.True(fit.Tools!.Count < tools.Length);
        Assert.True(ContextBudget.EstimateRequest(fit) <= 8192);
    }
}
