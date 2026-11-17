namespace Zeeq.Core.Documents;

/// <summary>Insert-only agent document result.</summary>
public closed record CreateDocumentResult
{
    /// <summary>The document was created.</summary>
    public sealed record Created(LibraryDocument Document) : CreateDocumentResult;

    /// <summary>A document already occupies this path.</summary>
    public sealed record Exists(LibraryDocument Document) : CreateDocumentResult;
}

/// <summary>Conditional replacement result.</summary>
public closed record ReplaceContentResult
{
    /// <summary>The replacement was saved.</summary>
    public sealed record Applied(LibraryDocument Document) : ReplaceContentResult;

    /// <summary>The identity or hash changed.</summary>
    public sealed record Stale(LibraryDocument Document) : ReplaceContentResult;

    /// <summary>The document is owned by a sync source.</summary>
    public sealed record NotEditable(LibraryDocument Document) : ReplaceContentResult;

    /// <summary>The document no longer exists.</summary>
    public sealed record NotFound : ReplaceContentResult;
}
