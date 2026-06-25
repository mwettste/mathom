using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mathom.Web.Media;

[ApiController]
[Route("media")]
[Authorize]
public class MediaController(MathomDbContext db, IMediaStore media, PhotoVariantService variants) : ControllerBase
{
    // Each (externalId, variant) maps to immutable bytes, so it is safe to cache for a year.
    private const string ImmutableCache = "public, max-age=31536000, immutable";

    [HttpGet("{externalId}")]
    public async Task<IActionResult> Get(string externalId, string? variant, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Reach the photo only through the user-scoped Items query: the global query filter
        // excludes soft-deleted items and the UserId predicate enforces ownership. Tracked
        // (not projected) so EnsureDisplayAsync can persist a freshly generated DisplayPath.
        var photo = await db.Items
            .Where(i => i.UserId == userId)
            .SelectMany(i => i.Photos)
            .FirstOrDefaultAsync(p => p.ExternalId == externalId, ct);

        if (photo is null) return NotFound();

        string key;
        string contentType;
        if (variant == "original")
        {
            key = photo.MediaPath;
            contentType = ContentTypeFor(key);
        }
        else
        {
            key = await variants.EnsureDisplayAsync(photo, ct); // lazily generates if absent
            contentType = "image/jpeg";
        }

        Response.Headers.CacheControl = ImmutableCache;
        var stream = await media.OpenReadAsync(key, ct);
        return File(stream, contentType);
    }

    private static string ContentTypeFor(string mediaPath) => Path.GetExtension(mediaPath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
