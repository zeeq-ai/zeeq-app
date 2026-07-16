/*
<agent_instructions>
This file contains the canonical way to set up test cases using a terse,
object-graph style construction of the seed data for test.  All tests
should prefer this mechanism to set up unique seeds for each test case.

Test can share an instance initialized in the constructor (each test
run invokes the constructor separately and gets an instance of the class).

See the `SeedContext.cs` and `EntityGraphMembershipExtensions.cs` to
understand how to use this facility.
</agent_instructions>
*/

using System.Collections;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Testing.EntityGraphs;

/// <summary>
/// Provides typed access to entities that have already been added to an entity graph.
/// </summary>
/// <param name="Entities">Entities currently registered in the graph.</param>
public sealed record EntityGraphContext(IReadOnlyList<object> Entities)
{
    /// <summary>
    /// Finds the first entity of type <typeparamref name="T"/> in the graph.
    /// </summary>
    /// <typeparam name="T">Entity type to locate.</typeparam>
    /// <returns>The first matching entity.</returns>
    public T Find<T>() =>
        Entities.OfType<T>().FirstOrDefault()
        ?? throw new InvalidOperationException(
            $"No entity of type {typeof(T).Name} found in the graph."
        );

    /// <summary>
    /// Finds the last entity of type <typeparamref name="T"/> in the graph.
    /// </summary>
    /// <typeparam name="T">Entity type to locate.</typeparam>
    /// <returns>The last matching entity.</returns>
    public T FindLast<T>() =>
        Entities.OfType<T>().LastOrDefault()
        ?? throw new InvalidOperationException(
            $"No entity of type {typeof(T).Name} found in the graph."
        );

    /// <summary>
    /// Finds all entities of type <typeparamref name="T"/> in the graph.
    /// </summary>
    /// <typeparam name="T">Entity type to locate.</typeparam>
    /// <returns>All matching entities.</returns>
    public IReadOnlyList<T> FindAll<T>() => Entities.OfType<T>().ToArray();
}

/// <summary>
/// Fluent builder for creating and optionally persisting test entity graphs.
/// </summary>
/// <typeparam name="TState">Current nested tuple state carried by the builder.</typeparam>
public sealed record EntityGraphBuilder<TState>(
    SeedContext Seed,
    TState State,
    IReadOnlyList<object> Entities,
    DbContext? Database = null,
    IReadOnlyList<object>? NonPersistentEntities = null
)
{
    /// <summary>
    /// Adds one entity to the graph.
    /// </summary>
    /// <param name="entityFactory">Factory that creates the entity from the seed context.</param>
    /// <param name="setup">Optional action that customizes the entity.</param>
    /// <typeparam name="TEntity">Type of entity to add.</typeparam>
    /// <returns>A builder with the entity pushed into the state tuple.</returns>
    public EntityGraphBuilder<(TState Previous, TEntity Entity)> Add<TEntity>(
        Func<SeedContext, TEntity> entityFactory,
        Action<TEntity>? setup = null
    )
    {
        var entity = entityFactory(Seed);
        setup?.Invoke(entity);
        return this.Push(entity);
    }

    /// <summary>
    /// Adds one entity that depends on the current builder state.
    /// </summary>
    /// <param name="entityFactory">Factory that creates the entity from seed and state.</param>
    /// <param name="setup">Optional action that customizes the entity.</param>
    /// <typeparam name="TEntity">Type of entity to add.</typeparam>
    /// <returns>A builder with the entity pushed into the state tuple.</returns>
    public EntityGraphBuilder<(TState Previous, TEntity Entity)> Add<TEntity>(
        Func<SeedContext, TState, TEntity> entityFactory,
        Action<TEntity>? setup = null
    )
    {
        var entity = entityFactory(Seed, State);
        setup?.Invoke(entity);
        return this.Push(entity);
    }

    /// <summary>
    /// Adds a requested number of default entities to the graph.
    /// </summary>
    /// <param name="entityFactory">Factory that creates each entity from the seed context and index.</param>
    /// <param name="count">Number of entities to create.</param>
    /// <typeparam name="TEntity">Type of entities to add.</typeparam>
    /// <returns>A builder with the entities pushed into the state tuple.</returns>
    public EntityGraphBuilder<(TState Previous, TEntity[] Entity)> AddMany<TEntity>(
        Func<SeedContext, int, TEntity> entityFactory,
        int count
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var entities = new TEntity[count];

        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = entityFactory(Seed, i);
        }

        return this.Push(entities);
    }

    /// <summary>
    /// Adds one entity per setup action to the graph.
    /// </summary>
    /// <param name="entityFactory">Factory that creates each entity from the seed context and index.</param>
    /// <param name="setupActions">Per-entity setup actions. Empty means one default entity.</param>
    /// <typeparam name="TEntity">Type of entities to add.</typeparam>
    /// <returns>A builder with the entities pushed into the state tuple.</returns>
    public EntityGraphBuilder<(TState Previous, TEntity[] Entity)> AddMany<TEntity>(
        Func<SeedContext, int, TEntity> entityFactory,
        params Action<TEntity>[] setupActions
    )
    {
        if (setupActions.Length == 0)
        {
            setupActions = [_ => { }];
        }

        var entities = new TEntity[setupActions.Length];

        for (var i = 0; i < entities.Length; i++)
        {
            var entity = entityFactory(Seed, i);
            setupActions[i].Invoke(entity);
            entities[i] = entity;
        }

        return this.Push(entities);
    }

    /// <summary>
    /// Adds one entity per setup action with access to prior graph entities.
    /// </summary>
    /// <param name="entityFactory">Factory that creates each entity from the seed context and index.</param>
    /// <param name="setupActions">Per-entity setup actions. Empty means one default entity.</param>
    /// <typeparam name="TEntity">Type of entities to add.</typeparam>
    /// <returns>A builder with the entities pushed into the state tuple.</returns>
    public EntityGraphBuilder<(TState Previous, TEntity[] Entity)> AddMany<TEntity>(
        Func<SeedContext, int, TEntity> entityFactory,
        params Action<TEntity, EntityGraphContext>[] setupActions
    )
    {
        if (setupActions.Length == 0)
        {
            setupActions = [(_, _) => { }];
        }

        var entities = new TEntity[setupActions.Length];
        var context = new EntityGraphContext(Entities);

        for (var i = 0; i < entities.Length; i++)
        {
            var entity = entityFactory(Seed, i);
            setupActions[i].Invoke(entity, context);
            entities[i] = entity;
        }

        return this.Push(entities);
    }

    /// <summary>
    /// Persists seed and graph entities when a database was supplied.
    /// </summary>
    /// <returns>An awaitable task.</returns>
    public async Task PersistStateAsync()
    {
        if (Database is null)
        {
            return;
        }

        var seedEntities = Seed.GetSeedEntities();
        var nonPersistentEntities = NonPersistentEntities ?? [];

        Database.AddRange(seedEntities);

        foreach (var entity in Entities)
        {
            if (ReferenceEquals(entity, Seed))
            {
                continue;
            }

            AddEntityOrRange(entity, seedEntities, nonPersistentEntities);
        }

        await Database.SaveChangesAsync();
    }

    private void AddEntityOrRange(
        object entity,
        IReadOnlyList<object> seedEntities,
        IReadOnlyList<object> nonPersistentEntities
    )
    {
        if (entity is OrganizationGraph organizationGraph)
        {
            AddEntityOrRange(organizationGraph.GetEntities(), seedEntities, nonPersistentEntities);
            return;
        }

        if (entity is string)
        {
            if (ShouldPersist(entity, seedEntities, nonPersistentEntities))
            {
                Database!.Add(entity);
            }

            return;
        }

        if (entity is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null && ShouldPersist(item, seedEntities, nonPersistentEntities))
                {
                    AddEntityOrRange(item, seedEntities, nonPersistentEntities);
                }
            }

            return;
        }

        if (ShouldPersist(entity, seedEntities, nonPersistentEntities))
        {
            Database!.Add(entity);
        }
    }

    private static bool ShouldPersist(
        object entity,
        IReadOnlyList<object> seedEntities,
        IReadOnlyList<object> nonPersistentEntities
    ) =>
        !seedEntities.Any(seedEntity => ReferenceEquals(seedEntity, entity))
        && !nonPersistentEntities.Any(nonPersistentEntity =>
            ReferenceEquals(nonPersistentEntity, entity)
        );
}

/// <summary>
/// Entry points and tuple helpers for building entity graphs.
/// </summary>
public static class EntityGraph
{
    /// <summary>
    /// Starts a graph with a caller-supplied seed context.
    /// </summary>
    /// <param name="context">Seed context for the graph.</param>
    /// <param name="database">Optional EF Core context used to persist the graph.</param>
    /// <returns>An entity graph builder.</returns>
    public static EntityGraphBuilder<SeedContext> SeedWith(
        SeedContext context,
        DbContext? database = null
    ) => new(context, context, [], database);

    /// <summary>
    /// Starts a graph with a generated seed context and pushes the seed into the result tuple.
    /// </summary>
    /// <param name="database">Optional EF Core context used to persist the graph.</param>
    /// <param name="userCount">Number of active seed users to create.</param>
    /// <returns>An entity graph builder with the seed in the result tuple.</returns>
    public static EntityGraphBuilder<(SeedContext Previous, SeedContext Current)> AddGeneratedSeed(
        DbContext? database = null,
        int userCount = 1
    )
    {
        var seed = SeedContext.Generate(userCount);
        return new EntityGraphBuilder<SeedContext>(seed, seed, [], database).Push(seed);
    }

    /// <summary>
    /// Starts a graph with a generated seed context and customized organization.
    /// </summary>
    /// <param name="database">Optional EF Core context used to persist the graph.</param>
    /// <param name="organizationSetup">Organization customization action.</param>
    /// <param name="userCount">Number of active seed users to create.</param>
    /// <returns>An entity graph builder with the seed in the result tuple.</returns>
    public static EntityGraphBuilder<(SeedContext Previous, SeedContext Current)> AddGeneratedSeed(
        DbContext? database,
        Action<Organization> organizationSetup,
        int userCount = 1
    )
    {
        var userSetupActions = Enumerable
            .Range(0, userCount)
            .Select(_ => (Action<User>)(_ => { }))
            .ToArray();
        var seed = SeedContext.Generate(organizationSetup, userSetupActions);

        return new EntityGraphBuilder<SeedContext>(seed, seed, [], database).Push(seed);
    }

    /// <summary>
    /// Pushes a new value into the builder state and persistence list.
    /// </summary>
    /// <param name="builder">Current builder.</param>
    /// <param name="next">Next value to push.</param>
    /// <param name="nonPersistentEntities">Entities to return in the graph state without saving.</param>
    /// <typeparam name="TState">Current state type.</typeparam>
    /// <typeparam name="TNext">Next value type.</typeparam>
    /// <returns>A new builder with nested tuple state.</returns>
    public static EntityGraphBuilder<(TState Previous, TNext Current)> Push<TState, TNext>(
        this EntityGraphBuilder<TState> builder,
        TNext next,
        IReadOnlyList<object>? nonPersistentEntities = null
    )
    {
        var previousNonPersistentEntities = builder.NonPersistentEntities ?? [];
        var nextNonPersistentEntities = nonPersistentEntities ?? [];

        return new(
            builder.Seed,
            (builder.State, next),
            [.. builder.Entities, next!],
            builder.Database,
            [.. previousNonPersistentEntities, .. nextNonPersistentEntities]
        );
    }

    /// <summary>
    /// Persists the graph and returns the single pushed entity.
    /// </summary>
    public static async Task<T1> BuildAsync<TInitial, T1>(
        this EntityGraphBuilder<(TInitial InitialEntity, T1 Entity1)> builder
    )
    {
        await builder.PersistStateAsync();
        return builder.State.Entity1;
    }

    /// <summary>
    /// Persists the graph and returns two pushed entities.
    /// </summary>
    public static async Task<(T1 Entity1, T2 Entity2)> BuildAsync<TInitial, T1, T2>(
        this EntityGraphBuilder<((TInitial InitialEntity, T1 Entity1) Previous, T2 Entity2)> builder
    )
    {
        await builder.PersistStateAsync();
        return (builder.State.Previous.Entity1, builder.State.Entity2);
    }

    /// <summary>
    /// Persists the graph and returns three pushed entities.
    /// </summary>
    public static async Task<(T1 Entity1, T2 Entity2, T3 Entity3)> BuildAsync<TInitial, T1, T2, T3>(
        this EntityGraphBuilder<(
            ((TInitial InitialEntity, T1 Entity1) Previous1, T2 Entity2) Previous0,
            T3 Entity3
        )> builder
    )
    {
        await builder.PersistStateAsync();
        return (
            builder.State.Previous0.Previous1.Entity1,
            builder.State.Previous0.Entity2,
            builder.State.Entity3
        );
    }

    /// <summary>
    /// Persists the graph and returns four pushed entities.
    /// </summary>
    public static async Task<(T1 Entity1, T2 Entity2, T3 Entity3, T4 Entity4)> BuildAsync<
        TInitial,
        T1,
        T2,
        T3,
        T4
    >(
        this EntityGraphBuilder<(
            (
                ((TInitial InitialEntity, T1 Entity1) Previous2, T2 Entity2) Previous1,
                T3 Entity3
            ) Previous0,
            T4 Entity4
        )> builder
    )
    {
        await builder.PersistStateAsync();
        return (
            builder.State.Previous0.Previous1.Previous2.Entity1,
            builder.State.Previous0.Previous1.Entity2,
            builder.State.Previous0.Entity3,
            builder.State.Entity4
        );
    }

    /// <summary>
    /// Persists the graph and returns five pushed entities.
    /// </summary>
    public static async Task<(
        T1 Entity1,
        T2 Entity2,
        T3 Entity3,
        T4 Entity4,
        T5 Entity5
    )> BuildAsync<TInitial, T1, T2, T3, T4, T5>(
        this EntityGraphBuilder<(
            (
                (
                    ((TInitial InitialEntity, T1 Entity1) Previous3, T2 Entity2) Previous2,
                    T3 Entity3
                ) Previous1,
                T4 Entity4
            ) Previous0,
            T5 Entity5
        )> builder
    )
    {
        await builder.PersistStateAsync();
        return (
            builder.State.Previous0.Previous1.Previous2.Previous3.Entity1,
            builder.State.Previous0.Previous1.Previous2.Entity2,
            builder.State.Previous0.Previous1.Entity3,
            builder.State.Previous0.Entity4,
            builder.State.Entity5
        );
    }
}
