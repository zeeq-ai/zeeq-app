using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for <see cref="SnippetIdentifierExtractor"/> — camelCase/PascalCase/snake_case/dotted
/// extraction, deduplication, length filtering, and the per-input cap.
/// </summary>
public sealed class SnippetIdentifierExtractorTests
{
    /// <summary>
    /// All tests in this file exercise index-time (code-snippet) extraction behavior, so they
    /// share the <see cref="SnippetIdentifierExtractor.IndexMinLength"/> cutoff via this wrapper
    /// rather than repeating it at every call site. Query-time behavior (
    /// <see cref="SnippetIdentifierExtractor.QueryMinLength"/>) is covered separately.
    /// </summary>
    private static string[] Extract(string? content) =>
        SnippetIdentifierExtractor.Extract(content, SnippetIdentifierExtractor.IndexMinLength);

    [Test]
    public async Task Extract_CamelCaseAndPascalCase_AreLoweredAndReturned()
    {
        var ids = Extract(
            "var repositoryUserName = ComputeRepositoryValue();"
        );

        await Assert.That(ids).Contains("repositoryusername");
        await Assert.That(ids).Contains("computerepositoryvalue");
    }

    [Test]
    public async Task Extract_SnakeCase_IsReturned()
    {
        var ids = Extract("total_repository_count = 5");

        await Assert.That(ids).Contains("total_repository_count");
    }

    [Test]
    public async Task Extract_DottedPath_ReturnsWholeAndComponents()
    {
        var ids = Extract(
            "ApplicationSettings.DatabaseConnection.Configuration.Open()"
        );

        await Assert.That(ids)
            .Contains(
                "applicationsettings.databaseconnection.configuration.open"
            );
        await Assert.That(ids).Contains("applicationsettings");
        await Assert.That(ids).Contains("databaseconnection");
    }

    [Test]
    public async Task Extract_ShortTokens_AreFiltered()
    {
        var ids = Extract("int a = b + cat;");

        await Assert.That(ids).DoesNotContain("a");
        await Assert.That(ids).DoesNotContain("b");
        await Assert.That(ids).DoesNotContain("cat");
    }

    [Test]
    public async Task Extract_Duplicates_AreDeduplicated()
    {
        var ids = Extract(
            "ComputeRepositoryValue(); ComputeRepositoryValue(); computerepositoryvalue();"
        );

        await Assert.That(ids.Count(id => id == "computerepositoryvalue")).IsEqualTo(1);
    }

    [Test]
    public async Task Extract_QueryMinLength_IsLowerThanIndexMinLength()
    {
        var text = "handler logger request repositoryname";

        var indexIds = SnippetIdentifierExtractor.Extract(
            text,
            SnippetIdentifierExtractor.IndexMinLength
        );
        var queryIds = SnippetIdentifierExtractor.Extract(
            text,
            SnippetIdentifierExtractor.QueryMinLength
        );

        // "handler"/"logger"/"request" (7 chars) clear QueryMinLength (6) but not
        // IndexMinLength (14) — only "repositoryname" (14 chars) survives both cutoffs.
        await Assert.That(indexIds).Contains("repositoryname");
        await Assert.That(indexIds).DoesNotContain("handler");
        await Assert.That(indexIds).DoesNotContain("logger");
        await Assert.That(indexIds).DoesNotContain("request");

        await Assert.That(queryIds).Contains("repositoryname");
        await Assert.That(queryIds).Contains("handler");
        await Assert.That(queryIds).Contains("logger");
        await Assert.That(queryIds).Contains("request");
    }

    [Test]
    public async Task Extract_Cap_IsEnforced()
    {
        var manyTokens = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"identifier{i:D4}"));

        var ids = Extract(manyTokens);

        await Assert.That(ids.Length).IsLessThanOrEqualTo(64);
    }

    [Test]
    public async Task Extract_NullOrBlank_ReturnsEmpty()
    {
        await Assert.That(Extract(null)).IsEmpty();
        await Assert.That(Extract("   ")).IsEmpty();
    }

    [Test]
    public async Task Extract_ReservedKeywords_AreExcluded()
    {
        var ids = Extract(
            "public partial class SomeRepositorySyncService(ILogger<SomeRepositorySyncService> log) { private partial void LogRepositorySyncError(string repositoryName, string repositoryLocation); }"
        );

        await Assert.That(ids).DoesNotContain("public");
        await Assert.That(ids).DoesNotContain("partial");
        await Assert.That(ids).DoesNotContain("class");
        await Assert.That(ids).DoesNotContain("private");
        await Assert.That(ids).DoesNotContain("void");
        await Assert.That(ids).DoesNotContain("string");
        await Assert.That(ids).Contains("somerepositorysyncservice");
        await Assert.That(ids).Contains("logrepositorysyncerror");
        await Assert.That(ids).Contains("repositoryname");
        await Assert.That(ids).Contains("repositorylocation");
    }

    [Test]
    public async Task Extract_LineAndBlockComments_AreStrippedNotScanned()
    {
        var ids = Extract(
            """
            // Other code, they must be registered as self rather than by interface.
            /* another comment block with prose words */
            public partial class SomeRepositorySyncService { }
            """
        );

        await Assert.That(ids).DoesNotContain("other");
        await Assert.That(ids).DoesNotContain("code");
        await Assert.That(ids).DoesNotContain("registered");
        await Assert.That(ids).DoesNotContain("rather");
        await Assert.That(ids).DoesNotContain("interface");
        await Assert.That(ids).DoesNotContain("prose");
        await Assert.That(ids).DoesNotContain("words");
        await Assert.That(ids).Contains("somerepositorysyncservice");
    }

    [Test]
    public async Task Extract_HashComments_AreStrippedNotScanned()
    {
        var ids = Extract(
            "# Loads configuration values\ndef load_application_settings(config_file_path):\n    pass"
        );

        await Assert.That(ids).DoesNotContain("loads");
        await Assert.That(ids).DoesNotContain("configuration");
        await Assert.That(ids).DoesNotContain("values");
        await Assert.That(ids).Contains("load_application_settings");
        await Assert.That(ids).Contains("config_file_path");
    }

    [Test]
    public async Task Extract_StringLiteralContents_AreStrippedNotScanned()
    {
        var ids = Extract(
            """
            private partial void LogRepositorySyncError(string repositoryName, string repositoryUrl) =>
                logger.LogRepositoryError("Error synchronizing repository {RepositoryName} from {RepositoryUrl}.", repositoryName, repositoryUrl);
            """
        );

        await Assert.That(ids).DoesNotContain("synchronizing");
        await Assert.That(ids).DoesNotContain("repository");
        await Assert.That(ids).Contains("logrepositorysyncerror");
        await Assert.That(ids).Contains("logrepositoryerror");
        await Assert.That(ids).Contains("repositoryname");
    }

    [Test]
    public async Task Extract_FluentApiChain_ReturnsMemberAndCallNamesNotKeywords()
    {
        var ids = Extract(
            """
            extension(IServiceCollection services)
            {
                public IServiceCollection AddZeeqEndpoints()
                {
                    // Register all IEndpoint instances
                    var result = services.Scan(scan =>
                        scan.FromApplicationDependencies()
                            .AddClasses(classes => classes.AssignableTo<IEndpoint>())
                            .AsImplementedInterfaces()
                            .WithTransientLifetime()
                    );
                    return result;
                }
            }
            """
        );

        await Assert.That(ids).DoesNotContain("extension");
        await Assert.That(ids).DoesNotContain("public");
        await Assert.That(ids).DoesNotContain("var");
        await Assert.That(ids).DoesNotContain("return");
        await Assert.That(ids).DoesNotContain("register");
        await Assert.That(ids).DoesNotContain("instances");
        await Assert.That(ids).Contains("iservicecollection");
        await Assert.That(ids).Contains("addzeeqendpoints");
        await Assert.That(ids).Contains("fromapplicationdependencies");
        await Assert.That(ids).Contains("asimplementedinterfaces");
        await Assert.That(ids).Contains("withtransientlifetime");
    }

    [Test]
    public async Task Extract_Rust_ReturnsTypesAndFunctionsNotKeywords()
    {
        var ids = Extract(
            """
            pub struct RepositorySyncJob {
                pub repository_url: String,
            }

            impl RepositorySyncJob {
                pub async fn run_repository_sync(&self, repository_workspace: &mut RepositoryWorkspace) -> Result<SyncProcessOutcome, SyncProcessingError> {
                    let sync_outcome = repository_workspace.walk_repository_files().await?;
                    Ok(sync_outcome)
                }
            }
            """
        );

        await Assert.That(ids).DoesNotContain("pub");
        await Assert.That(ids).DoesNotContain("struct");
        await Assert.That(ids).DoesNotContain("impl");
        await Assert.That(ids).DoesNotContain("async");
        await Assert.That(ids).DoesNotContain("fn");
        await Assert.That(ids).DoesNotContain("let");
        await Assert.That(ids).DoesNotContain("mut");
        await Assert.That(ids).Contains("repositorysyncjob");
        await Assert.That(ids).Contains("run_repository_sync");
        await Assert.That(ids).Contains("repository_workspace");
        await Assert.That(ids).Contains("walk_repository_files");
        await Assert.That(ids).Contains("syncprocessoutcome");
        await Assert.That(ids).Contains("syncprocessingerror");
    }

    [Test]
    public async Task Extract_Go_ReturnsTypesAndFunctionsNotKeywords()
    {
        var ids = Extract(
            """
            package ingest

            func RunRepositorySync(ctx context.Context, job *RepositoryIngestJob) (*SyncOperationResult, error) {
                defer job.IngestWorkspace.Close()
                for _, file := range job.Files {
                    go processRepositoryFile(file)
                }
                return &SyncOperationResult{Status: "done"}, nil
            }
            """
        );

        await Assert.That(ids).DoesNotContain("package");
        await Assert.That(ids).DoesNotContain("func");
        await Assert.That(ids).DoesNotContain("defer");
        await Assert.That(ids).DoesNotContain("range");
        await Assert.That(ids).DoesNotContain("return");
        await Assert.That(ids).Contains("runrepositorysync");
        await Assert.That(ids).Contains("repositoryingestjob");
        await Assert.That(ids).Contains("syncoperationresult");
        await Assert.That(ids).Contains("processrepositoryfile");
        await Assert.That(ids).Contains("ingestworkspace");
    }

    [Test]
    public async Task Extract_Elixir_ReturnsModuleAndFunctionNamesNotKeywords()
    {
        var ids = Extract(
            """
            defmodule Zeeq.RepositorySync do
              def run_repository_sync(job) do
                case IngestWorkspace.walk_repository_files(job) do
                  {:ok, files} -> process_repository_files(files)
                  {:error, reason} -> {:error, reason}
                end
              end
            end
            """
        );

        await Assert.That(ids).DoesNotContain("defmodule");
        await Assert.That(ids).DoesNotContain("def");
        await Assert.That(ids).DoesNotContain("case");
        await Assert.That(ids).DoesNotContain("end");
        await Assert.That(ids).Contains("repositorysync");
        await Assert.That(ids).Contains("run_repository_sync");
        await Assert.That(ids).Contains("ingestworkspace");
        await Assert.That(ids).Contains("walk_repository_files");
        await Assert.That(ids).Contains("process_repository_files");
    }

    [Test]
    public async Task Extract_C_ReturnsFunctionAndTypeNamesNotKeywords()
    {
        var ids = Extract(
            """
            #include <stdio.h>

            typedef struct {
                int repository_identifier;
                char *repository_url;
            } RepositoryIngestJob;

            static void process_ingest_job(RepositoryIngestJob *job) {
                unsigned int retry_attempt_count = 0;
                printf("Processing job\n");
            }
            """
        );

        await Assert.That(ids).DoesNotContain("typedef");
        await Assert.That(ids).DoesNotContain("struct");
        await Assert.That(ids).DoesNotContain("static");
        await Assert.That(ids).DoesNotContain("void");
        await Assert.That(ids).DoesNotContain("unsigned");
        await Assert.That(ids).DoesNotContain("processing");
        await Assert.That(ids).Contains("repositoryingestjob");
        await Assert.That(ids).Contains("process_ingest_job");
        await Assert.That(ids).Contains("retry_attempt_count");
    }

    [Test]
    public async Task Extract_Cpp_ReturnsClassAndMethodNamesNotKeywords()
    {
        var ids = Extract(
            """
            template <typename T>
            class RepositorySyncQueue final {
            public:
                explicit RepositorySyncQueue(size_t capacity);
                void EnqueueRepositoryJob(const SyncJobRequest& job);
            private:
                mutable std::vector<SyncJobRequest> jobs_;
            };
            """
        );

        await Assert.That(ids).DoesNotContain("template");
        await Assert.That(ids).DoesNotContain("typename");
        await Assert.That(ids).DoesNotContain("class");
        await Assert.That(ids).DoesNotContain("final");
        await Assert.That(ids).DoesNotContain("public");
        await Assert.That(ids).DoesNotContain("explicit");
        await Assert.That(ids).DoesNotContain("private");
        await Assert.That(ids).DoesNotContain("mutable");
        await Assert.That(ids).DoesNotContain("void");
        await Assert.That(ids).Contains("repositorysyncqueue");
        await Assert.That(ids).Contains("enqueuerepositoryjob");
        await Assert.That(ids).Contains("syncjobrequest");
    }

    [Test]
    public async Task Extract_Sql_ReturnsTableAndColumnNamesNotKeywords()
    {
        var ids = Extract(
            """
            -- Fetch active sync jobs
            SELECT job_identifier, repository_url
            FROM docs_ingest_runs
            WHERE processing_status = 'running'
            ORDER BY created_at DESC;
            """
        );

        await Assert.That(ids).DoesNotContain("select");
        await Assert.That(ids).DoesNotContain("from");
        await Assert.That(ids).DoesNotContain("where");
        await Assert.That(ids).DoesNotContain("order");
        await Assert.That(ids).DoesNotContain("fetch");
        await Assert.That(ids).DoesNotContain("active");
        await Assert.That(ids).Contains("job_identifier");
        await Assert.That(ids).Contains("repository_url");
        await Assert.That(ids).Contains("docs_ingest_runs");
        await Assert.That(ids).Contains("processing_status");
    }

    [Test]
    public async Task Extract_Sql_DecrementOperatorIsNotMistakenForComment()
    {
        var ids = Extract(
            "retryAttemptCount--; nextComputedValue = ComputeSomething();"
        );

        await Assert.That(ids).Contains("retryattemptcount");
        await Assert.That(ids).Contains("nextcomputedvalue");
        await Assert.That(ids).Contains("computesomething");
    }

    [Test]
    public async Task Extract_Php_ReturnsClassAndMethodNamesNotKeywords()
    {
        var ids = Extract(
            """
            <?php
            namespace App\Ingest;

            class RepositorySyncService {
                public function runRepositorySyncJob(RepositoryIngestJob $job): SyncOperationResult {
                    $outcome = $this->workspace->walkRepositoryFiles($job);
                    return new SyncOperationResult($outcome);
                }
            }
            """
        );

        await Assert.That(ids).DoesNotContain("namespace");
        await Assert.That(ids).DoesNotContain("class");
        await Assert.That(ids).DoesNotContain("public");
        await Assert.That(ids).DoesNotContain("function");
        await Assert.That(ids).DoesNotContain("return");
        await Assert.That(ids).Contains("repositorysyncservice");
        await Assert.That(ids).Contains("runrepositorysyncjob");
        await Assert.That(ids).Contains("repositoryingestjob");
        await Assert.That(ids).Contains("syncoperationresult");
        await Assert.That(ids).Contains("walkrepositoryfiles");
    }
}
