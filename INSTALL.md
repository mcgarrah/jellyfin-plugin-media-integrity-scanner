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
