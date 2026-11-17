using System.Reflection;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Tests;

/// <summary>Every MCP tool declares explicit behavior hints; the spec defaults are wrong for Zeeq.</summary>
public sealed class ToolAnnotationTests
{
    private static readonly string[] MutatingTools =
    [
        "create_document",
        "update_document_by_diff",
        "expert_code_review",
    ];

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
    public async Task AllTools_DeclareExplicitHints_AndNoToolIsOpenWorld()
    {
        var tools = AllTools().ToList();

        await Assert.That(tools.Count).IsEqualTo(12);
        foreach (var tool in tools)
        {
            var hints = tool.ProtocolTool.Annotations;
            await Assert.That(hints?.ReadOnlyHint).IsNotNull();
            await Assert.That(hints?.OpenWorldHint).IsFalse();
        }
    }

    [Test]
    public async Task OnlyAuthoringAndReviewTools_AreNotReadOnly()
    {
        var writable = AllTools()
            .Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == false)
            .Select(t => t.ProtocolTool.Name)
            .Order()
            .ToArray();

        await Assert.That(writable).IsEquivalentTo(MutatingTools.Order().ToArray());
    }

    [Test]
    public async Task UpdateDocumentByDiff_IsDestructive_AndCreateDocumentIsAdditive()
    {
        var byName = AllTools()
            .ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.Annotations!);

        await Assert.That(byName["update_document_by_diff"].DestructiveHint).IsTrue();
        await Assert.That(byName["create_document"].DestructiveHint).IsFalse();
        await Assert.That(byName["create_document"].IdempotentHint).IsFalse();
    }
}
