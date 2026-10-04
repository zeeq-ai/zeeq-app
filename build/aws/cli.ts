import { execFileSync, spawnSync } from "node:child_process";
import {
  mkdirSync,
  readFileSync,
  writeFileSync,
  existsSync,
  chmodSync,
} from "node:fs";
import { homedir } from "node:os";
import { join, dirname } from "node:path";
import { randomBytes, createHash } from "node:crypto";
import { createInterface } from "node:readline/promises";
import { parseArgs } from "node:util";
import { STSClient, GetCallerIdentityCommand } from "@aws-sdk/client-sts";
import {
  RDSClient,
  DescribeOrderableDBInstanceOptionsCommand,
} from "@aws-sdk/client-rds";

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    stack: { type: "string" },
    hostname: { type: "string" },
    region: { type: "string" },
    account: { type: "string" },
    "installation-id": { type: "string" },
    "zone-id": { type: "string" },
    backend: { type: "string" },
    "application-file": { type: "string" },
    "new-database": { type: "boolean" },
    "new-alb": { type: "boolean" },
    "external-dns": { type: "boolean" },
    "db-size": { type: "string" },
    "delete-data": { type: "boolean" },
    dns: { type: "boolean" },
    infrastructure: { type: "boolean" },
    "login-only": { type: "boolean" },
  },
});

const cli = process.env.PULUMI_CLI_PATH ?? "pulumi";
// A local backend is an operator prerequisite, not a workload artifact. Keep it after destroy for audit/recovery.
const localState = join(homedir(), ".local", "state", "zeeq-pulumi");
const passphrase = join(localState, "passphrase");

/** Uses real AWS endpoints and the selected state passphrase, even inside the local emulator workspace. */
function environment() {
  const env = { ...process.env };

  if (process.env.PULUMI_CLI_PATH) {
    env.PATH = `${dirname(process.env.PULUMI_CLI_PATH)}:${env.PATH}`;
  }

  delete env.AWS_ENDPOINT_URL;
  delete env.AWS_ENDPOINT_URL_SQS;

  if (
    !env.PULUMI_CONFIG_PASSPHRASE &&
    !env.PULUMI_CONFIG_PASSPHRASE_FILE &&
    existsSync(passphrase)
  ) {
    env.PULUMI_CONFIG_PASSPHRASE_FILE = passphrase;
  }

  return env;
}

/** Keeps state operations on the requested stack rather than the last selected stack. */
function scoped(args: string[]) {
  if (
    values.stack &&
    (["config", "up", "refresh", "destroy"].includes(args[0]) ||
      (args[0] === "stack" && ["output", "export"].includes(args[1])))
  ) {
    return [...args, "--stack", values.stack];
  }

  return args;
}

function command(args: string[], input?: string): string {
  return execFileSync(cli, scoped(args), {
    cwd: __dirname,
    env: environment(),
    input,
    encoding: "utf8",
    stdio: ["pipe", "pipe", "pipe"],
    maxBuffer: 32 * 1024 * 1024,
  });
}

function live(args: string[]) {
  const result = spawnSync(cli, scoped(args), {
    cwd: __dirname,
    env: environment(),
    stdio: "inherit",
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(
      `Pulumi operation failed (exit ${result.status}); retain the stack state and retry after resolving the reported error.`,
    );
  }
}

// Pass configuration on stdin so application secrets never appear in command arguments.
function set(key: string, value: string, secret = false) {
  command(["config", "set", key, ...(secret ? ["--secret"] : [])], value);
}

function get(key: string) {
  try {
    return command(["config", "get", key]).trim();
  } catch {
    return undefined;
  }
}

async function question(prompt: string, fallback?: string) {
  if (!process.stdin.isTTY) {
    throw new Error(`Missing choice: ${prompt}`);
  }

  const rl = createInterface({ input: process.stdin, output: process.stdout });

  try {
    return (
      (
        await rl.question(`${prompt}${fallback ? ` [${fallback}]` : ""}: `)
      ).trim() ||
      fallback ||
      ""
    );
  } finally {
    rl.close();
  }
}

/** Checks the settings shape required by startup; provider credentials are not tested here. */
export function validateApplication(input: any) {
  const a = input.AppSettings;

  if (!a?.Auth?.Providers?.length || !a?.Llm?.Models?.Fast?.ApiKey) {
    throw new Error(
      "Application settings require an OAuth provider and the Fast LLM API key currently required by runtime startup. GitHub/review/export settings are optional for login validation.",
    );
  }

  if (
    a.Auth.Providers.some(
      (p: any) =>
        !p.Name || !p.ClientId || !p.ClientSecret || !p.ServerCallbackUri,
    )
  ) {
    throw new Error(
      "Every OAuth provider requires its customer client ID, secret and callback URI.",
    );
  }

  for (const purpose of ["Signing", "Encryption"]) {
    if (!a.Auth.OpenIddict?.[`${purpose}CertificateBase64`]) {
      throw new Error(`Missing OpenIddict ${purpose}CertificateBase64.`);
    }

    if (a.Auth.OpenIddict?.[`${purpose}CertificatePath`]) {
      throw new Error(
        "AWS uses PFX payloads; remove mounted certificate paths.",
      );
    }
  }

  if (Buffer.byteLength(JSON.stringify(input)) > 60000) {
    throw new Error(
      "Application config exceeds the 60KB package limit; split secret payloads before installing.",
    );
  }
}

async function configure() {
  const backend = values.backend ?? `file://${localState}`;

  if (backend.startsWith("file://")) {
    mkdirSync(localState, { recursive: true, mode: 0o700 });

    if (!existsSync(passphrase)) {
      writeFileSync(passphrase, randomBytes(48).toString("base64"), {
        mode: 0o600,
      });
    }

    chmodSync(localState, 0o700);
  }

  command(["login", backend]);
  const stack = values.stack ?? (await question("Stack name", "sandbox"));

  try {
    command(["stack", "select", stack]);
  } catch {
    command(["stack", "init", stack]);
  }

  const region =
    values.region ??
    get("aws:region") ??
    (await question("Deployment region", "us-east-2"));

  const identity = await new STSClient({ region }).send(
    new GetCallerIdentityCommand({}),
  );

  const account = values.account ?? identity.Account!;

  if (account !== identity.Account) {
    throw new Error(
      "The active AWS identity does not match the selected account.",
    );
  }

  const hostname =
    values.hostname ??
    get("hostname") ??
    (await question("Application hostname"));

  const id =
    values["installation-id"] ??
    get("installationId") ??
    (await question(
      "Installation ID (3–16 lowercase letters/digits/hyphens)",
      "zeeq-sandbox",
    ));

  set("aws:region", region);
  set("accountId", account);
  set("hostname", hostname);
  set("installationId", id);

  if (!get("installedAt")) {
    set("installedAt", new Date().toISOString());
  }

  if (!get("stage")) {
    set("stage", "dns");
  }

  if (values["external-dns"]) {
    set("dnsMode", "external");
  }

  if (values["zone-id"]) {
    set("hostedZoneId", values["zone-id"]);
  }

  if (!get("database") && !values["new-database"]) {
    const choice = await question(
      "Database: new RDS or reuse a prepared, dedicated database? (new/reuse)",
      "new",
    );

    if (choice === "reuse") {
      const path = await question(
        "Reuse configuration JSON file (network and database objects)",
      );

      const input = JSON.parse(readFileSync(path, "utf8"));
      set("network", JSON.stringify(input.network));
      set("database", JSON.stringify(input.database));
    } else if (choice !== "new") {
      throw new Error("Choose new or reuse.");
    }
  }

  if (!get("database")) {
    const size =
      values["db-size"] ??
      (await question(
        "RDS size (db.t4g.micro / db.t4g.small / db.t4g.medium / other)",
        "db.t4g.micro",
      ));

    const result = await new RDSClient({ region }).send(
      new DescribeOrderableDBInstanceOptionsCommand({
        Engine: "postgres",
        EngineVersion: "18.6",
        DBInstanceClass: size,
        MaxRecords: 100,
      }),
    );

    if (
      !result.OrderableDBInstanceOptions?.some((o) => o.StorageType === "gp3")
    ) {
      throw new Error(
        "Selected RDS size is not orderable for PG18.6/gp3 in this region.",
      );
    }

    set("dbInstanceClass", size);
  }

  if (!get("ingress") && !values["new-alb"]) {
    const choice = await question(
      "Ingress: create dedicated ALB or reuse existing? (new/reuse)",
      "new",
    );

    if (choice === "reuse") {
      const input = JSON.parse(
        readFileSync(
          await question("Reuse JSON file (network and ingress objects)"),
          "utf8",
        ),
      );

      set("network", JSON.stringify(input.network));
      set("ingress", JSON.stringify(input.ingress));
    } else if (choice !== "new") {
      throw new Error("Choose new or reuse.");
    }
  }

  if (values["login-only"]) {
    set("workerCount", "0");
  }

  if (values["application-file"]) {
    let input: any;

    try {
      input = JSON.parse(readFileSync(values["application-file"], "utf8"));
    } catch {
      throw new Error(
        "The application settings file must be readable, valid JSON. Its contents were not logged.",
      );
    }

    validateApplication(input);
    set("application", JSON.stringify(input), true);
  }

  console.log(
    `Configured ${stack} in account ${account}, ${region}, for ${hostname}.`,
  );
}

/** Saves artifact references for audit and removal without copying secret-bearing raw state. */
function manifest() {
  const output = JSON.parse(
    command(["stack", "output", "installation", "--json"]),
  );

  const state = JSON.parse(command(["stack", "export"]));
  // Never persist raw stack export: it can contain encrypted secret payloads and private provider state.
  output.attemptedStage = get("stage");

  output.resources = state.deployment.resources
    .filter((r: any) => r.custom && !r.type.startsWith("pulumi:providers:"))
    .map((r: any) => ({
      urn: r.urn,
      type: r.type,
      id: r.id,
      arn: typeof r.outputs?.arn === "string" ? r.outputs.arn : undefined,
      protected: !!r.protect,
    }));

  writeFileSync(
    join(__dirname, "installation.json"),
    JSON.stringify(output, null, 2) + "\n",
    { mode: 0o600 },
  );

  console.log(
    `Manifest written to ${join(__dirname, "installation.json")}: ${output.resources.length} managed resources.`,
  );
}

async function main() {
  const operation = positionals[0];

  if (operation === "configure") {
    await configure();
  } else if (operation === "manifest") {
    manifest();
  } else if (operation === "deploy") {
    if ((values.dns || values.infrastructure) && get("stage") === "install") {
      throw new Error(
        "An installed stack cannot be switched back to DNS-only: that would remove infrastructure.",
      );
    }

    if (values.dns && get("stage") === "infrastructure") {
      throw new Error("Infrastructure cannot be switched back to DNS-only.");
    }

    set(
      "stage",
      values.dns ? "dns" : values.infrastructure ? "infrastructure" : "install",
    );

    if (!values.dns) {
      // Refresh the actual service revisions/counts before ignoreChanges can reuse cached values.
      live(["refresh", "--yes"]);

      const files = execFileSync(
        "git",
        ["ls-files", "--cached", "--others", "--exclude-standard"],
        { cwd: join(__dirname, "../.."), encoding: "utf8" },
      )
        .trim()
        .split("\n");

      const hash = createHash("sha256");

      for (const file of files
        .filter(
          (f) =>
            f.startsWith("src/") ||
            f.startsWith("build/") ||
            ["package.json", "yarn.lock", ".dockerignore"].includes(f),
        )
        .sort()) {
        hash.update(file);
        hash.update(readFileSync(join(__dirname, "../..", file)));
      }

      set("sourceRevision", hash.digest("hex").slice(0, 40));
    }

    try {
      live(["up", "--yes"]);
    } catch (error) {
      try {
        manifest();
      } catch {
        console.error(
          "Manifest unavailable; retain Pulumi state for recovery.",
        );
      }

      throw error;
    }

    manifest();

    if (values.dns) {
      console.log(
        get("dnsMode") === "external"
          ? "Add the manifest certificate-validation CNAMEs at your existing DNS provider, then deploy. Add endpoint CNAMEs once the ALB is created."
          : "Add the manifest nameServers as NS records for the installation hostname at the parent DNS provider, then deploy.",
      );
    }
  } else if (operation === "remove") {
    if (!values["delete-data"]) {
      throw new Error(
        "Removal deletes the managed database, secrets and tenant key. Export/backup first; use --delete-data to authorize complete teardown.",
      );
    }

    // Teardown is explicitly destructive; the manifest records artifacts before their deletion.
    set("deleteData", "true");
    live(["refresh", "--yes"]);
    // Remove provider-level RDS deletion protection and Pulumi protection while retaining resource shape.
    live(["up", "--yes"]);
    manifest();
    live(["destroy", "--yes"]);

    console.log(
      "Managed workload resources removed; the tenant KMS key is scheduled for deletion in seven days. Remove the external endpoint/validation CNAMEs or parent DNS delegation. Keep the state and manifest until verified.",
    );
  } else {
    throw new Error(
      "Use configure, deploy [--dns | --infrastructure], manifest, or remove --delete-data.",
    );
  }
}

if (require.main === module) {
  main().catch((error) => {
    console.error(
      error instanceof Error ? error.message : "Installation operation failed.",
    );

    process.exitCode = 1;
  });
}
