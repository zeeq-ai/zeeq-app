using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Platform.Documents;
using Zeeq.Platform.Documents.Editing;

namespace Zeeq.Mcp.Documents.Tests;

/// <summary>Authoring tools, ownership metadata, and public-library overlay behavior.</summary>
public sealed class DocumentAuthoringTests
{
    [McpServerTool(Name = "beta_probe", Title = "Beta Probe (BETA)")]
    private static string BetaProbe() => "probe";

    [Test]
    public async Task ToolDiscovery_WithoutBetaHeader_IncludesBothAuthoringTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }
        );
        services.AddMcpServer().WithZeeqMcpFilters();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (
            var name in new[]
            {
                nameof(DocumentLibraryMcpTools.CreateDocument),
                nameof(DocumentLibraryMcpTools.UpdateDocumentByDiff),
            }
        )
            options.ToolCollection.Add(
                McpServerTool.Create(typeof(DocumentLibraryMcpTools).GetMethod(name)!)
            );
        options.ToolCollection.Add(
            McpServerTool.Create(
                typeof(DocumentAuthoringTests).GetMethod(
                    nameof(BetaProbe),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                )!
            )
        );
        await using var server = McpServer.Create(
            Substitute.For<ITransport>(),
            options,
            serviceProvider: provider
        );
        var context = new RequestContext<ListToolsRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/list" },
            new ListToolsRequestParams()
        )
        {
            Services = provider,
        };
        await options.Handlers!.ListToolsHandler!(context, default);
        await Assert
            .That(options.ToolCollection.Select(t => t.ProtocolTool.Name))
            .IsEquivalentTo(["create_document", "update_document_by_diff"]);
        await Assert
            .That(options.ToolCollection.All(t => !t.ProtocolTool.Title!.Contains("(BETA)")))
            .IsTrue();
    }

    private sealed class CapturingMetricExporter : BaseExporter<Metric>
    {
        public HashSet<string> Names { get; } = [];
        public Dictionary<string, long> Sums { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                Names.Add(metric.Name);
                if (
                    metric.Name
                    is "zeeq_update_document_by_diff_total"
                        or "zeeq_create_document_total"
                        or "zeeq_document_sync_skipped_local_total"
                )
                {
                    long sum = 0;
                    foreach (ref readonly var point in metric.GetMetricPoints())
                        sum += point.GetSumLong();
                    Sums[metric.Name] = sum;
                }
            }
            return ExportResult.Success;
        }
    }

    [Test]
    public async Task AuthoringCounters_AreCollectedByOpenTelemetry()
    {
        var exporter = new CapturingMetricExporter();
        using var reader = new PeriodicExportingMetricReader(exporter, 60000);
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("*")
            .AddReader(reader)
            .Build();
        var (store, _, _, service) = Setup(Library());
        var actor = new AuditActorContext { Current = Actor };
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/x.md",
            "hash",
            "diff",
            "why"
        );
        await DocumentLibraryMcpTools.CreateDocument(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/x.md",
            "body",
            "why"
        );
        DocumentAuthoringTelemetry.SyncSkippedLocal.Add(1);
        await Assert.That(reader.Collect()).IsTrue();
        await Assert.That(exporter.Names).Contains("zeeq_update_document_by_diff_total");
        await Assert.That(exporter.Names).Contains("zeeq_create_document_total");
        await Assert.That(exporter.Names).Contains("zeeq_document_sync_skipped_local_total");
        await Assert.That(exporter.Sums["zeeq_update_document_by_diff_total"]).IsGreaterThan(0);
        await Assert.That(exporter.Sums["zeeq_create_document_total"]).IsGreaterThan(0);
        await Assert.That(exporter.Sums["zeeq_document_sync_skipped_local_total"]).IsGreaterThan(0);
    }

    private static readonly AuditActor Actor = AuditActor.Worker("test");

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(AuthClaims.OrganizationId, "org")], "test"));

    private static Library Library(string? source = null) =>
        new()
        {
            Id = "lib",
            OrganizationId = "org",
            Name = "kb",
            PublicSourceId = source,
        };

    private static LibraryDocument Local(string path = "/local.md", string content = "old\n") =>
        new()
        {
            Id = "doc",
            OrganizationId = "org",
            LibraryId = "lib",
            Path = path,
            Title = "Local",
            TitleNormalized = "local",
            Content = content,
            ContentHash = DocumentContentHash.Compute(content),
        };

    private static DocsPublicDocument Shared(string path = "/shared.md") =>
        new()
        {
            Id = "shared",
            PublicSourceId = "src",
            Path = path,
            Title = "Shared",
            TitleNormalized = "shared",
            Content = "public\n",
            ContentHash = DocumentContentHash.Compute("public\n"),
        };

    private static DocumentDiffEditor Editor() =>
        new([
            new EligibilityGuard(),
            new BaseHashGuard(),
            new ParseGuard(),
            new EmptyBaseGuard(),
            new ContextLinesGuard(),
            new UniqueOldSideGuard(),
            new ResultCheckGuard(),
            new TextDiffApplier(),
        ]);

    private static (
        ILibraryDocumentStore Store,
        IDocsPublicDocumentStore Public,
        IDocsPublicSourceStore Sources,
        DocumentAuthoringService Service
    ) Setup(Library library)
    {
        var store = Substitute.For<ILibraryDocumentStore>();
        var pub = Substitute.For<IDocsPublicDocumentStore>();
        var sources = Substitute.For<IDocsPublicSourceStore>();
        store.GetLibraryAsync("org", "kb", Arg.Any<CancellationToken>()).Returns(library);
        store
            .ListDocumentsAsync("org", "lib", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LibraryDocument>());
        if (library.PublicSourceId is not null)
            sources
                .GetByIdAsync("src", Arg.Any<CancellationToken>())
                .Returns(
                    new DocsPublicSource
                    {
                        Id = "src",
                        RepoUrl = "https://github.com/acme/docs.git",
                        Name = "docs",
                    }
                );
        return (
            store,
            pub,
            sources,
            new DocumentAuthoringService(
                store,
                pub,
                sources,
                Editor(),
                NullLogger<DocumentAuthoringService>.Instance
            )
        );
    }

    [Test]
    public async Task AuthoringInput_RejectsInvalidPathsAndOversizedRationaleBeforeWriting()
    {
        var library = Library();
        var (store, _, _, service) = Setup(library);
        var actor = new AuditActorContext { Current = Actor };
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var oversized = new string('r', 2001);
        foreach (var (path, rationale) in new[] { ("../bad", "why"), ("/ok.md", oversized) })
        {
            var create = await DocumentLibraryMcpTools.CreateDocument(
                store,
                service,
                actor,
                http,
                User(),
                "kb",
                path,
                "body",
                rationale
            );
            var update = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
                store,
                service,
                actor,
                http,
                User(),
                "kb",
                path,
                "hash",
                "diff",
                rationale
            );
            foreach (var result in new[] { create, update })
            {
                var message = ((TextContentBlock)result.Content[0]).Text;
                await Assert.That(message).Contains("code: invalid_input");
                await Assert.That(message).Contains("saved: false");
            }
            await Assert
                .That(
                    (
                        await service.CreateAsync(
                            "org",
                            library,
                            path,
                            "body",
                            rationale,
                            Actor,
                            null,
                            default
                        )
                    ).Status
                )
                .IsEqualTo(AuthoringStatus.InvalidInput);
            await Assert
                .That(
                    (
                        await service.UpdateAsync(
                            "org",
                            library,
                            path,
                            "hash",
                            "diff",
                            rationale,
                            Actor,
                            null,
                            default
                        )
                    ).Status
                )
                .IsEqualTo(AuthoringStatus.InvalidInput);
        }
        await store
            .DidNotReceiveWithAnyArgs()
            .CreateAgentDocumentAsync(default!, default!, default!, default, default);
        await store
            .DidNotReceiveWithAnyArgs()
            .TryReplaceContentAsync(default!, default!, default!, default!, default, default);
    }

    [Test]
    public async Task AuthoringInput_AllowsMaximumRationaleAndLimitsStoredClientName()
    {
        var library = Library();
        var (store, _, _, service) = Setup(library);
        var rationale = new string('r', 2000);
        var client = new string('c', 300);
        store
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new CreateDocumentResult.Created(call.ArgAt<LibraryDocument>(0)));
        var created = await service.CreateAsync(
            "org",
            library,
            "/new.md",
            "body",
            rationale,
            Actor,
            client,
            default
        );
        await Assert.That(created.Status).IsEqualTo(AuthoringStatus.Created);
        await store
            .Received(1)
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                rationale,
                Actor,
                new string('c', 256),
                Arg.Any<CancellationToken>()
            );
        var unicodeClient = new string('c', 255) + "\U0001F600";
        await service.CreateAsync(
            "org",
            library,
            "/unicode.md",
            "body",
            rationale,
            Actor,
            unicodeClient,
            default
        );
        await store
            .Received(1)
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                rationale,
                Actor,
                new string('c', 255),
                Arg.Any<CancellationToken>()
            );
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var mcpCreated = await DocumentLibraryMcpTools.CreateDocument(
            store,
            service,
            new AuditActorContext { Current = Actor },
            http,
            User(),
            "kb",
            "/mcp.md",
            "body",
            rationale
        );
        await Assert
            .That(((TextContentBlock)mcpCreated.Content[0]).Text)
            .Contains("status: created");
        var doc = Local();
        store.GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>()).Returns(doc);
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new ReplaceContentResult.Applied(call.ArgAt<LibraryDocument>(0)));
        var updated = await service.UpdateAsync(
            "org",
            library,
            "/local.md",
            doc.ContentHash!,
            "@@ -1 +1 @@\n-old\n+new\n",
            rationale,
            Actor,
            client,
            default
        );
        await Assert.That(updated.Status).IsEqualTo(AuthoringStatus.Applied);
        await store
            .Received(1)
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                doc.ContentHash!,
                rationale,
                Actor,
                new string('c', 256),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task StoreArgumentFailures_ReportSaveAsUnknown()
    {
        var (store, _, _, service) = Setup(Library());
        var doc = Local();
        store.GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>()).Returns(doc);
        store
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<CreateDocumentResult>(new ArgumentException("post-write")));
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<ReplaceContentResult>(new ArgumentException("post-write")));
        var actor = new AuditActorContext { Current = Actor };
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var create = await DocumentLibraryMcpTools.CreateDocument(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/new.md",
            "body",
            "why"
        );
        var update = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/local.md",
            doc.ContentHash!,
            "@@ -1 +1 @@\n-old\n+new\n",
            "why"
        );
        await Assert.That(((TextContentBlock)create.Content[0]).Text).Contains("saved: unknown");
        await Assert.That(((TextContentBlock)update.Content[0]).Text).Contains("saved: unknown");
    }

    [Test]
    public async Task Create_LocalAndPublicConflict_RespectOwnership()
    {
        var library = Library();
        var (store, _, _, service) = Setup(library);
        store
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var doc = call.ArgAt<LibraryDocument>(0);
                doc.Authoring = LibraryDocumentAuthoring.Agent;
                return new CreateDocumentResult.Created(doc);
            });
        var created = await service.CreateAsync(
            "org",
            library,
            "new.md",
            "# New\n",
            "why",
            Actor,
            "client",
            default
        );
        await Assert.That(created.Status).IsEqualTo(AuthoringStatus.Created);
        await Assert
            .That(created.Document!.Local!.Authoring)
            .IsEqualTo(LibraryDocumentAuthoring.Agent);
        var publicLibrary = Library("src");
        var (otherStore, pub, _, otherService) = Setup(publicLibrary);
        pub.GetByPathAsync("src", "/shared.md", Arg.Any<CancellationToken>()).Returns(Shared());
        var exists = await otherService.CreateAsync(
            "org",
            publicLibrary,
            "/shared.md",
            "# New\n",
            "why",
            Actor,
            "client",
            default
        );
        await Assert.That(exists.Status).IsEqualTo(AuthoringStatus.Exists);
        await Assert.That(exists.Document!.Public!.Id).IsEqualTo("shared");
        await otherStore
            .DidNotReceiveWithAnyArgs()
            .CreateAgentDocumentAsync(default!, default!, default!, default, default);
    }

    [Test]
    public async Task Create_InvalidatesListCacheForEveryCaller_AndReportsFailureAfterCommit()
    {
        var library = Library();
        var (store, pub, sources, _) = Setup(library);
        store
            .CreateAgentDocumentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new CreateDocumentResult.Created(call.ArgAt<LibraryDocument>(0)));
        var cache = Substitute.For<HybridCache>();
        var service = new DocumentAuthoringService(
            store,
            pub,
            sources,
            Editor(),
            NullLogger<DocumentAuthoringService>.Instance,
            cache
        );
        var result = await service.CreateAsync(
            "org",
            library,
            "/new.md",
            "new content",
            "why",
            Actor,
            "client",
            default
        );
        await Assert.That(result.Status).IsEqualTo(AuthoringStatus.Created);
        await cache
            .Received(1)
            .RemoveAsync(McpDocumentIndexCache.Key("org", "lib"), CancellationToken.None);
        cache
            .RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("cache failed"));
        var warning = await service.CreateAsync(
            "org",
            library,
            "/another.md",
            "other content",
            "why",
            Actor,
            "client",
            default
        );
        await Assert.That(warning.Status).IsEqualTo(AuthoringStatus.Created);
        await Assert.That(warning.ListMayBeStale).IsTrue();
    }

    [Test]
    public async Task PublicOverlay_LocalWinsFutureCollision_SharedRowsRemainUnchanged()
    {
        var library = Library("src");
        var (store, pub, sources, service) = Setup(library);
        var local = Local("/shared.md");
        var shared = Shared();
        store
            .GetByPathAsync("org", "lib", "/shared.md", Arg.Any<CancellationToken>())
            .Returns(local);
        store
            .ListDocumentsAsync("org", "lib", Arg.Any<CancellationToken>())
            .Returns(new[] { local });
        pub.GetByPathAsync("src", "/shared.md", Arg.Any<CancellationToken>()).Returns(shared);
        pub.ListSummariesBySourceAsync("src", Arg.Any<CancellationToken>())
            .Returns(new[] { shared });
        var resolved = await service.ResolveAsync("org", library, "/shared.md", default);
        await Assert.That(resolved!.Local).IsEqualTo(local);
        var listed = await service.ListAsync("org", library, default);
        await Assert.That(listed.Count).IsEqualTo(1);
        await Assert.That(listed[0].Id).IsEqualTo("doc");
        var listResult = await new ListDocumentsHandler(store, pub, sources).HandleAsync(
            "org",
            "kb",
            default
        );
        var rows = ((Ok<DocumentResponse[]>)listResult.Result).Value!;
        await Assert.That(rows.Length).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo("doc");
        var contentResult = await new GetDocumentContentHandler(store, pub, sources).HandleAsync(
            "org",
            "kb",
            "/shared.md",
            default
        );
        await Assert
            .That(((Ok<DocumentContentResponse>)contentResult.Result).Value!.Content)
            .IsEqualTo("old\n");
        await Assert.That(shared.Content).IsEqualTo("public\n");
    }

    [Test]
    public async Task PublicOverlay_UniqueLocalAndPublicBothVisible()
    {
        var library = Library("src");
        var (store, pub, sources, service) = Setup(library);
        var local = Local();
        var shared = Shared();
        store
            .GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>())
            .Returns(local);
        store
            .ListDocumentsAsync("org", "lib", Arg.Any<CancellationToken>())
            .Returns(new[] { local });
        pub.GetByPathAsync("src", "/shared.md", Arg.Any<CancellationToken>()).Returns(shared);
        pub.ListSummariesBySourceAsync("src", Arg.Any<CancellationToken>())
            .Returns(new[] { shared });
        var list = await new ListDocumentsHandler(store, pub, sources).HandleAsync(
            "org",
            "kb",
            default
        );
        await Assert
            .That(((Ok<DocumentResponse[]>)list.Result).Value!.Select(d => d.Path))
            .IsEquivalentTo(["/local.md", "/shared.md"]);
        var localResult = await new GetDocumentContentHandler(store, pub, sources).HandleAsync(
            "org",
            "kb",
            "/local.md",
            default
        );
        var publicResult = await new GetDocumentContentHandler(store, pub, sources).HandleAsync(
            "org",
            "kb",
            "/shared.md",
            default
        );
        await Assert
            .That(((Ok<DocumentContentResponse>)localResult.Result).Value!.Content)
            .IsEqualTo("old\n");
        await Assert
            .That(((Ok<DocumentContentResponse>)publicResult.Result).Value!.Content)
            .IsEqualTo("public\n");
        await Assert
            .That((await service.ResolveAsync("org", library, "/shared.md", default))!.Public!.Id)
            .IsEqualTo("shared");
    }

    [Test]
    public async Task Metadata_SanitizesGithubAndBuildsNotionLink()
    {
        var doc = Local();
        var github = new AuthoringDocument(
            null,
            Shared(),
            "https://user:secret@github.com/acme/docs.git"
        );
        var info = DocumentAuthoringMetadata.Describe(github);
        await Assert
            .That(info.Origin)
            .IsEqualTo("https://github.com/acme/docs/blob/HEAD/shared.md");
        await Assert
            .That(info.OriginEdit)
            .IsEqualTo("https://github.com/acme/docs/edit/HEAD/shared.md");
        var privateDoc = Local("/docs/readme.md");
        privateDoc.Authoring = LibraryDocumentAuthoring.Synced;
        privateDoc.SourceOrigin = new LibraryDocumentSourceOrigin(
            "GitHub",
            "https://github.com/acme/docs.git",
            "Docs/README.md"
        );
        var privateInfo = DocumentAuthoringMetadata.Describe(new(privateDoc, null, null));
        await Assert
            .That(privateInfo.Origin)
            .IsEqualTo("https://github.com/acme/docs/blob/HEAD/Docs/README.md");
        privateDoc.SourceOrigin = new LibraryDocumentSourceOrigin(
            "GitHub",
            "https://github.com/acme/docs.git"
        );
        var legacy = DocumentAuthoringMetadata.Describe(new(privateDoc, null, null));
        await Assert
            .That(legacy.Origin)
            .IsEqualTo("https://github.com/acme/docs/blob/HEAD/docs/readme.md");
        doc.SourceExternalId = "12345678-1234-1234-1234-123456789abc";
        var notion = DocumentAuthoringMetadata.Describe(new(doc, null, null));
        await Assert.That(notion.Source).IsEqualTo("Notion");
        await Assert
            .That(notion.Origin)
            .IsEqualTo("https://www.notion.so/12345678123412341234123456789abc");
        await Assert.That(notion.Editable).IsFalse();
        var notionResponse = DocumentLibraryMcpTools.RenderUpdate(
            "kb",
            new AuthoringResult(
                AuthoringStatus.NotEditable,
                new AuthoringDocument(doc, null, null)
            ),
            "why"
        );
        await Assert
            .That(notionResponse)
            .Contains("origin: https://www.notion.so/12345678123412341234123456789abc");
        await Assert.That(notionResponse).DoesNotContain("originEdit:");
        var githubResponse = DocumentLibraryMcpTools.RenderUpdate(
            "kb",
            new AuthoringResult(AuthoringStatus.NotEditable, github),
            "why"
        );
        await Assert
            .That(githubResponse)
            .Contains("originEdit: https://github.com/acme/docs/edit/HEAD/shared.md");
    }

    [Test]
    public async Task AuthoringTools_WithoutBetaHeader_ReturnStaleAndAppliedWithClientAudit()
    {
        var (store, _, _, service) = Setup(Library());
        var original = Local();
        store
            .GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>())
            .Returns(original);
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new ReplaceContentResult.Applied(call.ArgAt<LibraryDocument>(0)));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var actor = new AuditActorContext { Current = Actor };
        http.HttpContext.Items[McpRequestTelemetryContext.HttpContextItemKey] =
            new McpRequestTelemetryContext("test", "example-client", "1");
        var stale = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/local.md",
            "old",
            "@@ -1 +1 @@\n-old\n+new\n",
            "why"
        );
        await Assert.That(stale.Content.Count).IsEqualTo(2);
        await Assert.That(((TextContentBlock)stale.Content[0]).Text).Contains("status: stale");
        await Assert.That(stale.IsError).IsTrue();
        using var current = JsonDocument.Parse(((TextContentBlock)stale.Content[1]).Text);
        await Assert
            .That(current.RootElement.GetProperty("content").GetString())
            .IsEqualTo("old\n");
        var applied = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/local.md",
            original.ContentHash!,
            "@@ -1 +1 @@\n-old\n+new\n",
            "why"
        );
        await Assert.That(((TextContentBlock)applied.Content[0]).Text).Contains("status: applied");
        await Assert.That(applied.IsError).IsNotEqualTo(true);
        await store
            .Received(1)
            .TryReplaceContentAsync(
                Arg.Is<LibraryDocument>(d => d.Content == "new\n"),
                original.ContentHash!,
                "why",
                Actor,
                "example-client",
                Arg.Any<CancellationToken>()
            );
        await Assert.That(original.Content).IsEqualTo("old\n");
    }

    [Test]
    public async Task EditFailureAfterStoreCall_ReportsSaveAsUnknown()
    {
        var (store, _, _, service) = Setup(Library());
        var original = Local();
        store
            .GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>())
            .Returns(original);
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException<ReplaceContentResult>(
                    new InvalidOperationException("cache failure")
                )
            );
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var result = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            new AuditActorContext { Current = Actor },
            http,
            User(),
            "kb",
            "/local.md",
            original.ContentHash!,
            "@@ -1 +1 @@\n-old\n+new\n",
            "why"
        );
        var message = ((TextContentBlock)result.Content[0]).Text;
        await Assert.That(message).Contains("saved: unknown");
        await Assert.That(message).Contains("read the path before retrying");
    }

    [Test]
    public async Task Create_InvalidAndExistingPathsAreReported()
    {
        var (store, _, _, service) = Setup(Library());
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var actor = new AuditActorContext { Current = Actor };
        var invalid = await DocumentLibraryMcpTools.CreateDocument(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "../bad",
            "body",
            "why"
        );
        await Assert.That(((TextContentBlock)invalid.Content[0]).Text).Contains("invalid_input");
        var existing = Local();
        store
            .GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>())
            .Returns(existing);
        var exists = await DocumentLibraryMcpTools.CreateDocument(
            store,
            service,
            actor,
            http,
            User(),
            "kb",
            "/local.md",
            "body",
            "why"
        );
        await Assert.That(((TextContentBlock)exists.Content[0]).Text).Contains("status: exists");
        await Assert.That(exists.IsError).IsTrue();
    }

    [Test]
    public async Task RejectionResponses_IncludeStableCodes()
    {
        (AuthoringStatus Status, string Code, DiffOutcome Outcome)[] cases =
        [
            (AuthoringStatus.InvalidDiff, "invalid_diff", new DiffOutcome.InvalidDiff("detail")),
            (AuthoringStatus.ThinContext, "thin_context", new DiffOutcome.ThinContext("detail")),
            (AuthoringStatus.NoMatch, "no_match", new DiffOutcome.NoMatch("detail")),
            (AuthoringStatus.Ambiguous, "ambiguous", new DiffOutcome.Ambiguous("detail")),
            (AuthoringStatus.OutOfOrder, "out_of_order", new DiffOutcome.OutOfOrder("detail")),
            (AuthoringStatus.EmptyResult, "empty_result", new DiffOutcome.EmptyResult("detail")),
            (AuthoringStatus.TooLarge, "too_large", new DiffOutcome.TooLarge("detail")),
            (AuthoringStatus.ApplyFailed, "apply_failed", new DiffOutcome.ApplyFailed("detail")),
        ];
        foreach (var (status, code, outcome) in cases)
        {
            var message = DocumentLibraryMcpTools.RenderUpdate(
                "kb",
                new AuthoringResult(status, Outcome: outcome),
                "reason"
            );
            await Assert.That(message).Contains($"code: {code}");
            await Assert.That(message).Contains("saved: false");
        }
        await Assert
            .That(
                DocumentLibraryMcpTools.RenderUpdate(
                    "kb",
                    new AuthoringResult(
                        AuthoringStatus.OutOfOrder,
                        Outcome: new DiffOutcome.OutOfOrder("Hunk 2 is out of order.")
                    ),
                    "why"
                )
            )
            .Contains("Order the hunks");
        await Assert
            .That(
                DocumentLibraryMcpTools.RenderUpdate(
                    "kb",
                    new AuthoringResult(
                        AuthoringStatus.EmptyResult,
                        Outcome: new DiffOutcome.EmptyResult("empty")
                    ),
                    "why"
                )
            )
            .Contains("not empty");
        await Assert
            .That(
                DocumentLibraryMcpTools.RenderUpdate(
                    "kb",
                    new AuthoringResult(
                        AuthoringStatus.TooLarge,
                        Outcome: new DiffOutcome.TooLarge("limit")
                    ),
                    "why"
                )
            )
            .Contains("meet the stated limit");
        await Assert
            .That(
                DocumentLibraryMcpTools.RenderUpdate(
                    "kb",
                    new AuthoringResult(
                        AuthoringStatus.NoMatch,
                        Outcome: new DiffOutcome.NoMatch("Hunk 2 does not match.")
                    ),
                    "why"
                )
            )
            .Contains("Hunk 2 does not match.");
        await Assert
            .That(
                DocumentLibraryMcpTools.RenderUpdate(
                    "kb",
                    new AuthoringResult(
                        AuthoringStatus.Ambiguous,
                        Outcome: new DiffOutcome.Ambiguous("Hunk 3 matches more than once.")
                    ),
                    "why"
                )
            )
            .Contains("Hunk 3 matches more than once.");
    }

    [Test]
    public async Task PublicDocumentUpdate_ReturnsOriginAndDoesNotWrite()
    {
        var library = Library("src");
        var (store, pub, _, service) = Setup(library);
        pub.GetByPathAsync("src", "/shared.md", Arg.Any<CancellationToken>()).Returns(Shared());
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var result = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            new AuditActorContext { Current = Actor },
            http,
            User(),
            "kb",
            "/shared.md",
            "hash",
            "patch",
            "why"
        );
        var message = ((TextContentBlock)result.Content[0]).Text;
        await Assert.That(message).Contains("code: not_editable");
        await Assert.That(message).Contains("https://github.com/acme/docs/edit/HEAD/shared.md");
        await store
            .DidNotReceiveWithAnyArgs()
            .TryReplaceContentAsync(default!, default!, default!, default!, default, default);
    }

    [Test]
    public async Task AuthoringTelemetry_ReportsOutcomeWithoutContentOrDiff()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Zeeq",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(traces);
        var measurements = new ConcurrentQueue<string>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "zeeq_update_document_by_diff_total")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<int>(
            (instrument, value, tags, state) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "outcome")
                        measurements.Enqueue(tag.Value?.ToString() ?? "");
            }
        );
        meter.Start();
        var (store, pub, sources, _) = Setup(Library());
        var doc = Local(content: "private-content\n");
        store.GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>()).Returns(doc);
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new ReplaceContentResult.Applied(call.ArgAt<LibraryDocument>(0)));
        var logs = new CapturingLogger<DocumentAuthoringService>();
        var service = new DocumentAuthoringService(store, pub, sources, Editor(), logs);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var result = await DocumentLibraryMcpTools.UpdateDocumentByDiff(
            store,
            service,
            new AuditActorContext { Current = AuditActor.Worker("telemetry-unique") },
            http,
            User(),
            "kb",
            "/local.md",
            doc.ContentHash!,
            "@@ -1 +1 @@\n-private-content\n+private-diff\n",
            "private-rationale"
        );
        await Assert.That(((TextContentBlock)result.Content[0]).Text).Contains("status: applied");
        await Assert.That(measurements).Contains("applied");
        var edit = activities.Single(a =>
            a.DisplayName == "Zeeq.Documents.Edit"
            && Equals(a.GetTagItem("actor.id"), "telemetry-unique")
        );
        await Assert.That(edit.GetTagItem("outcome")?.ToString()).IsEqualTo("applied");
        await Assert
            .That(edit.Events.Select(e => e.Name))
            .IsEquivalentTo(Enumerable.Repeat("diff.guard", 8));
        await Assert
            .That(
                edit.Events.SelectMany(e => e.Tags)
                    .Where(t => t.Key == "guard")
                    .Select(t => t.Value?.ToString() ?? "")
            )
            .IsEquivalentTo(
                new[]
                {
                    "EligibilityGuard",
                    "BaseHashGuard",
                    "ParseGuard",
                    "EmptyBaseGuard",
                    "ContextLinesGuard",
                    "UniqueOldSideGuard",
                    "ResultCheckGuard",
                    "TextDiffApplier",
                }
            );
        var traceData = string.Join(
            " ",
            edit.TagObjects.Select(t => $"{t.Key}:{t.Value}")
                .Concat(edit.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}:{t.Value}")))
        );
        var logData = string.Join(" ", logs.Messages);
        await Assert.That(traceData).DoesNotContain("private-content");
        await Assert.That(traceData).DoesNotContain("private-diff");
        await Assert.That(logData).DoesNotContain("private-content");
        await Assert.That(logData).DoesNotContain("private-diff");
        await Assert.That(logData).DoesNotContain("private-rationale");
    }

    [Test]
    public async Task AuthoringFailures_RedactRequestAndExceptionFromTrace()
    {
        using var activity = new Activity("test").Start();
        SetupMcpFiltersExtensions.RecordMcpFailure(
            activity,
            new InvalidOperationException("private-content"),
            "update_document_by_diff",
            "test",
            "private-diff"
        );
        var serialized = string.Join(
            " ",
            activity.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}:{t.Value}"))
        );
        await Assert.That(serialized).DoesNotContain("private-content");
        await Assert.That(serialized).DoesNotContain("private-diff");
        await Assert.That(activity.Events.Single().Name).IsEqualTo("authoring.failure");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    [Test]
    public async Task UpdateSdkTool_InjectsTrustedServicesAndReturnsResult()
    {
        var (store, _, _, service) = Setup(Library());
        var doc = Local();
        store.GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>()).Returns(doc);
        store
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AuditActor>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new ReplaceContentResult.Applied(call.ArgAt<LibraryDocument>(0)));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items[McpRequestTelemetryContext.HttpContextItemKey] =
            new McpRequestTelemetryContext("test", "sdk-client", "1");
        var actors = new AuditActorContext { Current = Actor };
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(service)
            .AddSingleton<IAuditActorAccessor>(actors)
            .AddSingleton<IHttpContextAccessor>(http)
            .BuildServiceProvider();
        var tool = McpServerTool.Create(
            typeof(DocumentLibraryMcpTools).GetMethod(
                nameof(DocumentLibraryMcpTools.UpdateDocumentByDiff)
            )!,
            options: new McpServerToolCreateOptions { Services = services }
        );
        var schema = tool.ProtocolTool.InputSchema.ToString();
        await Assert.That(schema).DoesNotContain("actors");
        await Assert.That(schema).DoesNotContain("authoring");
        await Assert.That(schema).Contains("baseHash");
        var args = new CallToolRequestParams
        {
            Name = "update_document_by_diff",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["library"] = JsonSerializer.SerializeToElement("kb"),
                ["path"] = JsonSerializer.SerializeToElement("/local.md"),
                ["baseHash"] = JsonSerializer.SerializeToElement(doc.ContentHash),
                ["diff"] = JsonSerializer.SerializeToElement("@@ -1 +1 @@\n-old\n+new\n"),
                ["rationale"] = JsonSerializer.SerializeToElement("why"),
            },
        };
        await using var server = McpServer.Create(
            Substitute.For<ITransport>(),
            new McpServerOptions(),
            serviceProvider: services
        );
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            args
        )
        {
            Services = services,
            User = User(),
        };
        var result = await tool.InvokeAsync(context);
        await Assert.That(((TextContentBlock)result.Content[0]).Text).Contains("status: applied");
        await store
            .Received(1)
            .TryReplaceContentAsync(
                Arg.Any<LibraryDocument>(),
                doc.ContentHash!,
                "why",
                Actor,
                "sdk-client",
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReadSdkTool_ReturnsTwoTextBlocks()
    {
        var (store, _, _, service) = Setup(Library());
        var doc = Local();
        store.GetByPathAsync("org", "lib", "/local.md", Arg.Any<CancellationToken>()).Returns(doc);
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(service)
            .BuildServiceProvider();
        var tool = McpServerTool.Create(
            typeof(DocumentLibraryMcpTools).GetMethod(
                nameof(DocumentLibraryMcpTools.ReadDocumentByPath)
            )!,
            options: new McpServerToolCreateOptions { Services = services }
        );
        var args = new CallToolRequestParams
        {
            Name = "read_document_by_path",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["library"] = JsonSerializer.SerializeToElement("kb"),
                ["path"] = JsonSerializer.SerializeToElement("/local.md"),
            },
        };
        await using var server = McpServer.Create(
            Substitute.For<ITransport>(),
            new McpServerOptions(),
            serviceProvider: services
        );
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            args
        )
        {
            Services = services,
            User = User(),
        };
        var result = await tool.InvokeAsync(context);
        await Assert.That(result.Content.Count).IsEqualTo(2);
        await Assert.That(((TextContentBlock)result.Content[0]).Text).IsEqualTo("old\n");
        await Assert
            .That(((TextContentBlock)result.Content[1]).Text)
            .Contains("zeeq_document_meta");
    }
}
