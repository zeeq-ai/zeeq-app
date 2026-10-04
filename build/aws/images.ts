import * as aws from "@pulumi/aws";
import * as docker from "@pulumi/docker-build";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";

/** Builds immutable private images or returns supplied digests without adopting their repositories. */
export function images(c: InstallConfig, provider: aws.Provider) {
  const opts = { provider };

  const build = (name: string, dockerfile: string, supplied?: string) => {
    // Digest references avoid rebuilding or taking ownership of externally supplied images.
    if (supplied) {
      if (!supplied.includes("@sha256:")) {
        throw new Error(`${name}Image must use an immutable sha256 digest.`);
      }

      return pulumi.output(supplied);
    }

    const repository = new aws.ecr.Repository(
      `${name}-images`,
      {
        name: `${c.id}/${name}`,
        forceDelete: true,
        imageTagMutability: "IMMUTABLE",
        imageScanningConfiguration: { scanOnPush: true },
      },
      opts,
    );

    const authorization = aws.ecr.getAuthorizationTokenOutput(
      { registryId: c.accountId },
      opts,
    );

    const image = new docker.Image(`${name}-image`, {
      context: { location: "../.." },
      dockerfile: { location: dockerfile },
      platforms: ["linux/amd64"],
      tags: [
        pulumi.interpolate`${repository.repositoryUrl}:${c.cfg.require("sourceRevision")}`,
      ],
      push: true,
      buildArgs: {
        ZEEQ_BUILD_TARGET: "aws",
        GIT_SHA: c.cfg.require("sourceRevision"),
        ZEEQ_VERSION: c.cfg.get("version") ?? "0.0.0-aws",
        ZEEQ_VERSION_TAG: c.cfg.get("version") ?? "0.0.0-aws",
      },
      registries: [
        {
          address: repository.repositoryUrl,
          username: authorization.userName,
          password: pulumi.secret(authorization.password),
        },
      ],
    });

    return image.ref;
  };

  return {
    runtime: build(
      "runtime",
      "../../build/Dockerfile",
      c.cfg.get("runtimeImage"),
    ),
    collector: build(
      "collector",
      "../../build/otel-collector/Dockerfile.aws",
      c.cfg.get("collectorImage"),
    ),
  };
}
