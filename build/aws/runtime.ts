import * as aws from "@pulumi/aws";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";
import { database } from "./database";
import { ingress } from "./ingress";
import { images } from "./images";

/** Queue route emitted by the runtime topology mode; visibility timeout is in seconds. */
export interface Route {
  name: string;
  route: string;
  visibilityTimeout: number;
}

/** Defines queues, scoped IAM roles, secrets, and stopped services for the lifecycle provider. */
export function runtime(
  c: InstallConfig,
  provider: aws.Provider,
  vpcId: pulumi.Input<string>,
  subnets: pulumi.Input<string>[],
  taskSg: aws.ec2.SecurityGroup,
  db: ReturnType<typeof database>,
  edge: ReturnType<typeof ingress>,
  image: ReturnType<typeof images>,
  routes: Route[],
) {
  const opts = { provider };

  const key = new aws.kms.Key(
    "tenant-encryption",
    {
      description: `Zeeq tenant secrets for ${c.id}`,
      enableKeyRotation: true,
      rotationPeriodInDays: 90,
      deletionWindowInDays: 7,
    },
    { ...opts, protect: c.protectData },
  );

  new aws.kms.Alias(
    "tenant-encryption-alias",
    { name: `alias/${c.id}/tenant-secrets`, targetKeyId: key.keyId },
    opts,
  );

  const queues = routes.map((route, i) => {
    const dlq = new aws.sqs.Queue(
      `dead-letter-${i}`,
      {
        name: `${route.name}-dlq`,
        messageRetentionSeconds: 1209600,
        sqsManagedSseEnabled: true,
      },
      opts,
    );

    const queue = new aws.sqs.Queue(
      `queue-${i}`,
      {
        name: route.name,
        visibilityTimeoutSeconds: route.visibilityTimeout,
        receiveWaitTimeSeconds: 20,
        messageRetentionSeconds: 345600,
        sqsManagedSseEnabled: true,
        redrivePolicy: dlq.arn.apply((arn) =>
          JSON.stringify({ deadLetterTargetArn: arn, maxReceiveCount: 5 }),
        ),
      },
      opts,
    );

    new aws.sqs.RedriveAllowPolicy(
      `redrive-allow-${i}`,
      {
        queueUrl: dlq.url,
        redriveAllowPolicy: queue.arn.apply((arn) =>
          JSON.stringify({
            redrivePermission: "byQueue",
            sourceQueueArns: [arn],
          }),
        ),
      },
      opts,
    );

    return queue;
  });

  // Probe traffic never enters application queues, including when retrying an installation.
  const probeQueue =
    c.stage === "infrastructure"
      ? new aws.sqs.Queue(
          "installation-probe",
          {
            name: `${c.id}-installation-check`,
            visibilityTimeoutSeconds: 5,
            messageRetentionSeconds: 60,
            sqsManagedSseEnabled: true,
          },
          opts,
        )
      : undefined;

  const assumeRolePolicy = JSON.stringify({
    Version: "2012-10-17",
    Statement: [
      {
        Effect: "Allow",
        Principal: { Service: "ecs-tasks.amazonaws.com" },
        Action: "sts:AssumeRole",
        Condition: {
          StringEquals: { "aws:SourceAccount": c.accountId },
          ArnLike: {
            "aws:SourceArn": `arn:aws:ecs:${c.region}:${c.accountId}:*`,
          },
        },
      },
    ],
  });

  const role = (name: string) =>
    new aws.iam.Role(name, { assumeRolePolicy }, opts);

  const cluster = new aws.ecs.Cluster(
    "cluster",
    { name: c.id, settings: [{ name: "containerInsights", value: "enabled" }] },
    opts,
  );

  const logs = new aws.cloudwatch.LogGroup(
    "runtime-logs",
    { name: `/zeeq/${c.id}`, retentionInDays: 14 },
    opts,
  );

  const policy = (
    name: string,
    r: aws.iam.Role,
    statements: pulumi.Input<unknown>[],
  ) =>
    new aws.iam.RolePolicy(
      name,
      {
        role: r.id,
        policy: pulumi
          .all(statements)
          .apply((Statement) =>
            JSON.stringify({ Version: "2012-10-17", Statement }),
          ),
      },
      opts,
    );

  const statement = (
    Action: string[],
    Resource: pulumi.Input<string> | pulumi.Input<string>[],
  ) => ({ Effect: "Allow", Action, Resource });

  // Execution roles fetch images/secrets; task roles authorize application AWS API calls.
  const execution = (name: string, secrets: pulumi.Input<string>[]) => {
    const r = role(name);

    const p = policy(`${name}-permissions`, r, [
      statement(["ecr:GetAuthorizationToken"], "*"),
      statement(
        [
          "ecr:BatchGetImage",
          "ecr:GetDownloadUrlForLayer",
          "ecr:BatchCheckLayerAvailability",
        ],
        `arn:aws:ecr:${c.region}:${c.accountId}:repository/*`,
      ),
      statement(
        ["logs:CreateLogStream", "logs:PutLogEvents"],
        pulumi.interpolate`${logs.arn}:*`,
      ),
      ...(secrets.length
        ? [
            pulumi
              .all(secrets)
              .apply((arns) =>
                statement(["secretsmanager:GetSecretValue"], arns),
              ),
          ]
        : []),
      ...(c.cfg.getObject<string[]>("secretKmsKeyArns")?.length
        ? [
            statement(
              ["kms:Decrypt"],
              c.cfg.getObject<string[]>("secretKmsKeyArns")!,
            ),
          ]
        : []),
    ]);

    return { r, p };
  };

  // Infrastructure preparation needs no OAuth/PFX settings and creates no application secret version.
  const application =
    c.stage === "install"
      ? c.cfg.requireSecretObject<Record<string, any>>("application")
      : undefined;

  const configSecret = new aws.secretsmanager.Secret(
    "application-config",
    { name: `${c.id}/application-config`, recoveryWindowInDays: 0 },
    { ...opts, protect: c.protectData },
  );

  const configVersion = application
    ? new aws.secretsmanager.SecretVersion(
        "application-config-value",
        {
          secretId: configSecret.id,
          secretString: pulumi
            .all([application, key.arn])
            .apply(([input, arn]) => {
              // Preserve customer credentials while enforcing deployment-owned URLs and runtime safety settings.
              const settings = input.AppSettings ?? {};

              return JSON.stringify({
                ...input,
                Kestrel: {
                  Endpoints: {
                    Http: { Url: "http://+:8080", Protocols: "Http1AndHttp2" },
                  },
                },
                AppSettings: {
                  ...settings,
                  Http: {
                    ...settings.Http,
                    ApiBaseUri: `https://${c.hostname}`,
                    FrontendBaseUri: `https://${c.hostname}/web`,
                    AllowedCorsOrigins: [`https://${c.hostname}`],
                  },
                  Auth: {
                    ...settings.Auth,
                    Issuer: `https://${c.hostname}/`,
                    Resource: `https://${c.hostname}/mcp`,
                    FrontendBaseUri: `https://${c.hostname}/web`,
                    AccessTokenFormat: "SignedJwt",
                  },
                  Database: { MigrateOnStartup: false },
                  Llm: {
                    ...settings.Llm,
                    EncryptionProvider: "aws-kms",
                    AwsKmsKeyArn: arn,
                    GoogleKmsKeyName: "",
                  },
                  Cache: {
                    ...settings.Cache,
                    Provider: "Postgres",
                    CreateIfNotExists: false,
                    SchemaName: "cache",
                    TableName: "hybrid_cache",
                    UseWAL: false,
                  },
                },
                ZeeqMessaging: {
                  ...input.ZeeqMessaging,
                  Provider: "AwsSqs",
                  AwsSqs: {
                    Region: c.region,
                    QueuePrefix: c.id,
                    CreateMissingQueues: false,
                    MissingChannelPolicy: "Validate",
                    LongPollSeconds: 20,
                  },
                  TenantBuckets: {
                    PriorityBucketCount: c.buckets.priority,
                    DefaultBucketCount: c.buckets.standard,
                    LowBucketCount: c.buckets.low,
                  },
                },
              });
            }),
        },
        opts,
      )
    : undefined;

  const appExecution = execution("application-execution", [
    configSecret.arn,
    db.runtimeSecret,
  ]);

  const migrateExecution = execution("migration-execution", [db.ownerSecret]);

  const bootstrapExecution = db.bootstrapSecret
    ? execution("bootstrap-execution", [
        db.bootstrapSecret,
        db.ownerSecret,
        db.runtimeSecret,
      ])
    : undefined;

  const collectorExecution = execution("collector-execution", []);
  const webRole = role("web-role");
  const workerRole = role("worker-role");
  const migrationRole = role("migration-role");
  const collectorRole = role("collector-role");

  // Require tenant context; the application supplies the organization ID on every KMS operation.
  const crypto = key.arn.apply((arn) => ({
    ...statement(["kms:Encrypt", "kms:Decrypt"], arn),
    Condition: { StringLike: { "kms:EncryptionContext:organizationId": "?*" } },
  }));

  const queueArns = pulumi.all(queues.map((q) => q.arn));

  const webPolicy = policy("web-permissions", webRole, [
    queueArns.apply((arns) =>
      statement(
        ["sqs:SendMessage", "sqs:GetQueueUrl", "sqs:GetQueueAttributes"],
        arns,
      ),
    ),
    crypto,
  ]);

  const runtimeQueueArns = pulumi.all([
    ...queues.map((q) => q.arn),
    ...(probeQueue ? [probeQueue.arn] : []),
  ]);

  const workerPolicy = policy("worker-permissions", workerRole, [
    runtimeQueueArns.apply((arns) =>
      statement(
        [
          "sqs:SendMessage",
          "sqs:GetQueueUrl",
          "sqs:GetQueueAttributes",
          "sqs:ReceiveMessage",
          "sqs:DeleteMessage",
          "sqs:ChangeMessageVisibility",
        ],
        arns,
      ),
    ),
    crypto,
  ]);

  const definition = (
    name: string,
    runMode: string,
    exec: ReturnType<typeof execution>,
    r: aws.iam.Role,
    secrets: { name: string; valueFrom: pulumi.Input<string> }[],
    environment: Record<string, pulumi.Input<string>>,
    memory = 2048,
  ) =>
    new aws.ecs.TaskDefinition(
      name,
      {
        family: `${c.id}-${name}`,
        requiresCompatibilities: ["FARGATE"],
        networkMode: "awsvpc",
        cpu: "512",
        memory: String(memory),
        runtimePlatform: {
          cpuArchitecture: "X86_64",
          operatingSystemFamily: "LINUX",
        },
        executionRoleArn: exec.r.arn,
        taskRoleArn: r.arn,
        containerDefinitions: pulumi
          .all([
            image.runtime,
            logs.name,
            pulumi.all(
              secrets.map((s) =>
                pulumi
                  .output(s.valueFrom)
                  .apply((valueFrom) => ({ name: s.name, valueFrom })),
              ),
            ),
            pulumi.all(
              Object.entries(environment).map(([name, value]) =>
                pulumi.output(value).apply((value) => ({ name, value })),
              ),
            ),
          ])
          .apply(([img, group, values, env]) =>
            JSON.stringify([
              {
                name: "zeeq",
                image: img,
                essential: true,
                stopTimeout: 120,
                environment: [
                  { name: "DOTNET_ENVIRONMENT", value: "Aws" },
                  { name: "ASPNETCORE_ENVIRONMENT", value: "Aws" },
                  { name: "ZEEQ_RUN_MODE", value: runMode },
                  { name: "AWS_REGION", value: c.region },
                  ...env,
                ],
                secrets: values,
                portMappings:
                  runMode === "web" ? [{ containerPort: 8080 }] : [],
                logConfiguration: {
                  logDriver: "awslogs",
                  options: {
                    "awslogs-group": group,
                    "awslogs-region": c.region,
                    "awslogs-stream-prefix": name,
                  },
                },
              },
            ]),
          ),
      },
      {
        ...opts,
        dependsOn: [
          exec.p,
          ...(configVersion ? [configVersion] : []),
          ...queues,
        ],
      },
    );

  const dbSecret = [
    {
      name: "AppSettings__Database__ConnectionString",
      valueFrom: pulumi.interpolate`${db.runtimeSecret}:connectionString::`,
    },
  ];

  const appSecrets = [
    ...dbSecret,
    { name: "ZEEQ_CONFIG_JSON", valueFrom: configSecret.arn },
  ];

  const web = definition("web", "web", appExecution, webRole, appSecrets, {
    ZEEQ_MESSAGING_ROLE: "producer",
  });

  const worker = definition(
    "worker",
    "worker",
    appExecution,
    workerRole,
    appSecrets,
    { ZEEQ_MESSAGING_ROLE: "producer-consumer" },
    4096,
  );

  const migrate = definition(
    "migrate",
    "migrate",
    migrateExecution,
    migrationRole,
    [
      {
        name: "AppSettings__Database__ConnectionString",
        valueFrom: pulumi.interpolate`${db.ownerSecret}:connectionString::`,
      },
    ],
    {},
  );

  const cleanup = c.database
    ? definition(
        "cleanup",
        "db-cleanup",
        migrateExecution,
        migrationRole,
        [
          {
            name: "AppSettings__Database__ConnectionString",
            valueFrom: pulumi.interpolate`${db.ownerSecret}:connectionString::`,
          },
        ],
        {},
      )
    : undefined;

  const bootstrap = bootstrapExecution
    ? definition(
        "bootstrap",
        "db-bootstrap",
        bootstrapExecution,
        migrationRole,
        [
          {
            name: "ZeeqDeployment__MasterPassword",
            valueFrom: pulumi.interpolate`${db.bootstrapSecret!}:password::`,
          },
          {
            name: "ZeeqDeployment__OwnerPassword",
            valueFrom: pulumi.interpolate`${db.ownerSecret}:password::`,
          },
          {
            name: "ZeeqDeployment__RuntimePassword",
            valueFrom: pulumi.interpolate`${db.runtimeSecret}:password::`,
          },
        ],
        {
          AppSettings__Database__ConnectionString: pulumi.interpolate`Host=${db.host};Database=zeeq;Username=zeeq_admin;SSL Mode=VerifyFull;Root Certificate=/app/rds-ca.pem;Search Path=zeeq,public`,
        },
      )
    : undefined;

  const verify =
    c.stage === "infrastructure"
      ? definition(
          "verify-infrastructure",
          "verify-infrastructure",
          appExecution,
          workerRole,
          dbSecret,
          {
            ZeeqDeployment__KmsKeyArn: key.arn,
            ZeeqDeployment__ProbeQueueUrl: probeQueue!.url,
          },
        )
      : undefined;

  const namespace = new aws.servicediscovery.PrivateDnsNamespace(
    "internal-discovery",
    { name: `${c.id}.internal`, vpc: vpcId },
    opts,
  );

  const discovery = new aws.servicediscovery.Service(
    "web-discovery",
    {
      name: "web",
      dnsConfig: {
        namespaceId: namespace.id,
        routingPolicy: "MULTIVALUE",
        dnsRecords: [{ type: "A", ttl: 10 }],
      },
      healthCheckCustomConfig: { failureThreshold: 1 },
    },
    opts,
  );

  const collector = new aws.ecs.TaskDefinition(
    "collector",
    {
      family: `${c.id}-collector`,
      requiresCompatibilities: ["FARGATE"],
      networkMode: "awsvpc",
      cpu: "256",
      memory: "1024",
      executionRoleArn: collectorExecution.r.arn,
      taskRoleArn: collectorRole.arn,
      containerDefinitions: pulumi
        .all([image.collector, logs.name])
        .apply(([img, group]) =>
          JSON.stringify([
            {
              name: "collector",
              image: img,
              essential: true,
              portMappings: [{ containerPort: 4318 }, { containerPort: 13133 }],
              environment: [
                { name: "ZEEQ_ISSUER_URL", value: `https://${c.hostname}/` },
                {
                  name: "ZEEQ_TELEMETRY_AUDIENCE",
                  value: `https://${c.hostname}/mcp`,
                },
                {
                  name: "ZEEQ_OTLP_HTTP_ENDPOINT",
                  value: `http://web.${c.id}.internal:8080`,
                },
              ],
              logConfiguration: {
                logDriver: "awslogs",
                options: {
                  "awslogs-group": group,
                  "awslogs-region": c.region,
                  "awslogs-stream-prefix": "collector",
                },
              },
            },
          ]),
        ),
    },
    { ...opts, dependsOn: [collectorExecution.p] },
  );

  // Services start at zero. lifecycle.ts owns revisions/counts only after SQL and readiness gates pass.
  const service = (
    name: string,
    task: aws.ecs.TaskDefinition,
    target?: aws.lb.TargetGroup,
  ) =>
    new aws.ecs.Service(
      `${name}-service`,
      {
        name: `${c.id}-${name}`,
        cluster: cluster.arn,
        taskDefinition: task.arn,
        desiredCount: 0,
        launchType: "FARGATE",
        enableEcsManagedTags: true,
        propagateTags: "TASK_DEFINITION",
        deploymentCircuitBreaker: { enable: true, rollback: true },
        deploymentMinimumHealthyPercent: 100,
        deploymentMaximumPercent: 200,
        networkConfiguration: {
          subnets,
          securityGroups: [taskSg.id],
          assignPublicIp: false,
        },
        ...(name === "web"
          ? { serviceRegistries: { registryArn: discovery.arn } }
          : {}),
        loadBalancers: target
          ? [
              {
                targetGroupArn: target.arn,
                containerName: name === "collector" ? "collector" : "zeeq",
                containerPort: name === "collector" ? 4318 : 8080,
              },
            ]
          : [],
      },
      {
        ...opts,
        dependsOn: [...edge.rules, webPolicy, workerPolicy],
        ignoreChanges: ["taskDefinition", "desiredCount"],
      },
    );

  return {
    cluster,
    key,
    queues,
    logs,
    web,
    worker,
    migrate,
    bootstrap,
    cleanup,
    verify,
    collector,
    webService: service("web", web, edge.web),
    workerService: service("worker", worker),
    collectorService: service("collector", collector, edge.telemetry),
    configSecret,
  };
}
