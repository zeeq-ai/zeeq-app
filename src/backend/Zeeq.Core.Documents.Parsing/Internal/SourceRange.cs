namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// Half-open [Start, End) byte offsets into a source string.
/// </summary>
internal readonly record struct SourceRange(int Start, int End)
{
    public bool IsEmpty => Start >= End;
    public int Length => End - Start;

    public ReadOnlySpan<char> Span(string src) => src.AsSpan(Start, Length);

    public string Materialize(string src) => src[Start..End];
}
