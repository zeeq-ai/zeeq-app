import * as aws from "@pulumi/aws";
import * as pulumi from "@pulumi/pulumi";
import { execFileSync } from "node:child_process";
import { configuration } from "./config";
import { network } from "./network";
import { database } from "./database";
import { certificate } from "./certificate";
import { ingress } from "./ingress";
import { images } from "./images";
import { runtime, Route } from "./runtime";
import { Installation } from "./lifecycle";

const c = configuration();

// Bind every AWS resource to the selected account and common installation metadata.
const provider = new aws.Provider("installation-account", {
  region: c.region as aws.Region,
  allowedAccountIds: [c.accountId],
  defaultTags: { tags: c.tags },
});

const opts = { provider };

// External DNS and reused zones remain outside this stack's ownership.
const zone =
  c.dnsMode === "external" || c.zoneId
    ? undefined
    : new aws.route53.Zone(
        "installation-zone",
        { name: c.hostname, comment: `Zeeq ${c.id}` },
        opts,
      );

const zoneId = zone?.zoneId ?? pulumi.output(c.zoneId ?? "");
const cert = certificate(c, provider, zoneId);

/** DNS preparation output, including records needed before the installation stage. */
export const dns = {
  hostname: c.hostname,
  zoneId,
  nameServers: zone?.nameServers ?? pulumi.output([]),
  ownership:
    c.dnsMode === "external" ? "external" : zone ? "managed" : "reused",
  validationRecords: cert.records,
};

let manifest: pulumi.Input<Record<string, unknown>> = {
  schemaVersion: 1,
  installationId: c.id,
  accountId: c.accountId,
  region: c.region,
  installedAt: c.installedAt,
  stage: "dns",
  dns,
  remove: "npm run remove -- --delete-data",
  status:
    c.dnsMode === "external"
      ? "Add certificate-validation CNAMEs at the current DNS provider before proceeding."
      : "Delegate the installation zone before proceeding.",
};

if (c.stage !== "dns") {
  const net = network(c, provider);
  const edge = ingress(c, provider, zoneId, net.vpcId, net.publicSubnets, cert);

  if (c.dnsMode === "external") {
    pulumi.all(edge.dnsRecords.map((r) => r.value)).apply((values) => {
      if (!pulumi.runtime.isDryRun()) {
        console.log(
          `DNS action required: add CNAME ${c.hostname} and otel.${c.hostname} pointing to ${values[0]} at your current provider.`,
        );
      }
    });
  }

  const tasks = new aws.ec2.SecurityGroup(
    "task-network",
    {
      vpcId: net.vpcId,
      egress: [
        { protocol: "-1", fromPort: 0, toPort: 0, cidrBlocks: ["0.0.0.0/0"] },
      ],
    },
    opts,
  );

  [8080, 4318, 13133].forEach(
    (port) =>
      new aws.vpc.SecurityGroupIngressRule(
        `load-balancer-access-${port}`,
        {
          securityGroupId: tasks.id,
          referencedSecurityGroupId: edge.securityGroupId,
          ipProtocol: "tcp",
          fromPort: port,
          toPort: port,
        },
        opts,
      ),
  );

  new aws.vpc.SecurityGroupIngressRule(
    "internal-web-access",
    {
      securityGroupId: tasks.id,
      referencedSecurityGroupId: tasks.id,
      ipProtocol: "tcp",
      fromPort: 8080,
      toPort: 8080,
    },
    opts,
  );

  const db = database(c, provider, net.vpcId, net.privateSubnets, tasks);
  const image = images(c, provider);

  // The executable is the canonical catalog: no duplicated queue route algorithm in the installer.
  const raw = execFileSync(
    "dotnet",
    [
      "../../src/backend/Zeeq.Runtime.Server/bin/Release/net10.0/Zeeq.Runtime.Server.dll",
    ],
    {
      encoding: "utf8",
      env: {
        ...process.env,
        DOTNET_ENVIRONMENT: "Aws",
        ZEEQ_RUN_MODE: "topology",
        ZeeqMessaging__AwsSqs__QueuePrefix: c.id,
        ZeeqMessaging__TenantBuckets__PriorityBucketCount: String(
          c.buckets.priority,
        ),
        ZeeqMessaging__TenantBuckets__DefaultBucketCount: String(
          c.buckets.standard,
        ),
        ZeeqMessaging__TenantBuckets__LowBucketCount: String(c.buckets.low),
      },
    },
  );

  const routes = JSON.parse(raw.trim()) as Route[];

  if (
    routes.some((r) => !r.name.startsWith(`${c.id}-`) || r.name.length > 76) ||
    new Set(routes.map((r) => r.name)).size !== routes.length
  ) {
    throw new Error(
      "Invalid or colliding runtime queue catalog; reserve four characters for DLQ suffix.",
    );
  }

  const app = runtime(
    c,
    provider,
    net.vpcId,
    net.privateSubnets,
    tasks,
    db,
    edge,
    image,
    routes,
  );

  const install = new Installation(
    "installation",
    {
      region: c.region,
      cluster: app.cluster.arn,
      subnets: net.privateSubnets,
      securityGroups: [tasks.id],
      databaseId: db.db?.identifier,
      verificationTask: app.verify?.arn,
      cleanupTask: app.cleanup?.arn,
      bootstrapTask: app.bootstrap?.arn,
      migrationTask: app.migrate.arn,
      webTask: app.web.arn,
      workerTask: app.worker.arn,
      collectorTask: app.collector.arn,
      webService: app.webService.name,
      workerService: app.workerService.name,
      collectorService: app.collectorService.name,
      applicationEnabled: c.stage === "install",
      webCount: c.webCount,
      workerCount: c.workerCount,
      hostname: c.hostname,
    },
    {
      dependsOn: [
        ...net.ready,
        app.webService,
        app.workerService,
        app.collectorService,
        ...(db.db ? [db.db] : []),
      ],
      protect: c.protectData,
      customTimeouts: { create: "90m", update: "90m", delete: "30m" },
    },
  );

  manifest = {
    schemaVersion: 1,
    installationId: c.id,
    accountId: c.accountId,
    region: c.region,
    installedAt: c.installedAt,
    completedAt: install.completedAt,
    stage: c.stage,
    dns: { ...dns, endpointRecords: edge.dnsRecords },
    status:
      c.stage === "infrastructure"
        ? "Infrastructure and SQL prepared; application services stopped."
        : "Application rollout verified.",
    endpoints: {
      web: `https://${c.hostname}/web`,
      mcp: `https://${c.hostname}/mcp`,
      telemetry: `https://otel.${c.hostname}${c.ingress?.type === "network" ? `:${c.ingress.listenerPort! + 1}` : ""}`,
    },
    artifacts: {
      clusterArn: app.cluster.arn,
      loadBalancerArn: edge.lbArn,
      kmsKeyArn: app.key.arn,
      databaseHost: db.host,
      databaseArn: db.db?.arn,
      databaseMasterSecretArn: db.bootstrapSecret,
      ownerSecretArn: db.ownerSecret,
      runtimeSecretArn: db.runtimeSecret,
      applicationSecretArn: app.configSecret.arn,
      queueArns: app.queues.map((q) => q.arn),
      taskDefinitions: [
        app.web.arn,
        app.worker.arn,
        app.migrate.arn,
        app.collector.arn,
        app.bootstrap?.arn,
      ],
      logGroupArn: app.logs.arn,
    },
    reused: {
      network: c.reusedNetwork,
      ingress: c.ingress,
      database: c.database,
      hostedZoneId: c.zoneId,
    },
    removal: {
      command: "npm run remove -- --delete-data",
      managedDataDeleted: true,
      reusedResourcesDeleted: false,
      kmsDeletionDelayDays: 7,
      prerequisitesRetained: [
        "AWS account",
        "SSO/CI identity",
        "state backend",
        "external DNS records or parent DNS delegation",
      ],
    },
  };
}

/** Secret-free installation summary; the CLI adds managed resource IDs from stack state. */
export const installation = manifest;
