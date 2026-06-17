using System;
using System.Threading;
using System.Threading.Tasks;
using Mathom.Web.Data;
using Mathom.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mathom.Web.Processing;

public class ProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessingWorker> _logger;
    private readonly TimeSpan _idleDelay = TimeSpan.FromSeconds(1);

    public ProcessingWorker(IServiceScopeFactory scopeFactory, ILogger<ProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? claimedId = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MathomDbContext>();
                claimedId = await ClaimNextPendingIdAsync(db, stoppingToken);

                if (claimedId is { } id)
                {
                    var processor = scope.ServiceProvider.GetRequiredService<ItemProcessor>();
                    await processor.ProcessAsync(id, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Worker tick failed");
            }

            if (claimedId is null)
                await Task.Delay(_idleDelay, stoppingToken);
        }
    }

    // Claims the oldest pending item using row-level locking so concurrent workers don't double-process.
    public static async Task<Guid?> ClaimNextPendingIdAsync(MathomDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pending = await db.Items
            .FromSqlRaw(
                @"SELECT * FROM ""Items"" WHERE ""Status"" = {0} ORDER BY ""CreatedAt"" ASC LIMIT 1 FOR UPDATE SKIP LOCKED",
                (int)ItemStatus.Pending)
            .FirstOrDefaultAsync(ct);

        if (pending is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        pending.Status = ItemStatus.Processing;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pending.Id;
    }
}
