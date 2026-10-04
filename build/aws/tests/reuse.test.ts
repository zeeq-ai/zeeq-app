import { test } from "node:test";
import assert from "node:assert/strict";
import * as pulumi from "@pulumi/pulumi";

const resources: pulumi.runtime.MockResourceArgs[] = [];

pulumi.runtime.setAllConfig({
  "aws:region": "us-east-2",
  "zeeq-aws:installationId": "reuse-install",
  "zeeq-aws:accountId": "123456789012",
  "zeeq-aws:hostname": "reuse.example.com",
  "zeeq-aws:installedAt": "2026-10-04T00:00:00Z",
  "zeeq-aws:stage": "install",
  "zeeq-aws:sourceRevision": "test-sha",
  "zeeq-aws:hostedZoneId": "external-zone",
  "zeeq-aws:application": JSON.stringify({ AppSettings: {} }),
  "zeeq-aws:runtimeImage": "example/runtime@sha256:test",
  "zeeq-aws:collectorImage": "example/collector@sha256:test",
  "zeeq-aws:network": JSON.stringify({
    vpcId: "vpc-existing",
    publicSubnetIds: ["public-a", "public-b"],
    privateSubnetIds: ["private-a", "private-b"],
  }),
  "zeeq-aws:database": JSON.stringify({
    host: "existing-rds",
    securityGroupId: "existing-db-sg",
    ownerConnectionSecretArn: "owner-secret",
    runtimeConnectionSecretArn: "runtime-secret",
  }),
  "zeeq-aws:ingress": JSON.stringify({
    type: "application",
    arn: "external-alb",
    dnsName: "external.example",
    zoneId: "lb-zone",
    securityGroupId: "external-alb-sg",
    listenerArn: "external-listener",
    certificateArn: "external-certificate",
  }),
});

pulumi.runtime.setMocks(
  {
    newResource(args) {
      resources.push(args);

      return {
        id: `${args.name}-id`,
        state: {
          ...args.inputs,
          arn: `arn:test:${args.name}`,
          result: "dummy-password",
        },
      };
    },
    call(args) {
      return args.inputs;
    },
  },
  "zeeq-aws",
  "reuse",
  false,
);

test("reuse references shared infrastructure; only install-owned rules and SQL cleanup task are created", async () => {
  await pulumi.runtime.runInPulumiStack(async () => {
    const deployed = await import("../index");

    await new Promise<void>((resolve) =>
      pulumi.output(deployed.installation).apply(() => resolve()),
    );
  });

  for (const type of [
    "aws:ec2/vpc:Vpc",
    "aws:rds/instance:Instance",
    "aws:lb/loadBalancer:LoadBalancer",
    "aws:lb/listener:Listener",
    "aws:acm/certificate:Certificate",
    "aws:route53/zone:Zone",
  ]) {
    assert.equal(resources.filter((r) => r.type === type).length, 0, type);
  }

  assert.equal(
    resources.filter((r) => r.type === "aws:lb/listenerRule:ListenerRule")
      .length,
    2,
  );

  assert.ok(resources.some((r) => r.name === "cleanup"));
  assert.ok(!resources.some((r) => r.name === "bootstrap"));
});
