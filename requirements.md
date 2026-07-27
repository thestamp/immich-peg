# Immich Peg — System Design & Requirements

## Purpose

Immich Peg bridges shared albums between a **private** Immich instance and a **public** Immich instance. When a user shares an album on the private instance, Peg automatically:
1. Creates the album on the public instance
2. Copies all assets (images/videos) from private → public
3. Sets identical `peg_`-prefixed share link slugs on both sides

This lets the owner share a link immediately — the album appears on public with a working link before assets finish copying.

## Architecture

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│ Main Immich  │     │  Immich Peg  │     │ Public Immich│
│  (private)   │◄───►│  (bridge)    │◄───►│  (shared)    │
│ port 2283    │     │  port 8080   │     │ port 2283    │
└──────────────┘     └──────────────┘     └──────────────┘
```

Peg runs as a Docker container attached to both Immich Docker networks. It has no database — all state is stored in a JSON config file at `/data/config.json` (persisted via Docker volume).

## Sync Engine — Two-Phase Design

A sync cycle has two distinct phases, exposed as separate API endpoints and manual actions:

### Phase 1: Publish (metadata only)
- Fetch all shared albums from the main instance
- For each album: create matching album on public, replicate share link with `peg_` slug
- Remove albums from public that are no longer shared on main
- **Goal:** Albums and share links are live within seconds — user can share the link immediately

### Phase 2: Sync Assets (data only)
- For each published album: download assets from main, upload to public
- Add each asset to the public album *immediately* after upload (not batched at end)
- Self-healing: if an asset already exists on public but isn't in the album, just re-add it
- Track recently synced assets with timestamps for real-time dashboard display
- Save config after each asset so the dashboard updates in real-time

### Key sync behaviors:
- **Cancellation:** Pressing "Stop" or starting a new sync cancels the running one via `CancellationToken`
- **One-at-a-time:** Assets are downloaded and uploaded individually, not in batches
- **Self-healing:** Before uploading, check if the asset filename already exists anywhere on the public instance (bulk index built at start of sync). If it does, skip upload and just add to album
- **Idempotent:** Safe to re-run — existing albums and share links are preserved, only missing assets are copied

## Share Link Slugs (`peg_`)

- Format: `peg_` + 8 random hex characters (e.g., `peg_d7a3c972`)
- Applied to **both** main and public instances (identical slug)
- Idempotent: once a `peg_` slug exists on either side, it is reused
- On sync, any non-`peg_` share is deleted and replaced with the `peg_` version

## API Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Health check |
| GET | `/api/dashboard` | Full dashboard data (health, stats, recent assets, permissions) |
| POST | `/api/sync/publish` | Run Phase 1 — metadata sync |
| POST | `/api/sync/assets` | Run Phase 2 — asset sync |
| POST | `/api/sync/stop` | Cancel running sync |
| POST | `/api/sync/trigger` | Full sync (legacy — used by scheduler) |
| GET/POST | `/api/setup` | Setup wizard |
| POST | `/api/setup/status` | Check if setup is complete |
| GET/POST | `/api/settings` | View/update settings |
| POST | `/api/reset` | Factory reset |
| GET | `/api/logs` | Streaming log endpoint |

**Dashboard response includes:** `sync_active` boolean (checked via `CancellationToken`), `recent_assets` list, health/permission status.

## Configuration

Stored at `/data/config.json`:

```json
{
  "main": { "url": "...", "api_key": "..." },
  "public": { "url": "...", "api_key": "..." },
  "sync_interval_minutes": 5,
  "setup_complete": true,
  "settings_enabled": true,
  "dashboard_enabled": true,
  "synced_albums": {
    "<main_album_id>": {
      "public_album_id": "...",
      "album_name": "...",
      "asset_count": 42,
      "last_synced": "2026-..."
    }
  },
  "recent_assets": [
    { "filename": "IMG_001.jpg", "album_name": "Vacation", "timestamp": "..." }
  ],
  "albums_synced": 150,
  "assets_copied": 5000
}
```

## Immich API Details (v3)

Peg targets **Immich v3.x** REST API:

- **List albums:** `GET /api/albums?isShared=true&isOwned=true` (filters to owned+shared)
- **List album assets:** `POST /api/search/metadata` with `albumIds` array
- **Upload asset:** `POST /api/assets` (multipart form: `assetData`, `fileCreatedAt`, `fileModifiedAt`)
- **Add to album:** `PUT /api/albums/{id}/assets` with `{ids: [...]}`
- **Share links:** `GET/POST /api/shared-links`, `DELETE /api/shared-links/{id}`
- **Bulk lookup:** `POST /api/search/metadata` (no filters) → build `filename → assetId` map

Note: The `isShared`/`isOwned` query params are specific to v3. The v2 param `shared` returns unfiltered results (do not use).

## Security Features

- **Settings lock:** Can be disabled via settings page checkbox. When locked, `/settings` returns 403. Only way to re-enable: edit `/data/config.json` and restart.
- **Dashboard lock:** Can be independently disabled. When locked, `/dashboard` returns 403.
- **Both locked:** If both settings and dashboard are disabled, the web server does not start at all (container runs scheduler only, no port exposed). Re-enable via config file + restart.
- **Masked API keys:** Settings page shows `••••••••` placeholders. Empty submissions preserve existing keys. Only non-empty values update.
- **Confirmation dialog:** Changing settings shows a confirmation prompt, especially when locking UI.

## Deployment

```bash
docker run -d \
  --name immich-peg \
  --network immich_default \
  --network immich-public_default \
  -p 8080:8080 \
  -v immich-peg-data:/data \
  --restart unless-stopped \
  thestamp/immich-peg:main
```

- CI/CD: GitHub Actions builds on every push to `main`, tags as `thestamp/immich-peg:main`, pushes to Docker Hub
- No database — JSON config file in Docker volume
- Scheduler: background service runs full sync every `sync_interval_minutes`

## Required Immich API Key Permissions

**Main instance (read-only for Peg):**
- `asset.read`, `album.read`

**Public instance (read+write):**
- `asset.upload`, `asset.create`, `album.read`, `album.create`, `album.update`, `album.delete`, `shared-link.read`, `shared-link.create`, `shared-link.delete`

Or use `"all"` for both.

## Real-Time Dashboard

- Polls `/api/dashboard` every 2s while sync is active, 5s when idle
- Shows: health indicators, sync status badge, published albums table, recently synced assets (with filenames and timestamps), streaming log pane
- Activity bar with spinner animation when sync is running
- Separate "Publish" and "Sync Assets" buttons; "Stop" button during active sync
- Settings gear icon (hidden when settings are locked)