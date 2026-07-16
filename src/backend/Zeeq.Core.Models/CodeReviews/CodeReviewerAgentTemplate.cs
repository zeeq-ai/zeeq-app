namespace Zeeq.Core.Models;

/// <summary>
/// A reusable, code-defined reviewer-agent starting point exposed to the
/// management UI so operators can clone a curated persona rather than authoring
/// a prompt from scratch.
/// </summary>
/// <remarks>
/// Templates are compile-time content, not persisted rows. They carry no
/// organization, repository, or timestamp identity and are never returned by the
/// persisted-agent management APIs. The runtime fallback reviewer and the
/// template catalog both read from <see cref="CodeReviewerAgentTemplateLibrary" />
/// so a single definition stays authoritative.
/// </remarks>
/// <param name="Key">Stable template identifier used for selection and telemetry.</param>
/// <param name="DisplayName">Human-readable persona name shown in the library.</param>
/// <param name="ReviewFacet">Facet label this persona owns, for example <c>General</c>.</param>
/// <param name="Description">Short summary describing when to reach for this persona.</param>
/// <param name="ModelTier">Semantic Zeeq model tier the persona defaults to.</param>
/// <param name="Prompt">Reviewer instructions seeded into a new agent's prompt.</param>
/// <param name="ActivationConfiguration">Default file activation rules for the persona.</param>
public sealed record CodeReviewerAgentTemplate(
    string Key,
    string DisplayName,
    string ReviewFacet,
    string Description,
    CodeReviewModelTier ModelTier,
    string Prompt,
    CodeReviewerActivationConfiguration ActivationConfiguration
);
