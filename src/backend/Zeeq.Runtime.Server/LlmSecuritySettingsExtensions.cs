using Zeeq.Core.Llm;
using Zeeq.Core.Security;

namespace Zeeq.Runtime.Server;

/// <summary>
/// Composition-root adapter from <see cref="LlmSettings"/> to <see cref="SecuritySettings"/>.
/// </summary>
/// <remarks>
/// Provider-neutral encryption config still binds from <c>AppSettings:Llm:*</c>
/// (zeeq-ai/zeeq-app#192 intentionally kept the existing config surface — only the C# project
/// boundary moved). This mapping lives here, not in <c>Zeeq.Core.Common</c>, which owns no
/// encrypted-value orchestration. Both <c>Program.cs</c> and <c>ZeeqWorkerHost.cs</c> call this
/// so the two hosts cannot drift if a future encryption setting is added to only one path.
/// </remarks>
internal static class LlmSecuritySettingsExtensions
{
    extension(LlmSettings settings)
    {
        internal SecuritySettings ToSecuritySettings() =>
            new()
            {
                EncryptionProvider = settings.EncryptionProvider,
                DataProtectionKeyRingPath = settings.DataProtectionKeyRingPath,
                GoogleKmsKeyName = settings.GoogleKmsKeyName,
            };
    }
}
