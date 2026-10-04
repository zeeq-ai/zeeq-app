import * as aws from "@pulumi/aws";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";

/** Creates owned routing resources while preserving any supplied load balancer and listener. */
export function ingress(
  c: InstallConfig,
  provider: aws.Provider,
  zoneId: pulumi.Input<string>,
  vpcId: pulumi.Input<string>,
  publicSubnets: pulumi.Input<string>[],
  certificate: { arn: pulumi.Input<string> },
) {
  const opts = { provider };
  const certificateArn = certificate.arn;

  const sg = c.ingress
    ? undefined
    : new aws.ec2.SecurityGroup(
        "ingress-network",
        {
          vpcId,
          ingress: [
            {
              protocol: "tcp",
              fromPort: 443,
              toPort: 443,
              cidrBlocks: ["0.0.0.0/0"],
            },
          ],
          egress: [
            {
              protocol: "-1",
              fromPort: 0,
              toPort: 0,
              cidrBlocks: ["0.0.0.0/0"],
            },
          ],
        },
        opts,
      );

  const lb = c.ingress
    ? undefined
    : new aws.lb.LoadBalancer(
        "ingress",
        {
          loadBalancerType: "application",
          subnets: publicSubnets,
          securityGroups: [sg!.id],
          idleTimeout: 300,
          enableHttp2: true,
        },
        opts,
      );

  const type = c.ingress?.type ?? "application";

  const groups = [8080, 4318].map(
    (port, i) =>
      new aws.lb.TargetGroup(
        i === 0 ? "web-targets" : "telemetry-targets",
        {
          vpcId,
          port,
          protocol: type === "network" ? "TCP" : "HTTP",
          targetType: "ip",
          deregistrationDelay: 120,
          healthCheck: {
            protocol: "HTTP",
            path: i === 0 ? "/health" : "/",
            port: i === 0 ? "traffic-port" : "13133",
            matcher: "200",
          },
        },
        opts,
      ),
  );

  let listener: aws.lb.Listener | undefined;
  const rules: pulumi.Resource[] = [];

  if (type === "application") {
    if (!c.ingress) {
      listener = new aws.lb.Listener(
        "https",
        {
          loadBalancerArn: lb!.arn,
          port: 443,
          protocol: "HTTPS",
          sslPolicy: "ELBSecurityPolicy-TLS13-1-2-2021-06",
          certificateArn: certificateArn,
          defaultActions: [
            {
              type: "fixed-response",
              fixedResponse: { contentType: "text/plain", statusCode: "404" },
            },
          ],
        },
        opts,
      );
    } else {
      rules.push(
        new aws.lb.ListenerCertificate(
          "reused-listener-certificate",
          {
            listenerArn: c.ingress.listenerArn!,
            certificateArn: certificateArn,
          },
          opts,
        ),
      );
    }

    const listenerArn = listener?.arn ?? c.ingress!.listenerArn!;

    groups.forEach((group, i) =>
      rules.push(
        new aws.lb.ListenerRule(
          `host-${i}`,
          {
            listenerArn,
            priority: (c.ingress?.rulePriority ?? 100) + i,
            conditions: [
              {
                hostHeader: {
                  values: [i === 0 ? c.hostname : `otel.${c.hostname}`],
                },
              },
            ],
            actions: [{ type: "forward", targetGroupArn: group.arn }],
          },
          opts,
        ),
      ),
    );
  } else {
    // NLB has no host routing. Reuse adds two explicitly reserved TLS listener ports.
    groups.forEach((group, i) =>
      rules.push(
        new aws.lb.Listener(
          `nlb-listener-${i}`,
          {
            loadBalancerArn: c.ingress!.arn,
            port: c.ingress!.listenerPort! + i,
            protocol: "TLS",
            certificateArn: c.ingress!.certificateArn!,
            defaultActions: [{ type: "forward", targetGroupArn: group.arn }],
          },
          opts,
        ),
      ),
    );
  }

  const dnsName = lb?.dnsName ?? pulumi.output(c.ingress!.dnsName);
  const lbZone = lb?.zoneId ?? pulumi.output(c.ingress!.zoneId);

  if (c.dnsMode === "managed") {
    [c.hostname, `otel.${c.hostname}`].forEach(
      (host, i) =>
        new aws.route53.Record(
          `endpoint-${i}`,
          {
            zoneId,
            name: host,
            type: "A",
            aliases: [
              { name: dnsName, zoneId: lbZone, evaluateTargetHealth: true },
            ],
          },
          opts,
        ),
    );
  }

  return {
    dnsRecords: [c.hostname, `otel.${c.hostname}`].map((name) => ({
      name,
      type: "CNAME",
      value: dnsName,
    })),
    web: groups[0],
    telemetry: groups[1],
    securityGroupId: sg?.id ?? pulumi.output(c.ingress!.securityGroupId),
    rules,
    lbArn: lb?.arn ?? pulumi.output(c.ingress!.arn),
  };
}
