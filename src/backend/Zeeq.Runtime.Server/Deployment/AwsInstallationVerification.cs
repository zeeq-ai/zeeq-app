using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Npgsql;
using Zeeq.Core.Security;
using Zeeq.Platform.Storage.Aws;

namespace Zeeq.Runtime.Server;

/// <summary>Exercises runtime grants on a prepared installation while its application services are stopped.</summary>
internal static class AwsInstallationVerification
{
    internal static async Task RunAsync(AppSettings settings, IConfiguration configuration)
    {
        await using var database = new NpgsqlConnection(
            settings.Database.EffectiveConnectionString
        );
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText =
            "SELECT current_user='zeeq_runtime' AND has_table_privilege(current_user,'zeeq.core_users','SELECT') AND NOT has_schema_privilege(current_user,'zeeq','CREATE') AND NOT has_schema_privilege(current_user,'cron','USAGE')";
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("Runtime database privilege boundary failed.");
        await using var transaction = await database.BeginTransactionAsync();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO cache.hybrid_cache(id,value,expiresattime) VALUES (@id,@value,now()+interval '30 seconds')";
        command.Parameters.AddWithValue("id", $"installation-check:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("value", Encoding.UTF8.GetBytes("probe"));
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
        Console.WriteLine("Restricted runtime database access and cache writes verified.");

        using var kms = new AmazonKeyManagementServiceClient();
        var encryption = new AwsKmsEncryptionProvider(
            new SecuritySettings
            {
                EncryptionProvider = DataEncryptionProviders.AwsKms,
                GoogleKmsKeyName = string.Empty,
                DataProtectionKeyRingPath = string.Empty,
                AwsKmsKeyArn =
                    configuration["ZeeqDeployment:KmsKeyArn"]
                    ?? throw new InvalidOperationException("Verification key required."),
            },
            kms
        );
        var plaintext = Encoding.UTF8.GetBytes("installation-check");
        var ciphertext = await encryption.EncryptAsync(
            "installation-check",
            plaintext,
            CancellationToken.None
        );
        var decrypted = await encryption.DecryptAsync(
            "installation-check",
            ciphertext,
            CancellationToken.None
        );
        if (!plaintext.SequenceEqual(decrypted))
            throw new InvalidOperationException("KMS round-trip failed.");
        try
        {
            await encryption.DecryptAsync(
                "different-organization",
                ciphertext,
                CancellationToken.None
            );
            throw new InvalidOperationException("KMS allowed a different organization context.");
        }
        catch (InvalidCiphertextException) { }
        Console.WriteLine("Runtime KMS round-trip and organization isolation verified.");

        using var sqs = new AmazonSQSClient();
        // Pulumi supplies a dedicated, short-retention installation queue, never an application queue.
        // Retry markers may share it, but there are no application consumers to delay.
        var queue =
            configuration["ZeeqDeployment:ProbeQueueUrl"]
            ?? throw new InvalidOperationException("Verification queue required.");
        var marker = $"installation-check:{Guid.NewGuid():N}";
        await sqs.SendMessageAsync(
            new SendMessageRequest { QueueUrl = queue, MessageBody = marker }
        );
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var messages = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queue,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 2,
                }
            );
            var own = messages.Messages.FirstOrDefault(m => m.Body == marker);
            if (own is null)
                continue;
            await sqs.DeleteMessageAsync(
                new DeleteMessageRequest { QueueUrl = queue, ReceiptHandle = own.ReceiptHandle }
            );
            Console.WriteLine("Runtime SQS send, receive and delete verified.");
            return;
        }
        throw new InvalidOperationException(
            "Verification SQS message was not received; inspect the queue before starting consumers."
        );
    }
}
