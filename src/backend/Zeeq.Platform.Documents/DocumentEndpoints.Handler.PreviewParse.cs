using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Parsing;
using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Previews what the parse and snippet-indexing pipeline would extract from a document's
/// current content — title, keywords, headings, and composed section/code snippets — without
/// writing anything. Runs the same pure <see cref="MarkdownParser.Parse"/> /
/// <see cref="SnippetComposer.Compose"/> steps the write path uses, on demand, over the
/// document's already-persisted content.
/// </summary>
/// <remarks>
/// Shares <see cref="GetDocumentContentHandler"/>'s public-source vs. private/local branching
/// and effective-filter re-check via <see cref="DocumentContentResolvingHandler"/> — see that
/// base class's remarks for why the filter check exists.
/// </remarks>
public sealed class PreviewDocumentParseHandler(
    ILibraryDocumentStore store,
    IDocsPublicDocumentStore publicDocuments,
    IDocsPublicSourceStore publicSources,
    SnippetIndexingSettings snippetIndexingSettings
) : DocumentContentResolvingHandler(store, publicDocuments, publicSources), IEndpointHandler
{
    /// <summary>Handles the parse-preview request.</summary>
    public async Task<
        Results<Ok<DocumentParsePreviewResponse>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(string orgId, string name, string path, CancellationToken ct)
    {
        var resolution = await ResolveContentAsync(orgId, name, path, ct);

        if (resolution.Kind == DocumentResolutionKind.BadRequest)
        {
            return TypedResults.BadRequest(new DocumentError(resolution.ErrorMessage!));
        }

        if (resolution.Kind == DocumentResolutionKind.NotFound)
        {
            return TypedResults.NotFound();
        }

        var fileName = System.IO.Path.GetFileNameWithoutExtension(resolution.ResolvedPath!);
        var parsed = MarkdownParser.Parse(resolution.Content!, fileName);
        var snippets = SnippetComposer.Compose(parsed, snippetIndexingSettings);

        return TypedResults.Ok(
            LibraryEndpointMapping.ToParsePreviewResponse(resolution.ResolvedPath!, parsed, snippets)
        );
    }
}
