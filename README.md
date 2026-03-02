# Jellyfin AniWorld Downloader Plugin

A Jellyfin plugin that lets you search and download anime from [aniworld.to](https://aniworld.to) directly within Jellyfin's web UI. Fully integrated — no external tools needed.

## Status: ✅ Working

Downloads are fully functional. Tested with Solo Leveling S01E01-E02 via VOE and Vidmoly providers. Files download to proper Jellyfin-compatible naming (`Series Name/Season XX/Series Name - SXXEXX.mkv`).

## Features

- **🔍 Search** — Find anime on aniworld.to from within Jellyfin
- **📺 Browse** — View series info with covers, genres, seasons, and episode lists
- **⬇️ Single Download** — Download individual episodes with provider/language selection
- **📦 Batch Download** — Download all episodes in a season with one click
- **🎛️ Multiple Providers** — VOE (recommended), Vidmoly, Vidoza
- **🌐 Multiple Languages** — German Dub, English Sub, German Sub
- **📊 Download Manager** — Real-time progress tracking, cancel, clear completed
- **🎬 FFmpeg Integration** — Uses Jellyfin's bundled ffmpeg for HLS→MKV conversion
- **📁 Smart Naming** — Auto-organizes into `Series/Season XX/Series - SXXEXX.mkv`

## Architecture

```
Jellyfin.Plugin.AniWorld/
  Plugin.cs                          # Plugin entry point
  PluginServiceRegistrator.cs        # DI service registration
  Configuration/
    PluginConfiguration.cs           # Settings (download path, language, provider)
  Services/
    AniWorldService.cs               # Core scraper (search, series, episodes, providers)
    DownloadService.cs               # Download manager with ffmpeg
  Extractors/
    IStreamExtractor.cs              # Extractor interface
    VoeExtractor.cs                  # VOE stream URL extraction
    VidozaExtractor.cs               # Vidoza stream URL extraction
    VidmolyExtractor.cs              # Vidmoly stream URL extraction
  Api/
    AniWorldController.cs            # REST API endpoints
  Web/
    aniworld.html                    # Main plugin page (search + downloads)
    aniworld.js                      # Main page JavaScript
    config.html                      # Configuration page
    config.js                        # Configuration JavaScript
```

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/AniWorld/Search?query=` | Search for anime |
| GET | `/AniWorld/Series?url=` | Get series information |
| GET | `/AniWorld/Episodes?url=` | Get episode list for a season |
| GET | `/AniWorld/Episode?url=` | Get episode details with provider links |
| POST | `/AniWorld/Download` | Start a download |
| GET | `/AniWorld/Downloads` | List active downloads |
| GET | `/AniWorld/Downloads/{id}` | Get download status |
| DELETE | `/AniWorld/Downloads/{id}` | Cancel a download |

## How It Works

1. **Search**: Uses aniworld.to's AJAX search API (`POST /ajax/search`)
2. **Scraping**: Parses series/season/episode pages to extract provider links
3. **Provider Resolution**: Follows aniworld.to redirect URLs to provider embed pages
4. **Stream Extraction**: Each provider has a dedicated extractor:
   - **VOE**: Decodes obfuscated JSON (ROT13 + junk removal + base64 + char shift) to get HLS URL
   - **Vidoza**: Extracts MP4 URL from source tags
   - **Vidmoly**: Extracts HLS URL from JavaScript sources
5. **Download**: Uses ffmpeg to download HLS streams, saving as MKV

## Installation

### Manual Installation

1. Build the plugin:
   ```bash
   cd Jellyfin.Plugin.AniWorld
   dotnet build --configuration Release
   ```

2. Copy to Jellyfin plugins directory:
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/AniWorldDownloader
   cp bin/Release/net9.0/Jellyfin.Plugin.AniWorld.dll /var/lib/jellyfin/plugins/AniWorldDownloader/
   cp meta.json /var/lib/jellyfin/plugins/AniWorldDownloader/
   ```

3. Restart Jellyfin:
   ```bash
   sudo systemctl restart jellyfin
   ```

## Configuration

After installation, go to **Dashboard > Plugins > AniWorld Downloader** to configure:

- **Download Path** - Where to save downloaded anime (should be a Jellyfin library path)
- **Preferred Language** - Default language (German Dub / English Sub / German Sub)
- **Preferred Provider** - Default provider (VOE recommended)
- **Naming Template** - File naming pattern with {title}, {year}, {season}, {episode} variables
- **Max Concurrent Downloads** - How many downloads can run simultaneously

## Requirements

- Jellyfin 10.11.x
- .NET 9.0 (for building)
- ffmpeg (bundled with Jellyfin)

## Reference

Inspired by [AniWorld-Downloader](https://github.com/phoenixthrush/AniWorld-Downloader) by phoenixthrush.

## License

MIT
