import * as aws from "@pulumi/aws";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";

/** Returns a selected VPC or creates public ingress and private workload subnets. */
export function network(c: InstallConfig, provider: aws.Provider) {
  const opts = { provider };
  const ready: pulumi.Resource[] = [];

  if (c.reusedNetwork) {
    return {
      ready,
      vpcId: pulumi.output(c.reusedNetwork.vpcId),
      publicSubnets: c.reusedNetwork.publicSubnetIds.map((id) =>
        pulumi.output(id),
      ),
      privateSubnets: c.reusedNetwork.privateSubnetIds.map((id) =>
        pulumi.output(id),
      ),
    };
  }

  const vpc = new aws.ec2.Vpc(
    "network",
    {
      cidrBlock: "10.72.0.0/16",
      enableDnsHostnames: true,
      enableDnsSupport: true,
    },
    opts,
  );

  const zones = aws.getAvailabilityZonesOutput({ state: "available" }, opts);

  const gateway = new aws.ec2.InternetGateway(
    "internet",
    { vpcId: vpc.id },
    opts,
  );

  const publicRoutes = new aws.ec2.RouteTable(
    "public-routes",
    {
      vpcId: vpc.id,
      routes: [{ cidrBlock: "0.0.0.0/0", gatewayId: gateway.id }],
    },
    opts,
  );

  // The ALB must cover every availability zone used by ECS tasks.
  const publicSubnets = [0, 1, 2].map((i) => {
    const subnet = new aws.ec2.Subnet(
      `public-${i}`,
      {
        vpcId: vpc.id,
        cidrBlock: `10.72.${i}.0/24`,
        availabilityZone: zones.names.apply((z) => z[i]),
        mapPublicIpOnLaunch: false,
      },
      opts,
    );

    ready.push(
      new aws.ec2.RouteTableAssociation(
        `public-route-${i}`,
        { subnetId: subnet.id, routeTableId: publicRoutes.id },
        opts,
      ),
    );

    return subnet.id;
  });

  const eip = new aws.ec2.Eip("nat-address", { domain: "vpc" }, opts);

  const nat = new aws.ec2.NatGateway(
    "nat",
    { allocationId: eip.id, subnetId: publicSubnets[0] },
    { ...opts, dependsOn: [gateway] },
  );

  const privateRoutes = new aws.ec2.RouteTable(
    "private-routes",
    {
      vpcId: vpc.id,
      routes: [{ cidrBlock: "0.0.0.0/0", natGatewayId: nat.id }],
    },
    opts,
  );

  const privateSubnets = [0, 1, 2].map((i) => {
    const subnet = new aws.ec2.Subnet(
      `private-${i}`,
      {
        vpcId: vpc.id,
        cidrBlock: `10.72.${i + 10}.0/24`,
        availabilityZone: zones.names.apply((z) => z[i]),
      },
      opts,
    );

    ready.push(
      new aws.ec2.RouteTableAssociation(
        `private-route-${i}`,
        { subnetId: subnet.id, routeTableId: privateRoutes.id },
        opts,
      ),
    );

    return subnet.id;
  });

  return { ready, vpcId: vpc.id, publicSubnets, privateSubnets };
}
