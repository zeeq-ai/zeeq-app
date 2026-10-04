# SQS prototype

Standalone Brighter SQS adapter for Zeeq's messaging core. Application startup does not select this transport yet.

Register with `AddZeeqAwsSqsMessaging`. Supply credentials, an `ITenantTierResolver`, and an `IDeadLetterWriter` when consuming. Standard queues belong to routes; multiple independent handlers per message are rejected. Brighter replaces `Header.Topic` with the physical queue name, so `Header.Bag["zeeqLogicalRoute"]` preserves the logical route. Positive `PublishAfterAsync` delays are unsupported by this prototype.

Run integration tests with isolated Floci 2.1.0 (Docker required):

```sh
dotnet run --project src/backend/Zeeq.Platform.Messaging.AwsSqs.Tests.Integration
```

To reuse local Floci, set `ZEEQ_SQS_TEST_ENDPOINT=http://localhost:4566`. Tests create uniquely named queues and delete them afterward.

Exercise the same compiled Zeeq publisher/handler harness from standalone CSharpRepl:

```sh
NO_COLOR=1 csharprepl \
  -r src/backend/Zeeq.Platform.Messaging.AwsSqs.Tests.Integration/Zeeq.Platform.Messaging.AwsSqs.Tests.Integration.csproj \
  --eval 'var result = await Zeeq.Platform.Messaging.AwsSqs.Tests.Integration.SqsPipelinePrototype.RunAsync("http://localhost:4566"); System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));'
```

This uses a recording dead-letter sink and dummy emulator credentials. It proves local transport and handler behavior; AWS permissions, production persistence and application provider selection require subsequent integration.
