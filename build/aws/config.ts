import * as pulumi from "@pulumi/pulumi";

/** Existing dedicated database and connection secrets; the installer does not own them. */
export interface ReuseDatabase {
  host: string;
  port?: number;
  securityGroupId: string;
  ownerConnectionSecretArn: string;
  runtimeConnectionSecretArn: string;
}

/** Existing load balancer details and listener capacity reserved for this installation. */
export interface ReuseIngress {
  type: "application" | "network";
  arn: string;
  dnsName: string;
  zoneId: string;
  securityGroupId: string;
  listenerArn?: string;
  certificateArn?: string;
  listenerPort?: number;
  rulePriority?: number;
}

/** Validates stack inputs and supplies shared deployment defaults and ownership tags. */
export function configuration() {
  const cfg = new pulumi.Config();
  const aws = new pulumi.Config("aws");

  const id = cfg.require("installationId");
  const accountId = cfg.require("accountId");
  const region = aws.require("region");
  const hostname = cfg.require("hostname");

  if (!/^[a-z][a-z0-9-]{2,15}$/.test(id)) {
    throw new Error(
      "installationId must be 3–16 lowercase ASCII letters/digits/hyphens, starting with a letter.",
    );
  }

  if (!/^\d{12}$/.test(accountId)) {
    throw new Error("accountId must contain 12 digits.");
  }

  if (!/^[a-z0-9.-]+$/.test(hostname) || !hostname.includes(".")) {
    throw new Error("hostname must be a DNS name, without a scheme or path.");
  }

  const stage = cfg.get("stage") ?? "dns";

  if (!["dns", "infrastructure", "install"].includes(stage)) {
    throw new Error("stage must be dns, infrastructure or install.");
  }

  const version = cfg.get("version");

  if (
    version &&
    !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(version)
  ) {
    throw new Error(
      "version must be a semantic version, for example 1.2.3 or 0.0.0-aws.",
    );
  }

  const dnsMode = cfg.get("dnsMode") ?? "managed";

  if (!["managed", "external"].includes(dnsMode)) {
    throw new Error("dnsMode must be managed or external.");
  }

  const reusedNetwork = cfg.getObject<{
    vpcId: string;
    publicSubnetIds: string[];
    privateSubnetIds: string[];
  }>("network");

  const ingress = cfg.getObject<ReuseIngress>("ingress");
  const database = cfg.getObject<ReuseDatabase>("database");

  if ((ingress || database) && !reusedNetwork) {
    throw new Error(
      "Reused ingress/database requires an explicitly selected existing network.",
    );
  }

  if (ingress?.type === "application" && !ingress.listenerArn) {
    throw new Error("ALB reuse requires listenerArn.");
  }

  if (
    ingress?.type === "network" &&
    (!ingress.certificateArn || ingress.listenerPort !== 443)
  ) {
    throw new Error(
      "NLB reuse requires certificateArn and an unused listenerPort; ports 443 and 444 must be unused for web/OTLP.",
    );
  }

  return {
    cfg,
    id,
    accountId,
    region,
    hostname,
    stage,
    dnsMode,
    reusedNetwork,
    ingress,
    database,
    zoneId: cfg.get("hostedZoneId"),
    installedAt: cfg.require("installedAt"),
    tags: {
      ...cfg.getObject<Record<string, string>>("tags"),
      "zeeq:installation": id,
      "zeeq:installed-at": cfg.require("installedAt"),
      "zeeq:version": cfg.get("version") ?? "source",
      ManagedBy: "Pulumi",
      "zeeq:source-revision": cfg.get("sourceRevision") ?? "dns",
    },
    protectData: !(cfg.getBoolean("deleteData") ?? false),
    dbSize: cfg.get("dbInstanceClass") ?? "db.t4g.micro",
    dbStorage: cfg.getNumber("dbStorageGiB") ?? 20,
    multiAz: cfg.getBoolean("multiAz") ?? false,
    buckets: cfg.getObject<{ priority: number; standard: number; low: number }>(
      "buckets",
    ) ?? { priority: 1, standard: 1, low: 1 },
    webCount: cfg.getNumber("webCount") ?? 1,
    workerCount: cfg.getNumber("workerCount") ?? 1,
  };
}

/** Validated configuration shared by the resource modules. */
export type InstallConfig = ReturnType<typeof configuration>;
