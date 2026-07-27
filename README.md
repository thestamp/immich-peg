# Immich Peg

Sync sidecar that replicates shared albums from a private [Immich](https://immich.app) instance to a public one. Two-phase sync: metadata/publish first, then assets.

## Features

- **Two-phase sync** — publish album metadata (names, sharing slugs) separately from copying assets
- **Self-healing** — detects missing assets on the public side and re-adds them
- **`peg_` slug management** — creates and maintains consistent sharing slugs on both instances
- **Permission validation** — verifies API keys have the required permissions
- **Dashboard UI** — real-time health, album stats, sync controls, recent activity
- **Settings with API key masking** — lockable configuration with reset protection
- **Docker deployment** — single container attached to both Immich Docker networks

## Quick Start

```bash
docker run -d \
  --name immich-peg \
  --network immich_default \
  -p 8080:8080 \
  -v immich-peg-data:/data \
  --restart unless-stopped \
  thestamp/immich-peg:main

docker network connect immich-public_default immich-peg
```

Then open `http://your-host:8080` and complete the setup wizard.

## Setup

1. Create API keys in both Immich instances (Admin → API Keys)
2. Open the setup wizard at `http://your-host:8080/setup.html`
3. Enter URLs (Docker hostnames like `immich_server:2283`) and API keys
4. Configure sync interval (1-60 minutes)
5. Optionally lock settings to prevent changes

## API Keys

The API key for your **main** (private) Immich instance needs these permissions:
- `asset.read` — list and download assets
- `album.read` — list shared albums and their assets
- `album.share` — create/manage sharing links

The API key for your **public** Immich instance needs:
- `asset.upload` — upload assets
- `asset.create` — create assets
- `album.read` — list albums
- `album.update` — add assets to albums
- `album.share` — create/manage sharing links

## Architecture

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐
│ Main Immich │◄────│  Immich Peg  │────►│Public Immich│
│  (private)  │     │  (sync sidecar)    │  (public)   │
└─────────────┘     └──────────────┘     └─────────────┘
                            │
                    ┌───────┴───────┐
                    │  Dashboard UI │
                    │  (port 8080)  │
                    └───────────────┘
```

## Sync Flow

1. **Publish** — for each shared album on main:
   - Creates matching album on public (if missing)
   - Replicates `peg_XXXXXXXX` share slug on both instances
   - Syncs share settings (allow download, show metadata)

2. **Copy Assets** — for each album:
   - Lists assets on both sides
   - Uploads missing assets to public
   - Re-adds assets already on public but not in the album (self-healing)
   - Tracks synced/total counts per album

## Development

```bash
# Build locally
dotnet build -c Release

# Build Docker image
docker build -t immich-peg .

# Run locally
ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet run
```

### Project structure

```
ImmichPeg/
├── Program.cs              # Minimal API endpoints
├── Models/
│   └── Models.cs           # Data models
├── Services/
│   ├── ImmichClient.cs     # Immich API client
│   ├── SyncEngine.cs       # Sync logic
│   ├── SyncConfigService.cs # Config persistence
│   └── SyncBackgroundService.cs # Scheduled syncs
├── wwwroot/                # Static UI
│   ├── index.html
│   ├── setup.html
│   ├── dashboard.html
│   └── settings.html
└── Dockerfile
```

## Tech Stack

- .NET 10 (ASP.NET Core Minimal API)
- Docker multi-stage build
- Vanilla HTML/CSS/JS UI (no framework)
- Immich API v3
