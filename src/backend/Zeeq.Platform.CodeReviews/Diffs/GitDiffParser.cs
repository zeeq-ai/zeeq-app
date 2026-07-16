using System.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Parses uploaded unified git diffs into file-level sections.
/// </summary>
/// <remarks>
/// The parser scans the diff once, avoids per-line string allocation, and only
/// materializes file paths and patch slices when emitting the public result.
/// </remarks>
public sealed class GitDiffParser
{
    private const string DevNullPath = "/dev/null";
    private const string DiffGitPrefix = "diff --git ";
    private const string OldPrefix = "--- ";
    private const string NewPrefix = "+++ ";

    /// <summary>
    /// Parses a unified git diff into uploaded-diff file entries.
    /// </summary>
    /// <param name="unifiedDiff">
    /// Raw git unified diff text, typically produced by <c>git diff --binary</c> or
    /// <c>git show --binary</c>.
    /// </param>
    /// <returns>
    /// One entry per <c>diff --git</c> section, preserving each section's patch text and best-known
    /// current and previous file paths.
    /// </returns>
    public IReadOnlyList<UploadedDiffFile> Parse(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        var parser = new Parser(unifiedDiff);
        return parser.Parse();
    }

    /// <summary>
    /// Internal mutation states tracked while one file section is being parsed.
    /// </summary>
    /// <remarks>
    /// Git can describe the same file state through several signals: mode lines, patch markers,
    /// rename/copy metadata, and binary markers. This enum keeps those parser-only signals separate
    /// until the final uploaded-diff state is emitted.
    /// </remarks>
    private enum ParserMutationState
    {
        Unknown,
        Added,
        Modified,
        Deleted,
        Renamed,
        Copied,
        Binary,
    }

    /// <summary>
    /// Single-pass parser over the uploaded diff text.
    /// </summary>
    /// <remarks>
    /// The parser is a <see langword="ref struct" /> so it can hold spans over the source diff
    /// without copying the text. It materializes strings only when an <see cref="UploadedDiffFile" />
    /// is emitted.
    /// </remarks>
    private ref struct Parser(string source)
    {
        private readonly ReadOnlySpan<char> _text = source.AsSpan();
        private readonly List<UploadedDiffFile> _files = [];

        private int _position;

        /// <summary>
        /// Scans the diff and emits one result for each <c>diff --git</c> file section.
        /// </summary>
        public IReadOnlyList<UploadedDiffFile> Parse()
        {
            while (TryReadLine(out var line))
            {
                if (!line.Text.StartsWith(DiffGitPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                ParseFile(line);
            }

            return _files;
        }

        /// <summary>
        /// Parses a single <c>diff --git</c> section and appends the emitted file entry.
        /// </summary>
        /// <remarks>
        /// Path information is gathered from the header first, then refined from <c>---</c> and
        /// <c>+++</c> patch markers. Rename and copy metadata wins last because git can preserve the
        /// old and new names there even when a section has no textual hunks.
        /// </remarks>
        private void ParseFile(SourceLine diffHeader)
        {
            var patchStart = diffHeader.EndIncludingNewline;
            var patchEnd = _text.Length;
            var nextPosition = _text.Length;

            var oldPath = TextSlice.Empty;
            var newPath = TextSlice.Empty;
            var oldFileMarker = TextSlice.Empty;
            var newFileMarker = TextSlice.Empty;
            var renameFrom = TextSlice.Empty;
            var renameTo = TextSlice.Empty;
            var copyFrom = TextSlice.Empty;
            var copyTo = TextSlice.Empty;
            var state = ParserMutationState.Unknown;

            if (!TryParseDiffGitHeader(diffHeader.Text, diffHeader.Start, out oldPath, out newPath))
            {
                oldPath = TextSlice.Empty;
                newPath = TextSlice.Empty;
            }

            while (TryReadLine(out var line))
            {
                if (line.Text.StartsWith(DiffGitPrefix, StringComparison.Ordinal))
                {
                    patchEnd = line.Start;
                    nextPosition = line.Start;
                    break;
                }

                if (line.Text.StartsWith("new file mode ", StringComparison.Ordinal))
                {
                    state = ParserMutationState.Added;
                    continue;
                }

                if (line.Text.StartsWith("deleted file mode ", StringComparison.Ordinal))
                {
                    state = ParserMutationState.Deleted;
                    continue;
                }

                if (line.Text.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    state = ParserMutationState.Renamed;
                    renameFrom = StripPathPrefix(ParseFileMarker(line, "rename from ".Length));
                    continue;
                }

                if (line.Text.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    renameTo = StripPathPrefix(ParseFileMarker(line, "rename to ".Length));
                    continue;
                }

                if (line.Text.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    state = ParserMutationState.Copied;
                    copyFrom = StripPathPrefix(ParseFileMarker(line, "copy from ".Length));
                    continue;
                }

                if (line.Text.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    copyTo = StripPathPrefix(ParseFileMarker(line, "copy to ".Length));
                    continue;
                }

                if (line.Text.StartsWith(OldPrefix, StringComparison.Ordinal))
                {
                    oldFileMarker = ParseFileMarker(line, OldPrefix.Length);
                    continue;
                }

                if (line.Text.StartsWith(NewPrefix, StringComparison.Ordinal))
                {
                    newFileMarker = ParseFileMarker(line, NewPrefix.Length);
                    continue;
                }

                if (
                    line.Text.StartsWith("Binary files ", StringComparison.Ordinal)
                    || line.Text.StartsWith("GIT binary patch", StringComparison.Ordinal)
                )
                {
                    if (state is ParserMutationState.Unknown or ParserMutationState.Modified)
                    {
                        state = ParserMutationState.Binary;
                    }
                }
            }

            // Patch markers are more authoritative than the header for additions and deletions
            // because they use /dev/null to identify the missing side of the file pair.
            if (oldFileMarker.IsEmpty == false)
            {
                oldPath = NormalizePatchPath(oldFileMarker);
            }

            if (newFileMarker.IsEmpty == false)
            {
                newPath = NormalizePatchPath(newFileMarker);
            }

            // Rename/copy metadata can be the only reliable path source for metadata-only changes.
            if (state == ParserMutationState.Renamed)
            {
                oldPath = renameFrom.IsEmpty ? oldPath : renameFrom;
                newPath = renameTo.IsEmpty ? newPath : renameTo;
            }
            else if (state == ParserMutationState.Copied)
            {
                oldPath = copyFrom.IsEmpty ? oldPath : copyFrom;
                newPath = copyTo.IsEmpty ? newPath : copyTo;
            }

            if (state == ParserMutationState.Unknown)
            {
                state = InferState(oldFileMarker, newFileMarker);
            }

            // Header paths still contain both sides for adds/deletes; clear the absent side when no
            // patch marker was available to normalize it to the same shape as /dev/null markers.
            if (state == ParserMutationState.Added && oldFileMarker.IsEmpty)
            {
                oldPath = TextSlice.Empty;
            }

            if (state == ParserMutationState.Deleted && newFileMarker.IsEmpty)
            {
                newPath = TextSlice.Empty;
            }

            var path = newPath.IsEmpty ? oldPath.ToString(source) : newPath.ToString(source);
            var previousPath = oldPath.IsEmpty ? null : oldPath.ToString(source);

            _files.Add(
                new UploadedDiffFile(
                    path,
                    previousPath,
                    ToUploadedMutationState(state),
                    TrimTrailingNewline(new TextSlice(patchStart, patchEnd - patchStart))
                        .ToString(source)
                )
            );

            _position = nextPosition;
        }

        /// <summary>
        /// Reads the next source line and advances the parser position.
        /// </summary>
        /// <remarks>
        /// <see cref="SourceLine.EndIncludingNewline" /> preserves the boundary needed to include
        /// the original patch section exactly while still exposing <see cref="SourceLine.Text" />
        /// without newline characters for prefix checks.
        /// </remarks>
        private bool TryReadLine(out SourceLine line)
        {
            if (_position >= _text.Length)
            {
                line = default;
                return false;
            }

            var start = _position;
            var remaining = _text[start..];
            var newlineOffset = remaining.IndexOfAny('\r', '\n');

            if (newlineOffset < 0)
            {
                _position = _text.Length;
                line = new SourceLine(start, _text.Length, _text.Length, _text[start..]);
                return true;
            }

            var end = start + newlineOffset;
            var next = end + 1;

            if (_text[end] == '\r' && next < _text.Length && _text[next] == '\n')
            {
                next++;
            }

            _position = next;
            line = new SourceLine(start, end, next, _text[start..end]);
            return true;
        }

        /// <summary>
        /// Parses the old and new paths from a <c>diff --git</c> header line.
        /// </summary>
        /// <remarks>
        /// Git emits unquoted paths for simple names and quoted, escaped paths when names contain
        /// whitespace or special characters. The absolute start is used to keep unquoted paths as
        /// slices into the original source text.
        /// </remarks>
        private readonly bool TryParseDiffGitHeader(
            ReadOnlySpan<char> line,
            int absoluteStart,
            out TextSlice oldPath,
            out TextSlice newPath
        )
        {
            var payload = line[DiffGitPrefix.Length..];

            if (!payload.StartsWith('"'))
            {
                return TryParseUnquotedDiffGitHeader(
                    line,
                    absoluteStart,
                    payload,
                    out oldPath,
                    out newPath
                );
            }

            return TryParseQuotedDiffGitHeader(payload, out oldPath, out newPath);
        }

        /// <summary>
        /// Parses a simple unquoted <c>diff --git a/path b/path</c> header.
        /// </summary>
        /// <remarks>
        /// The split uses the last <c> b/</c> marker so paths that contain spaces before the new
        /// side still parse correctly.
        /// </remarks>
        private readonly bool TryParseUnquotedDiffGitHeader(
            ReadOnlySpan<char> line,
            int absoluteStart,
            ReadOnlySpan<char> payload,
            out TextSlice oldPath,
            out TextSlice newPath
        )
        {
            oldPath = TextSlice.Empty;
            newPath = TextSlice.Empty;

            var newPathOffset = payload.LastIndexOf(" b/", StringComparison.Ordinal);
            if (newPathOffset < 0)
            {
                return false;
            }

            var oldStart = DiffGitPrefix.Length;
            var oldEnd = DiffGitPrefix.Length + newPathOffset;
            var newStart = oldEnd + 1;
            var newEnd = line.Length;

            oldPath = StripPathPrefix(new TextSlice(absoluteStart + oldStart, oldEnd - oldStart));
            newPath = StripPathPrefix(new TextSlice(absoluteStart + newStart, newEnd - newStart));
            return true;
        }

        /// <summary>
        /// Parses a quoted <c>diff --git</c> header with git path escapes.
        /// </summary>
        private readonly bool TryParseQuotedDiffGitHeader(
            ReadOnlySpan<char> payload,
            out TextSlice oldPath,
            out TextSlice newPath
        )
        {
            oldPath = TextSlice.Empty;
            newPath = TextSlice.Empty;

            if (
                !TryParseQuotedPath(payload, out oldPath, out var charsConsumed)
                || charsConsumed >= payload.Length
            )
            {
                return false;
            }

            var remaining = payload[charsConsumed..].TrimStart();
            if (!TryParseQuotedPath(remaining, out newPath, out _))
            {
                oldPath = TextSlice.Empty;
                return false;
            }

            oldPath = StripPathPrefix(oldPath);
            newPath = StripPathPrefix(newPath);
            return true;
        }

        /// <summary>
        /// Parses the path-like payload after a diff metadata prefix.
        /// </summary>
        /// <remarks>
        /// This is used for patch markers such as <c>--- a/file.cs</c> and metadata lines such as
        /// <c>rename from old-name.cs</c>. Quoted markers are unescaped immediately because they are
        /// no longer contiguous slices of the source text.
        /// </remarks>
        private static TextSlice ParseFileMarker(SourceLine line, int prefixLength)
        {
            var marker = line.Text[prefixLength..];

            if (marker.StartsWith('"') && TryParseQuotedPath(marker, out var quotedPath, out _))
            {
                return quotedPath;
            }

            var start = line.Start + prefixLength;
            return new TextSlice(start, line.End - start);
        }

        /// <summary>
        /// Converts a patch marker path into a normal repository path.
        /// </summary>
        /// <remarks>
        /// <c>/dev/null</c> represents the absent side of added or deleted files and is normalized
        /// to an empty slice so callers can emit <see langword="null" /> previous paths.
        /// </remarks>
        private readonly TextSlice NormalizePatchPath(TextSlice path)
        {
            if (IsDevNull(path))
            {
                return TextSlice.Empty;
            }

            return StripPathPrefix(path);
        }

        /// <summary>
        /// Removes git's synthetic <c>a/</c> or <c>b/</c> path prefix when present.
        /// </summary>
        /// <remarks>
        /// Quoted paths are already materialized strings, so stripping them creates a new value;
        /// unquoted paths remain lightweight slices into the original source text.
        /// </remarks>
        private readonly TextSlice StripPathPrefix(TextSlice path)
        {
            if (path.Length <= 2)
            {
                return path;
            }

            var span = path.Value is { } value
                ? value.AsSpan()
                : _text.Slice(path.Start, path.Length);
            if ((span[0] != 'a' && span[0] != 'b') || span[1] != '/')
            {
                return path;
            }

            return path.Value is { } text
                ? new TextSlice(0, text.Length - 2, text[2..])
                : new TextSlice(path.Start + 2, path.Length - 2);
        }

        /// <summary>
        /// Infers the mutation state from patch markers when no explicit git metadata was present.
        /// </summary>
        private ParserMutationState InferState(TextSlice oldFileMarker, TextSlice newFileMarker)
        {
            if (IsDevNull(oldFileMarker))
            {
                return ParserMutationState.Added;
            }

            if (IsDevNull(newFileMarker))
            {
                return ParserMutationState.Deleted;
            }

            return ParserMutationState.Modified;
        }

        /// <summary>
        /// Returns whether a path marker is git's <c>/dev/null</c> sentinel.
        /// </summary>
        private readonly bool IsDevNull(TextSlice slice)
        {
            if (slice.IsEmpty)
            {
                return false;
            }

            return slice.Value is { } value
                ? value.AsSpan().SequenceEqual(DevNullPath)
                : _text.Slice(slice.Start, slice.Length).SequenceEqual(DevNullPath);
        }

        /// <summary>
        /// Removes the final newline from a captured patch section while preserving internal lines.
        /// </summary>
        private readonly TextSlice TrimTrailingNewline(TextSlice slice)
        {
            var length = slice.Length;

            if (length > 0 && _text[slice.Start + length - 1] == '\n')
            {
                length--;
            }

            if (length > 0 && _text[slice.Start + length - 1] == '\r')
            {
                length--;
            }

            return new TextSlice(slice.Start, length);
        }

        /// <summary>
        /// Converts parser-only mutation state into the public uploaded-diff mutation state.
        /// </summary>
        private static UploadedDiffFileMutationState ToUploadedMutationState(
            ParserMutationState state
        ) =>
            state switch
            {
                ParserMutationState.Added => UploadedDiffFileMutationState.Added,
                ParserMutationState.Deleted => UploadedDiffFileMutationState.Deleted,
                ParserMutationState.Renamed => UploadedDiffFileMutationState.Renamed,
                ParserMutationState.Copied => UploadedDiffFileMutationState.Copied,
                ParserMutationState.Binary => UploadedDiffFileMutationState.Binary,
                _ => UploadedDiffFileMutationState.Modified,
            };

        /// <summary>
        /// Parses one git-quoted path and returns the unescaped path value.
        /// </summary>
        /// <remarks>
        /// Git quote parsing is intentionally small and supports the escapes emitted by git for
        /// tabs, newlines, carriage returns, quotes, and backslashes. Unknown escapes are preserved
        /// as their escaped character to match git's permissive behavior.
        /// </remarks>
        private static bool TryParseQuotedPath(
            ReadOnlySpan<char> text,
            out TextSlice path,
            out int charsConsumed
        )
        {
            path = TextSlice.Empty;
            charsConsumed = 0;

            if (text.IsEmpty || text[0] != '"')
            {
                return false;
            }

            var builder = new StringBuilder(text.Length);
            var escaping = false;

            for (var i = 1; i < text.Length; i++)
            {
                var current = text[i];

                if (escaping)
                {
                    builder.Append(UnescapeGitPathCharacter(current));
                    escaping = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (current == '"')
                {
                    charsConsumed = i + 1;
                    path = new TextSlice(0, builder.Length, builder.ToString());
                    return true;
                }

                builder.Append(current);
            }

            return false;
        }

        /// <summary>
        /// Converts a single escaped git path character to its text value.
        /// </summary>
        private static char UnescapeGitPathCharacter(char escaped) =>
            escaped switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => escaped,
            };
    }

    /// <summary>
    /// A line in the source diff with both content and absolute source offsets.
    /// </summary>
    /// <remarks>
    /// The parser uses absolute offsets so file paths and patch bodies can remain slices of the
    /// original uploaded diff until a public result needs a string.
    /// </remarks>
    private readonly ref struct SourceLine(
        int start,
        int end,
        int endIncludingNewline,
        ReadOnlySpan<char> text
    )
    {
        /// <summary>
        /// Absolute start offset of the line content in the source diff.
        /// </summary>
        public int Start { get; } = start;

        /// <summary>
        /// Absolute end offset of the line content, excluding newline characters.
        /// </summary>
        public int End { get; } = end;

        /// <summary>
        /// Absolute end offset including the line break, if one was present.
        /// </summary>
        public int EndIncludingNewline { get; } = endIncludingNewline;

        /// <summary>
        /// Line content without trailing newline characters.
        /// </summary>
        public ReadOnlySpan<char> Text { get; } = text;
    }

    /// <summary>
    /// A path or patch slice that is either an offset into the source diff or a materialized value.
    /// </summary>
    /// <remarks>
    /// Most paths can stay as source offsets. Quoted git paths must be unescaped, so those are
    /// stored in <see cref="Value" /> while still using the same lightweight shape.
    /// </remarks>
    private readonly record struct TextSlice(int Start, int Length, string? Value = null)
    {
        /// <summary>
        /// Shared empty slice used for absent paths.
        /// </summary>
        public static readonly TextSlice Empty = new(0, 0);

        /// <summary>
        /// Returns whether the slice represents an absent value.
        /// </summary>
        public bool IsEmpty => Length == 0 && string.IsNullOrEmpty(Value);

        /// <summary>
        /// Converts the slice to memory over either the materialized value or the original source.
        /// </summary>
        public ReadOnlyMemory<char> ToMemory(string source) =>
            Value is { } value ? value.AsMemory() : source.AsMemory(Start, Length);

        /// <summary>
        /// Materializes the slice as a string.
        /// </summary>
        public string ToString(string source) => ToMemory(source).ToString();
    }
}

/// <summary>
/// File entry emitted by the uploaded-diff parser.
/// </summary>
/// <param name="Path">Current repository path for the file after the diff is applied.</param>
/// <param name="PreviousPath">
/// Previous repository path when the file existed before the diff; <see langword="null" /> for
/// added files.
/// </param>
/// <param name="MutationState">Best-known mutation state inferred from git diff metadata.</param>
/// <param name="Patch">Raw patch section for this file, excluding the leading <c>diff --git</c> line.</param>
public sealed record UploadedDiffFile(
    string Path,
    string? PreviousPath,
    UploadedDiffFileMutationState MutationState,
    string Patch
);

/// <summary>
/// Mutation state for a file entry in an uploaded diff.
/// </summary>
public enum UploadedDiffFileMutationState
{
    /// <summary>
    /// A file introduced by the pull request.
    /// </summary>
    Added,

    /// <summary>
    /// An existing file changed by the pull request.
    /// </summary>
    Modified,

    /// <summary>
    /// A file removed by the pull request.
    /// </summary>
    Deleted,

    /// <summary>
    /// A file moved to a new path by the pull request.
    /// </summary>
    Renamed,

    /// <summary>
    /// A file copied from an existing path by the pull request.
    /// </summary>
    Copied,

    /// <summary>
    /// A binary file whose contents are not represented as a text patch.
    /// </summary>
    Binary,
}
