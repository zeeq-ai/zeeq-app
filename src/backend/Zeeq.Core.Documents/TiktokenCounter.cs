namespace Zeeq.Core.Documents;

/// <summary>
/// Estimates token counts for document content using cl100k_base via the tryAGI Tiktoken package.
/// </summary>
/// <remarks>
/// This is an OpenAI tokenizer estimate. Claude-specific token accounting is intentionally deferred.
/// </remarks>
public static class TiktokenCounter
{
    private static readonly Lazy<Tiktoken.Encoder> Encoder = new(() =>
        Tiktoken.TikTokenEncoder.CreateForModel("gpt-4")
    );

    /// <summary>
    /// Counts tokens in <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The searchable document content.</param>
    /// <returns>The estimated token count, or <c>0</c> for empty content.</returns>
    public static int CountTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : Encoder.Value.CountTokens(text);

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxTokens"/> tokens.
    /// </summary>
    /// <remarks>
    /// Used before hashing and embedding a snippet payload so provider inputs stay within the
    /// model's context window and the resulting <c>ContentHash</c> is computed over the exact
    /// truncated text (making re-runs stable). Encodes, takes the first N token ids, and decodes
    /// back to a string; no-ops when the text already fits.
    /// </remarks>
    /// <param name="text">The payload text.</param>
    /// <param name="maxTokens">The maximum number of tokens to retain.</param>
    /// <returns>The original text when it fits, otherwise the decoded token-truncated prefix.</returns>
    public static string Truncate(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0)
        {
            return text;
        }

        var tokens = Encoder.Value.Encode(text);

        if (tokens.Count <= maxTokens)
        {
            return text;
        }

        return Encoder.Value.Decode([.. tokens.Take(maxTokens)]);
    }
}
