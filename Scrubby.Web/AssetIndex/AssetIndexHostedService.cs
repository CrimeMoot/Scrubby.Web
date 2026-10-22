using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scrubby.Web.AssetIndex;

namespace Scrubby.Web.AssetIndex;

public class AssetIndexHostedService(
    AssetIndexService index,
    ILogger<AssetIndexHostedService> logger,
    IHostApplicationLifetime lifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!index.AutoIndex) return;

        try
        {
            await index.EnsureLoadedAsync();
            logger.LogInformation("Checking for repositories that still need indexing");
            _ = Task.Run(() => IndexAsync(lifetime.ApplicationStopping));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic asset indexing failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task IndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await index.IndexMissingAsync(cancellationToken);
            if (status.TotalRepos == 0)
            {
                logger.LogInformation("Asset index is up to date");
                return;
            }

            logger.LogInformation(
                "Asset indexing finished: {Indexed}/{Total} repos, {Failed} failed",
                status.IndexedRepos, status.TotalRepos, status.FailedRepos);
            foreach (var error in status.Errors)
                logger.LogWarning("Asset index error: {Error}", error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Asset indexing crashed");
        }
    }
}
