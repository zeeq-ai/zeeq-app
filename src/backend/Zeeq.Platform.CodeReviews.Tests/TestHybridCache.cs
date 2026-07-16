using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// In-memory HybridCache for tests: calls the factory on first access and stores the result.
/// </summary>
internal sealed class TestHybridCache : HybridCache
{
    private readonly Dictionary<string, object?> _values = [];

    public override async ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_values.TryGetValue(key, out var value))
            return (T)value!;

        var created = await factory(state, cancellationToken);
        _values[key] = created;
        return created;
    }

    public override ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default
    )
    {
        _values[key] = value;
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveByTagAsync(
        string tag,
        CancellationToken cancellationToken = default
    ) => ValueTask.CompletedTask;
}
