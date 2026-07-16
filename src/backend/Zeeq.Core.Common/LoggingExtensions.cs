using System.Runtime.CompilerServices;
using Serilog;

namespace Zeeq.Core.Common;

/// <summary>
/// Extension methods for the Serilog ILogger interface.
/// </summary>
public static class LoggerExtensions
{
    /// <summary>
    /// Logs the current location in the code.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="memberName">The name of the calling member.</param>
    /// <param name="sourceFilePath">The source file path of the calling member.</param>
    /// <param name="sourceLineNumber">The line number in the source file.</param>
    /// <returns>The logger instance with the context added.</returns>
    public static ILogger Here(
        this ILogger logger,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0
    )
    {
        var srcFile = Path.GetFileName(sourceFilePath);
        var here = $" {srcFile}:{memberName}@{sourceLineNumber}";

        return logger
            .ForContext("Here", here)
            .ForContext("MemberName", memberName)
            .ForContext("FilePath", sourceFilePath)
            .ForContext("LineNumber", sourceLineNumber);
    }
}
