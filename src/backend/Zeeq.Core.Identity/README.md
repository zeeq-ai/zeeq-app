# Zeeq Identity

This holds the OpenIddict server implementation for Zeeq auth for both MCP and the API surface area.

Auth is multi-modal meaning that it must support external IdP, internally created tokens, etc.

Local Aspire runs use [0factor](../../../build/0factor/README.md) as the external development provider. Its stable provider key is `mock`; Zeeq retains ownership of app cookies, MCP tokens, org membership, and team roles. See the [e2e test plan](../../../build/0factor/e2e-test-plan.md) for migration verification.
