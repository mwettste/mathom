using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mathom.Web.Capture;

[ApiController]
[Route("capture")]
public class CaptureController : ControllerBase
{
    private readonly MathomDbContext _db;
    public CaptureController(MathomDbContext db) => _db = db;

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
}
