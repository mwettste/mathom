using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;

namespace Mathom.Web.Media;

/// <summary>
/// Ensures a photo's downscaled display variant exists. Shared by the processing worker (which
/// feeds the variant to OCR) and the media controller (which lazily backfills it for display).
/// Idempotent: the variant is generated and persisted once, then reused.
/// </summary>
public sealed class PhotoVariantService(MathomDbContext db, IMediaStore media, ImageVariantProcessor processor)
{
    /// <summary>Returns the photo's <see cref="ItemPhoto.DisplayPath"/>, generating and persisting
    /// it from the original on first call. <paramref name="photo"/> must be tracked by
    /// <see cref="MathomDbContext"/> so the new key is saved.</summary>
    public async Task<string> EnsureDisplayAsync(ItemPhoto photo, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(photo.DisplayPath))
            return photo.DisplayPath;

        await using var original = await media.OpenReadAsync(photo.MediaPath, ct);
        await using var variant = await processor.CreateDisplayVariantAsync(original, ct);
        var key = await media.SaveAsync(variant, ".jpg", ct);

        photo.DisplayPath = key;
        await db.SaveChangesAsync(ct);
        return key;
    }
}
