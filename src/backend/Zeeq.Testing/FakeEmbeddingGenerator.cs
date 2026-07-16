using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Zeeq.Testing;

/// <summary>
/// Deterministic, network-free <see cref="IEmbeddingGenerator{String, Embedding}"/> test double.
/// </summary>
/// <remarks>
/// Retries are entirely internal to the real SDK client (<c>ClientRetryPolicy</c>, see
/// <c>LlmClientFactory.CreateDefaultEmbeddingGenerator</c>) and opaque to callers — from the
/// pipeline's perspective, a <see cref="GenerateAsync"/> call either eventually succeeds or throws
/// once, after the SDK's own retries are already exhausted. This fake therefore only needs a
/// deterministic "succeeds" shape and a simple "throws" mode; it does not simulate retry
/// sequencing, backoff timing, or <c>Retry-After</c> — those are verified once against the real
/// client, not re-tested per pipeline run against a fake.
/// </remarks>
public sealed class FakeEmbeddingGenerator(int dimensions = 768) : IEmbeddingGenerator<string, Embedding<float>>
{
    private int _activeCalls;

    /// <summary>
    /// When set, every <see cref="GenerateAsync"/> call throws this instead of returning a result —
    /// simulates the SDK exhausting its own retries and surfacing a final provider failure.
    /// </summary>
    public Exception? ThrowOnGenerate { get; set; }

    /// <summary>
    /// When set, every call awaits this task before proceeding — lets a concurrency/backpressure
    /// test hold N calls open simultaneously and release them on demand (e.g. via a shared
    /// <see cref="TaskCompletionSource"/>).
    /// </summary>
    public Func<Task>? Gate { get; set; }

    /// <summary>The highest number of <see cref="GenerateAsync"/> calls observed in flight at once.</summary>
    public int MaxConcurrentCalls { get; private set; }

    /// <summary>Every input string passed to <see cref="GenerateAsync"/>, in call order.</summary>
    public List<string> Requests { get; } = [];

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var active = Interlocked.Increment(ref _activeCalls);
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);

        try
        {
            if (Gate is { } gate)
            {
                await gate();
            }

            if (ThrowOnGenerate is { } exception)
            {
                throw exception;
            }

            var inputs = values.ToArray();
            lock (Requests)
            {
                Requests.AddRange(inputs);
            }

            var dimension = options?.Dimensions ?? dimensions;
            var results = new GeneratedEmbeddings<Embedding<float>>(
                inputs.Select(input => new Embedding<float>(DeterministicVector(input, dimension)))
            );

            return results;
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>
    /// Produces a stable unit-length vector from a SHA-256 hash of <paramref name="input"/>, so
    /// identical inputs always embed identically and near-duplicate inputs embed to nearby (but
    /// not identical) vectors — good enough to exercise ranking without a real provider.
    /// </summary>
    private static ReadOnlyMemory<float> DeterministicVector(string input, int dimension)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var rng = new Random(BitConverter.ToInt32(seed, 0));

        var vector = new float[dimension];
        var sumOfSquares = 0.0;
        for (var i = 0; i < dimension; i++)
        {
            var value = (float)(rng.NextDouble() * 2 - 1);
            vector[i] = value;
            sumOfSquares += value * value;
        }

        // Normalize to unit length so cosine distance behaves sensibly in tests.
        var magnitude = (float)Math.Sqrt(sumOfSquares);
        if (magnitude > 0)
        {
            for (var i = 0; i < dimension; i++)
            {
                vector[i] /= magnitude;
            }
        }

        return vector;
    }
}
