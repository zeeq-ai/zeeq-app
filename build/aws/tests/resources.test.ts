import { test } from "node:test";
import assert from "node:assert/strict";
import * as pulumi from "@pulumi/pulumi";

const resources: pulumi.runtime.MockResourceArgs[] = [];

pulumi.runtime.setAllConfig({
  "aws:region": "us-east-2",
  "zeeq-aws:installationId": "test-install",
  "zeeq-aws:accountId": "123456789012",
  "zeeq-aws:hostname": "test.example.com",
  "zeeq-aws:installedAt": "2026-10-04T00:00:00Z",
  "zeeq-aws:stage": "install",
  "zeeq-aws:sourceRevision": "test-sha",
  "zeeq-aws:application": JSON.stringify({
    AppSettings: { Auth: { Providers: [] } },
  }),
  "zeeq-aws:runtimeImage": "example/runtime@sha256:test",
  "zeeq-aws:collectorImage": "example/collector@sha256:test",
});

pulumi.runtime.setMocks(
  {
    newResource(args) {
      resources.push(args);

      return {
        id: `${args.name}-id`,
        state: {
          ...args.inputs,
          arn: `arn:aws:test::123456789012:${args.name}`,
          address: "private.test",
          url: `https://sqs.example/${args.name}`,
          zoneId: "zone",
          dnsName: "lb.example",
          nameServers: ["ns.example"],
          masterUserSecrets: [
            {
              secretArn:
                "arn:aws:secretsmanager:us-east-2:123456789012:secret:master",
            },
          ],
          result: "dummy-password",
          domainValidationOptions: [
            "test.example.com",
            "otel.test.example.com",
          ].map((domainName) => ({
            domainName,
            resourceRecordName: `_test.${domainName}`,
            resourceRecordValue: "validation.example",
            resourceRecordType: "CNAME",
          })),
        },
      };
    },
    call(args) {
      if (args.token.includes("getAvailabilityZones")) {
        return { names: ["us-east-2a", "us-east-2b"] };
      }

      return args.inputs;
    },
  },
  "zeeq-aws",
  "test",
  false,
);

test("Pulumi owns queues and native DLQs; runtime IAM cannot create or delete queues", async () => {
  await pulumi.runtime.runInPulumiStack(async () => {
    const deployed = await import("../index");

    await new Promise<void>((resolve) =>
      pulumi.output(deployed.installation).apply(() => resolve()),
    );
  });

  const queues = resources.filter((r) => r.type === "aws:sqs/queue:Queue");
  assert.equal(queues.length, 52);
  assert.equal(queues.filter((q) => q.inputs.redrivePolicy).length, 26);

  assert.equal(
    queues.filter((q) => q.inputs.messageRetentionSeconds === 1209600).length,
    26,
  );

  const policies = resources.filter(
    (r) => r.name === "web-permissions" || r.name === "worker-permissions",
  );

  for (const p of policies) {
    assert.ok(!p.inputs.policy.includes("sqs:CreateQueue"));
    assert.ok(!p.inputs.policy.includes("sqs:DeleteQueue"));
  }

  assert.equal(
    resources.filter((r) => r.type === "aws:lb/loadBalancer:LoadBalancer")
      .length,
    1,
  );

  const db = resources.find((r) => r.type === "aws:rds/instance:Instance")!;
  assert.equal(db.inputs.instanceClass, "db.t4g.micro");
  assert.equal(db.inputs.publiclyAccessible, false);
  assert.equal(db.inputs.manageMasterUserPassword, true);

  const services = resources.filter(
    (r) => r.type === "aws:ecs/service:Service",
  );

  assert.ok(services.every((r) => r.inputs.desiredCount === 0));

  const definitions = resources.filter(
    (r) => r.type === "aws:ecs/taskDefinition:TaskDefinition",
  );

  for (const task of definitions.filter(
    (t) => t.name === "web" || t.name === "worker",
  )) {
    assert.ok(!task.inputs.containerDefinitions.includes("MasterPassword"));
  }
});
