namespace Zeeq.Core.Models;

/// <summary>
/// Curated catalog of reusable reviewer-agent templates surfaced to the
/// management UI and used to build the runtime fallback reviewer.
/// </summary>
/// <remarks>
/// This library is the single source of truth for built-in reviewer personas.
/// The runtime fallback reviewer (when a repository has no enabled agents) is
/// built from <see cref="PrincipalSoftwareEngineer" />, so the prompt lives in
/// exactly one place. Each persona is defined in its own
/// <c>CodeReviewerAgentTemplateLibrary.&lt;Persona&gt;.cs</c> partial; append new
/// personas to <see cref="All" /> without touching resolution or endpoint code.
/// </remarks>
public static partial class CodeReviewerAgentTemplateLibrary
{
    /// <summary>
    /// All built-in reviewer templates, in display order.
    /// </summary>
    /// <remarks>
    /// Expression-bodied so it evaluates on access rather than during static
    /// initialization: the persona properties live in sibling partial files, and
    /// static initializer ordering across partials is not guaranteed, so eager
    /// initialization here could observe not-yet-initialized (null) personas.
    /// </remarks>
    public static IReadOnlyList<CodeReviewerAgentTemplate> All =>
        [
            PrincipalSoftwareEngineer,
            LogicalReviewer,
            StructuralReviewer,
            PerformanceEngineer,
            SecurityReviewer,
            PrincipalSdet,
        ];
}
