using Microsoft.Extensions.AI;
using Pgvector;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Validates the persisted half-precision representation before cosine operations.</summary>
internal static class EmbeddingVectorValidation
{
    internal static HalfVector? ToHalfVector(Embedding<float> embedding, int dimensions)
    {
        var input = embedding.Vector.Span;
        if (dimensions != 768 || input.Length != dimensions)
            return null;
        var values = new Half[dimensions];
        var nonzero = false;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (Half)input[i];
            if (!Half.IsFinite(values[i]))
                return null;
            nonzero |= values[i] != (Half)0;
        }
        return nonzero ? new HalfVector(values) : null;
    }
}
