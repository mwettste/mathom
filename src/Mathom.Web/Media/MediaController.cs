using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Media;

[ApiController]
[Route("media")]
[Authorize]
public class MediaController(MathomDbContext db, IMediaStore media) : ControllerBase
{
    [HttpGet("{photoId:guid}")]
    public async Task<IActionResult> Get(Guid photoId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Reach the photo only through the user-scoped Items query: the global query filter
        // excludes soft-deleted items, and the UserId predicate enforces ownership.
        var mediaPath = await db.Items
            .Where(i => i.UserId == userId)
            .SelectMany(i => i.Photos)
            .Where(p => p.Id == photoId)
            .Select(p => p.MediaPath)
            .FirstOrDefaultAsync(ct);

        if (mediaPath is null) return NotFound();

        var stream = await media.OpenReadAsync(mediaPath, ct);
        return File(stream, ContentTypeFor(mediaPath));
    }

    private static string ContentTypeFor(string mediaPath) => Path.GetExtension(mediaPath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
