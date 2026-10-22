using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Scrubby.Web.AssetIndex;

namespace Scrubby.Web.Controllers;

/// <summary>
/// Read-only sprite search API, compatible with the external AssetMonitor API
/// used by the Icon search views (amApi base + /repo, /search, /sprite/{id}, /asset/{id}).
/// </summary>
[Route("api/asset")]
[ApiController]
public class AssetApiController(AssetIndexService index) : ControllerBase
{
    [HttpGet("repo")]
    public async Task<IActionResult> GetRepos()
    {
        var repos = await index.GetReposAsync();
        return Ok(repos);
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] AssetSearchParameters parameters)
    {
        if (parameters == null) return BadRequest();
        var response = await index.SearchAsync(parameters);
        return Ok(response);
    }

    [HttpGet("sprite/{id:guid}")]
    public async Task<IActionResult> GetSprite(Guid id)
    {
        var sprite = await index.GetSpriteAsync(id);
        return sprite == null ? NotFound() : Ok(sprite);
    }

    [HttpGet("asset/{id:guid}")]
    public async Task<IActionResult> GetAsset(Guid id)
    {
        var (data, contentType) = await index.GetAssetAsync(id);
        if (data == null) return NotFound();
        return File(data, contentType ?? "application/octet-stream");
    }

    [HttpGet("repo/{repo}/{**path}")]
    public async Task<IActionResult> GetSpritesheet(string repo, string path)
    {
        var sheet = await index.GetSpritesheetAsync(repo, path);
        return sheet == null ? NotFound() : Ok(sheet);
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        await index.EnsureLoadedAsync();
        return Ok(new { ready = true, dataDirectory = index.DataDirectory });
    }

    [HttpPost("admin/reindex")]
    public async Task<IActionResult> Reindex([FromQuery] string repo)
    {
        var status = await index.ReindexAsync(HttpContext.RequestAborted, repo);
        return Ok(status);
    }
}
