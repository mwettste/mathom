using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mathom.Web.Capture;

[ApiController]
[Route("capture")]
public class CaptureController : ControllerBase
{
    private readonly MathomDbContext _db;
    private readonly IMediaStore _media;
    public CaptureController(MathomDbContext db, IMediaStore media)
    {
        _db = db;
        _media = media;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] CaptureRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "Text is required." });

        var key = string.IsNullOrWhiteSpace(req.IdempotencyKey)
            ? Guid.NewGuid().ToString()
            : req.IdempotencyKey;

        var existing = await _db.Items.FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var item = Item.CreatePending(SourceType.Text, req.Text, key, DateTimeOffset.UtcNow);
        _db.Items.Add(item);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var race = await _db.Items.FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
            return Ok(new { id = race!.Id });
        }

        return Created((string?)null, new { id = item.Id });
    }

    [HttpPost("voice")]
    public async Task<IActionResult> PostVoice(
        [FromForm] IFormFile? audio,
        [FromForm] string? idempotencyKey,
        CancellationToken ct)
    {
        if (audio is null || audio.Length == 0)
            return BadRequest(new { error = "Audio is required." });

        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString()
            : idempotencyKey;

        var existing = await _db.Items.FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
        if (existing is not null)
            return Ok(new { id = existing.Id });

        var ext = Path.GetExtension(audio.FileName);
        if (string.IsNullOrEmpty(ext))
            ext = audio.ContentType.Contains("mp4") || audio.ContentType.Contains("mpeg") ? ".m4a" : ".webm";

        string mediaPath;
        await using (var stream = audio.OpenReadStream())
            mediaPath = await _media.SaveAsync(stream, ext, ct);

        var item = Item.CreatePending(SourceType.Voice, "", key, DateTimeOffset.UtcNow);
        item.MediaPath = mediaPath;
        _db.Items.Add(item);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var race = await _db.Items.FirstOrDefaultAsync(i => i.IdempotencyKey == key, ct);
            return Ok(new { id = race!.Id });
        }

        return Created((string?)null, new { id = item.Id });
    }
}
