using System;
using System.Collections.Generic;

namespace Scrubby.Web.AssetIndex;

/// <summary>Configuration entry from AssetIndex/repos.json.</summary>
public class RepoConfig
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string GitURL { get; set; }
    public string DefaultBranch { get; set; } = "master";
    public string AssetLicense { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Repo metadata returned by GET /api/repo.</summary>
public class RepoInfo
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string GitURL { get; set; }
    public string Branch { get; set; }
    public string LastCheckedCommit { get; set; }
    public string AssetLicense { get; set; }
    public string LicenseNotice { get; set; }
    public Guid Id { get; set; }
}

/// <summary>
/// Compact, version-controlled index format. Sprite/asset ids and per-frame references are
/// regenerated deterministically on load, so only the minimum is persisted.
/// </summary>
public class CompactRepoIndex
{
    public string Commit { get; set; }
    public List<CompactFile> Files { get; set; } = new();
}

public class CompactFile
{
    public string P { get; set; }   // DMI path
    public int W { get; set; }      // frame width
    public int H { get; set; }      // frame height
    public List<CompactState> S { get; set; } = new();
}

public class CompactState
{
    public string N { get; set; }   // state name
    public int D { get; set; }      // directions
    public int F { get; set; }      // frames
}


/// <summary>Reference to a rendered asset (image frame / gif).</summary>
public class AssetRef
{
    public Guid Id { get; set; }
    public string AssetType { get; set; }
    public DateTime LastChanged { get; set; }
    public string LastCommit { get; set; }
}

public class SpriteFrameRef
{
    public int Direction { get; set; }
    public int Frame { get; set; }
    public AssetRef Asset { get; set; }
}

/// <summary>Indexed DMI state. Persisted to disk and served by the API.</summary>
public class SpriteRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }        // e.g. icons/obj/weapons/turrets.dmi@openTurretCover
    public string DmiPath { get; set; }     // e.g. icons/obj/weapons/turrets.dmi
    public string Repo { get; set; }
    public Guid RepoId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dirs { get; set; }
    public int Frames { get; set; }
    public Guid PreviewAsset { get; set; }
    public List<SpriteFrameRef> SpriteFrames { get; set; } = new();
    public List<SpriteFrameRef> RenderedAnimations { get; set; }
    public string LastCommit { get; set; }
    public DateTime LastChanged { get; set; }
}

/// <summary>Search request body (matches AssetMonitor's AssetSearchParameters).</summary>
public class AssetSearchParameters
{
    public int SearchType { get; set; }
    public string SearchPattern { get; set; }
    public bool IgnoreCase { get; set; }
    public Guid? RepoFilterGuid { get; set; }
    public string RepoFilter { get; set; }
}

public class SearchResult
{
    public Guid Id { get; set; }
    public string ResultType { get; set; } = "Sprite";
    public string Name { get; set; }
    public string Path { get; set; }
    public string Repo { get; set; }
    public Guid RepoID { get; set; }
    public Guid PreviewAsset { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Directions { get; set; }
    public int Frames { get; set; }
    public object Animations { get; set; }
}

public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = new();
}

public class SpritesheetInfo
{
    public string FileHash { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public List<SpriteRecord> Sprites { get; set; } = new();
}
