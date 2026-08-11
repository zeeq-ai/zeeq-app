# Zeeq

![Screenshot of the Zeeq telemetry dashboard](https://zeeq.ai/screens/carousel/session-token-telemetry.webp)

Zeeq ("zeek") is an MCP server that provides teams with visibility into which content is being activated and used in agent context.

This telemetry lets teams *see* how agents are using context to affect code authoring.

The same context origin and telemetry are applied to code reviews which can be invoked *in the agent loop* (versus in CI on the PR).

It is designed to be self hostable, relatively consolidated on a few technologies (can run entirely on Postgres; no additional infrastructure), yet operationally scalable.

---

## Local setup

Zeeq is designed to be easy to setup and run using [Mise](https://mise.jdx.dev/installing-mise.html) to install the necessary runtime components.

For local setup see: <https://zeeq.ai/docs/managing-zeeq/local-development>

---

## Self hosted setup

Deploying to Google Cloud? See: <https://zeeq.ai/docs/configuration/gcp-runtime>

---

## License

Zeeq AI
Copyright (C) 2026 Zeeq Labs, Inc

Zeeq is licensed under the GNU Affero General Public License,
version 3 or later. See [LICENSE](LICENSE) for details.
