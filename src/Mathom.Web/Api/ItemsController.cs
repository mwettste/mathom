using System.Security.Claims;
using Mathom.Web.Auth;
using Mathom.Web.Contexts;
using Mathom.Web.Notes;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Api;

[ApiController]
[Authorize]
[Route("api/v1/items")]
public class ItemsController(SearchService search, ContextService contexts, NoteService notes) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = await search.GetAsync(userId, id, ct);
        if (item is null)
            return ApiError.Result(StatusCodes.Status404NotFound, "not_found", "Item not found.");

        ContextDto? context = null;
        if (item.ContextId is { } contextId)
        {
            var own = (await contexts.ListAsync(userId, ct)).FirstOrDefault(c => c.Id == contextId);
            if (own is not null) context = new ContextDto(own.Id, own.Name);
        }

        return Ok(new ItemDetailDto(
            item.Id, ApiEnums.Lower(item.Status), ApiEnums.Lower(item.SourceType),
            item.ItemType is { } type ? ApiEnums.Lower(type) : null,
            item.Actionable, item.Title, item.CleanText, item.RawText, item.SourceLanguage,
            item.Tags.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            item.Translations.OrderBy(t => t.Locale, StringComparer.Ordinal).Select(t => new TranslationDto(t.Locale, t.Title, t.CleanText)).ToList(),
            item.Error, item.CreatedAt, item.ProcessedAt, context));
    }

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, MoveRequest request, CancellationToken ct)
    {
        var moved = await notes.MoveAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, id, request.ContextId, ct);
        return moved ? NoContent() : NotFoundError();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await notes.SoftDeleteAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, id, ct);
        return deleted ? NoContent() : NotFoundError();
    }

    [HttpPost("{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (await notes.ReprocessAsync(userId, id, ct)) return Accepted();
        // False means missing or Pending/Processing; only a lookup tells them apart.
        if (await search.GetAsync(userId, id, ct) is null) return NotFoundError();
        return ApiError.Result(StatusCodes.Status409Conflict, "not_reprocessable", "Item is already queued or being processed.");
    }

    private static IActionResult NotFoundError() =>
        ApiError.Result(StatusCodes.Status404NotFound, "not_found", "Item not found.");
}
