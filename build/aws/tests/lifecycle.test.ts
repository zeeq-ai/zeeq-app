import { test } from "node:test";
import assert from "node:assert/strict";
import { rollout, RolloutInput, RuntimeOperations } from "../lifecycle";

const input: RolloutInput = {
  region: "us-east-2",
  cluster: "cluster",
  subnets: [],
  securityGroups: [],
  bootstrapTask: "bootstrap",
  migrationTask: "migrate",
  webTask: "web-v2",
  workerTask: "worker-v2",
  collectorTask: "collector-v2",
  webService: "web",
  workerService: "worker",
  collectorService: "collector",
  webCount: 1,
  workerCount: 1,
  hostname: "sandbox.example.com",
};

function fake(events: string[], fail?: string): RuntimeOperations {
  return {
    async task(name) {
      events.push(name);

      if (name === fail) {
        throw new Error("SQL failed");
      }
    },
    async service(name) {
      events.push(`start:${name}`);
    },
    async healthy(url) {
      events.push(`healthy:${url}`);
    },
  };
}

test("a failed migration leaves all service revisions unchanged", async () => {
  const events: string[] = [];
  await assert.rejects(rollout(input, fake(events, "migrate")), /SQL failed/);
  assert.deepEqual(events, ["bootstrap", "migrate"]);
});

test("a failed privileged bootstrap never runs migrations or services", async () => {
  const events: string[] = [];
  await assert.rejects(rollout(input, fake(events, "bootstrap")));
  assert.deepEqual(events, ["bootstrap"]);
});

test("collector starts only after the public OIDC issuer is ready", async () => {
  const events: string[] = [];
  await rollout(input, fake(events));

  assert.deepEqual(events, [
    "bootstrap",
    "migrate",
    "start:web",
    "healthy:https://sandbox.example.com/.well-known/openid-configuration",
    "start:worker",
    "start:collector",
    "healthy:https://sandbox.example.com/health",
  ]);
});

test("infrastructure preparation runs SQL without starting application services", async () => {
  const events: string[] = [];

  await rollout(
    { ...input, applicationEnabled: false, verificationTask: "verify" },
    fake(events),
  );

  assert.deepEqual(events, ["bootstrap", "migrate", "verify"]);
});
