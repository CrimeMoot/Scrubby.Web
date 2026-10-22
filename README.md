# Scrubby.Web (Icon Search)

A hosted, read-only **sprite/DMI search** for SS13 — the Icon Search part of Scrubby, revived and slimmed down. The rest of the site (round/CKey search, database, auth) has been removed on purpose.

The deployment is fully cloud-based: GitHub Actions indexes sprites and publishes the data, GitHub Container Registry hosts the Docker image running as a site. There is nothing to clone, build or configure to use it.

## How it works

```
 GitHub Actions (Docker)                    Docker (Scrubby.Web)
 ┌───────────────────────────┐              ┌──────────────────────────────┐
 │ Scrubby.AssetIndexer       │  compact     │  - reads index/*.json.gz     │
 │  - downloads repo tarballs │  index       │  - renders images on demand  │
 │  - parses DMIs (DMISharp)  │ ──────────▶  │    from source DMIs (raw)    │
 │  - writes compact index    │ asset-data   │  - in-memory cache only      │
 └───────────────────────────┘  branch       └──────────────────────────────┘
```

1. The **Build Asset Index** workflow (`.github/workflows/index-assets.yml`) runs the `Scrubby.AssetIndexer` indexer (weekly + on demand) and force-pushes `index/*.json.gz` + `repos.json` to the **`asset-data` branch**.
2. The web app reads the index from that branch on demand (per repository, cached in memory) and renders PNG/GIF frames from the source DMIs on `raw.githubusercontent.com`. Nothing is stored locally.
3. The **Publish Scrubby.Web Container** workflow (`.github/workflows/deploy-docker.yml`) builds the site container on every push to `master` and publishes it to `ghcr.io/crimemoot/scrubby.web`.

All configuration is baked into `appsettings.json`:

```json
"IconSearch":  { "ApiBaseUrl": "/api/asset" },
"AssetIndex":  { "RemoteBaseUrl": "https://raw.githubusercontent.com/CrimeMoot/Scrubby.Web/asset-data" }
```

Index details and the repository catalog: [Scrubby.Web/AssetIndex/README.md](Scrubby.Web/AssetIndex/README.md).

## API

Served under `/api/asset` and drop-in compatible with AssetMonitor (`am.melonmesa.com`):

| Endpoint | Description |
| --- | --- |
| `GET  /api/asset/repo` | repository list |
| `POST /api/asset/search` | sprite search (`searchType`, `searchPattern`, `ignoreCase`, `repoFilter`) |
| `GET  /api/asset/sprite/{id}` | sprite detail (directions/frames/animations) |
| `GET  /api/asset/asset/{id}` | rendered PNG or GIF (rendered on demand) |
| `GET  /api/asset/repo/{repo}/{path}` | all sprites in one DMI file |
| `GET  /api/asset/status` | index status |

## Adding repositories

Append an entry to `Scrubby.Web/AssetIndex/repos.json` and run the **Build Asset Index** workflow (`only_repos=myfork` builds just the new one).

---

Original Scrubby by bobbah; revived fork by CrimeMoot. This is the web front-end of the Scrubby suite of log-parsing software for /tg/station.
