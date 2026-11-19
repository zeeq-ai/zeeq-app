namespace Zeeq.Core.Documents;

/// <summary>Identifies the cached MCP document index shared by list and write paths.</summary>
public static class McpDocumentIndexCache
{
    /// <summary>Returns the current index cache key for an organization and library.</summary>
    public static string Key(string organizationId, string libraryId) =>
        $"mcp:list_documents:v2:{organizationId}:{libraryId}";
}
