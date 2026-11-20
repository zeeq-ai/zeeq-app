namespace Zeeq.Core.Documents;

/// <summary>The authenticated audience for a user-facing library request.</summary>
public sealed record LibraryAudience(string OrganizationId, string UserId, bool IsAdministrator);

/// <summary>A selectable team or a retained grant to a disabled team.</summary>
public sealed record LibraryVisibilityTeam(string Id, string Name, bool IsAvailable);

/// <summary>The current grants and selectable teams for a library.</summary>
public sealed record LibraryVisibilitySettings(string[] TeamIds, LibraryVisibilityTeam[] Teams);

/// <summary>A replacement with an explicit baseline and consent to organization-wide access.</summary>
public sealed record LibraryVisibilityChange(
    string[] TeamIds,
    string[] ExpectedTeamIds,
    bool ConfirmEveryone
);

/// <summary>Outcome of an atomic visibility and library update.</summary>
public enum LibraryVisibilitySaveStatus
{
    /// <summary>The edit was committed.</summary>
    Saved,

    /// <summary>The library no longer exists.</summary>
    Missing,

    /// <summary>The supplied baseline differs from the current grants.</summary>
    Conflict,

    /// <summary>A newly selected team is unavailable.</summary>
    InvalidTeams,

    /// <summary>The request must confirm broadening access to the organization.</summary>
    ConfirmationRequired,
}

/// <summary>Access checks for user requests and writes of organization-local library grants.</summary>
public interface ILibraryVisibilityStore
{
    /// <summary>Check access to a named library before retrieving its content.</summary>
    Task<bool> CanOpenAsync(LibraryAudience audience, string libraryName, CancellationToken ct);

    /// <summary>List the allowed libraries in name order.</summary>
    Task<IReadOnlyList<Library>> ListVisibleAsync(LibraryAudience audience, CancellationToken ct);

    /// <summary>Project the allowed library names for library-scoped queries.</summary>
    Task<string[]> ListVisibleNamesAsync(LibraryAudience audience, CancellationToken ct);

    /// <summary>Read the current grants and choices for the authenticated administrator.</summary>
    Task<LibraryVisibilitySettings?> GetSettingsAsync(
        string organizationId,
        string libraryName,
        CancellationToken ct
    );

    /// <summary>Replace the grants and update the library within one transaction.</summary>
    Task<(LibraryVisibilitySaveStatus Status, Library? Library)> SaveAsync(
        Library library,
        LibraryVisibilityChange change,
        bool requestFullResync,
        CancellationToken ct
    );
}
