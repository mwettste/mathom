using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Domain;

namespace Mathom.Web.Processing;

public record CleanupTag(string Name, TagKind Kind);

public record CleanupResult(
    string Title,
    string CleanText,
    ItemType ItemType,
    bool Actionable,
    IReadOnlyList<CleanupTag> Tags,
    string? Language = null);

public record TranslationResult(string Title, string CleanText);

/// <summary>One context offered to the suggestion call. <c>Key</c> is an opaque id (c1..cN), never the real context id.</summary>
public record ContextCandidate(
    string Key,
    string Name,
    IReadOnlyList<string> GlossaryTerms,
    IReadOnlyList<string> RecentTitles);

public interface ILlmClient
{
    Task<CleanupResult> CleanupAsync(string rawText, IReadOnlyList<string> glossary, CancellationToken ct);

    Task<TranslationResult> TranslateAsync(
        string sourceTitle, string sourceText, string targetLocale, string styleHint,
        IReadOnlyList<string> glossaryTerms, CancellationToken ct);

    /// <summary>Returns the chosen candidate <c>Key</c>, or null for "none".</summary>
    Task<string?> SuggestContextAsync(
        string title, string text, IReadOnlyList<ContextCandidate> candidates, CancellationToken ct);
}
