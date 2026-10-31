namespace Zeeq.Platform.CodeReviews;

/// <summary>Repository links could not be stabilized while libraries changed concurrently.</summary>
public sealed class LibraryMappingConflictException()
    : Exception("Repository library mappings changed concurrently. Retry the request.");
