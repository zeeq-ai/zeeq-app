namespace Zeeq.Platform.Storage.Google.Tests;

/// <summary>
/// Placeholder tests for the Google storage integration test assembly.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Storage.Google.Tests --output detailed --disable-logo
/// </summary>
public sealed class GoogleStorageProjectTests
{
    [Test]
    public async Task GoogleStorageProject_LoadsAssembly()
    {
        await Assert
            .That(typeof(GoogleStorageProjectTests).Assembly.GetName().Name)
            .IsEqualTo("Zeeq.Platform.Storage.Google.Tests");
    }
}
