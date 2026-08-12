# Installation Guide

## Requirements

- Jellyfin 10.11 or newer
- FFmpeg (typically bundled with Jellyfin as `jellyfin-ffmpeg`)

## Method 1: Plugin Repository (Recommended)

This method allows Jellyfin to track updates automatically.

1. Open Jellyfin and navigate to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name:** `mcgarrah-plugins`
   - **URL:** `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json`
3. Click **Save**
4. Go to the **Catalog** tab
5. Find **Media Integrity Scanner** and click **Install**
6. **Restart Jellyfin**

After restart, the plugin appears under **Dashboard → Plugins → My Plugins**.

## Using the Development (Pre-Release) Channel

The plugin's own **Settings → Update Channel** dropdown lets you track either Stable or Development builds — but switching it alone does nothing. Jellyfin's plugin catalog only ever knows about versions from repositories you've explicitly registered under **Dashboard → Plugins → Repositories**; the plugin's update checker doesn't fetch a manifest URL on its own, it only reads whatever Jellyfin has already found there. If you followed Method 1 above, Jellyfin only knows about the **stable** manifest — it has no reason to ever look at the development one, so switching the channel setting will silently never find an update.

To actually enable the Development channel:

1. **Dashboard → Plugins → Repositories → Add** a *second* repository (in addition to the one from Method 1):
   - **Name:** `mcgarrah-plugins-dev`
   - **URL:** `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest-unstable.json`
2. Click **Save**
3. Open the plugin's own **Settings** page and set **Update Channel** to **Development (pre-releases)**
4. **Restart Jellyfin** (or use the dashboard's **Check for Updates** button) so the plugin re-checks with the newly-registered repository in view

Once both repositories are registered, you can switch the channel back and forth freely — no need to add/remove repositories again.

## Method 2: Manual Installation from GitHub Release

1. Download the latest release zip from:
   https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/releases/latest

2. Extract the zip contents into a folder named `MediaIntegrityScanner` inside your Jellyfin plugins directory:

   **Linux (package install):**
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/MediaIntegrityScanner
   unzip media-integrity-scanner-*.zip -d /var/lib/jellyfin/plugins/MediaIntegrityScanner
   ```

   **Linux (Docker):**
   ```bash
   mkdir -p /path/to/jellyfin/config/plugins/MediaIntegrityScanner
   unzip media-integrity-scanner-*.zip -d /path/to/jellyfin/config/plugins/MediaIntegrityScanner
   ```

   **Windows:**
   ```
   Extract to: %PROGRAMDATA%\Jellyfin\Server\plugins\MediaIntegrityScanner\
   ```

3. **Restart Jellyfin**

## Method 3: Build from Source

```bash
git clone https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner.git
cd jellyfin-plugin-media-integrity-scanner

dotnet restore
dotnet publish --configuration Release --output ./publish
```

Then copy the contents of `./publish` to your plugins directory as described in Method 2.

## Verifying Installation

After restarting Jellyfin:

1. Go to **Dashboard → Plugins → My Plugins**
2. You should see **Media Integrity Scanner** listed with version `0.1.0.0`
3. The plugin's dashboard page is accessible from the plugin settings

## Proxmox LXC Notes

If your Jellyfin runs in a Proxmox LXC container:

- The plugins directory is typically at `/var/lib/jellyfin/plugins/`
- Ensure the `jellyfin` user owns the plugin files:
  ```bash
  chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/MediaIntegrityScanner
  ```
- Restart Jellyfin inside the container:
  ```bash
  systemctl restart jellyfin
  ```

## Backing Up Your Data

**Jellyfin's own Dashboard → Advanced → Backups (Database / Metadata / Subtitles / Trickplay) does not cover this plugin's data, no matter which options are checked.** Confirmed by reading the real Jellyfin server source, not assumed: the backup feature's code has a fixed, hardcoded list of paths it copies, and none of them ever reference a plugin's own data directory. This is a Jellyfin-wide limitation affecting every third-party plugin's persisted data, not something specific to this plugin.

What's actually at risk is the scan-history database (`media-integrity.db` under the plugin's configuration directory) — your pass/fail/error results and timestamps. It's regenerable by re-running a scan, not irreplaceable data like the media itself, but losing it means losing history you may not want to re-earn by re-scanning a large library.

**The plugin has its own backup/restore, specifically because of this gap:**

1. Open the plugin's **Dashboard** page → **Database Backup** section
2. Click **Backup Now** to snapshot the current database (safe to run at any time, including mid-scan — it doesn't stop or interfere with an in-progress scan)
3. The backup appears in the list below, with its creation time and size
4. Click **Restore** next to any backup to roll back to that snapshot — useful before a destructive test (clearing history and re-scanning), so you can restore and compare afterward instead of re-scanning the whole library again

Restore (not Backup) requires no scan to be in progress — the dashboard will show an error if you try mid-scan.

## Database Maintenance

The same `media-integrity.db` also gets an integrity check and space reclamation, separate from backup/restore:

1. Open the plugin's **Dashboard** page → **Database Maintenance** section to see its current on-disk size and how much space is reclaimable
2. Click **Run Maintenance Now** to run `PRAGMA integrity_check` immediately, followed by a `VACUUM` if that check passes (skipped automatically if the check fails, rather than rewriting a database already known to be corrupt)
3. This also runs automatically on a weekly schedule (Sundays) unless disabled under **Settings → Database Maintenance** — always skipped for that week if a scan happens to be running at the time

Requires no scan to be in progress, same as Restore — the dashboard will show an error if you try mid-scan.

## Uninstalling

### Via Jellyfin UI
1. Go to **Dashboard → Plugins → My Plugins**
2. Click on **Media Integrity Scanner**
3. Click **Uninstall**
4. Restart Jellyfin

### Manually
1. Delete the plugin folder:
   ```bash
   rm -rf /var/lib/jellyfin/plugins/MediaIntegrityScanner
   ```
2. Restart Jellyfin

## Troubleshooting

**Plugin doesn't appear after install:**
- Check Jellyfin logs: `journalctl -u jellyfin | grep -i integrity`
- Verify the DLL is in the correct directory
- Ensure file permissions allow the `jellyfin` user to read the files

**"Incompatible plugin" warning:**
- You need Jellyfin 10.11 or newer. Check your version under **Dashboard → General**

**Plugin repository not showing the plugin:**
- Verify the repository URL is exactly: `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json`
- Try removing and re-adding the repository
- Restart Jellyfin and check the catalog again

**Development channel selected but no update ever appears:**
- The Development manifest URL (`.../main/manifest-unstable.json`) needs to be registered as its *own separate* entry under Dashboard → Plugins → Repositories — the stable repository from Method 1 does not cover it. See [Using the Development (Pre-Release) Channel](#using-the-development-pre-release-channel) above.
- After registering it, restart Jellyfin or use the dashboard's **Check for Updates** button — the plugin only checks once automatically per restart, then once daily.
