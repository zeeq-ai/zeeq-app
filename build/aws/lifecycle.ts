import * as pulumi from "@pulumi/pulumi";

/** Serializable resource references and desired counts used for rollout and teardown. */
export interface RolloutInput {
  region: string;
  cluster: string;
  subnets: string[];
  securityGroups: string[];
  verificationTask?: string;
  cleanupTask?: string;
  bootstrapTask?: string;
  migrationTask: string;
  webTask: string;
  workerTask: string;
  collectorTask: string;
  webService: string;
  workerService: string;
  collectorService: string;
  webCount: number;
  workerCount: number;
  hostname: string;
  databaseId?: string;
  applicationEnabled?: boolean;
}

/** Runtime actions kept separate from ordering so failure handling can be tested without AWS. */
export interface RuntimeOperations {
  task(taskDefinition: string): Promise<void>;
  service(
    service: string,
    taskDefinition: string,
    desiredCount: number,
  ): Promise<void>;
  healthy(url: string): Promise<void>;
}

/** The order is a contract: candidate definitions never change a service before SQL succeeds. */
export async function rollout(
  input: RolloutInput,
  operations: RuntimeOperations,
) {
  if (input.bootstrapTask) {
    await operations.task(input.bootstrapTask);
  }

  await operations.task(input.migrationTask);

  if (input.applicationEnabled === false) {
    if (input.verificationTask) {
      await operations.task(input.verificationTask);
    }

    return;
  }

  await operations.service(input.webService, input.webTask, input.webCount);

  // The collector resolves the issuer during startup, so discovery must be reachable first.
  await operations.healthy(
    `https://${input.hostname}/.well-known/openid-configuration`,
  );

  await operations.service(
    input.workerService,
    input.workerTask,
    input.workerCount,
  );

  await operations.service(input.collectorService, input.collectorTask, 1);
  await operations.healthy(`https://${input.hostname}/health`);
}

async function operations(input: RolloutInput): Promise<RuntimeOperations> {
  // Import inside the provider methods so SDK clients/credentials are never serialized into state.
  const ecs = await import("@aws-sdk/client-ecs");
  const client = new ecs.ECSClient({ region: input.region });
  const wait = (ms: number) =>
    new Promise((resolve) => setTimeout(resolve, ms));

  return {
    async task(taskDefinition) {
      console.log(`Running installation task ${taskDefinition}.`);

      const result = await client.send(
        new ecs.RunTaskCommand({
          cluster: input.cluster,
          taskDefinition,
          launchType: "FARGATE",
          count: 1,
          enableECSManagedTags: true,
          propagateTags: "TASK_DEFINITION",
          networkConfiguration: {
            awsvpcConfiguration: {
              subnets: input.subnets,
              securityGroups: input.securityGroups,
              assignPublicIp: "DISABLED",
            },
          },
        }),
      );

      if (
        result.failures?.length ||
        result.tasks?.length !== 1 ||
        !result.tasks[0].taskArn
      ) {
        throw new Error(
          "ECS could not start the installation task. Inspect ECS failures/events.",
        );
      }

      const arn = result.tasks[0].taskArn;
      const deadline = Date.now() + 30 * 60_000;

      while (Date.now() < deadline) {
        const status = await client.send(
          new ecs.DescribeTasksCommand({
            cluster: input.cluster,
            tasks: [arn],
          }),
        );

        if (status.failures?.length) {
          throw new Error(`Unable to inspect installation task ${arn}.`);
        }

        const task = status.tasks?.[0];

        if (task?.lastStatus === "STOPPED") {
          const container = task.containers?.find((c) => c.name === "zeeq");

          if (container?.exitCode !== 0) {
            throw new Error(
              `Installation task ${arn} failed. Inspect its CloudWatch log; no service rollout was authorized.`,
            );
          }

          console.log(`Installation task ${arn} succeeded.`);

          return;
        }

        await wait(5000);
      }

      await client.send(
        new ecs.StopTaskCommand({
          cluster: input.cluster,
          task: arn,
          reason: "Installation task deadline exceeded",
        }),
      );

      throw new Error(`Installation task ${arn} timed out and was stopped.`);
    },
    async service(service, taskDefinition, desiredCount) {
      console.log(
        `Converging ECS service ${service} to ${desiredCount} tasks.`,
      );

      await client.send(
        new ecs.UpdateServiceCommand({
          cluster: input.cluster,
          service,
          taskDefinition,
          desiredCount,
          forceNewDeployment: true,
        }),
      );

      const result = await ecs.waitUntilServicesStable(
        { client, maxWaitTime: 1200, minDelay: 10, maxDelay: 20 },
        { cluster: input.cluster, services: [service] },
      );

      if (result.state !== "SUCCESS") {
        throw new Error(`ECS service ${service} did not stabilize.`);
      }

      // Circuit-breaker rollback can be stable while running an older definition.
      const status = await client.send(
        new ecs.DescribeServicesCommand({
          cluster: input.cluster,
          services: [service],
        }),
      );

      const current = status.services?.[0];

      if (
        status.failures?.length ||
        current?.taskDefinition !== taskDefinition ||
        current.runningCount !== desiredCount ||
        current.deployments?.some((d) => d.rolloutState === "FAILED")
      ) {
        throw new Error(
          `ECS service ${service} did not converge to the requested revision.`,
        );
      }
    },
    async healthy(url) {
      const deadline = Date.now() + 15 * 60_000;
      let failure = "No response";

      while (Date.now() < deadline) {
        try {
          const response = await fetch(url, {
            signal: AbortSignal.timeout(30_000),
          });

          if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
          }

          if (url.endsWith("openid-configuration")) {
            const discovery = (await response.json()) as { issuer: string };

            if (discovery.issuer !== `https://${input.hostname}/`) {
              throw new Error(
                "OIDC issuer does not match the installation hostname.",
              );
            }
          }

          return;
        } catch (error) {
          failure = error instanceof Error ? error.message : "Request failed";
        }

        await wait(10_000);
      }

      throw new Error(
        `Readiness failed at ${url}: ${failure}. Check DNS CNAMEs and ECS/ALB health before retrying.`,
      );
    },
  };
}

/** Static RDS settings must be applied before extensions and migrations can run. */
async function parametersReady(input: RolloutInput) {
  if (input.databaseId) {
    const rds = await import("@aws-sdk/client-rds");
    const client = new rds.RDSClient({ region: input.region });

    const status = await client.send(
      new rds.DescribeDBInstancesCommand({
        DBInstanceIdentifier: input.databaseId,
      }),
    );

    if (
      status.DBInstances?.[0]?.DBParameterGroups?.some(
        (p) => p.ParameterApplyStatus !== "in-sync",
      )
    ) {
      throw new Error(
        "RDS parameters are not in-sync; apply/reboot before SQL bootstrap.",
      );
    }
  }
}

/** Runs imperative ECS work under Pulumi ordering, update locking, and resource lifecycle. */
class InstallationProvider implements pulumi.dynamic.ResourceProvider {
  async create(input: RolloutInput) {
    await parametersReady(input);
    await rollout(input, await operations(input));

    return {
      id: `${input.cluster}/installation`,
      outs: { ...input, completedAt: new Date().toISOString() },
    };
  }

  async diff(_id: string, old: RolloutInput, input: RolloutInput) {
    const immutable = ["cluster", "region", "hostname"] as const;

    return {
      changes:
        JSON.stringify(old, Object.keys(input).sort()) !==
        JSON.stringify(input, Object.keys(input).sort()),
      replaces: immutable.filter((k) => old[k] !== input[k]),
    };
  }

  async update(_id: string, _old: RolloutInput, input: RolloutInput) {
    await parametersReady(input);
    await rollout(input, await operations(input));

    return { outs: { ...input, completedAt: new Date().toISOString() } };
  }

  async delete(_id: string, input: RolloutInput) {
    const op = await operations(input);

    for (const [service, definition] of [
      [input.collectorService, input.collectorTask],
      [input.workerService, input.workerTask],
      [input.webService, input.webTask],
    ]) {
      await op.service(service, definition, 0);
    }

    // Only reused databases need SQL cleanup; owned RDS instances are removed by Pulumi.
    if (input.cleanupTask) {
      await op.task(input.cleanupTask);
    }
  }
}

/** Managed by the Pulumi engine, including its update lock and reverse teardown ordering. */
export class Installation extends pulumi.dynamic.Resource {
  readonly completedAt!: pulumi.Output<string>;
  constructor(
    name: string,
    input: pulumi.Inputs,
    opts: pulumi.CustomResourceOptions,
  ) {
    super(
      new InstallationProvider(),
      name,
      { ...input, completedAt: undefined },
      opts,
    );
  }
}
