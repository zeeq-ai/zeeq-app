#!/usr/bin/env bash
set -euo pipefail

# Keep this focused set in sync with changed C# 15 union declarations and consumers.
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet csharpier check \
  src/backend/Zeeq.Platform.CodeReviews/ExpertCodeReviewContracts.cs \
  src/backend/Zeeq.Platform.CodeReviews/ExpertCodeReviewRunner.cs \
  src/backend/Zeeq.Platform.CodeReviews.Tests/ExpertCodeReviewRunnerTests.cs \
  src/backend/Zeeq.Platform.Ingest/IngestTriggerCoordinator.cs \
  src/backend/Zeeq.Platform.Ingest/Notion/NotionIngestDispatcher.cs \
  src/backend/Zeeq.Platform.Ingest/Notion/NotionIngestRunner.cs \
  src/backend/Zeeq.Mcp/CodeReviews/CodeReviewMcpTools.cs \
  src/backend/Zeeq.Mcp.Tests/CodeReviews/CodeReviewMcpToolsTests.cs
