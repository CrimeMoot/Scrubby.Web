using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scrubby.Web.AssetIndex;

// Headless asset index builder used by CI (see .github/workflows/index-assets.yml).
// It downloads the configured repositories, parses their DMIs and writes the compact,
// version-controlled index (repos.json + index/*.json.gz) to AssetIndex:DataDirectory.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<AssetIndexService>();

using var host = builder.Build();
var index = host.Services.GetRequiredService<AssetIndexService>();

Console.WriteLine($"Building asset index into {index.DataDirectory}");

var status = await index.ReindexAsync();

Console.WriteLine($"Done: {status.IndexedRepos}/{status.TotalRepos} repos indexed, {status.FailedRepos} failed");
foreach (var error in status.Errors)
    Console.WriteLine($"  ! {error}");

return status.IndexedRepos == 0 && status.TotalRepos > 0 ? 1 : 0;