using System.ComponentModel;
using System.Security.Claims;
using Zeeq.Core.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp;

/// <summary>
/// MCP tool that returns short quips.  Use this for testing.
/// </summary>
[McpServerToolType, Description("Provides short quips or jokes to lighten the mood.")]
public class QuipTool
{
    /// <summary>
    /// Returns a random short quip.
    /// </summary>
    [McpServerTool, Description("Get a short quip.")]
    public static string GetQuip(ILogger<QuipTool> logger, ClaimsPrincipal? user)
    {
        var identity = user?.AuthenticatedUser()?.Email ?? "unknown";

        logger.LogInformation("GetQuip called by user: {User}", identity);

        var quips = new[]
        {
            "Why don't scientists trust atoms? Because they make up everything!",
            "I told my computer I needed a break, and it said 'No problem, I'll go to sleep.'",
            "Why did the scarecrow win an award? Because he was outstanding in his field!",
            "I would tell you a joke about UDP, but you might not get it.",
            "Why do programmers prefer dark mode? Because light attracts bugs!",
            "There are only 10 kinds of people in the world: those who understand binary and those who don't.",
            "Why do Java developers wear glasses? Because they don't C#.",
            "A SQL query walks into a bar, walks up to two tables and asks: 'Can I join you?'",
            "How many programmers does it take to change a light bulb? None — that's a hardware problem.",
            "Debugging: being the detective in a crime movie where you are also the murderer.",
            "Why did the developer go broke? Because he used up all his cache.",
            "I'd tell you a UDP joke, but you might not get it. (Yes, again. It's that good.)",
            "Real programmers count from 0.",
            "Why do programmers mix up Halloween and Christmas? Because Oct 31 equals Dec 25.",
            "There's no place like 127.0.0.1.",
        };

        return $"I've got one for you, {user?.AuthenticatedUser()?.Email ?? "unknown"}: {quips[Random.Shared.Next(quips.Length)]}";
    }
}
