namespace Zeeq.Platform.Documents;

internal static class LibraryNameValidator
{
    public static bool IsRouteSafe(string name) =>
        name.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'
        );
}
