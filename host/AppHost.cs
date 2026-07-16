using Aspire.Hosting.Yarp;

var builder = DistributedApplication.CreateBuilder(args);

// OpenIddict issuer/resource for local dev (AppSettings:Auth:Issuer/Resource).
// Reused as the telemetry audience so an existing MCP user token authenticates
// to the collector's oidc extension with no new resource/scope registration -
// see otel-collector-config.filtered.yaml. Trailing slash on the issuer is
// required: OpenIddict's SetIssuer canonicalizes to a root-path URI, and the
// oidc extension's discovery client does an exact string match against it.
var zeeqIssuerUrl = builder.Configuration["ZEEQ_ISSUER_URL"] ?? "http://zeeq-web.localhost:8095/";
var zeeqTelemetryAudience =
    builder.Configuration["ZEEQ_TELEMETRY_AUDIENCE"] ?? "http://zeeq-web.localhost:8095/mcp";

var standaloneWorkerMode =
    builder.Configuration["ZEEQ_ASPIRE_MODE"] is { } aspireMode
    && string.Equals(aspireMode, "split", StringComparison.OrdinalIgnoreCase);

var postgresHostPort = int.TryParse(
    builder.Configuration["ZEEQ_HOST_PORT"],
    out var configuredPostgresHostPort
)
    ? configuredPostgresHostPort
    : 35432;

// Setup Postgres
var username = builder.AddParameter("username", "zeeq", secret: true);
var password = builder.AddParameter("password", "P@ssw0rd", secret: true);

var postgres = builder
    .AddPostgres("postgres", userName: username, password: password)
    .WithEndpoint(
        "tcp",
        endpoint =>
        {
            endpoint.Port = postgresHostPort;
        }
    )
    .WithDockerfile("../build/postgres")
    .WithVolume("zeeq-pg-data", "/var/lib/postgresql")
    .WithLifetime(ContainerLifetime.Persistent);

var postgresdb = postgres.AddDatabase("zeeq-db", "zeeq");
const string pubSubProjectId = "zeeq-dev-local";
const string pubSubEmulatorHost = "localhost:18085";
var csharpReplConnectEnabled = builder.ExecutionContext.IsRunMode;

// Enables connection to Cloud SQL via proxy (env from ./config/mise.toml)
var cloudSqlProxy = builder.AddExecutable(
    "cloud-sql-proxy",
    "cloud-sql-proxy",
    ".",
    ["--port", "55432"]
);

// Run the pub/sub emulator
var pubSubEmulator = builder
    .AddContainer("gcp-pubsub-emulator", "google/cloud-sdk:568.0.0-emulators")
    .WithArgs(
        "gcloud",
        "beta",
        "emulators",
        "pubsub",
        "start",
        $"--project={pubSubProjectId}",
        "--host-port=0.0.0.0:18085"
    )
    .WithHttpEndpoint(port: 18085, targetPort: 18085, name: "http", isProxied: false)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithImagePullPolicy(ImagePullPolicy.Missing);

// Start the server
var backend = builder
    .AddProject<Projects.Zeeq_Runtime_Server>("zeeq-server")
    .WaitFor(postgresdb)
    .WaitFor(pubSubEmulator)
    .WithReference(postgresdb)
    .WithEnvironment("PUBSUB_EMULATOR_HOST", pubSubEmulatorHost)
    .WithEnvironment("PUBSUB_PROJECT_ID", pubSubProjectId)
    .WithEnvironment("ZEEQ_MESSAGING_ROLE", standaloneWorkerMode ? "producer" : "producer-consumer")
    .WithUrlForEndpoint(
        "http",
        url =>
        {
            url.DisplayText = "API Docs";
            url.Url = "http://zeeq-api.localhost:8095/scalar";
        }
    );

if (csharpReplConnectEnabled)
{
    foreach (var (name, value) in ProcessUtils.TryGetCSharpReplConnectEnvironment())
    {
        backend.WithEnvironment(name, value);
    }
}

if (standaloneWorkerMode)
{
    builder
        .AddProject<Projects.Zeeq_Runtime_Server>(
            "zeeq-worker",
            options =>
            {
                options.ExcludeLaunchProfile = true;
                options.ExcludeKestrelEndpoints = true;
            }
        )
        .WaitFor(postgresdb)
        .WaitFor(pubSubEmulator)
        .WithReference(postgresdb)
        .WithEnvironment("PUBSUB_EMULATOR_HOST", pubSubEmulatorHost)
        .WithEnvironment("PUBSUB_PROJECT_ID", pubSubProjectId)
        .WithEnvironment("ZEEQ_RUN_MODE", "worker")
        .WithEnvironment("ZEEQ_MESSAGING_ROLE", "producer-consumer")
        .WithEnvironment("AppSettings__Database__WorkerConnectionString", postgresdb)
        .WithExplicitStart()
        .WithParentRelationship(backend);
}

// Standalone build in watch mode that will produce the OpenAPI spec and kick off Kubb
// GEN=true copies only the app OpenAPI schema to src/web and runs the Vue Kubb client
// generation.
var buildGenerate = builder
    .AddExecutable(
        "openapi-build-generate",
        "dotnet",
        "../src/backend/Zeeq.Runtime.Server",
        ["watch", "build", "--non-interactive", "/p:GEN=true"]
    )
    .WithEnvironment("GEN", "true")
    .WithParentRelationship(backend);

// Fresh client generation on startup.
var kubbInitialGenerate = builder
    .AddExecutable("kubb-initial-generate", "yarn", "../src/web", ["generate"])
    .WithParentRelationship(backend);

// Vue front-end app.
var frontend = builder
    .AddViteApp(name: "zeeq-web", appDirectory: "../src/web")
    .WaitFor(kubbInitialGenerate)
    .WaitFor(backend)
    .WithChildRelationship(kubbInitialGenerate)
    .WithYarn()
    // Without this, Aspire marks the resource Healthy as soon as the process
    // starts, not once Vite is actually serving - letting WaitFor(frontend)
    // elsewhere race ahead of a cold-start compile.
    .WithHttpHealthCheck("/")
    .WithUrlForEndpoint(
        "http",
        url =>
        {
            url.DisplayText = "Zeeq UI";
            url.Url = "http://zeeq-web.localhost:8095";
        }
    );

// Nuxt Docus (docus.dev) site for documentation
var docusDocs = builder
    .AddViteApp(name: "zeeq-docs", appDirectory: "../docs")
    .WithYarn()
    .WithUrlForEndpoint(
        "http",
        url =>
        {
            url.DisplayText = "Zeeq Docs";
            url.Url = "http://zeeq-docs.localhost:8095";
        }
    );

// Add devtunnel proxy to the web app
var tunnel = builder
    .AddDevTunnel(
        "webapp-tunnel",
        tunnelId: "zeeq-webapp-tunnel",
        options: new()
        {
            AllowAnonymous = true,
            Description = "Tunnel for exposing the web app to GitHub.",
            Labels = ["webapp", "github"],
        }
    )
    .WithReference(backend);

// Add the MCP inspector; manual start to run
var inspector = builder
    .AddExecutable("mcp-inspector", "npx", ".", ["@modelcontextprotocol/inspector"])
    .WithEnvironment("MCP_AUTO_OPEN_ENABLED", "false")
    .WithEnvironment("DANGEROUSLY_OMIT_AUTH", "true")
    .WithEnvironment("ALLOWED_ORIGINS", "http://zeeq-inspector.localhost:8095")
    .WithHttpEndpoint(targetPort: 6274)
    .WithHttpEndpoint(targetPort: 6277, name: "mcp-proxy")
    .WithUrlForEndpoint(
        "http",
        url =>
        {
            url.DisplayText = "MCP Inspector";
            url.Url =
                "http://zeeq-inspector.localhost:8095/?transport=streamable-http&serverUrl=http://zeeq-web.localhost:8095/mcp";
        }
    )
    .WithParentRelationship(frontend);

// Add a local OAuth2 server for testing
// See: https://github.com/axa-group/oauth2-mock-server
var oauth2 = builder
    .AddExecutable("oauth2-mock-server", "npx", ".", ["oauth2-mock-server", "-p", "9321"])
    .WithParentRelationship(frontend);

// Add glider MCP server (need HTTP for Codex...)
// See: https://glidermcp.com/
var gliderMcp = builder
    .AddExecutable(
        "glider-mcp-server",
        "dotnet",
        "../",
        "glider",
        "--transport",
        "http",
        "--port",
        "5061",
        "--solution",
        "zeeq.slnx"
    )
    .WithHttpEndpoint(5051, 5061, name: "http", isProxied: true)
    .WithParentRelationship(backend);

// Yarp proxy to make it more pleasant to use.
var proxy = builder
    .AddYarp("yarp-reverse-proxy")
    .WithHostPort(8095)
    .WithConfiguration(yarp =>
    {
        // Proxy route to the backend as: http://zeeqapi.localhost:8095
        var backendCluster = yarp.AddCluster(backend);
        yarp.AddRoute(backendCluster).WithMatchHosts("zeeq-api.localhost");

        // Proxy route to the frontend as: http://zeeq-web.localhost:8095
        yarp.AddRoute(frontend).WithMatchHosts("zeeq-web.localhost");

        // Proxy route to the documentation site as: http://zeeq-docs.localhost:8095
        yarp.AddRoute(docusDocs).WithMatchHosts("zeeq-docs.localhost");

        // Proxy route to the MCP inspector as: http://zeeq-inspector.localhost:8095
        yarp.AddRoute(inspector.GetEndpoint("http")).WithMatchHosts("zeeq-inspector.localhost");
    });

// Additional reverse proxy listener on port 8096.  Google OAuth doesn't allow
// .localhost domains, so callbacks from Google land on localhost:8096 instead.
// All traffic here goes to backend (not the SPA) so the server-side
// callback handler at GET /auth/callback/google is reachable at that URL.

var proxyExternal = builder
    .AddYarp("yarp-reverse-proxy-external")
    .WithHostPort(8096)
    .WithConfiguration(yarp =>
    {
        var backendCluster = yarp.AddCluster(backend.GetEndpoint("http"));
        yarp.AddRoute(backendCluster).WithMatchHosts("localhost", "localhost:8096");
    });

// Add the OTEL collector for agent-harness telemetry (Claude Code, Codex,
// Copilot Chat); we use this to filter, authenticate, and forward to both the
// Aspire dashboard (local visibility) and Zeeq's own OTLP ingest.
// See: https://opentelemetry.io/docs/collector/install/docker/
// See: https://hub.docker.com/r/otel/opentelemetry-collector-contrib
var otel = builder
    .AddContainer("otel-collector", "otel/opentelemetry-collector-contrib:0.153.0")
    .WithBindMount(
        "./otel-collector-config.filtered.yaml", // 👈 The local config file
        "/etc/otel-collector-config.yaml",
        isReadOnly: true
    )
    .WithArgs("--config=/etc/otel-collector-config.yaml")
    // oidc discovery is a one-shot fetch at container startup (no retry) that
    // routes through the full zeeq-web.localhost -> yarp -> Vite -> backend
    // chain, so wait for all of it, not just the backend.
    .WaitFor(backend)
    .WaitFor(frontend)
    .WaitFor(proxy)
    .WithReference(backend)
    // Sets the OTLP config for the Aspire collector (for local visibility)
    .WithOtlpExporter(OtlpProtocol.Grpc)
    // zeeq-web.localhost is a browser/OS loopback hostname, not a Docker
    // network name - it won't resolve inside this container without help.
    // host-gateway routes it back through the host machine to YARP's
    // published port instead, preserving the hostname the oidc extension
    // needs verbatim (it doubles as the discovery URL and the `iss` match).
    // Requires Docker 20.10+.
    .WithEnvironment("ZEEQ_ISSUER_URL", zeeqIssuerUrl) // 👈 Used in otel-collector-config.filtered.yaml
    .WithEnvironment("ZEEQ_TELEMETRY_AUDIENCE", zeeqTelemetryAudience)
    .WithEnvironment("ZEEQ_OTLP_HTTP_ENDPOINT", backend.GetEndpoint("http"))
    .WithContainerRuntimeArgs("--add-host", "zeeq-web.localhost:host-gateway")
    .WithHttpEndpoint(44317, 4317, name: "otel-grpc")
    .WithHttpEndpoint(44318, 4318, name: "otel-http")
    .WithHttpEndpoint(44319, 4319, name: "otel-grpc-health")
    .WithHttpEndpoint(44320, 4320, name: "otel-http-health");

builder.Build().Run();
