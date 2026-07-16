namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// Enumerates keyword tokens extracted from front-matter keywords/tags.
/// </summary>
internal ref struct KeywordEnumerable
{
    private readonly string _src;
    private readonly SourceRange[] _ranges;

    internal KeywordEnumerable(string src, SourceRange[] ranges)
    {
        _src = src;
        _ranges = ranges;
    }

    /// <summary>Returns an enumerator over the keyword tokens.</summary>
    public Enumerator GetEnumerator() => new(_src, _ranges);

    /// <summary>Enumerates individual keyword tokens.</summary>
    public ref struct Enumerator
    {
        private readonly string _src;
        private readonly SourceRange[] _ranges;
        private int _index;

        internal Enumerator(string src, SourceRange[] ranges)
        {
            _src = src;
            _ranges = ranges;
            _index = 0;
        }

        /// <summary>The current keyword token.</summary>
        public ReadOnlySpan<char> Current => _ranges[_index - 1].Span(_src);

        /// <summary>Advances to the next keyword token.</summary>
        public bool MoveNext()
        {
            if (_index < _ranges.Length)
            {
                _index++;
                return true;
            }
            return false;
        }
    }
}
