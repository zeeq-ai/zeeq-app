using Danom;

namespace Zeeq.Platform.WorldModel.Afa;

/// <summary>
/// A validated one-to-three segment Area/Feature/Action path.
/// </summary>
public readonly record struct WorldModelPath
{
    private WorldModelPath(string value, WorldModelNodeKind kind)
    {
        Value = value;
        Kind = kind;
    }

    /// <summary>Gets the canonical dotted lower snake_case path.</summary>
    public string Value { get; }

    /// <summary>Gets the hierarchy level derived from the path depth.</summary>
    public WorldModelNodeKind Kind { get; }

    /// <summary>Gets the final path segment.</summary>
    public string Segment => Value[(Value.LastIndexOf('.') + 1)..];

    /// <summary>Gets the parent path, or <see langword="null"/> for an Area.</summary>
    public string? ParentPath =>
        Value.LastIndexOf('.') is var separator && separator >= 0 ? Value[..separator] : null;

    /// <summary>Validates and creates a canonical world-model path.</summary>
    public static Result<WorldModelPath, string> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return Result<WorldModelPath, string>.Error(
                "AFA path is required without surrounding whitespace."
            );
        }

        var segments = value.Split('.');
        if (segments.Length is < 1 or > 3 || segments.Any(segment => !IsValidSegment(segment)))
        {
            return Result<WorldModelPath, string>.Error(
                "AFA path must contain one to three lower snake_case segments."
            );
        }

        var kind = segments.Length switch
        {
            1 => WorldModelNodeKind.Area,
            2 => WorldModelNodeKind.Feature,
            3 => WorldModelNodeKind.Action,
            _ => WorldModelNodeKind.Unknown,
        };

        return Result<WorldModelPath, string>.Ok(new(value, kind));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= 128
        && segment[0] != '_'
        && segment[^1] != '_'
        && segment.All(character =>
            character == '_' || char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)
        );
}
