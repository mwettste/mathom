using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

public class ItemProcessor
{
    private readonly MathomDbContext _db;
    private readonly ILlmClient _llm;
    private readonly ILogger<ItemProcessor> _logger;

    public ItemProcessor(MathomDbContext db, ILlmClient llm, ILogger<ItemProcessor> logger)
    {
        _db = db;
        _llm = llm;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid itemId, CancellationToken ct)
    {
        var item = await _db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        if (item.Status is not (ItemStatus.Pending or ItemStatus.Failed or ItemStatus.Processing)) return;

        item.Status = ItemStatus.Processing;
        await _db.SaveChangesAsync(ct);

        try
        {
            var result = await _llm.CleanupAsync(item.RawText, ct);

            item.Title = result.Title;
            item.CleanText = result.CleanText;
            item.ItemType = result.ItemType;
            item.Actionable = result.Actionable;
            item.Error = null;

            foreach (var t in result.Tags)
            {
                var tag = await _db.Tags.FirstOrDefaultAsync(x => x.Name == t.Name && x.Kind == t.Kind, ct);
                if (tag is null)
                {
                    tag = new Tag { Name = t.Name, Kind = t.Kind };
                    _db.Tags.Add(tag);
                    await _db.SaveChangesAsync(ct);
                }
                if (!item.ItemTags.Any(it => it.TagId == tag.Id))
                    item.ItemTags.Add(new ItemTag { ItemId = item.Id, TagId = tag.Id });
            }

            item.ProcessedAt = DateTimeOffset.UtcNow;
            item.Status = ItemStatus.Ready;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for item {ItemId}", itemId);
            item.Status = ItemStatus.Failed;
            item.Error = ex.Message;
            await _db.SaveChangesAsync(ct);
        }
    }
}
