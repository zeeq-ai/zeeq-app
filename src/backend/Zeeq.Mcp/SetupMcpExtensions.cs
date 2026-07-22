using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zeeq.Mcp.Carts;
using Zeeq.Mcp.CodeReviews;
using Zeeq.Mcp.Documents;

namespace Zeeq.Mcp;

/// <summary>
/// Service registration extensions for the Zeeq MCP server.
/// </summary>
public static class SetupMcpExtensions
{
    /// <summary>
    /// Adds the Zeeq MCP server and tool discovery.
    /// </summary>
    public static IServiceCollection AddZeeqMcp(
        this IServiceCollection services,
        IHostEnvironment environment
    )
    {
        var name = environment.IsDevelopment() ? "Zeeq MCP (local dev)" : "Zeeq MCP";

        services
            .AddZeeqCodeReviewMcp()
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = name,
                    Description =
                        "Zeeq model context protocol (MCP) server for canonical docs, code reviews, and telemetry",
                    Version = "1.0.0", // TODO: Make this driven by release process.
                    Icons =
                    [
                        new()
                        {
                            Source = "https://zeeq.ai/android-chrome-192x192.png",
                            MimeType = "image/png",
                            Sizes = ["192x192"],
                        },
                        new()
                        {
                            Source = "https://zeeq.ai/favicon.svg",
                            MimeType = "image/svg+xml",
                            Sizes = ["any"],
                        },
                    ],
                    WebsiteUrl = "https://zeeq.ai",
                };

                options.ServerInstructions = """
                    Zeeq provides:
                    - Search and read canonical documentation to produce high quality code
                    - Code review tools to run expert code reviews efficiently and retrieve findings

                    Prefer working in known library to scope tool usage.

                    <session_bootstrap>
                    Call `list_documents` once near session start / first research for the compact knowledge index; `list_libraries` only when library scope is unknown.
                    </session_bootstrap>

                    <research_flow>
                    Zeeq has a library of canonical documentation available
                    zeeq:// path prefixes are requests to use the Zeeq MCP to read a document in the library

                    1. `search_sections` for guidance/rationale/constraints/edge cases;
                    2. `search_code_snippets` for local patterns/boilerplate/tests/API shapes;
                    3. `read_document_by_path` for full docs context;
                    4. `search_documents` for efficient full-text search.
                    </research_flow>

                    <code_review_usage>
                    When code changes are ready, use `expert_code_review` to get expert review grounded in docs
                    The tool is context efficient; dump git diffs and upload to a signed URL then trigger a review job
                    Reduce mistakes and blind spots by relying on external reviewers
                    </code_review_usage>

                    Pay attention to tool trigger conditions <tool_name.triggers> and <tool_name.flow> to know when and how to use the tool effectively.
                    """;
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .AddAuthorizationFilters()
            .WithZeeqMcpFilters()
            // 👇 Add other assemblies here to include tools
            .WithToolsFromAssembly(Assembly.GetAssembly(typeof(SetupMcpExtensions)))
            .WithToolsFromAssembly(Assembly.GetAssembly(typeof(DocumentLibraryMcpTools)))
            .WithToolsFromAssembly(Assembly.GetAssembly(typeof(SetupCodeReviewMcp)))
            .WithToolsFromAssembly(Assembly.GetAssembly(typeof(CartMcpTools)));

        return services;
    }
}
