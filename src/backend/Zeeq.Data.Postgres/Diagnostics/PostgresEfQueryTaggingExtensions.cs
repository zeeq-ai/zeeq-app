using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Diagnostics;

/// <summary>
/// Adds Zeeq query-operation tags to EF Core queries.
/// </summary>
/// <remarks>
/// EF Core's built-in <see cref="EntityFrameworkQueryableExtensions.TagWithCallSite{T}" />
/// records the source file and line number. This extension composes that built-in
/// behavior with a stable semantic operation name and caller member name so
/// PostgreSQL spans remain useful after provider-level Npgsql tracing is removed.
/// Use this on production EF query roots before terminal operations such as
/// <c>FirstOrDefaultAsync</c>, <c>ToArrayAsync</c>, or <c>ExecuteUpdateAsync</c>.
/// </remarks>
public static class PostgresEfQueryTaggingExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>
        /// Tags an EF query with a semantic operation name plus source call-site metadata.
        /// </summary>
        /// <param name="operationName">Stable operation name, such as <c>code_review.find_by_id</c>.</param>
        /// <param name="memberName">Compiler-supplied caller member name.</param>
        /// <param name="filePath">Compiler-supplied caller source file path.</param>
        /// <param name="lineNumber">Compiler-supplied caller source line number.</param>
        /// <returns>The source query annotated with Zeeq and EF Core call-site tags.</returns>
        public IQueryable<T> TagWithOperationCallSite(
            string operationName,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0
        ) =>
            source
                .TagWith($"Zeeq.Query: {operationName}; Member: {memberName}")
                .TagWithCallSite(filePath, lineNumber);
    }
}
