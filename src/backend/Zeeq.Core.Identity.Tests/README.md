# Zeeq Identity Tests

Unit and integration tests for Zeeq Identity components, including user management, authentication, and authorization.

This project references `Zeeq.Testing` for shared testing utilities and fixtures to facilitate comprehensive testing of identity-related functionality.

The [0factor e2e test plan](../../../build/0factor/e2e-test-plan.md) covers local provider sign-in, identity continuity, multiple users, organizations, teams, invitations, session boundaries, and protocol failures. Provider setup and persistence are described in the [development guide](../../../build/0factor/README.md).

`ExternalLogin0FactorIntegrationTests` and `ZeroFactorProtocolIntegrationTests` are explicit live checks for a running local Aspire stack. The protocol tests read the copied realm fixture and use .NET cryptography to verify PKCE exchanges, signed identities, userinfo, callbacks, and rejection cases for every enabled persona. The ordinary suite uses isolated fixtures and does not contact 0factor.
