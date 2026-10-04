import * as aws from "@pulumi/aws";
import * as random from "@pulumi/random";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";

/** Prepares private database access and separate bootstrap, migration, and runtime credentials. */
export function database(
  c: InstallConfig,
  provider: aws.Provider,
  vpcId: pulumi.Input<string>,
  subnets: pulumi.Input<string>[],
  taskSg: aws.ec2.SecurityGroup,
) {
  const opts = { provider };

  // Reuse grants network access only; SQL prerequisites and external secrets remain operator-owned.
  if (c.database) {
    new aws.vpc.SecurityGroupIngressRule(
      "reused-db-access",
      {
        securityGroupId: c.database.securityGroupId,
        referencedSecurityGroupId: taskSg.id,
        ipProtocol: "tcp",
        fromPort: c.database.port ?? 5432,
        toPort: c.database.port ?? 5432,
      },
      opts,
    );

    return {
      host: pulumi.output(c.database.host),
      ownerSecret: pulumi.output(c.database.ownerConnectionSecretArn),
      runtimeSecret: pulumi.output(c.database.runtimeConnectionSecretArn),
      bootstrapSecret: undefined,
      ownerPassword: undefined,
      runtimePassword: undefined,
    };
  }

  const group = new aws.ec2.SecurityGroup(
    "database-network",
    {
      vpcId,
      ingress: [
        {
          protocol: "tcp",
          fromPort: 5432,
          toPort: 5432,
          securityGroups: [taskSg.id],
        },
      ],
    },
    opts,
  );

  const subnet = new aws.rds.SubnetGroup(
    "database-subnets",
    { subnetIds: subnets },
    opts,
  );

  // RDS uses pg_cron for partman maintenance; pg_partman_bgw is unavailable on RDS.
  const parameters = new aws.rds.ParameterGroup(
    "database-parameters",
    {
      family: "postgres18",
      parameters: [
        {
          name: "shared_preload_libraries",
          value: "pg_stat_statements,pg_tle,pg_cron",
          applyMethod: "pending-reboot",
        },
        {
          name: "cron.database_name",
          value: "zeeq",
          applyMethod: "pending-reboot",
        },
        {
          name: "max_worker_processes",
          value: "16",
          applyMethod: "pending-reboot",
        },
        {
          name: "cron.max_running_jobs",
          value: "3",
          applyMethod: "pending-reboot",
        },
        { name: "rds.force_ssl", value: "1", applyMethod: "pending-reboot" },
      ],
    },
    opts,
  );

  const db = new aws.rds.Instance(
    "database",
    {
      identifier: `${c.id}-postgres`,
      engine: "postgres",
      engineVersion: "18.6",
      instanceClass: c.dbSize,
      allocatedStorage: c.dbStorage,
      maxAllocatedStorage: c.cfg.getNumber("dbMaxStorageGiB") ?? 100,
      storageType: "gp3",
      storageEncrypted: true,
      dbName: "zeeq",
      username: "zeeq_admin",
      manageMasterUserPassword: true,
      publiclyAccessible: false,
      multiAz: c.multiAz,
      availabilityZone: c.multiAz ? undefined : c.cfg.get("dbAvailabilityZone"),
      vpcSecurityGroupIds: [group.id],
      dbSubnetGroupName: subnet.name,
      parameterGroupName: parameters.name,
      backupRetentionPeriod: 7,
      autoMinorVersionUpgrade: false,
      deletionProtection: c.protectData,
      skipFinalSnapshot: true,
      deleteAutomatedBackups: true,
    },
    { ...opts, protect: c.protectData },
  );

  // Keep migration/cron ownership stable; web and worker receive only runtime credentials.
  const makeSecret = (name: string, user: string) => {
    const password = new random.RandomPassword(`${name}-password`, {
      length: 40,
      special: false,
    });

    const secret = new aws.secretsmanager.Secret(
      name,
      { name: `${c.id}/${name}`, recoveryWindowInDays: 0 },
      { ...opts, protect: c.protectData },
    );

    new aws.secretsmanager.SecretVersion(
      `${name}-value`,
      {
        secretId: secret.id,
        secretString: pulumi
          .all([db.address, password.result])
          .apply(([host, pass]) =>
            JSON.stringify({
              password: pass,
              connectionString: `Host=${host};Database=zeeq;Username=${user};Password=${pass};SSL Mode=VerifyFull;Root Certificate=/app/rds-ca.pem;Search Path=zeeq,public;Maximum Pool Size=20`,
            }),
          ),
      },
      opts,
    );

    return secret.arn;
  };

  return {
    host: db.address,
    db,
    ownerSecret: makeSecret("database-owner", "zeeq_owner"),
    runtimeSecret: makeSecret("database-runtime", "zeeq_runtime"),
    bootstrapSecret: db.masterUserSecrets.apply((s) => s[0].secretArn),
    ownerPassword: "password",
    runtimePassword: "password",
  };
}
