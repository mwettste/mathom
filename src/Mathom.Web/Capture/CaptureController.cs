using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Auth;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mathom.Web.Capture;

[ApiController]
[Route("capture")]
[Route("api/v1/capture")]
[Authorize]
public class CaptureController(MathomDbContext db, IMediaStore media, Mathom.Web.Contexts.ContextService contexts, TimeProvider time) : ControllerBase
{
    // Upper bounds to keep a single capture from being unbounded (DoS / disk fill).
    private const int MaxTextLength = 100_000;          // ~100 KB of text
    private const long MaxVoiceBytes = 25 * 1024 * 1024; // 25 MB audio
    private const int MaxPhotoCount = 8;
    private const long MaxPhotoBytes = 15 * 1024 * 1024; // 15 MB per image
    private const int MaxCaptureNoteLength = 4_000;
    private static readonly TimeSpan MaxCapturedAtAge = TimeSpan.FromDays(30);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] CaptureRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return ApiError.Result(StatusCodes.Status400BadRequest, "text_required", "Text is required.");
        if (req.Text.Length > MaxTextLength)
            return ApiError.Result(StatusCodes.Status400BadRequest, "text_too_long", "Text is too long.");

        if (!IsValidTarget(req.Target))
            return InvalidTarget();

        var key = string.IsNullOrWhiteSpace(req.IdempotencyKey)
            ? Guid.NewGuid().ToString()
            : req.IdempotencyKey;

        var existing = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = Item.CreatePending(SourceType.Text, req.Text, key, userId, CreatedAt(req.CapturedAt));
        item.ContextId = await ResolveContextAsync(req.Target, userId, ct);
        db.Items.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var race = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
            return Ok(new { id = race!.Id });
        }

        return Created((string?)null, new { id = item.Id });
    }

    [HttpPost("voice")]
    [RequestSizeLimit(MaxVoiceBytes)]
    public async Task<IActionResult> PostVoice(
        [FromForm] IFormFile? audio,
        [FromForm] string? idempotencyKey,
        [FromForm] string? target,
        [FromForm] DateTimeOffset? capturedAt,
        CancellationToken ct)
    {
        if (audio is null || audio.Length == 0)
            return ApiError.Result(StatusCodes.Status400BadRequest, "audio_required", "Audio is required.");
        // Explicit guard in addition to [RequestSizeLimit]: the attribute rejects oversized
        // bodies at the Kestrel layer before buffering; this returns a clean 413 and is
        // enforced regardless of host (e.g. in tests, where the pipe limit isn't applied).
        if (audio.Length > MaxVoiceBytes)
            return ApiError.Result(StatusCodes.Status413PayloadTooLarge, "audio_too_large", "Audio is too large.");
        if (!IsValidTarget(target))
            return InvalidTarget();

        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString()
            : idempotencyKey;

        var existing = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var ext = Path.GetExtension(audio.FileName);
        if (string.IsNullOrEmpty(ext))
            ext = audio.ContentType.Contains("mp4") || audio.ContentType.Contains("mpeg") ? ".m4a" : ".webm";

        string mediaPath;
        await using (var stream = audio.OpenReadStream())
            mediaPath = await media.SaveAsync(stream, ext, ct);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = Item.CreatePending(SourceType.Voice, "", key, userId, CreatedAt(capturedAt));
        item.MediaPath = mediaPath;
        item.ContextId = await ResolveContextAsync(target, userId, ct);
        db.Items.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var race = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
            return Ok(new { id = race!.Id });
        }

        return Created((string?)null, new { id = item.Id });
    }

    [HttpPost("photo")]
    [RequestSizeLimit(MaxPhotoCount * MaxPhotoBytes)]
    public async Task<IActionResult> PostPhoto(
        [FromForm] List<IFormFile>? images,
        [FromForm] string? idempotencyKey,
        [FromForm] string? context,
        [FromForm] string? target,
        [FromForm] DateTimeOffset? capturedAt,
        CancellationToken ct)
    {
        if (images is null || images.Count == 0)
            return ApiError.Result(StatusCodes.Status400BadRequest, "images_required", "At least one image is required.");
        if (images.Count > MaxPhotoCount)
            return ApiError.Result(StatusCodes.Status400BadRequest, "too_many_images", $"At most {MaxPhotoCount} images per capture.");
        if (!IsValidTarget(target))
            return InvalidTarget();

        var exts = new string[images.Count];
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (img.Length == 0)
                return ApiError.Result(StatusCodes.Status400BadRequest, "image_empty", "An image was empty.");
            if (img.Length > MaxPhotoBytes)
                return ApiError.Result(StatusCodes.Status413PayloadTooLarge, "image_too_large", "An image is too large.");
            var ext = PhotoExtension(img);
            if (ext is null)
                return ApiError.Result(StatusCodes.Status400BadRequest, "unsupported_image_type", "Only JPEG, PNG, or WebP images are supported.");
            exts[i] = ext;
        }

        var note = string.IsNullOrWhiteSpace(context) ? null : context.Trim();
        if (note is { Length: > MaxCaptureNoteLength })
            return ApiError.Result(StatusCodes.Status400BadRequest, "capture_note_too_long", "Context is too long.");

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey;

        var existing = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = Item.CreatePending(SourceType.Photo, "", key, userId, CreatedAt(capturedAt));
        item.CaptureNote = note;
        item.ContextId = await ResolveContextAsync(target, userId, ct);
        for (var i = 0; i < images.Count; i++)
        {
            string mediaPath;
            await using (var stream = images[i].OpenReadStream())
                mediaPath = await media.SaveAsync(stream, exts[i], ct);
            item.Photos.Add(new ItemPhoto
            {
                Id = Guid.NewGuid(),
                MediaPath = mediaPath,
                Order = i,
                CreatedAt = item.CreatedAt,
            });
        }

        db.Items.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var race = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
            return Ok(new { id = race!.Id });
        }

        return Created((string?)null, new { id = item.Id });
    }

    // null/empty = the user's current context; "inbox" = no context. Anything else is rejected.
    private static bool IsValidTarget(string? target) =>
        string.IsNullOrEmpty(target) || target.Equals("inbox", StringComparison.OrdinalIgnoreCase);

    private static IActionResult InvalidTarget() =>
        ApiError.Result(StatusCodes.Status400BadRequest, "invalid_target",
            "Target must be omitted or \"inbox\".");

    private Task<Guid?> ResolveContextAsync(string? target, string userId, CancellationToken ct) =>
        string.IsNullOrEmpty(target) ? contexts.GetCurrentAsync(userId, ct) : Task.FromResult<Guid?>(null);

    // A client-supplied capture time is honored only within [now - 30 days, now].
    private DateTimeOffset CreatedAt(DateTimeOffset? capturedAt)
    {
        var now = time.GetUtcNow();
        return capturedAt is { } c && c <= now && c >= now - MaxCapturedAtAge ? c.ToUniversalTime() : now;
    }

    // Accepts jpeg/png/webp by content type or file extension; returns the canonical
    // extension to store, or null if unsupported.
    private static string? PhotoExtension(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var ct = file.ContentType?.ToLowerInvariant() ?? "";
        if (ext is ".jpg" or ".jpeg" || ct.Contains("jpeg")) return ".jpg";
        if (ext == ".png" || ct.Contains("png")) return ".png";
        if (ext == ".webp" || ct.Contains("webp")) return ".webp";
        return null;
    }
}
