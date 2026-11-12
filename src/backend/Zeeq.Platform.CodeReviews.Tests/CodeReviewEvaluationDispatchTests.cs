using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationDispatchTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RacedExistingRequestKeyWithDifferentInputsReturnsConflict(bool changeDraft)
    {
        var fixture = new Fixture();
        // Capture the original request's real admission fingerprint, then simulate a competing
        // request winning between the pre-admission lookup and this request's admission.
        await fixture.Handler.CreateAsync(
            "org",
            "repo",
            fixture.Request,
            fixture.User,
            CancellationToken.None
        );
        var admitted = fixture.Admission!;
        var manifest = new CodeReviewEvaluationRunManifest(
            1,
            "org",
            "repo",
            "dataset",
            1,
            "agent",
            "reviewer",
            "facet",
            null,
            admitted.ReviewerConfiguration,
            admitted.ReviewerSelection,
            admitted.JudgeSelection,
            admitted.JudgeRubricVersion,
            admitted.SharedSystemPrompt,
            admitted.SharedSystemPromptHash,
            admitted.RepositoryConfigurationJson,
            admitted.LibraryIds,
            admitted.BuildVersion,
            admitted.SystemPromptVersion,
            "user",
            admitted.RequestFingerprint,
            DateTimeOffset.UtcNow,
            null,
            []
        );
        fixture
            .Runs.FindByRequestKeyAsync("org", "key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeReviewEvaluationRunDetail?>(null));
        fixture
            .Runs.AdmitAsync(
                Arg.Any<CodeReviewEvaluationRunAdmission>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationRunAdmissionResult(
                    CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey,
                    fixture.Run,
                    null
                )
            );
        fixture
            .Runs.FindAsync("org", "run", Arg.Any<CancellationToken>())
            .Returns(new CodeReviewEvaluationRunDetail(fixture.Run, manifest));
        fixture.Publisher.ClearReceivedCalls();
        fixture.Runs.ClearReceivedCalls();
        var changed = changeDraft
            ? fixture.Request with
            {
                Draft = fixture.Request.Draft! with { Prompt = "different draft" },
            }
            : fixture.Request with
            {
                ExpectedVersion = 2,
            };

        var result = await fixture.Handler.CreateAsync(
            "org",
            "repo",
            changed,
            fixture.User,
            CancellationToken.None
        );

        await Assert
            .That(
                result is Conflict<EvaluationRunConflictDto> conflict
                    && conflict.Value!.Code == "request_key_conflict"
            )
            .IsTrue();
        await fixture
            .Runs.Received(1)
            .FindByRequestKeyAsync("org", "key", Arg.Any<CancellationToken>());
        await fixture
            .Runs.Received(1)
            .AdmitAsync(Arg.Any<CodeReviewEvaluationRunAdmission>(), Arg.Any<CancellationToken>());
        await fixture.Runs.Received(1).FindAsync("org", "run", Arg.Any<CancellationToken>());
        await fixture
            .Publisher.DidNotReceive()
            .PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task PublishFailureMarksQueuedRunDispatchFailed()
    {
        var fixture = new Fixture();
        fixture
            .Publisher.PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException(new InvalidOperationException("controlled publish failure"))
            );
        fixture
            .Runs.MarkDispatchFailedAsync(
                "org",
                "run",
                "DispatchFailed",
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                fixture.Run.State = CodeReviewEvaluationRunState.DispatchFailed;
                return fixture.Run;
            });

        var result = await fixture.Handler.CreateAsync(
            "org",
            "repo",
            fixture.Request,
            fixture.User,
            CancellationToken.None
        );

        await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(503);
        await Assert.That(fixture.Run.State).IsEqualTo(CodeReviewEvaluationRunState.DispatchFailed);
        await fixture
            .Runs.Received(1)
            .MarkDispatchFailedAsync("org", "run", "DispatchFailed", Arg.Any<CancellationToken>());
        await fixture
            .Publisher.Received(1)
            .PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReplayAfterTierRemapReturnsOriginalRunAndChangedInputConflicts()
    {
        var fixture = new Fixture();
        var first = await fixture.Handler.CreateAsync(
            "org",
            "repo",
            fixture.Request,
            fixture.User,
            CancellationToken.None
        );
        await Assert.That(((IStatusCodeHttpResult)first).StatusCode).IsEqualTo(202);
        var admission = fixture.Admission!;
        var manifest = new CodeReviewEvaluationRunManifest(
            1,
            "org",
            "repo",
            "dataset",
            1,
            "agent",
            "reviewer",
            "facet",
            null,
            admission.ReviewerConfiguration,
            admission.ReviewerSelection,
            admission.JudgeSelection,
            admission.JudgeRubricVersion,
            admission.SharedSystemPrompt,
            admission.SharedSystemPromptHash,
            admission.RepositoryConfigurationJson,
            admission.LibraryIds,
            admission.BuildVersion,
            admission.SystemPromptVersion,
            "user",
            admission.RequestFingerprint,
            DateTimeOffset.UtcNow,
            null,
            []
        );
        fixture
            .Runs.FindByRequestKeyAsync("org", "key", Arg.Any<CancellationToken>())
            .Returns(new CodeReviewEvaluationRunDetail(fixture.Run, manifest));
        fixture
            .Settings.FindConfigurationAsync("org", Arg.Any<CancellationToken>())
            .Returns(
                new OrganizationLlmConfiguration
                {
                    Fast = OrganizationLlmConfiguration.Default.Fast,
                    High = new()
                    {
                        Provider = "OpenAI",
                        Model = "changed-model",
                        KeyId = "changed-key",
                    },
                    Max = OrganizationLlmConfiguration.Default.Max,
                }
            );
        fixture.Settings.ClearReceivedCalls();
        fixture.Publisher.ClearReceivedCalls();

        var replay = await fixture.Handler.CreateAsync(
            "org",
            "repo",
            fixture.Request,
            fixture.User,
            CancellationToken.None
        );
        var changed = await fixture.Handler.CreateAsync(
            "org",
            "repo",
            fixture.Request with
            {
                ExpectedVersion = 2,
            },
            fixture.User,
            CancellationToken.None
        );

        await Assert.That(((IStatusCodeHttpResult)replay).StatusCode).IsEqualTo(202);
        await Assert.That(((IStatusCodeHttpResult)changed).StatusCode).IsEqualTo(409);
        await Assert
            .That(replay is JsonHttpResult<EvaluationRunDto> json && json.Value!.Id == "run")
            .IsTrue();
        await fixture
            .Settings.DidNotReceive()
            .FindConfigurationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture
            .Publisher.DidNotReceive()
            .PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            );
        await fixture
            .Runs.Received(1)
            .AdmitAsync(Arg.Any<CodeReviewEvaluationRunAdmission>(), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        public readonly ICodeReviewEvaluationRunStore Runs =
            Substitute.For<ICodeReviewEvaluationRunStore>();
        public readonly IZeeqMessagePublisher Publisher = Substitute.For<IZeeqMessagePublisher>();
        public readonly ILlmSettingsStore Settings = Substitute.For<ILlmSettingsStore>();
        public readonly CodeReviewEvaluationRun Run = new()
        {
            Id = "run",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "key",
            State = CodeReviewEvaluationRunState.Queued,
        };
        public readonly CreateEvaluationRunRequest Request = new(
            "dataset",
            1,
            "agent",
            null,
            new("reviewer", "facet", CodeReviewModelTier.High, "draft prompt", true, new()),
            "key"
        );
        public readonly ClaimsPrincipal User = new(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "user")], "test")
        );
        public CodeReviewEvaluationRunAdmission? Admission { get; private set; }
        public EvaluationRunHandler Handler { get; }

        public Fixture()
        {
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "membership",
                            OrganizationId = "org",
                            UserId = "user",
                            Role = "owner",
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "user",
                        },
                    ])
                );
            var repositories = Substitute.For<ICodeRepositoryStore>();
            repositories
                .FindActiveForOrganizationAsync("org", "repo", Arg.Any<CancellationToken>())
                .Returns(
                    new CodeRepository
                    {
                        Id = "repo",
                        OrganizationId = "org",
                        Provider = "github",
                        OwnerQualifiedName = "owner/repo",
                        DisplayName = "repo",
                        Enabled = true,
                    }
                );
            var agents = Substitute.For<ICodeReviewerAgentStore>();
            agents
                .FindAsync("org", "agent", Arg.Any<CancellationToken>())
                .Returns(
                    new CodeReviewerAgent
                    {
                        Id = "agent",
                        OrganizationId = "org",
                        RepositoryId = "repo",
                        DisplayName = "reviewer",
                        ReviewFacet = "facet",
                        ModelTier = CodeReviewModelTier.High,
                        Prompt = "saved prompt",
                    }
                );
            Settings
                .FindConfigurationAsync("org", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<OrganizationLlmConfiguration?>(null));
            Runs.AdmitAsync(
                    Arg.Any<CodeReviewEvaluationRunAdmission>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    Admission = call.Arg<CodeReviewEvaluationRunAdmission>();
                    return new CodeReviewEvaluationRunAdmissionResult(
                        CodeReviewEvaluationRunAdmissionOutcome.Admitted,
                        Run,
                        null
                    );
                });
            var client = Substitute.For<IChatClient>();
            var resolver = new CodeReviewLlmTierResolver(
                Settings,
                null!,
                null!,
                new(client, client, client),
                new LlmSettings(),
                NullLogger<CodeReviewLlmTierResolver>.Instance
            );
            Handler = new(
                new(memberships),
                repositories,
                agents,
                Substitute.For<ICodeReviewerAgentRevisionStore>(),
                Runs,
                Substitute.For<ICodeReviewEvaluationCaseResultStore>(),
                Substitute.For<ICodeReviewEvaluationArtifactStore>(),
                resolver,
                Publisher,
                new CodeReviewSettings { EvaluationsEnabled = true }
            );
        }
    }
}
