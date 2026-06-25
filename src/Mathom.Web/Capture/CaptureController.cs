using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
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
[Authorize]
public class CaptureController(MathomDbContext db, IMediaStore media) : ControllerBase
{
    // Upper bounds to keep a single capture from being unbounded (DoS / disk fill).
    private const int MaxTextLength = 100_000;          // ~100 KB of text
    private const long MaxVoiceBytes = 25 * 1024 * 1024; // 25 MB audio
    private const int MaxPhotoCount = 8;
    private const long MaxPhotoBytes = 15 * 1024 * 1024; // 15 MB per image
    private const int MaxCaptureNoteLength = 4_000;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] CaptureRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "Text is required." });
        if (req.Text.Length > MaxTextLength)
            return BadRequest(new { error = "Text is too long." });

        var key = string.IsNullOrWhiteSpace(req.IdempotencyKey)
            ? Guid.NewGuid().ToString()
            : req.IdempotencyKey;

        var existing = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = Item.CreatePending(SourceType.Text, req.Text, key, userId, DateTimeOffset.UtcNow);
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
        CancellationToken ct)
    {
        if (audio is null || audio.Length == 0)
            return BadRequest(new { error = "Audio is required." });
        // Explicit guard in addition to [RequestSizeLimit]: the attribute rejects oversized
        // bodies at the Kestrel layer before buffering; this returns a clean 413 and is
        // enforced regardless of host (e.g. in tests, where the pipe limit isn't applied).
        if (audio.Length > MaxVoiceBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Audio is too large." });

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
        var item = Item.CreatePending(SourceType.Voice, "", key, userId, DateTimeOffset.UtcNow);
        item.MediaPath = mediaPath;
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
        CancellationToken ct)
    {
        if (images is null || images.Count == 0)
            return BadRequest(new { error = "At least one image is required." });
        if (images.Count > MaxPhotoCount)
            return BadRequest(new { error = $"At most {MaxPhotoCount} images per capture." });

        var exts = new string[images.Count];
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (img.Length == 0)
                return BadRequest(new { error = "An image was empty." });
            if (img.Length > MaxPhotoBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "An image is too large." });
            var ext = PhotoExtension(img);
            if (ext is null)
                return BadRequest(new { error = "Only JPEG, PNG, or WebP images are supported." });
            exts[i] = ext;
        }

        var note = string.IsNullOrWhiteSpace(context) ? null : context.Trim();
        if (note is { Length: > MaxCaptureNoteLength })
            return BadRequest(new { error = "Context is too long." });

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey;

        var existing = await db.Items.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var item = Item.CreatePending(SourceType.Photo, "", key, userId, DateTimeOffset.UtcNow);
        item.CaptureNote = note;
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
                CreatedAt = DateTimeOffset.UtcNow,
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
