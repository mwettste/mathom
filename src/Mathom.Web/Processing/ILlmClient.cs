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

public interface ILlmClient
{
    Task<CleanupResult> CleanupAsync(string rawText, IReadOnlyList<string> glossary, CancellationToken ct);

    Task<TranslationResult> TranslateAsync(
        string sourceTitle, string sourceText, string targetLocale, string styleHint,
        IReadOnlyList<string> glossaryTerms, CancellationToken ct);
}
