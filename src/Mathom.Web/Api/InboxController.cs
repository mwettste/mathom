using System.Security.Claims;
using Mathom.Web.Auth;
using Mathom.Web.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mathom.Web.Api;

[ApiController]
[Authorize]
[Route("api/v1/inbox")]
public class InboxController(SearchService search) : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        InboxCursor? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!InboxCursor.TryDecode(cursor, out var decoded))
                return ApiError.Result(StatusCodes.Status400BadRequest, "invalid_cursor", "The cursor is not valid.");
            after = decoded;
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var entries = await search.InboxPageAsync(userId, after, take, ct);

        var items = entries
            .Select(e => new InboxItemDto(e.Id, ApiEnums.Lower(e.Status), ApiEnums.Lower(e.SourceType),
                e.Title, e.Snippet, e.CreatedAt))
            .ToList();
        var next = entries.Count == take ? new InboxCursor(entries[^1].CreatedAt, entries[^1].Id).Encode() : null;
        return Ok(new InboxPageDto(items, next));
    }
}
