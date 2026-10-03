using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Core.Llm.Tests;

/// <summary>
/// Verifies the Azure SDK request body across an Agent Framework function-call loop.
/// </summary>
public sealed class AzureToolCallingTests
{
    [Test]
    [Arguments("gpt-6-sol")]
    [Arguments("gpt-6-luna")]
    public async Task AzureGpt6Agent_WithFunctionTools_SendsExplicitNoneOnEveryRequest(string model)
    {
        // Use a local HTTP endpoint so this exercises the actual SDK serialization
        // without requiring credentials or making a paid provider call.
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        using var listener = new HttpListener();
        var endpoint = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(endpoint);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var capture = CaptureToolLoopAsync(listener, model, timeout.Token);

        var factory = new LlmClientFactory(
            EmptyServiceProvider.Instance,
            NullLoggerFactory.Instance
        );
        using var client = factory.CreateChatClient(
            new ResolvedLlmConfiguration("Azure OpenAI", model, "test-key", "test", endpoint)
        );
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "ok";
            },
            name: "test_tool"
        );
        var agent = client.AsAIAgent(instructions: "Call test_tool then reply OK.", tools: [tool]);
        var response = await agent.RunAsync(
            "Run the check.",
            options: new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = 200 }),
            cancellationToken: timeout.Token
        );
        var requests = await capture;

        await Assert.That(toolCalls).IsEqualTo(1);
        await Assert.That(response.Text).IsEqualTo("OK");
        await Assert.That(requests.Count).IsEqualTo(2);
        foreach (var request in requests)
        {
            using var body = JsonDocument.Parse(request);
            await Assert
                .That(body.RootElement.GetProperty("reasoning_effort").GetString())
                .IsEqualTo("none");
            await Assert.That(body.RootElement.GetProperty("tools").GetArrayLength()).IsEqualTo(1);
        }
    }

    private static async Task<List<string>> CaptureToolLoopAsync(
        HttpListener listener,
        string model,
        CancellationToken cancellationToken
    )
    {
        var requests = new List<string>();
        for (var turn = 0; turn < 2; turn++)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            using var reader = new StreamReader(context.Request.InputStream);
            requests.Add(await reader.ReadToEndAsync(cancellationToken));
            var message =
                turn == 0
                    ? (object)
                        new
                        {
                            role = "assistant",
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = "call_test",
                                    type = "function",
                                    function = new { name = "test_tool", arguments = "{}" },
                                },
                            },
                        }
                    : new { role = "assistant", content = "OK" };
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    id = $"chatcmpl-{turn}",
                    @object = "chat.completion",
                    created = 0,
                    model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message,
                            finish_reason = turn == 0 ? "tool_calls" : "stop",
                        },
                    },
                }
            );
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
            context.Response.Close();
        }

        return requests;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
