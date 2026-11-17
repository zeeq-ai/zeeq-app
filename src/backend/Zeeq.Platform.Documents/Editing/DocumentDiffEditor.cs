using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TextDiff;
using TextDiff.Exceptions;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents.Editing;

/// <summary>A guard receives the current edit and the rest of the pipeline.</summary>
public interface IDiffGuard
{
    /// <summary>Validates or transforms the edit before invoking the next guard.</summary>
    ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    );
}

/// <summary>Continuation in a diff guard pipeline.</summary>
public delegate ValueTask<DiffOutcome> DiffDelegate(DiffContext context);

/// <summary>State of one edit while it travels through the guard chain.</summary>
public sealed class DiffContext(LibraryDocument document, string baseHash, string diff)
{
    /// <summary>Current document as read from storage.</summary>
    public LibraryDocument Document { get; } = document;

    /// <summary>Hash supplied by the editor.</summary>
    public string BaseHash { get; } = baseHash;

    /// <summary>Proposed unified diff.</summary>
    public string Diff { get; } = diff;
    internal List<List<DiffLine>> Hunks { get; set; } = [];
    internal string[] Lines { get; set; } = [];
    internal List<int> Positions { get; } = [];
    internal List<DiffMatch> Matches { get; } = [];
}

internal readonly record struct DiffLine(char Prefix, string Text);

internal readonly record struct DiffMatch(int Count, int First, bool AtStart, bool AtEnd);

/// <summary>Runs the configured guards in registration order.</summary>
/// <remarks>Parse must precede context matching, which must precede unique matching.
/// Result checking wraps the terminal applier. Additional guards must preserve this order.</remarks>
public sealed partial class DocumentDiffEditor
{
    private readonly IDiffGuard[] guards;
    private readonly ILogger<DocumentDiffEditor>? logger;

    /// <summary>Creates an editor with the mandatory guards in order and optional additional guards.</summary>
    public DocumentDiffEditor(
        IEnumerable<IDiffGuard> guards,
        ILogger<DocumentDiffEditor>? logger = null
    )
    {
        this.guards = guards.ToArray();
        this.logger = logger;
        Type[] required =
        [
            typeof(EligibilityGuard),
            typeof(BaseHashGuard),
            typeof(ParseGuard),
            typeof(EmptyBaseGuard),
            typeof(ContextLinesGuard),
            typeof(UniqueOldSideGuard),
            typeof(ResultCheckGuard),
            typeof(TextDiffApplier),
        ];
        var previous = -1;
        foreach (var type in required)
        {
            var positions = this
                .guards.Select((guard, index) => (guard, index))
                .Where(item => item.guard.GetType() == type)
                .Select(item => item.index)
                .ToArray();
            if (positions.Length != 1 || positions[0] <= previous)
                throw new ArgumentException(
                    "Mandatory diff guards must each appear once in order.",
                    nameof(guards)
                );
            previous = positions[0];
        }
        if (previous != this.guards.Length - 1)
            throw new ArgumentException("The diff applier must be the last guard.", nameof(guards));
    }

    /// <summary>Creates the standard guard pipeline without additional guards.</summary>
    public static DocumentDiffEditor CreateDefault() =>
        new([
            new EligibilityGuard(),
            new BaseHashGuard(),
            new ParseGuard(),
            new EmptyBaseGuard(),
            new ContextLinesGuard(),
            new UniqueOldSideGuard(),
            new ResultCheckGuard(),
            new TextDiffApplier(),
        ]);

    /// <summary>Evaluates the diff without changing persisted state.</summary>
    public ValueTask<DiffOutcome> ApplyAsync(DiffContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DiffDelegate next = _ => throw new InvalidOperationException("No diff applier registered.");
        foreach (var guard in guards.Reverse())
        {
            var continuation = next;
            next = async input =>
            {
                Activity.Current?.AddEvent(
                    new ActivityEvent(
                        "diff.guard",
                        tags: new ActivityTagsCollection { { "guard", guard.GetType().Name } }
                    )
                );
                var calledNext = false;
                var result = await guard.InvokeAsync(
                    input,
                    nested =>
                    {
                        calledNext = true;
                        return continuation(nested);
                    },
                    ct
                );
                if (!calledNext && result is DiffOutcome.Rejected && logger is not null)
                    LogGuardRejected(logger, guard.GetType().Name, result.GetType().Name);
                return result;
            };
        }
        return next(context);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Document guard {Guard} rejected edit with {Outcome}"
    )]
    private static partial void LogGuardRejected(ILogger logger, string guard, string outcome);
}

/// <summary>Rejects edits to synchronized documents.</summary>
public sealed class EligibilityGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    ) =>
        context.Document.Authoring == LibraryDocumentAuthoring.Synced
        || context.Document.SourceExternalId is not null
        || context.Document.SourceOrigin is not null
            ? ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.NotEditable(context.Document.SourceOrigin)
            )
            : next(context);
}

/// <summary>Rejects edits based on an obsolete content hash.</summary>
public sealed class BaseHashGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    ) =>
        context.BaseHash != context.Document.ContentHash
            ? ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.Stale(context.Document.Content, context.Document.ContentHash)
            )
            : next(context);
}

/// <summary>Parses the patch and rejects malformed or multi-file input.</summary>
public sealed class ParseGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        // Bound memory before parsing and bound matching work before searching lines.
        if (context.Diff.Length > 1_000_000 || context.Document.Content.Length > 1_000_000)
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.TooLarge("Input exceeds the edit limit.")
            );
        var hasLf = false;
        var hasCrlf = false;
        for (var i = 0; i < context.Document.Content.Length; i++)
        {
            if (context.Document.Content[i] != '\n')
                continue;
            if (i > 0 && context.Document.Content[i - 1] == '\r')
                hasCrlf = true;
            else
                hasLf = true;
        }
        if (hasLf && hasCrlf)
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.InvalidDiff("Mixed line endings are not supported.")
            );
        var hasGitHeader = false;
        var hasOldHeader = false;
        var hasNewHeader = false;
        var hunks = new List<List<DiffLine>>();
        List<DiffLine>? current = null;
        var raw = context.Diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            if (
                current is not null
                && (
                    line.StartsWith("diff --git")
                    || (
                        line.StartsWith("--- ")
                        && i + 1 < raw.Length
                        && raw[i + 1].StartsWith("+++ ")
                    )
                )
            )
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.InvalidDiff("Multiple files are not supported.")
                );
            if (line.StartsWith("@@"))
            {
                // Counts and line numbers may be inaccurate; the complete body is validated below.
                if (
                    !Regex.IsMatch(
                        line,
                        @"^@@ -[0-9]+(?:,[0-9]+)? \+[0-9]+(?:,[0-9]+)? @@(?:.*)?$",
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
                    )
                )
                    return ValueTask.FromResult<DiffOutcome>(
                        new DiffOutcome.InvalidDiff("Invalid hunk header.")
                    );
                current = [];
                hunks.Add(current);
                continue;
            }
            if (current is null)
            {
                if (line.StartsWith("diff --git"))
                {
                    if (hasGitHeader || hasOldHeader || hasNewHeader)
                        return ValueTask.FromResult<DiffOutcome>(
                            new DiffOutcome.InvalidDiff("Multiple files are not supported.")
                        );
                    hasGitHeader = true;
                }
                else if (line.StartsWith("--- "))
                {
                    if (hasOldHeader || hasNewHeader)
                        return ValueTask.FromResult<DiffOutcome>(
                            new DiffOutcome.InvalidDiff("Multiple files are not supported.")
                        );
                    hasOldHeader = true;
                }
                else if (line.StartsWith("+++ "))
                {
                    if (hasNewHeader)
                        return ValueTask.FromResult<DiffOutcome>(
                            new DiffOutcome.InvalidDiff("Multiple files are not supported.")
                        );
                    hasNewHeader = true;
                }
                var header =
                    line.StartsWith("--- ")
                    || line.StartsWith("+++ ")
                    || line.StartsWith("diff ")
                    || line.StartsWith("index ")
                    || line.StartsWith("new file")
                    || line.StartsWith("deleted file")
                    || line.StartsWith("similarity")
                    || line.StartsWith("rename ")
                    || line.Length == 0;
                if (header)
                    continue;
                if (line[0] is ' ' or '-' or '+')
                {
                    current = [];
                    hunks.Add(current);
                }
                else
                    return ValueTask.FromResult<DiffOutcome>(
                        new DiffOutcome.InvalidDiff("Invalid preamble.")
                    );
            }
            // The marker is advisory: existing document newline state is authoritative.
            if (line == "\\ No newline at end of file")
                continue;
            if (line.StartsWith('\\'))
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.InvalidDiff("Invalid newline marker.")
                );
            // NOTE: Bare empty context lines are explicitly allowed by fixture 27.
            if (line.Length == 0)
                current!.Add(new(' ', ""));
            else if (line[0] is ' ' or '-' or '+')
                current!.Add(new(line[0], line[1..]));
            else
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.InvalidDiff("Invalid hunk line.")
                );
        }
        if (hunks.Any(h => h.Count == 0))
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.InvalidDiff("Hunk has no body.")
            );
        context.Hunks = hunks;
        if (context.Hunks.Count == 0)
            return ValueTask.FromResult<DiffOutcome>(new DiffOutcome.InvalidDiff("No hunks."));
        var lines = context.Document.Content.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);
        context.Lines = [.. lines];
        var oldLineCount = context.Hunks.Sum(h => (long)h.Count(line => line.Prefix != '+'));
        if (oldLineCount * context.Lines.Length > 50_000_000)
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.TooLarge("Diff requires too much matching work.")
            );
        return next(context);
    }
}

/// <summary>Handles the single unambiguous insertion into an empty document.</summary>
public sealed class EmptyBaseGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        if (context.Document.Content.Length != 0)
            return next(context);
        // NOTE: Hunk line numbers are advisory, including on an empty base. The single
        // insertion has exactly one possible location regardless of the supplied range.
        if (context.Hunks.Count != 1 || context.Hunks[0].Any(l => l.Prefix != '+'))
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.NoMatch("Empty base requires one insertion.")
            );
        var content = string.Join("\n", context.Hunks[0].Select(l => l.Text)) + "\n";
        return ValueTask.FromResult(DiffResults.Check(content));
    }
}

/// <summary>Requires three context lines except at document boundaries.</summary>
public sealed class ContextLinesGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        context.Matches.Clear();
        foreach (var h in context.Hunks)
        {
            var old = h.Where(l => l.Prefix != '+').Select(l => l.Text).ToArray();
            if (old.Length == 0)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.ThinContext("Hunk has no old side.")
                );
            var match = DiffMatching.Find(context.Lines, old);
            context.Matches.Add(match);
            if (match.Count == 0)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.NoMatch($"Hunk {context.Matches.Count} does not match.")
                );
            var leading = h.TakeWhile(l => l.Prefix == ' ').Count();
            var trailing = h.AsEnumerable().Reverse().TakeWhile(l => l.Prefix == ' ').Count();
            if ((leading < 3 && !match.AtStart) || (trailing < 3 && !match.AtEnd))
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.ThinContext("Hunk has insufficient context.")
                );
        }
        return next(context);
    }
}

/// <summary>Requires a unique ordered exact old-side match for every hunk.</summary>
public sealed class UniqueOldSideGuard : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        var previous = -1;
        var previousEnd = -1;
        context.Positions.Clear();
        for (var i = 0; i < context.Hunks.Count; i++)
        {
            var h = context.Hunks[i];
            var old = h.Where(l => l.Prefix != '+').Select(l => l.Text).ToArray();
            var match = context.Matches[i];
            if (match.Count == 0)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.NoMatch($"Hunk {i + 1} does not match.")
                );
            if (match.Count > 1)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.Ambiguous($"Hunk {i + 1} matches more than once.")
                );
            if (match.First < previous)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.OutOfOrder($"Hunk {i + 1} is out of order.")
                );
            if (match.First < previousEnd)
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.ApplyFailed("Hunks overlap.")
                );
            previous = match.First;
            previousEnd = match.First + old.Length;
            context.Positions.Add(match.First);
        }
        return next(context);
    }
}

internal static class DiffMatching
{
    internal static DiffMatch Find(string[] lines, string[] block)
    {
        var count = 0;
        var first = -1;
        var atStart = false;
        var atEnd = false;
        for (var i = 0; i + block.Length <= lines.Length; i++)
        {
            var matches = true;
            for (var offset = 0; offset < block.Length && matches; offset++)
                matches = block[offset] == lines[i + offset];
            if (!matches)
                continue;
            if (count == 0)
                first = i;
            count++;
            atStart |= i == 0;
            atEnd |= i + block.Length == lines.Length;
        }
        return new DiffMatch(count, first, atStart, atEnd);
    }
}

/// <summary>Checks the applied result after the remaining pipeline completes.</summary>
public sealed class ResultCheckGuard : IDiffGuard
{
    /// <inheritdoc />
    public async ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        var outcome = await next(context);
        return outcome is DiffOutcome.Applied applied
            ? DiffResults.Check(applied.Content)
            : outcome;
    }
}

/// <summary>Applies the patch through TextDiff.</summary>
public sealed class TextDiffApplier : IDiffGuard
{
    /// <inheritdoc />
    public ValueTask<DiffOutcome> InvokeAsync(
        DiffContext context,
        DiffDelegate next,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = new TextDiffer().Process(context.Document.Content, context.Diff).Text;
            // Keep the stored final-newline state for nonempty bases, regardless of diff markers.
            if (context.Document.Content.EndsWith('\n') && !result.EndsWith('\n'))
                result += context.Document.Content.EndsWith("\r\n") ? "\r\n" : "\n";
            else if (!context.Document.Content.EndsWith('\n') && result.EndsWith('\n'))
                result = result[..^1].TrimEnd('\r');
            // Independently verify TextDiff applied precisely the validated old sides.
            // This reference is never saved; TextDiff remains the applier.
            var expectedLines = new List<string>();
            var cursor = 0;
            for (var i = 0; i < context.Hunks.Count; i++)
            {
                var hunk = context.Hunks[i];
                var position = context.Positions[i];
                expectedLines.AddRange(context.Lines[cursor..position]);
                expectedLines.AddRange(
                    hunk.Where(line => line.Prefix != '-').Select(line => line.Text)
                );
                cursor = position + hunk.Count(line => line.Prefix != '+');
            }
            expectedLines.AddRange(context.Lines[cursor..]);
            var newline = context.Document.Content.Contains("\r\n") ? "\r\n" : "\n";
            var expected = string.Join(newline, expectedLines);
            if (context.Document.Content.EndsWith('\n'))
                expected += newline;
            if (!string.Equals(result, expected, StringComparison.Ordinal))
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.ApplyFailed("Applied content differs from the validated hunks.")
                );
            if (string.Equals(result, context.Document.Content, StringComparison.Ordinal))
                return ValueTask.FromResult<DiffOutcome>(
                    new DiffOutcome.InvalidDiff("Diff does not change the document.")
                );
            return ValueTask.FromResult(DiffResults.Check(result));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.InvalidDiff("Diff could not be parsed.")
            );
        }
        catch (TextDiffException)
        {
            return ValueTask.FromResult<DiffOutcome>(
                new DiffOutcome.ApplyFailed("Diff could not be applied.")
            );
        }
    }
}

internal static class DiffResults
{
    internal static DiffOutcome Check(string content) =>
        content.Length > 200_000 ? new DiffOutcome.TooLarge("Result exceeds 200000 characters.")
        : string.IsNullOrWhiteSpace(content)
            ? new DiffOutcome.EmptyResult("Result must not be empty.")
        : new DiffOutcome.Applied(content, Hash(content));

    internal static string Hash(string content) => DocumentContentHash.Compute(content);
}
