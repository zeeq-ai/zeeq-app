# Zeeq

![Screenshot of the Zeeq telemetry dashboard](https://zeeq.ai/screens/carousel/session-token-telemetry.webp)

Zeeq ("zeek") is an MCP server that provides teams with visibility into which content is being activated and used in agent context.

This telemetry lets teams *see* how agents are using context to affect code authoring.

![Screenshot of the Zeeq telemetry dashboard for tool calls](https://zeeq.ai/screens/carousel/topline-telemetry.webp)

The same context origin and telemetry are applied to code reviews which can be invoked *in the agent loop* (versus in CI on the PR).

It is designed to be self hostable, relatively consolidated on a few technologies (can run entirely on Postgres; no additional infrastructure), yet operationally scalable.

Zeeq was originally built as an internal platform at [Motion](https://www.usemotion.com/) ($500m, series C, YC startup) to shape agentic coding output and is now released as an open source tool.

It is a key part of Motion's internal agentic coding platform, which is used by a team of 40+ engineers to ship with high confidence using AI coding tools.

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
