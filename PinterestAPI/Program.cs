using System.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<IVideoProcessor, VideoProcessor>();
builder.Services.Configure<VideoSettings>(builder.Configuration.GetSection("VideoSettings"));

var app = builder.Build();

// Configure static files
var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
Directory.CreateDirectory(videoDir);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(videoDir),
    RequestPath = "",
    ContentTypeProvider = GetContentTypeProvider()
});

// Routes
app.MapGet("/", () => Results.Ok(new
{
    status = "Video Processing API is running",
    endpoints = new[]
    {
        "POST /download?url=...&filename=...",
        "POST /upload?url=...&filename=...&outputDir=...",
        "POST /fetch?url=...&filename=...&outputDir=..."
    }
}));

app.MapPost("/download", async (string url, string? filename, IVideoProcessor processor) =>
{
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    var result = await processor.DownloadVideoAsync(url, filename);
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Error, statusCode: 500);
});

app.MapPost("/upload", async (HttpRequest req, IVideoProcessor processor) =>
{
    var url = req.Query["url"].ToString();
    var filename = req.Query["filename"].ToString();
    var outputDir = req.Query["outputDir"].ToString();

    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    var result = await processor.ConvertToHlsAsync(url, filename, outputDir, req.Scheme, req.Host.ToString());
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Error, statusCode: 500);
});

app.MapPost("/fetch", async (HttpRequest req, IVideoProcessor processor) =>
{
    var url = req.Query["url"].ToString();
    var filename = req.Query["filename"].ToString();
    var outputDir = req.Query["outputDir"].ToString();

    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    var result = await processor.FetchAsHlsAsync(url, filename, outputDir, req.Scheme, req.Host.ToString());
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Error, statusCode: 500);
});

app.Run();

// Helper methods
static FileExtensionContentTypeProvider GetContentTypeProvider()
{
    var provider = new FileExtensionContentTypeProvider();
    provider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
    provider.Mappings[".ts"] = "video/mp2t";
    return provider;
}

// Models and Services
public class VideoSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int HlsSegmentTime { get; set; } = 6;
    public string RootDirectory { get; set; } = "wwwroot";
}

public interface IVideoProcessor
{
    Task<ProcessResult> DownloadVideoAsync(string url, string? filename);
    Task<ProcessResult> ConvertToHlsAsync(string url, string? filename, string? outputDir, string scheme, string host);
    Task<ProcessResult> FetchAsHlsAsync(string url, string? filename, string? outputDir, string scheme, string host);
}

public class VideoProcessor : IVideoProcessor
{
    private readonly VideoSettings _settings;
    private readonly ILogger<VideoProcessor> _logger;

    public VideoProcessor(IOptions<VideoSettings> settings, ILogger<VideoProcessor> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ProcessResult> DownloadVideoAsync(string url, string? filename)
    {
        try
        {
            filename = NormalizeFilename(filename, ".mp4");
            var outputPath = Path.Combine(_settings.RootDirectory, filename);

            var args = $"-i \"{url}\" -y -c copy \"{outputPath}\"";
            var result = await RunFfmpegAsync(args);

            if (result.Success)
            {
                result.Data = new { url = $"/videos/{filename}" };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for URL: {Url}", url);
            return ProcessResult.Failure($"Download failed: {ex.Message}");
        }
    }

    public async Task<ProcessResult> ConvertToHlsAsync(string url, string? filename, string? outputDir, string scheme, string host)
    {
        try
        {
            var (outputPath, segmentPattern, relativePath) = PrepareHlsOutput(filename, outputDir);

            var args = $"-i \"{url}\" -c:v h264 -c:a aac -strict -2 -f hls " +
                      $"-hls_time {_settings.HlsSegmentTime} -hls_playlist_type vod " +
                      $"-hls_segment_filename \"{segmentPattern}\" \"{outputPath}\"";

            var result = await RunFfmpegAsync(args);

            if (result.Success)
            {
                result.Data = new
                {
                    path = $"{scheme}://{host}{relativePath}",
                    folder = Path.GetDirectoryName(outputPath)
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HLS conversion failed for URL: {Url}", url);
            return ProcessResult.Failure($"Conversion failed: {ex.Message}");
        }
    }

    public async Task<ProcessResult> FetchAsHlsAsync(string url, string? filename, string? outputDir, string scheme, string host)
    {
        try
        {
            var (outputPath, segmentPattern, relativePath) = PrepareHlsOutput(filename, outputDir);

            var args = $"-i \"{url}\" -c copy -f hls " +
                      $"-hls_time {_settings.HlsSegmentTime} -hls_playlist_type vod " +
                      $"-hls_segment_filename \"{segmentPattern}\" \"{outputPath}\"";

            var result = await RunFfmpegAsync(args);

            if (result.Success)
            {
                result.Data = new
                {
                    playlist = outputPath,
                    folder = Path.GetDirectoryName(outputPath),
                    url = $"{scheme}://{host}{relativePath}"
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HLS fetch failed for URL: {Url}", url);
            return ProcessResult.Failure($"Fetch failed: {ex.Message}");
        }
    }

    private async Task<ProcessResult> RunFfmpegAsync(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_settings.FfmpegPath, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            _logger.LogInformation("FFmpeg process completed successfully");
            return ProcessResult.Ok();
        }

        _logger.LogError("FFmpeg failed with exit code {ExitCode}: {Error}", process.ExitCode, stderr);
        return ProcessResult.Failure($"FFmpeg failed with exit code {process.ExitCode}");
    }

    private (string outputPath, string segmentPattern, string relativePath) PrepareHlsOutput(string? filename, string? outputDir)
    {
        var name = NormalizeFilename(filename);
        var directory = string.IsNullOrWhiteSpace(outputDir)
            ? Path.Combine(Directory.GetCurrentDirectory(), "videos")
            : Path.Combine(_settings.RootDirectory, outputDir);

        Directory.CreateDirectory(directory);

        var m3u8File = $"{name}.m3u8";
        var outputPath = Path.Combine(directory, m3u8File);
        var segmentPattern = Path.Combine(directory, $"{name}_segment_%03d.ts");
        var relativePath = "/" + Path.Combine(outputDir ?? "", m3u8File).Replace("\\", "/");

        return (outputPath, segmentPattern, relativePath);
    }

    private string NormalizeFilename(string? filename, string extension = "")
    {
        if (string.IsNullOrWhiteSpace(filename))
            return Guid.NewGuid().ToString() + extension;

        var name = Path.GetFileNameWithoutExtension(filename);
        return string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString() : name + extension;
    }
}

public class ProcessResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }

    public static ProcessResult Ok(object? data = null) => new() { Success = true, Data = data };
    public static ProcessResult Failure(string error) => new() { Success = false, Error = error };
}