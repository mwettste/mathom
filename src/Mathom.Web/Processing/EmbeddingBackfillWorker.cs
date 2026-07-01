using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

// On startup, embeds Ready notes that have no current vector (null or produced by an older
// model). Idempotent and best-effort; runs to completion then idles. Disabled under Testing.
public class EmbeddingBackfillWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<EmbeddingBackfillWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int embedded, total = 0;
            do
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MathomDbContext>();
                var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
                embedded = await BackfillBatchAsync(db, embeddings, BatchSize, stoppingToken);
                total += embedded;
            } while (embedded > 0 && !stoppingToken.IsCancellationRequested);

            if (total > 0) logger.LogInformation("Embedding backfill complete: {Total} note(s) embedded.", total);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Embedding backfill worker failed.");
        }
    }

    // Embeds up to batchSize Ready notes lacking a current-model vector. Returns the count embedded.
    public static async Task<int> BackfillBatchAsync(
        MathomDbContext db, IEmbeddingClient embeddings, int batchSize, CancellationToken ct)
    {
        var model = embeddings.ModelId;
        var batch = await db.Items
            .Where(i => i.Status == ItemStatus.Ready
                     && (i.Embedding == null || i.EmbeddingModel != model))
            .OrderBy(i => i.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var count = 0;
        foreach (var item in batch)
        {
            try
            {
                var vector = await embeddings.EmbedAsync($"{item.Title}\n{item.CleanText}", ct);
                item.Embedding = new Pgvector.Vector(vector);
                item.EmbeddingModel = model;
                item.EmbeddedAt = DateTimeOffset.UtcNow;
                count++;
            }
            catch (Exception)
            {
                // Best-effort: leave this note for a later run rather than failing the batch.
            }
        }
        await db.SaveChangesAsync(ct);
        return count;
    }
}
