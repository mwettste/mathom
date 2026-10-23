using System.Security.Claims;
using Mathom.Web.Auth;
using Mathom.Web.Contexts;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Api;

[ApiController]
[Authorize]
[Route("api/v1/items")]
public class ItemsController(SearchService search, ContextService contexts) : ControllerBase
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
            item.Actionable, item.Title, item.CleanText, item.SourceLanguage, item.Tags,
            item.Translations.Select(t => new TranslationDto(t.Locale, t.Title, t.CleanText)).ToList(),
            item.Error, item.CreatedAt, item.ProcessedAt, context));
    }
}
