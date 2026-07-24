using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewExecutionContractTests
{
    [Test]
    public async Task UserPromptFrom_WithNoSharedPromptFragment_RendersSelfClosingGuidanceTag()
    {
        var context = ExecutionContext();
        var input = context.ToPromptInput([]);

        var prompt = CodeReviewUserPrompt.From(input);

        await Assert.That(prompt.SharedPullRequestPromptBody).Contains("<organization_guidance />");
    }

    [Test]
    public async Task UserPromptFrom_WithSharedPromptFragment_RendersOrganizationGuidanceBlock()
    {
        var context = ExecutionContext(sharedPromptFragment: "Always flag missing null checks.");
        var input = context.ToPromptInput([]);

        var prompt = CodeReviewUserPrompt.From(input);

        await Assert.That(prompt.SharedPullRequestPromptBody).Contains("<organization_guidance>");
        await Assert
            .That(prompt.SharedPullRequestPromptBody)
            .Contains("Always flag missing null checks.");
    }

    [Test]
    public async Task FileFilter_WithEmptyFilter_KeepsAllFilesInScope()
    {
        var scope = CodeReviewFileFilterEvaluator.Apply(Files(), CodeReviewFileFilter.Empty);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo([
                "src/backend/Code.cs",
                "docs/readme.md",
                "web/src/generated/api.ts",
                "src/backend/Old.cs",
            ]);
        await Assert.That(scope.OutOfScopeFiles).IsEmpty();
    }

    [Test]
    public async Task FileFilter_WithIncludeOnly_AllowsMatchingFilesOnly()
    {
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles =
            [
                new()
                {
                    MatchType = CodeReviewFileNameMatchType.PathPrefix,
                    Pattern = "src/backend",
                },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(Files(), filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/backend/Code.cs", "src/backend/Old.cs"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["docs/readme.md", "web/src/generated/api.ts"]);
    }

    [Test]
    public async Task FileFilter_WithExcludeOnly_RemovesMatchingFiles()
    {
        var filter = new CodeReviewFileFilter
        {
            ExcludedFiles =
            [
                new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".md" },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(Files(), filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo([
                "src/backend/Code.cs",
                "web/src/generated/api.ts",
                "src/backend/Old.cs",
            ]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["docs/readme.md"]);
    }

    [Test]
    public async Task FileFilter_WithIncludeAndExclude_ExcludeWins()
    {
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles =
            [
                new()
                {
                    MatchType = CodeReviewFileNameMatchType.Glob,
                    Pattern = "src/backend/*.cs",
                },
            ],
            ExcludedFiles =
            [
                new()
                {
                    MatchType = CodeReviewFileNameMatchType.ExactPath,
                    Pattern = "src/backend/Old.cs",
                },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(Files(), filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/backend/Code.cs"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["docs/readme.md", "web/src/generated/api.ts", "src/backend/Old.cs"]);
    }

    [Test]
    public async Task FileFilter_GlobWithRecursiveWildcard_MatchesFileDirectlyInDirectory()
    {
        // "**" must match zero intervening path segments, not require at
        // least one — the same real recursive-glob semantics IngestFileFilter
        // now shares via Microsoft.Extensions.FileSystemGlobbing.
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles =
            [
                new() { MatchType = CodeReviewFileNameMatchType.Glob, Pattern = "docs/**/*.md" },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(Files(), filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["docs/readme.md"]);
    }

    [Test]
    public async Task FileFilter_GlobWithRecursiveWildcard_AlsoMatchesDeeplyNestedFile()
    {
        // Same contract as the direct-child case above, for the other shape
        // "**" must cover: arbitrarily deep nesting, not just zero segments.
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "docs/nested/deep/guide.md",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+# Guide"
            ),
            new CodeReviewFileSnapshot(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles =
            [
                new() { MatchType = CodeReviewFileNameMatchType.Glob, Pattern = "docs/**/*.md" },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["docs/nested/deep/guide.md"]);
    }

    [Test]
    public async Task FileFilter_WithEmptyFilter_ExcludesBaselineLockfilesAndBuildOutput()
    {
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
            new CodeReviewFileSnapshot(
                "yarn.lock",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+dep@1.0.0:"
            ),
            new CodeReviewFileSnapshot(
                "src/web/package-lock.json",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+{}"
            ),
            new CodeReviewFileSnapshot(
                "src/web/node_modules/dep/index.js",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+module.exports = {};"
            ),
            new CodeReviewFileSnapshot(
                "src/backend/Migrations/20260101000000_Init.Designer.cs",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+// <auto-generated />"
            ),
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, CodeReviewFileFilter.Empty);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/backend/Code.cs"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo([
                "yarn.lock",
                "src/web/package-lock.json",
                "src/web/node_modules/dep/index.js",
                "src/backend/Migrations/20260101000000_Init.Designer.cs",
            ]);
    }

    [Test]
    public async Task FileFilter_WithExplicitIncludeMatch_OverridesBaselineExclusion()
    {
        // A repo that wants lockfile diffs reviewed (e.g. for supply-chain awareness)
        // opts back in via its own IncludedFiles allowlist rather than needing the
        // baseline default-exclusion list changed.
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "yarn.lock",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+dep@1.0.0:"
            ),
            new CodeReviewFileSnapshot(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles = [new() { MatchType = CodeReviewFileNameMatchType.Glob, Pattern = "*.lock" }],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert.That(scope.InScopeFiles.Select(file => file.Path)).IsEquivalentTo(["yarn.lock"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/backend/Code.cs"]);
    }

    [Test]
    public async Task FileFilter_WithRepoExcludeOverridingIncludedBaselineOverride_ExcludeStillWins()
    {
        // Repo ExcludedFiles is the highest-precedence rule: it wins even over a repo
        // IncludedFiles match that would otherwise override the baseline exclusion.
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "yarn.lock",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+dep@1.0.0:"
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles = [new() { MatchType = CodeReviewFileNameMatchType.Glob, Pattern = "*.lock" }],
            ExcludedFiles =
            [
                new() { MatchType = CodeReviewFileNameMatchType.ExactPath, Pattern = "yarn.lock" },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert.That(scope.InScopeFiles).IsEmpty();
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["yarn.lock"]);
    }

    [Test]
    public async Task FileFilter_WithBlanketExtensionInclude_DoesNotOverrideBaselineExclusion()
    {
        // Regression guard for the front-end "TypeScript" preset, whose IncludedFiles
        // allowlist is a bare Extension(".json") rule. That's a blanket "all .json
        // source files" scoping rule, not an explicit opt-in to reviewing
        // package-lock.json — only a targeted ExactPath/PathPrefix/Glob include should
        // be able to override a baseline exclusion.
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "package-lock.json",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+{}"
            ),
            new CodeReviewFileSnapshot(
                "src/config.json",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+{}"
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles = [new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".json" }],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/config.json"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["package-lock.json"]);
    }

    [Test]
    public async Task FileFilter_WithEmptyFilter_ExcludesMobilePlatformNoise()
    {
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "ios/App.xcworkspace/xcuserdata/me.xcuserdatad/state.xcuserstate",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+binary"
            ),
            new CodeReviewFileSnapshot(
                "android/local.properties",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+sdk.dir=/Users/me/Library/Android/sdk"
            ),
            new CodeReviewFileSnapshot(
                "android/app/app-release.apk",
                null,
                CodeReviewFileMutationState.Added,
                "@@ -0 +1\n+binary"
            ),
            new CodeReviewFileSnapshot(
                "src/main/kotlin/App.kt",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+fun main() {}"
            ),
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, CodeReviewFileFilter.Empty);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/main/kotlin/App.kt"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo([
                "ios/App.xcworkspace/xcuserdata/me.xcuserdatad/state.xcuserstate",
                "android/local.properties",
                "android/app/app-release.apk",
            ]);
    }

    [Test]
    public async Task FileFilter_WithEmptyFilter_ExcludesBinaryMutationStateRegardlessOfExtension()
    {
        // The baseline extension list can't enumerate every binary extension in existence
        // (.webp, .wasm, .so, extensionless binaries, ...) — MutationState.Binary is a
        // content-based catch-all for whatever the diff/PR source already detected.
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "assets/hero.webp",
                null,
                CodeReviewFileMutationState.Binary,
                ""
            ),
            new CodeReviewFileSnapshot(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, CodeReviewFileFilter.Empty);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["src/backend/Code.cs"]);
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["assets/hero.webp"]);
    }

    [Test]
    public async Task FileFilter_WithBlanketExtensionInclude_DoesNotOverrideBinaryExclusion()
    {
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "assets/hero.webp",
                null,
                CodeReviewFileMutationState.Binary,
                ""
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles = [new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".webp" }],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert.That(scope.InScopeFiles).IsEmpty();
        await Assert
            .That(scope.OutOfScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["assets/hero.webp"]);
    }

    [Test]
    public async Task FileFilter_WithExplicitPathInclude_OverridesBinaryExclusion()
    {
        var files = new[]
        {
            new CodeReviewFileSnapshot(
                "assets/hero.webp",
                null,
                CodeReviewFileMutationState.Binary,
                ""
            ),
        };
        var filter = new CodeReviewFileFilter
        {
            IncludedFiles =
            [
                new() { MatchType = CodeReviewFileNameMatchType.ExactPath, Pattern = "assets/hero.webp" },
            ],
        };

        var scope = CodeReviewFileFilterEvaluator.Apply(files, filter);

        await Assert
            .That(scope.InScopeFiles.Select(file => file.Path))
            .IsEquivalentTo(["assets/hero.webp"]);
    }

    [Test]
    public async Task XmlValidator_WithValidReviewsXml_DeserializesOutput()
    {
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate(
            """
            <reviews noAgentsActivated="false">
              <review facet="Structural" agent="Structural Reviewer">
                <summary><![CDATA[Looks good.]]></summary>
                <details><![CDATA[One issue exists.]]></details>
                <findings>
                  <finding level="MAJOR" file="src/example.cs" line="42" side="RIGHT">
                    <summary><![CDATA[Fix this.]]></summary>
                    <details><![CDATA[Detailed body.]]></details>
                  </finding>
                </findings>
              </review>
            </reviews>
            """
        );

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Output!.Reviews).Count().IsEqualTo(1);
        await Assert
            .That(result.Output.Reviews.Single().Findings.Single().Level)
            .IsEqualTo(CodeReviewFindingLevel.Major);
        await Assert
            .That(result.Output.Reviews.Single().Findings.Single().Summary)
            .IsEqualTo("Fix this.");
        await Assert
            .That(result.Output.Reviews.Single().Findings.Single().Details)
            .IsEqualTo("Detailed body.");
    }

    [Test]
    public async Task XmlValidatorSerialize_WithFindingContent_EmitsCDataForAllProseElements()
    {
        var xml = CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Facet = "General",
                        Agent = "Principal Software Engineer",
                        Summary = "Found one issue with Dictionary<string,int>.",
                        Details = "Details with & special chars.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Critical,
                                File = "src/App.cs",
                                Summary = "Unsafe XML-like output",
                                Details = "Markdown with <xml> and `List<string>`.",
                            },
                        ],
                    },
                ],
            }
        );

        await Assert.That(xml).Contains("level=\"CRITICAL\"");
        // Summary is now a child element with CDATA, not an attribute
        await Assert.That(xml).Contains("<![CDATA[Unsafe XML-like output]]>");
        await Assert.That(xml).Contains("<![CDATA[Markdown with <xml> and `List<string>`.]]>");
        await Assert.That(xml).Contains("<![CDATA[Found one issue with Dictionary<string,int>.]]>");
        await Assert.That(xml).Contains("<![CDATA[Details with & special chars.]]>");
        await Assert.That(xml).DoesNotContain("summary=\"Unsafe XML-like output\"");
        // Finding details serialized as <details>, not legacy <body>
        await Assert.That(xml).Contains("<details>");
        await Assert.That(xml).DoesNotContain("<body>");
    }

    [Test]
    public async Task XmlValidator_WithMalformedXml_ReturnsInvalidResult()
    {
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate("<reviews><review></reviews>");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.ErrorMessage).IsNotNull();
    }

    [Test]
    public async Task XmlValidator_WithEmptyReviewerAndFindingFields_ReturnsInvalidResult()
    {
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate(
            """
            <reviews noAgentsActivated="false">
              <review facet="" agent="">
                <summary />
                <details />
                <findings>
                  <finding level="CRITICAL" file="">
                    <summary></summary>
                    <details><![CDATA[]]></details>
                  </finding>
                </findings>
              </review>
            </reviews>
            """
        );

        await Assert.That(result.IsValid).IsFalse();
        await Assert
            .That(result.ErrorMessage)
            .IsEqualTo("review[0] must include a non-empty facet attribute.");
    }

    [Test]
    public async Task XmlValidator_WithFindingMissingLevel_ReturnsInvalidResult()
    {
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate(
            """
            <reviews noAgentsActivated="false">
              <review facet="General" agent="Principal Software Engineer">
                <summary><![CDATA[Found an issue.]]></summary>
                <details><![CDATA[One issue exists.]]></details>
                <findings>
                  <finding file="src/Program.cs">
                    <summary><![CDATA[Infinite loop blocks execution.]]></summary>
                    <details><![CDATA[The while loop never exits.]]></details>
                  </finding>
                </findings>
              </review>
            </reviews>
            """
        );

        await Assert.That(result.IsValid).IsFalse();
        await Assert
            .That(result.ErrorMessage)
            .IsEqualTo("review[0].finding[0] must include a non-empty level attribute.");
    }

    [Test]
    public async Task XmlValidator_WithLegacyFindingSummaryAttributeAndCDataBody_NormalizesAndValidates()
    {
        // Backward compatibility: stored artifacts written before the child-element convention
        // used summary="…" as an attribute and placed the body as direct CDATA text.
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate(
            """
            <reviews noAgentsActivated="false">
              <review facet="General" agent="Principal Software Engineer">
                <summary>Found an issue.</summary>
                <details>One issue exists.</details>
                <findings>
                  <finding level="CRITICAL" summary="Infinite loop blocks execution." file="src/Program.cs"><![CDATA[The while loop never exits.]]></finding>
                </findings>
              </review>
            </reviews>
            """
        );

        await Assert.That(result.IsValid).IsTrue();
        var finding = result.Output!.Reviews.Single().Findings.Single();
        await Assert.That(finding.Summary).IsEqualTo("Infinite loop blocks execution.");
        await Assert.That(finding.Details).IsEqualTo("The while loop never exits.");
    }

    [Test]
    public async Task XmlValidator_WithLegacyBodyElement_NormalizesToDetailsAndValidates()
    {
        // Backward compatibility: stored artifacts written before the <body> → <details> rename
        // must still deserialize correctly. NormalizeFindingShapes renames <body> → <details>
        // before XmlSerializer runs, so finding.Details is populated from the legacy element.
        var validator = new CodeReviewXmlOutputValidator();

        var result = validator.Validate(
            """
            <reviews noAgentsActivated="false">
              <review facet="General" agent="Principal Software Engineer">
                <summary><![CDATA[Found an issue.]]></summary>
                <details><![CDATA[One issue exists.]]></details>
                <findings>
                  <finding level="CRITICAL" file="src/Program.cs">
                    <summary><![CDATA[Infinite loop blocks execution.]]></summary>
                    <body><![CDATA[The while loop never exits.]]></body>
                  </finding>
                </findings>
              </review>
            </reviews>
            """
        );

        await Assert.That(result.IsValid).IsTrue();
        var finding = result.Output!.Reviews.Single().Findings.Single();
        await Assert.That(finding.Details).IsEqualTo("The while loop never exits.");
    }

    [Test]
    public async Task XmlValidator_WhenReviewerRetriesAreExhausted_CreatesValidPlaceholder()
    {
        var validator = new CodeReviewXmlOutputValidator();
        var agent = RuntimeAgent("agent_structural", "Structural", "Structural Reviewer");
        var placeholder = validator.CreateFailedReviewerPlaceholder(
            agent,
            "Malformed output after retries."
        );
        var xml = CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument { Reviews = [placeholder] }
        );

        var result = validator.Validate(xml);

        await Assert.That(result.IsValid).IsTrue();
        var review = result.Output!.Reviews.Single();
        var finding = review.Findings.Single();
        await Assert.That(review.Facet).IsEqualTo("Structural");
        await Assert.That(review.Agent).IsEqualTo("Structural Reviewer");
        await Assert.That(finding.File).IsEqualTo("(reviewer-output)");
        await Assert.That(finding.Summary).IsEqualTo("Reviewer output failed validation");
        await Assert.That(finding.Details).IsEqualTo("Malformed output after retries.");
        await Assert.That(xml).Contains("Reviewer output could not be validated.");
    }

    private static CodeReviewExecutionContext ExecutionContext(string sharedPromptFragment = "")
    {
        var files = Files();
        var scope = CodeReviewFileFilterEvaluator.Apply(
            files,
            new CodeReviewFileFilter
            {
                ExcludedFiles =
                [
                    new()
                    {
                        MatchType = CodeReviewFileNameMatchType.PathPrefix,
                        Pattern = "web/src/generated",
                    },
                ],
            }
        );

        return new(
            Review(),
            PullRequest(),
            new CodeReviewPullRequestSnapshot(
                "Add review pipeline",
                "The PR wires the runner contracts.",
                files,
                [
                    new CodeReviewDeveloperFeedbackComment(
                        "alice",
                        "Please check the concurrency path.",
                        DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
                        "https://github.example/comment/1",
                        "src/backend/Code.cs",
                        12
                    ),
                ]
            ),
            new CodeRepositoryReviewConfiguration { SharedPromptFragment = sharedPromptFragment },
            [RuntimeAgent("agent_structural", "Structural", "Structural Reviewer")],
            scope.InScopeFiles,
            scope.OutOfScopeFiles
        );
    }

    private static IReadOnlyList<CodeReviewFileSnapshot> Files() =>
        [
            new(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
            new("docs/readme.md", null, CodeReviewFileMutationState.Added, "@@ -0 +1\n+# Docs"),
            new(
                "web/src/generated/api.ts",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+export {}"
            ),
            new(
                "src/backend/Old.cs",
                null,
                CodeReviewFileMutationState.Deleted,
                "@@ -1 +0\n-var old = 1;"
            ),
        ];

    private static CodeReviewerRuntimeAgent RuntimeAgent(
        string id,
        string facet,
        string displayName
    ) =>
        new(
            id,
            displayName,
            facet,
            CodeReviewModelTier.High,
            "Review structure and correctness.",
            CodeReviewerActivationConfiguration.Empty
        );

    private static CodeReviewRecord Review() =>
        new()
        {
            Id = "cr_123",
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            UpdatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            OrganizationId = "org_123",
            TeamId = "team_123",
            PullRequestRecordId = "pr_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "wonderlydotcom/zeeq",
            PullRequestNumber = 42,
            Branch = "feature/code-review",
            Title = "Add review pipeline",
            AuthorLogin = "alice",
            Status = CodeReviewStatus.Pending,
            RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
            RemainingReviewBudget = 10,
        };

    private static PullRequestRecord PullRequest() =>
        new()
        {
            Id = "pr_123",
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            UpdatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "wonderlydotcom/zeeq",
            PullRequestNumber = 42,
            GitHubNodeId = "node_123",
            Title = "Add review pipeline",
            Branch = "feature/code-review",
            BaseBranch = "main",
            HeadSha = "abc123",
            AuthorLogin = "alice",
            HtmlUrl = "https://github.example/wonderlydotcom/zeeq/pull/42",
            State = PullRequestState.Open,
            CreatedFromWebhookAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            LastWebhookAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
        };
}
