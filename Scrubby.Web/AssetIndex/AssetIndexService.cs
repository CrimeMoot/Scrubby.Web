using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DMISharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace Scrubby.Web.AssetIndex;

public record AssetMeta(Guid SpriteId, int Direction, int Frame, bool IsGif);

/// <summary>
/// Local sprite index compatible with the AssetMonitor API used by the Icon search views.
///
/// Two modes:
///  - Local: builds the index from GitHub tarballs and stores a compact index + rendered assets on disk.
///  - Remote (<see cref="IsRemote"/>): reads the compact index published by CI from GitHub and renders
///    images on demand from the source DMIs, keeping everything in memory.
/// </summary>
public class AssetIndexService
{
    private readonly ILogger<AssetIndexService> _logger;
    private readonly string _dataDirectory;
    private readonly string _reposPath;
    private readonly string _gitHubToken;
    private readonly string _remoteBaseUrl;
    private readonly bool _autoIndex;
    private readonly bool _renderAssets;
    private readonly HashSet<string> _onlyRepos;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Scrubby.AssetIndex" } }
    };

    private readonly List<RepoConfig> _repos = new();
    private readonly Dictionary<string, RepoInfo> _repoInfo = new();
    private readonly Dictionary<Guid, SpriteRecord> _spritesById = new();
    private readonly Dictionary<string, List<SpriteRecord>> _spritesByDmi = new();
    private readonly Dictionary<Guid, AssetMeta> _assetMeta = new();
    private readonly HashSet<string> _loadedRepos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte[]> _remoteAssetCache = new();
    private readonly ConcurrentDictionary<string, byte[]> _remoteDmiCache = new();
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _repoLock = new(1, 1);
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public AssetIndexService(IConfiguration configuration, IHostEnvironment env, ILogger<AssetIndexService> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("AssetIndex");
        var repoRoot = env.ContentRootPath;

        _dataDirectory = ResolvePath(repoRoot, section["DataDirectory"] ?? "asset-index-data");
        _reposPath = ResolvePath(repoRoot, section["ReposPath"] ?? "AssetIndex/repos.json");
        _gitHubToken = section["GitHubToken"] ?? "";
        _autoIndex = section.GetValue("AutoIndex", false);
        _renderAssets = section.GetValue("RenderAssets", true);
        _remoteBaseUrl = (section["RemoteBaseUrl"] ?? "").TrimEnd('/');

        var only = section["OnlyRepos"];
        _onlyRepos = string.IsNullOrWhiteSpace(only)
            ? null
            : new HashSet<string>(only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRemote => !string.IsNullOrEmpty(_remoteBaseUrl);

    public bool AutoIndex => _autoIndex && !IsRemote;

    public bool HasData => _spritesById.Count > 0;

    public string DataDirectory => _dataDirectory;

    private static string ResolvePath(string root, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(root, path);

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            if (IsRemote)
                await LoadRemoteCatalogAsync();
            else
            {
                LoadLocalCatalog();
                LoadLocalIndex();
            }

            _loaded = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ---------------------------------------------------------------------
    // Catalog
    // ---------------------------------------------------------------------

    private void RegisterRepo(RepoConfig repo, string branch = null)
    {
        _repos.Add(repo);
        _repoInfo[repo.Name] = new RepoInfo
        {
            Name = repo.Name,
            DisplayName = repo.DisplayName ?? repo.Name,
            GitURL = repo.GitURL,
            Branch = branch ?? repo.DefaultBranch ?? "master",
            AssetLicense = repo.AssetLicense,
            Id = DeterministicGuid("repo|" + repo.Name)
        };
    }

    private void LoadLocalCatalog()
    {
        if (!File.Exists(_reposPath))
        {
            _logger.LogWarning("Asset index catalog not found at {Path}", _reposPath);
            return;
        }

        var repos = JsonSerializer.Deserialize<List<RepoConfig>>(File.ReadAllText(_reposPath), JsonOptions);
        if (repos == null) return;
        foreach (var repo in repos.Where(r => r.Enabled && (_onlyRepos == null || _onlyRepos.Contains(r.Name))))
            RegisterRepo(repo);
    }

    private async Task LoadRemoteCatalogAsync()
    {
        var json = await GetStringAsync($"{_remoteBaseUrl}/repos.json");
        if (json == null)
        {
            _logger.LogWarning("Remote asset index catalog not found at {Url}/repos.json", _remoteBaseUrl);
            return;
        }

        // The published catalog may be either the input format (RepoConfig) or the enriched format (RepoInfo).
        try
        {
            var infos = JsonSerializer.Deserialize<List<RepoInfo>>(json, JsonOptions);
            if (infos != null && infos.Count > 0 && infos.All(i => i.Id != Guid.Empty))
            {
                foreach (var info in infos)
                {
                    _repos.Add(new RepoConfig
                    {
                        Name = info.Name,
                        DisplayName = info.DisplayName,
                        GitURL = info.GitURL,
                        DefaultBranch = info.Branch
                    });
                    _repoInfo[info.Name] = info;
                }
                return;
            }
        }
        catch (JsonException)
        {
            // fall through to RepoConfig parsing
        }

        var configs = JsonSerializer.Deserialize<List<RepoConfig>>(json, JsonOptions);
        if (configs == null) return;
        foreach (var repo in configs.Where(r => r.Enabled && (_onlyRepos == null || _onlyRepos.Contains(r.Name))))
            RegisterRepo(repo);
    }

    public async Task<IReadOnlyList<RepoInfo>> GetReposAsync()
    {
        await EnsureLoadedAsync();
        return _repoInfo.Values.OrderBy(r => r.DisplayName.ToUpperInvariant()).ToList();
    }

    // ---------------------------------------------------------------------
    // Index loading
    // ---------------------------------------------------------------------

    private void LoadLocalIndex()
    {
        var indexDir = Path.Combine(_dataDirectory, "index");
        if (!Directory.Exists(indexDir)) return;

        foreach (var file in Directory.GetFiles(indexDir, "*.json*"))
        {
            try
            {
                var repo = Path.GetFileName(file).Split('.')[0];
                var compact = DeserializeCompact(File.ReadAllBytes(file));
                if (compact == null) continue;
                foreach (var sprite in ExpandCompact(repo, compact))
                    AddSpriteInMemory(sprite);
                _loadedRepos.Add(repo);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load asset index file {File}", file);
            }
        }
    }

    private async Task EnsureRepoLoadedAsync(string repo)
    {
        if (!IsRemote) return;
        if (string.IsNullOrEmpty(repo)) return;
        if (_loadedRepos.Contains(repo)) return;

        await _repoLock.WaitAsync();
        try
        {
            if (_loadedRepos.Contains(repo)) return;
            var data = await GetBytesAsync($"{_remoteBaseUrl}/index/{repo}.json.gz")
                       ?? await GetBytesAsync($"{_remoteBaseUrl}/index/{repo}.json");
            if (data == null)
            {
                _loadedRepos.Add(repo); // don't retry on every request
                return;
            }

            var compact = DeserializeCompact(data);
            if (compact != null)
                foreach (var sprite in ExpandCompact(repo, compact))
                    AddSpriteInMemory(sprite);
            _loadedRepos.Add(repo);
        }
        finally
        {
            _repoLock.Release();
        }
    }

    private void AddSpriteInMemory(SpriteRecord sprite)
    {
        _spritesById[sprite.Id] = sprite;
        var dmiKey = $"{sprite.Repo}|{sprite.DmiPath}";
        if (!_spritesByDmi.TryGetValue(dmiKey, out var list))
        {
            list = new List<SpriteRecord>();
            _spritesByDmi[dmiKey] = list;
        }
        if (!list.Contains(sprite)) list.Add(sprite);

        foreach (var frame in sprite.SpriteFrames)
            _assetMeta[frame.Asset.Id] = new AssetMeta(sprite.Id, frame.Direction, frame.Frame, false);
        if (sprite.RenderedAnimations != null)
            foreach (var anim in sprite.RenderedAnimations)
                _assetMeta[anim.Asset.Id] = new AssetMeta(sprite.Id, anim.Direction, anim.Frame, true);
    }

    private void RemoveRepoFromMemory(string repo)
    {
        var stale = _spritesById.Values.Where(s => s.Repo == repo).Select(s => s.Id).ToList();
        foreach (var id in stale)
        {
            var sprite = _spritesById[id];
            foreach (var frame in sprite.SpriteFrames) _assetMeta.Remove(frame.Asset.Id);
            if (sprite.RenderedAnimations != null)
                foreach (var anim in sprite.RenderedAnimations) _assetMeta.Remove(anim.Asset.Id);
            _spritesById.Remove(id);
        }
        var keys = _spritesByDmi.Keys.Where(k => k.StartsWith(repo + "|", StringComparison.Ordinal)).ToList();
        foreach (var key in keys) _spritesByDmi.Remove(key);
        _loadedRepos.Remove(repo);
    }

    // ---------------------------------------------------------------------
    // Search / lookup
    // ---------------------------------------------------------------------

    public async Task<SearchResponse> SearchAsync(AssetSearchParameters parameters)
    {
        await EnsureLoadedAsync();
        var response = new SearchResponse();

        if (!string.IsNullOrEmpty(parameters.RepoFilter))
            await EnsureRepoLoadedAsync(parameters.RepoFilter);

        Regex regex = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(parameters.SearchPattern))
            {
                var options = RegexOptions.CultureInvariant;
                if (parameters.IgnoreCase) options |= RegexOptions.IgnoreCase;
                regex = new Regex(parameters.SearchPattern, options);
            }
        }
        catch (Exception)
        {
            return response; // invalid regex -> no results
        }

        foreach (var sprite in _spritesById.Values)
        {
            if (!string.IsNullOrEmpty(parameters.RepoFilter) &&
                !string.Equals(sprite.Repo, parameters.RepoFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (parameters.RepoFilterGuid.HasValue && !sprite.RepoId.Equals(parameters.RepoFilterGuid.Value))
                continue;
            if (regex != null && !regex.IsMatch(sprite.Name))
                continue;

            response.Results.Add(new SearchResult
            {
                Id = sprite.Id,
                Name = sprite.Name,
                Path = sprite.Path,
                Repo = sprite.Repo,
                RepoID = sprite.RepoId,
                PreviewAsset = sprite.PreviewAsset,
                Width = sprite.Width,
                Height = sprite.Height,
                Directions = sprite.Dirs,
                Frames = sprite.Frames,
                Animations = sprite.RenderedAnimations
            });
        }

        return response;
    }

    public async Task<SpriteRecord> GetSpriteAsync(Guid id)
    {
        await EnsureLoadedAsync();
        return _spritesById.TryGetValue(id, out var sprite) ? sprite : null;
    }

    public async Task<SpritesheetInfo> GetSpritesheetAsync(string repo, string path)
    {
        await EnsureLoadedAsync();
        await EnsureRepoLoadedAsync(repo);
        var key = $"{repo}|{path}";
        if (!_spritesByDmi.TryGetValue(key, out var sprites))
            return null;

        return new SpritesheetInfo
        {
            FileHash = DeterministicGuid("sheet|" + key).ToString(),
            Name = Path.GetFileName(path),
            Path = path,
            Sprites = sprites
        };
    }

    // ---------------------------------------------------------------------
    // Assets
    // ---------------------------------------------------------------------

    public async Task<(byte[] Data, string ContentType)> GetAssetAsync(Guid id)
    {
        await EnsureLoadedAsync();
        return IsRemote ? await GetRemoteAssetAsync(id) : await GetLocalAssetAsync(id);
    }

    private async Task<(byte[] Data, string ContentType)> GetLocalAssetAsync(Guid id)
    {
        var pngPath = Path.Combine(_dataDirectory, "assets", id + ".png");
        var gifPath = Path.Combine(_dataDirectory, "assets", id + ".gif");

        if (File.Exists(pngPath)) return (await File.ReadAllBytesAsync(pngPath), "image/png");
        if (File.Exists(gifPath)) return (await File.ReadAllBytesAsync(gifPath), "image/gif");

        if (!_assetMeta.TryGetValue(id, out var meta) || !_spritesById.TryGetValue(meta.SpriteId, out var sprite))
            return (null, null);

        await _renderLock.WaitAsync();
        try
        {
            if (File.Exists(pngPath)) return (await File.ReadAllBytesAsync(pngPath), "image/png");
            if (File.Exists(gifPath)) return (await File.ReadAllBytesAsync(gifPath), "image/gif");

            var dmiPath = Path.Combine(_dataDirectory, "dmi", sprite.Repo, sprite.DmiPath);
            if (!File.Exists(dmiPath)) return (null, null);

            using var ms = new MemoryStream(await File.ReadAllBytesAsync(dmiPath));
            using var dmi = new DMIFile(ms);
            var state = dmi.States.FirstOrDefault(s => s.Name == sprite.Name);
            if (state == null) return (null, null);

            if (meta.IsGif)
            {
                var data = RenderGif(state, meta.Direction);
                await File.WriteAllBytesAsync(gifPath, data);
                return (data, "image/gif");
            }

            var frame = RenderFrame(state, meta.Direction, meta.Frame);
            await File.WriteAllBytesAsync(pngPath, frame);
            return (frame, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render asset {AssetId}", id);
            return (null, null);
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private async Task<(byte[] Data, string ContentType)> GetRemoteAssetAsync(Guid id)
    {
        if (_remoteAssetCache.TryGetValue(id, out var cached))
            return (cached, cached.Length > 3 && cached[0] == 0x47 ? "image/gif" : "image/png");

        if (!_assetMeta.TryGetValue(id, out var meta) || !_spritesById.TryGetValue(meta.SpriteId, out var sprite))
            return (null, null);
        if (!_repoInfo.TryGetValue(sprite.Repo, out var repo))
            return (null, null);

        var dmiBytes = await GetRemoteDmiAsync(repo, sprite.DmiPath);
        if (dmiBytes == null) return (null, null);

        try
        {
            using var ms = new MemoryStream(dmiBytes);
            using var dmi = new DMIFile(ms);
            var state = dmi.States.FirstOrDefault(s => s.Name == sprite.Name);
            if (state == null) return (null, null);

            if (meta.IsGif)
            {
                var data = RenderGif(state, meta.Direction);
                Cache(_remoteAssetCache, id, data);
                return (data, "image/gif");
            }

            var frame = RenderFrame(state, meta.Direction, meta.Frame);
            Cache(_remoteAssetCache, id, frame);
            return (frame, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render remote asset {AssetId}", id);
            return (null, null);
        }
    }

    private async Task<byte[]> GetRemoteDmiAsync(RepoInfo repo, string dmiPath)
    {
        var (owner, name) = ParseGitUrl(repo.GitURL);
        if (owner == null) return null;
        var key = $"{repo.Name}|{dmiPath}";
        if (_remoteDmiCache.TryGetValue(key, out var cached)) return cached;

        var url = $"https://raw.githubusercontent.com/{owner}/{name}/{repo.Branch}/{dmiPath}";
        var bytes = await GetBytesAsync(url);
        if (bytes != null) Cache(_remoteDmiCache, key, bytes);
        return bytes;
    }

    private static void Cache<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, TKey key, TValue value)
    {
        if (cache.Count > 20000) cache.Clear();
        cache[key] = value;
    }

    // ---------------------------------------------------------------------
    // Indexing (local mode)
    // ---------------------------------------------------------------------

    public async Task<IndexStatus> ReindexAsync(CancellationToken cancellationToken = default, string onlyRepo = null)
    {
        await EnsureLoadedAsync();
        var repos = string.IsNullOrWhiteSpace(onlyRepo)
            ? _repos
            : _repos.Where(r => string.Equals(r.Name, onlyRepo, StringComparison.OrdinalIgnoreCase)).ToList();
        return await IndexReposAsync(repos, new IndexStatus(), cancellationToken);
    }

    /// <summary>Indexes only the repos that have no persisted index yet.</summary>
    public async Task<IndexStatus> IndexMissingAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync();
        var indexDir = Path.Combine(_dataDirectory, "index");
        var missing = _repos.Where(r => !File.Exists(Path.Combine(indexDir, r.Name + ".json.gz")) &&
                                        !File.Exists(Path.Combine(indexDir, r.Name + ".json"))).ToList();
        return await IndexReposAsync(missing, new IndexStatus(), cancellationToken);
    }

    private async Task<IndexStatus> IndexReposAsync(List<RepoConfig> repos, IndexStatus status,
        CancellationToken cancellationToken)
    {
        status.TotalRepos = repos.Count;
        foreach (var repo in repos)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await IndexRepoAsync(repo, cancellationToken);
                status.IndexedRepos++;
                _logger.LogInformation("Indexed repo {Repo} ({Indexed}/{Total})", repo.Name, status.IndexedRepos, status.TotalRepos);
            }
            catch (Exception ex)
            {
                status.FailedRepos++;
                status.Errors.Add($"{repo.Name}: {ex.Message}");
                _logger.LogError(ex, "Failed to index repo {Repo}", repo.Name);
            }
        }

        ExportCatalog();
        return status;
    }

    private async Task IndexRepoAsync(RepoConfig repo, CancellationToken cancellationToken)
    {
        RemoveRepoFromMemory(repo.Name);

        var (owner, name) = ParseGitUrl(repo.GitURL);
        if (owner == null) throw new InvalidOperationException($"Unparseable git URL: {repo.GitURL}");

        var branches = new[] { repo.DefaultBranch, "HEAD", "master", "main" }.Distinct();
        HttpResponseMessage response = null;
        string usedBranch = null;
        foreach (var branch in branches)
        {
            var candidate = await HttpClient.GetAsync(
                $"https://codeload.github.com/{owner}/{name}/tar.gz/{branch}",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (candidate.IsSuccessStatusCode)
            {
                response = candidate;
                usedBranch = branch;
                break;
            }
            candidate.Dispose();
        }

        if (response == null)
            throw new InvalidOperationException($"Could not download {owner}/{name} (tried branches: {string.Join(", ", branches)})");

        using (response)
        {
            var repoId = DeterministicGuid("repo|" + repo.Name);
            if (_repoInfo.TryGetValue(repo.Name, out var info))
            {
                info.Branch = usedBranch;
                info.LastCheckedCommit = await TryGetHeadCommitAsync(owner, name, usedBranch);
            }

            var sprites = new List<SpriteRecord>();
            var assetsDir = Path.Combine(_dataDirectory, "assets");
            var dmiDir = Path.Combine(_dataDirectory, "dmi", repo.Name);

            await using var tarball = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzip = new GZipStream(tarball, CompressionMode.Decompress);
            await DmiTarEnumerator.EnumerateDmiAsync(gzip, async (tarPath, dmiBytes) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dmiPath = NormalizeTarPath(tarPath);
                if (_renderAssets)
                {
                    Directory.CreateDirectory(dmiDir);
                    var localDmi = Path.Combine(dmiDir, dmiPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(localDmi)!);
                    await File.WriteAllBytesAsync(localDmi, dmiBytes, cancellationToken);
                }

                using var ms = new MemoryStream(dmiBytes, writable: false);
                foreach (var record in IndexDmi(repo.Name, repoId, dmiPath, ms, _repoInfo.TryGetValue(repo.Name, out var ri) ? ri.LastCheckedCommit : null))
                {
                    sprites.Add(record);
                    if (_renderAssets)
                        RenderPreview(assetsDir, record, ms);
                }
            }, cancellationToken);

            foreach (var sprite in sprites) AddSpriteInMemory(sprite);
            SaveCompactIndex(repo.Name, sprites);
            _logger.LogInformation("Repo {Repo}: {Count} sprites from {Name}", repo.Name, sprites.Count, owner + "/" + name);
        }
    }

    private void RenderPreview(string assetsDir, SpriteRecord record, MemoryStream dmiStream)
    {
        var path = Path.Combine(assetsDir, record.PreviewAsset + ".png");
        if (File.Exists(path)) return;
        try
        {
            dmiStream.Position = 0;
            using var dmi = new DMIFile(dmiStream);
            var state = dmi.States.FirstOrDefault(s => s.Name == record.Name);
            if (state == null) return;
            var data = RenderFrame(state, 0, 0);
            Directory.CreateDirectory(assetsDir);
            File.WriteAllBytes(path, data);
        }
        catch
        {
            // preview rendering is best-effort
        }
    }

    private IEnumerable<SpriteRecord> IndexDmi(string repo, Guid repoId, string dmiPath, Stream stream, string commit)
    {
        DMIFile dmi;
        try
        {
            dmi = new DMIFile(stream);
        }
        catch
        {
            yield break;
        }

        using (dmi)
        {
            foreach (var state in dmi.States)
                yield return BuildSprite(repo, repoId, dmiPath, state.Name, state.Width, state.Height,
                    Math.Max(1, state.Dirs), Math.Max(1, state.Frames), commit);
        }
    }

    // ---------------------------------------------------------------------
    // Deterministic model construction
    // ---------------------------------------------------------------------

    public static Guid SpriteId(string repo, string dmiPath, string name) =>
        DeterministicGuid($"{repo}|{dmiPath}|{name}");

    public static Guid FrameAssetId(string repo, string dmiPath, string name, int dir, int frame) =>
        DeterministicGuid($"{repo}|{dmiPath}|{name}|frame|{dir}|{frame}");

    public static Guid GifAssetId(string repo, string dmiPath, string name, int dir) =>
        DeterministicGuid($"{repo}|{dmiPath}|{name}|gif|{dir}");

    private SpriteRecord BuildSprite(string repo, Guid repoId, string dmiPath, string name, int width, int height,
        int dirs, int frames, string commit)
    {
        var record = new SpriteRecord
        {
            Id = SpriteId(repo, dmiPath, name),
            Name = name,
            DmiPath = dmiPath,
            Path = $"{dmiPath}@{name}",
            Repo = repo,
            RepoId = repoId,
            Width = width,
            Height = height,
            Dirs = dirs,
            Frames = frames,
            LastCommit = commit
        };

        for (var dir = 0; dir < dirs; dir++)
        for (var frame = 0; frame < frames; frame++)
            record.SpriteFrames.Add(new SpriteFrameRef
            {
                Direction = dir,
                Frame = frame,
                Asset = new AssetRef { Id = FrameAssetId(repo, dmiPath, name, dir, frame), AssetType = "Frame", LastCommit = commit }
            });

        record.PreviewAsset = record.SpriteFrames.Count > 0
            ? record.SpriteFrames[0].Asset.Id
            : FrameAssetId(repo, dmiPath, name, 0, 0);

        if (frames > 1)
        {
            record.RenderedAnimations = new List<SpriteFrameRef>();
            for (var dir = 0; dir < dirs; dir++)
                record.RenderedAnimations.Add(new SpriteFrameRef
                {
                    Direction = dir,
                    Frame = 0,
                    Asset = new AssetRef { Id = GifAssetId(repo, dmiPath, name, dir), AssetType = "Animation", LastCommit = commit }
                });
        }

        return record;
    }

    private IEnumerable<SpriteRecord> ExpandCompact(string repo, CompactRepoIndex compact)
    {
        var repoId = DeterministicGuid("repo|" + repo);
        foreach (var file in compact.Files)
        foreach (var state in file.S)
            yield return BuildSprite(repo, repoId, file.P, state.N, file.W, file.H,
                Math.Max(1, state.D), Math.Max(1, state.F), compact.Commit);
    }

    private void SaveCompactIndex(string repo, List<SpriteRecord> sprites)
    {
        var indexDir = Path.Combine(_dataDirectory, "index");
        Directory.CreateDirectory(indexDir);

        var compact = new CompactRepoIndex
        {
            Commit = sprites.FirstOrDefault()?.LastCommit,
            Files = sprites
                .GroupBy(s => s.DmiPath)
                .Select(g => new CompactFile
                {
                    P = g.Key,
                    W = g.First().Width,
                    H = g.First().Height,
                    S = g.Select(s => new CompactState { N = s.Name, D = s.Dirs, F = s.Frames }).ToList()
                }).ToList()
        };

        using var fs = File.Create(Path.Combine(indexDir, repo + ".json.gz"));
        using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gz, compact, JsonOptions);
    }

    private void ExportCatalog()
    {
        if (IsRemote) return;
        Directory.CreateDirectory(_dataDirectory);
        var infos = _repoInfo.Values.OrderBy(r => r.Name).ToList();
        File.WriteAllText(Path.Combine(_dataDirectory, "repos.json"), JsonSerializer.Serialize(infos, JsonOptions));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static CompactRepoIndex DeserializeCompact(byte[] data)
    {
        // gzip magic number
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<CompactRepoIndex>(gz, JsonOptions);
        }

        return JsonSerializer.Deserialize<CompactRepoIndex>(data, JsonOptions);
    }

    private async Task<string> GetStringAsync(string url)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Url}", url);
            return null;
        }
    }

    private async Task<byte[]> GetBytesAsync(string url)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Url}", url);
            return null;
        }
    }

    private async Task<string> TryGetHeadCommitAsync(string owner, string name, string branch)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{name}/commits/{branch}");
            if (!string.IsNullOrEmpty(_gitHubToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _gitHubToken);
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.GetProperty("sha").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] RenderFrame(DMIState state, int direction, int frame)
    {
        using var image = state.GetFrame((StateDirection)direction, frame);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] RenderGif(DMIState state, int direction)
    {
        using var ms = new MemoryStream();
        var encoder = new GifEncoder { ColorTableMode = GifColorTableMode.Global };
        state.SaveAnimatedGIF(ms, (StateDirection)direction, encoder);
        return ms.ToArray();
    }

    private static string NormalizeTarPath(string tarPath)
    {
        var parts = tarPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? tarPath : string.Join("/", parts[1..]);
    }

    private static (string Owner, string Name) ParseGitUrl(string gitUrl)
    {
        if (string.IsNullOrWhiteSpace(gitUrl)) return (null, null);
        var url = gitUrl.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        var idx = url.LastIndexOf('/');
        if (idx <= 0) return (null, null);
        var name = url[(idx + 1)..];
        var owner = url[..idx].Split('/')[^1];
        return (owner, name);
    }

    public static Guid DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}

public class IndexStatus
{
    public int TotalRepos { get; set; }
    public int IndexedRepos { get; set; }
    public int FailedRepos { get; set; }
    public List<string> Errors { get; set; } = new();
}
