using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniWorld.Extractors;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Manages downloads from aniworld.to using ffmpeg.
/// </summary>
public class DownloadService
{
    private readonly AniWorldService _aniWorldService;
    private readonly IEnumerable<IStreamExtractor> _extractors;
    private readonly ILogger<DownloadService> _logger;
    private readonly ConcurrentDictionary<string, DownloadTask> _activeTasks = new();
    private readonly SemaphoreSlim _downloadSemaphore;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    /// <param name="aniWorldService">AniWorld service.</param>
    /// <param name="extractors">Available stream extractors.</param>
    /// <param name="logger">Logger instance.</param>
    public DownloadService(
        AniWorldService aniWorldService,
        IEnumerable<IStreamExtractor> extractors,
        ILogger<DownloadService> logger)
    {
        _aniWorldService = aniWorldService;
        _extractors = extractors;
        _logger = logger;

        var maxDownloads = Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 2;
        _downloadSemaphore = new SemaphoreSlim(maxDownloads, maxDownloads);
    }

    /// <summary>
    /// Gets all active download tasks.
    /// </summary>
    /// <returns>List of active downloads.</returns>
    public List<DownloadTask> GetActiveDownloads()
    {
        return _activeTasks.Values.ToList();
    }

    /// <summary>
    /// Gets a specific download task by ID.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <returns>The download task, or null.</returns>
    public DownloadTask? GetDownload(string taskId)
    {
        _activeTasks.TryGetValue(taskId, out var task);
        return task;
    }

    /// <summary>
    /// Starts a download for an episode.
    /// </summary>
    /// <param name="episodeUrl">The aniworld.to episode URL.</param>
    /// <param name="languageKey">Language key (1, 2, or 3).</param>
    /// <param name="provider">Provider name (VOE, Vidoza, Vidmoly).</param>
    /// <param name="outputPath">The output file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The download task ID.</returns>
    public Task<string> StartDownloadAsync(
        string episodeUrl,
        string languageKey,
        string provider,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid().ToString("N")[..12];
        var task = new DownloadTask
        {
            Id = taskId,
            EpisodeUrl = episodeUrl,
            Provider = provider,
            Language = languageKey,
            OutputPath = outputPath,
            Status = DownloadStatus.Queued,
            StartedAt = DateTime.UtcNow,
        };

        _activeTasks[taskId] = task;

        // Run in background
        _ = Task.Run(async () => await ExecuteDownloadAsync(task, cancellationToken).ConfigureAwait(false), cancellationToken);

        return Task.FromResult(taskId);
    }

    /// <summary>
    /// Cancels a download.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <returns>True if cancelled.</returns>
    public bool CancelDownload(string taskId)
    {
        if (_activeTasks.TryGetValue(taskId, out var task))
        {
            task.CancellationSource?.Cancel();
            task.Status = DownloadStatus.Cancelled;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a completed/failed/cancelled download from the list.
    /// </summary>
    /// <param name="taskId">The task ID.</param>
    /// <returns>True if removed.</returns>
    public bool RemoveDownload(string taskId)
    {
        if (_activeTasks.TryRemove(taskId, out var task))
        {
            if (task.Status == DownloadStatus.Downloading)
            {
                task.CancellationSource?.Cancel();
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears all completed, failed, and cancelled downloads from the list.
    /// </summary>
    /// <returns>Number of cleared tasks.</returns>
    public int ClearCompleted()
    {
        var toRemove = _activeTasks.Values
            .Where(t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            .Select(t => t.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            _activeTasks.TryRemove(id, out _);
        }

        return toRemove.Count;
    }

    private async Task ExecuteDownloadAsync(DownloadTask task, CancellationToken externalToken)
    {
        task.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = task.CancellationSource.Token;

        try
        {
            await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
            task.Status = DownloadStatus.Resolving;

            // 1. Get episode details
            var details = await _aniWorldService.GetEpisodeDetailsAsync(task.EpisodeUrl, token).ConfigureAwait(false);
            task.EpisodeTitle = details.TitleEn ?? details.TitleDe ?? "Unknown";

            if (!details.ProvidersByLanguage.TryGetValue(task.Language, out var providers) ||
                !providers.TryGetValue(task.Provider, out var redirectUrl))
            {
                task.Status = DownloadStatus.Failed;
                task.Error = $"Provider {task.Provider} not available for language key {task.Language}";
                return;
            }

            // 2. Resolve redirect to provider embed URL
            var embedUrl = await _aniWorldService.ResolveRedirectAsync(redirectUrl, token).ConfigureAwait(false);
            _logger.LogInformation("Resolved to embed URL: {EmbedUrl}", embedUrl);

            // 3. Extract direct stream URL
            var extractor = _extractors.FirstOrDefault(e =>
                e.ProviderName.Equals(task.Provider, StringComparison.OrdinalIgnoreCase));

            if (extractor == null)
            {
                task.Status = DownloadStatus.Failed;
                task.Error = $"No extractor available for provider: {task.Provider}";
                return;
            }

            task.Status = DownloadStatus.Extracting;
            var streamUrl = await extractor.GetDirectLinkAsync(embedUrl, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(streamUrl))
            {
                task.Status = DownloadStatus.Failed;
                task.Error = "Failed to extract stream URL from provider";
                return;
            }

            _logger.LogInformation("Stream URL: {StreamUrl}", streamUrl);

            // 4. Download with ffmpeg
            task.Status = DownloadStatus.Downloading;
            task.StreamUrl = streamUrl;

            // Ensure output directory exists
            var dir = Path.GetDirectoryName(task.OutputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await DownloadWithFfmpegAsync(task, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                return;
            }

            task.Status = DownloadStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.Progress = 100;
            _logger.LogInformation("Download completed: {Path}", task.OutputPath);
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {Url}", task.EpisodeUrl);
            task.Status = DownloadStatus.Failed;
            task.Error = ex.Message;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadWithFfmpegAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        var ffmpegPath = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new InvalidOperationException("ffmpeg not found. Please ensure ffmpeg is installed.");
        }

        // Build ffmpeg command for HLS download
        var args = $"-i \"{task.StreamUrl}\" -c copy -bsf:a aac_adtstoasc -y \"{task.OutputPath}\"";

        _logger.LogDebug("Running: {Ffmpeg} {Args}", ffmpegPath, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        // Parse ffmpeg progress from stderr
        var progressPattern = new Regex(@"time=(?<time>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        var durationPattern = new Regex(@"Duration:\s*(?<dur>\d+:\d+:\d+\.\d+)", RegexOptions.Compiled);
        TimeSpan? totalDuration = null;

        _ = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) continue;

                if (totalDuration == null)
                {
                    var durMatch = durationPattern.Match(line);
                    if (durMatch.Success && TimeSpan.TryParse(durMatch.Groups["dur"].Value, out var dur))
                    {
                        totalDuration = dur;
                    }
                }

                var timeMatch = progressPattern.Match(line);
                if (timeMatch.Success && TimeSpan.TryParse(timeMatch.Groups["time"].Value, out var currentTime))
                {
                    if (totalDuration.HasValue && totalDuration.Value.TotalSeconds > 0)
                    {
                        task.Progress = (int)(currentTime.TotalSeconds / totalDuration.Value.TotalSeconds * 100);
                    }
                }
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }
    }

    private static string? FindFfmpeg()
    {
        // Check common locations
        var paths = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "ffmpeg",
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Try PATH
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (process != null)
            {
                var result = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    return result;
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }
}

/// <summary>
/// Represents an active download task.
/// </summary>
public class DownloadTask
{
    /// <summary>Gets or sets the task ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode URL.</summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode title.</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the output file path.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream URL.</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets the download status.</summary>
    public DownloadStatus Status { get; set; }

    /// <summary>Gets or sets the progress (0-100).</summary>
    public int Progress { get; set; }

    /// <summary>Gets or sets error message if failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the started timestamp.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the completed timestamp.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the cancellation token source.</summary>
    [JsonIgnore]
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Download status enum.
/// </summary>
public enum DownloadStatus
{
    /// <summary>Queued for download.</summary>
    Queued,

    /// <summary>Resolving provider links.</summary>
    Resolving,

    /// <summary>Extracting stream URL.</summary>
    Extracting,

    /// <summary>Downloading with ffmpeg.</summary>
    Downloading,

    /// <summary>Download completed.</summary>
    Completed,

    /// <summary>Download failed.</summary>
    Failed,

    /// <summary>Download cancelled.</summary>
    Cancelled,
}
