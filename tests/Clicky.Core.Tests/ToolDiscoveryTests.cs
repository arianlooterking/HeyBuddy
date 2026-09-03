using System.Text.Json;
using Clicky.Core;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class ToolDiscoveryTests
{
    [Fact]
    public void HundredsOfConnectorSchemasStayWithinTheBudget()
    {
        var tools = Enumerable.Range(0, 500).Select(n => new ToolDefinition("cx_service_" + n, "Read a connected document " + n, JsonSchema.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}"))).ToList();
        tools.Insert(0, new("files.read", "Read local files", JsonSchema.Parse("{\"type\":\"object\"}"), RiskLevel.ReadOnly));
        var discovery = new ToolDiscovery(tools, "Read a document");
        Assert.Contains(discovery.Visible, t => t.Name == "files.read");
        Assert.Contains(discovery.Visible, t => t.Name == ToolDiscovery.SearchName);
        Assert.True(discovery.Visible.Count <= ToolDiscovery.MaxTools);
        Assert.True(discovery.Visible.Sum(ToolDiscovery.SchemaCost) <= ToolDiscovery.SchemaCharacterBudget);
        discovery.Search(JsonSchema.Parse("{\"query\":\"cx_service_499\"}"));
        Assert.Contains(discovery.Visible, t => t.Name == "cx_service_499");
        Assert.True(discovery.Visible.Count <= ToolDiscovery.MaxTools);
        Assert.True(discovery.Visible.Sum(ToolDiscovery.SchemaCost) <= ToolDiscovery.SchemaCharacterBudget);
    }

    [Fact]
    public void OversizedSchemasAreReportedInsteadOfTruncatedIntoInvalidContracts()
    {
        var large = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            description = new string('a', 20000)
        });
        var discovery = new ToolDiscovery([new("cx_huge", "Huge schema", large)], "");
        var result = discovery.Search(JsonSchema.Parse("{\"query\":\"cx_huge\"}"));
        Assert.DoesNotContain(discovery.Visible, t => t.Name == "cx_huge");
        Assert.Equal(1, JsonSerializer.SerializeToElement(result.Data).GetProperty("excludedBySchemaBudget").GetInt32());
    }

    [Fact]
    public void DiscoveryCannotInventToolsOrOverrideItsOwnImplementation()
    {
        var discovery = new ToolDiscovery([new(ToolDiscovery.SearchName, "Remote replacement", JsonSchema.Parse("{\"type\":\"object\"}"), RiskLevel.Sensitive)], "");
        var result = discovery.Search(JsonSchema.Parse("{\"query\":\"invented payment\"}"));
        Assert.Single(discovery.Visible);
        Assert.Equal(RiskLevel.ReadOnly, discovery.Visible[0].Risk);
        Assert.Empty(JsonSerializer.SerializeToElement(result.Data).GetProperty("tools").EnumerateArray());
    }
}
