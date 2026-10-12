# Zeeq Testing Facilities

Common testing utilities, base classes, and fixtures for Zeeq unit and integration tests. Reference this project from backend test projects.

## Biome

Use `TestBiome` for backend scenarios with Zeeq users, organizations, teams, memberships, and domain rows. Import `Zeeq.Testing.Biomes`. The four `[BiomeGraft]` catalogs generate the regular singular/plural Add methods; input factories preserve production `init` models.

```csharp
await TestBiome.Start(db, out var seed)
    .AddRepository(out var repository)
    .AddPullRequest(out var pullRequest, input => input.PullRequestNumber = 42)
    .AddReview(out var review)
    .BuildAsync();
```

See [Biome/README.md](Biome/README.md) for anchors, transience, clock precision, aggregates and intentional numbering resets. Keep direct SUT inputs, projection DTOs and constraint-test inserts direct. Fixtures and transaction isolation are unchanged.
