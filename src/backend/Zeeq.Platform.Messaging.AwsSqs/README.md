# AWS SQS messaging

Brighter SQS adapter for Zeeq's messaging core. Select it with `ZeeqMessaging:Provider=AwsSqs` and set `ZeeqMessaging:AwsSqs:QueuePrefix` (1–16 ASCII letters, digits, hyphens or underscores). `mise run up aws` configures Floci and three tenant buckets per topic.

Register once per service collection. Use `AddZeeqAwsSqsMessageProducers`, `AddZeeqAwsSqsMessageConsumers`, or `AddZeeqAwsSqsMessaging` for combined processes. Supply credentials, an `ITenantTierResolver`, and an `IDeadLetterWriter` when consuming. Standard queues belong to routes; multiple independent handlers per message are rejected. Brighter replaces `Header.Topic` with the physical queue name, so `Header.Bag["zeeqLogicalRoute"]` preserves the logical route. Positive `PublishAfterAsync` delays are unsupported, matching GCP Pub/Sub.

`Validate` creates missing queues before Brighter validates them; existing queues are unchanged. `Create` delegates provisioning to Brighter. `Assume` skips reconciliation, although Brighter producers still resolve queue URLs. Runtime credentials use the AWS credential chain; `AWS_ENDPOINT_URL_SQS` or `AWS_ENDPOINT_URL` selects an emulator, and `AWS_REGION` supplies the region. Explicit `ZeeqMessaging:AwsSqs` settings take precedence. The runtime uses the app-owned Postgres dead-letter sink.

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

This standalone harness uses a recording dead-letter sink and dummy emulator credentials. Runtime verification uses connected CSharpRepl with the actual application services and Postgres sink.
