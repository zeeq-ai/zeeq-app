# Biome test setup

This shared setup consumes `Zeeq.Biome` and `Zeeq.Biome.EntityFrameworkCore` **1.1.0** from nuget.org. Import `Zeeq.Testing.Biomes` for the entry point, seed, aggregate, input blueprints and generated grafts.

Generated seed rows and scenario outputs share a frozen, PostgreSQL-precision clock. Passing a TimeProvider keeps that clock live and rounds every read. Passing an existing SeedContext preserves its supplied values. Factories use mutable construction inputs and ordinary object initializers; production models retain their init properties.

```csharp
// Membership: extra users receive no automatic memberships.
await TestBiome.Start(db, out var seed)
    .AddInvitation(out var invitation, i => i.Email = "invite@example.test")
    .Transient()
    .AddOrganization(out var organization, i => i.IsActivated = false)
    .BuildAsync();

// Identity: the owned user output/seed row anchors aliases.
await TestBiome.Start(db, out var identitySeed)
    .Under(identitySeed.Owner)
    .AddAlias(out var alias, i =>
    {
        i.Kind = UserAliasKind.GitHub;
        i.DisplayValue = " @CharlieDigital ";
    })
    .BuildAsync();

// Code reviews: PR -> repository ancestry is explicit.
await TestBiome.Start(db, out _)
    .AddRepository(out var repository)
    .AddPullRequest(out var pr, i => i.PullRequestNumber = 42)
    .AddReview(out var review)
    .BuildAsync();

// Docs: source outputs anchor public runs; library IDs need no extra rows.
await TestBiome.Start(db, out _)
    .AddRun(out var privateRun, i => i.LibraryId = "lib_private")
    .AddPublicSource(out var source)
    .AddPublicRun(out var publicRun, i => i.LibraryId = "lib_public")
    .BuildAsync();
```

The four `[BiomeGraft]` catalogs generate singular default/customized and plural count/customizer forms. `[Graft]` keeps the app-facing singular/plural names. For context customization use the package's `Add`/`AddOne` directly with the area blueprint. Use `Under(existingOutput)` to return to a parent. Inputs are ephemeral and cannot be anchors. A root input blueprint stays root even after `Under`.

PR numbering deliberately resets to `Index + 1` for each call and parent. Explicit duplicate values remain allowed; no continuous reservation sequence replaces those boundaries. Lookup outputs do not create PR rows.

`Transient()` excludes the session's most recent Add step. Apply it immediately after that step. The pilot's two-review split is valid because both outputs use the same PR, explicit timestamps, and no index/sequence-dependent fields. Other mixed batches require their own equivalence proof. SUT inputs stay transient wherever the SUT owns insertion.

OrganizationGraph implements IBiomeAggregate and persists its four existing rows in FK order. Its parts are not automatically parent anchors. No global defaults, presence tracking, automatic ancestor construction, or compatibility Push API are introduced. Input factories cannot distinguish omitted null from explicitly supplied null; factories retain the original prototype fallback behavior.
