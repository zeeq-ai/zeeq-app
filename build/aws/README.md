# AWS deployment

Pulumi owns the installation, including database preparation, migrations and service rollout. The C# runtime exports its queue catalog; the installer does not duplicate routing rules.

Use Node.js 24+, .NET 10, Docker BuildKit, AWS CLI v2 and Pulumi 3.267.0. Authenticate with an SSO profile. Run commands in this directory.

```sh
npm ci
npm run check
npm test
dotnet build ../../src/backend/Zeeq.Runtime.Server -c Release
npm run configure -- --stack sandbox
npm run deploy -- --dns
# Add certificate-validation DNS records, then prepare infrastructure without app credentials.
npm run deploy -- --infrastructure
npm run configure -- --stack sandbox --application-file application.local.json
npm run deploy
npm run manifest
```

Copy `application.example.json` to `application.local.json` and replace every placeholder. Protect that file; configuration is encrypted in Pulumi state and injected by Secrets Manager. Use `configure --external-dns` to keep DNS at your current provider. Add the manifest’s certificate-validation CNAMEs there; add the application and telemetry CNAMEs to the ALB hostname printed during provisioning. If a parent hosted zone already exists, set `hostedZoneId` and skip delegation. The default local state backend and passphrase are under `~/.local/state/zeeq-pulumi`; back up both. A remote backend can be selected with `configure --backend <URL>`.

`configure` asks whether to create/reuse PostgreSQL and ingress. New PostgreSQL defaults to the smallest supported RDS size, `db.t4g.micro`; select a larger size for sustained work. Reuse requires the selected VPC/subnets and an existing database prepared for the owner/runtime identity contract. See `reuse.example.json`.

Deploy refreshes state, builds/pushes immutable private images, updates task definitions, runs privileged bootstrap and owner migrations, then starts web, worker and collector. A failed SQL task changes no service revision. Schema changes may still have been applied; use compatible migrations and backups. The dynamic resource uses the Pulumi update lock. Do not use direct `pulumi up` for upgrades without first refreshing service state.

`installation.json` contains timestamps, account/region, endpoints, resource URNs/IDs/ARNs, reused references and removal instructions. It contains no secret values. Keep it until removal has been verified.

```sh
npm run remove -- --delete-data
```

This removes the installation's protection settings and destroys its managed resources, including database data, queues, images, logs and secrets. Export data first. For a reused, dedicated database it runs SQL cleanup for application objects while retaining external roles/extensions and the instance. Shared VPCs/load balancers/listeners/certificates/zones/secrets remain externally owned. Remove external CNAMEs or the parent NS delegation yourself. AWS KMS enforces a seven-day key deletion wait; the manifest records it. The account, authentication, state backend and passphrase remain operator prerequisites.

The default is a minimal sandbox: single-AZ RDS, one NAT gateway and one web/worker task. Larger database sizes, Multi-AZ, task counts, storage, buckets, tags, network and image digests are stack configuration. An NLB has no host rules: reuse needs its security group and unused TLS ports 443 (web) and 444 (telemetry), plus a matching certificate. Prefer ALB for normal hostname-based HTTPS.

The infrastructure stage also runs a one-off verification under the worker role: restricted database/cache access, KMS round-trip with organization isolation, and SQS send/receive/delete. It is intended for preparation while consumers remain stopped.

For login validation, copy `application.login.example.json` to the ignored `application.local.json` and configure with `--login-only --application-file application.local.json`. This sets workerCount to zero. Supply one OAuth provider, PFX payloads/passwords and the Fast LLM API key currently required at runtime startup. GitHub, review-link/export keys and an administrator subject can be added later. Set workerCount to one when enabling review processing.

Run this from the repository root to generate the OpenIddict certificates and random passwords:

```sh
./build/certs/gen-openiddit-aws-certs.sh
```

Replace the four fields under `AppSettings.Auth.OpenIddict` in `build/aws/application.local.json`, in the order printed:

1. `SigningCertificateBase64` — copy, then press Enter.
2. `SigningCertificatePassword` — copy, then press Enter.
3. `EncryptionCertificateBase64` — copy, then press Enter.
4. `EncryptionCertificatePassword` — copy.

The script does not edit the settings file or upload secrets. Temporary certificate files are removed on exit. Keep the settings file private and reuse the same values for subsequent deployments; rerunning generates new keys. These certificates are separate from the ACM HTTPS certificate.
