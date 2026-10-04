import * as aws from "@pulumi/aws";
import * as pulumi from "@pulumi/pulumi";
import { InstallConfig } from "./config";

/** Reuses a certificate or requests ACM validation for the web and telemetry hostnames. */
export function certificate(
  c: InstallConfig,
  provider: aws.Provider,
  zoneId: pulumi.Input<string>,
) {
  const opts = { provider };
  const reusedArn = c.ingress?.certificateArn ?? c.cfg.get("certificateArn");

  if (reusedArn) {
    return { arn: pulumi.output(reusedArn), records: pulumi.output([]) };
  }

  const cert = new aws.acm.Certificate(
    "https-certificate",
    {
      domainName: c.hostname,
      subjectAlternativeNames: [`otel.${c.hostname}`],
      validationMethod: "DNS",
    },
    opts,
  );

  const records = cert.domainValidationOptions.apply((options) =>
    options.map((o) => ({
      name: o.resourceRecordName,
      type: o.resourceRecordType,
      value: o.resourceRecordValue,
    })),
  );

  const managed =
    c.dnsMode === "managed"
      ? [c.hostname, `otel.${c.hostname}`].map(
          (domain, i) =>
            new aws.route53.Record(
              `certificate-validation-${i}`,
              {
                zoneId,
                name: cert.domainValidationOptions.apply(
                  (o) =>
                    o.find((v) => v.domainName === domain)!.resourceRecordName,
                ),
                type: "CNAME",
                ttl: 60,
                records: [
                  cert.domainValidationOptions.apply(
                    (o) =>
                      o.find((v) => v.domainName === domain)!
                        .resourceRecordValue,
                  ),
                ],
              },
              opts,
            ),
        )
      : [];

  // DNS preparation exports validation records without waiting for operator DNS changes.
  const validation =
    c.stage !== "dns"
      ? new aws.acm.CertificateValidation(
          "https-validation",
          {
            certificateArn: cert.arn,
            validationRecordFqdns:
              c.dnsMode === "managed"
                ? managed.map((r) => r.fqdn)
                : records.apply((r) => r.map((v) => v.name)),
          },
          opts,
        )
      : undefined;

  return { arn: validation?.certificateArn ?? cert.arn, records };
}
