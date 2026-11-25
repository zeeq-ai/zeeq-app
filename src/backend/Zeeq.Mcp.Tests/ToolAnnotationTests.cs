using System.Reflection;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Tests;

/// <summary>Every MCP tool declares explicit behavior hints; the spec defaults are wrong for Zeeq.</summary>
public sealed class ToolAnnotationTests
{
    /// <summary>
    /// The full hint contract per tool. A new tool must add a row here, so its hints are a
    /// deliberate choice rather than the spec defaults.
    /// </summary>
    /// <remarks>
    /// NOTE: Read-only tools leave Destructive and Idempotent unset (null); the MCP spec only
    /// gives those hints meaning when ReadOnly is false. No Zeeq tool is open-world.
    /// </remarks>
    private static readonly Dictionary<
        string,
        (bool ReadOnly, bool? Destructive, bool? Idempotent)
    > ExpectedHints = new()
    {
        ["create_document"] = (false, false, false),
        ["update_document_by_diff"] = (false, true, false),
        ["expert_code_review"] = (false, false, false),
        ["get_review_findings"] = (true, null, null),
        ["triage_review_finding"] = (false, false, true),
        ["get_cart_findings"] = (true, null, null),
        ["get_quip"] = (true, null, null),
        ["list_libraries"] = (true, null, null),
        ["list_documents"] = (true, null, null),
        ["read_document_by_path"] = (true, null, null),
        ["search_documents"] = (true, null, null),
        ["search_sections"] = (true, null, null),
        ["search_code_snippets"] = (true, null, null),
    };

    private static IEnumerable<McpServerTool> AllTools() =>
        typeof(QuipTool)
            .Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t =>
                t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            )
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m));

    [Test]
    public async Task EveryTool_HasAnExpectedHintContract()
    {
        var names = AllTools().Select(t => t.ProtocolTool.Name).Order().ToArray();

        await Assert.That(names).IsEquivalentTo(ExpectedHints.Keys.Order().ToArray());
    }

    [Test]
    public async Task EveryTool_DeclaresItsExpectedHints_AndIsNotOpenWorld()
    {
        foreach (var tool in AllTools())
        {
            var name = tool.ProtocolTool.Name;
            var hints = tool.ProtocolTool.Annotations;
            var expected = ExpectedHints[name];

            await Assert.That(hints).IsNotNull().Because(name);
            await Assert.That(hints!.ReadOnlyHint).IsEqualTo(expected.ReadOnly).Because(name);
            await Assert.That(hints.DestructiveHint).IsEqualTo(expected.Destructive).Because(name);
            await Assert.That(hints.IdempotentHint).IsEqualTo(expected.Idempotent).Because(name);
            await Assert.That(hints.OpenWorldHint).IsFalse().Because(name);
        }
    }
}
