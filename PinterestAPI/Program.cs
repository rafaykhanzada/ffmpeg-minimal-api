using System.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PinterestAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
// Add services
builder.Services.AddSingleton<IimageProcessor, ImageProcessor>();
builder.Services.AddSingleton<IVideoProcessor, VideoProcessor>();
builder.Services.Configure<VideoSettings>(builder.Configuration.GetSection("VideoSettings"));
builder.Services.Configure<ImageSettings>(builder.Configuration.GetSection("ImageSettings"));

// Add Antiforgery - .NET 8+ only
builder.Services.AddAntiforgery();  // <-- register antiforgery services

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
var app = builder.Build();

app.UseCors("AllowAll");
// Configure static files
var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
Directory.CreateDirectory(videoDir);

app.UseStaticFiles(
    new StaticFileOptions
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
        "POST /image/upload?path=...",
        "POST /image/upload/base64?filename=...&outputDir=...",
        "POST /video/download?url=...&filename=...",
        "POST /video/upload?url=...&filename=...&outputDir=...",
        "POST /video/fetch?url=...&filename=...&outputDir=..."
    }
}));

app.MapPost("/image/upload", async (HttpRequest req, IimageProcessor processor) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file"); // name must match the multipart field
    var fileName = req.Query["fileName"].ToString();
    var path = req.Query["path"].ToString();

    if (file == null) return Results.BadRequest(new { error = "file is required" });

    processor.Host = $"{req.Scheme}://{req.Host}/";
    var result = await processor.UploadImageAsync(file, fileName, path);
    return result.Success ? Results.Ok(result) : Results.Problem(result.Message,null, 500);
}).DisableAntiforgery(); // opt out for just this endpoint;

app.MapPost("/video/download", async (string url, string? filename, IVideoProcessor processor) =>
{
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    var result = await processor.DownloadVideoAsync(url, filename);
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Message, statusCode: 500);
});

app.MapPost("/video/upload", async (HttpRequest req, IVideoProcessor processor) =>
{
    var url = req.Query["url"].ToString();
    var filename = req.Query["filename"].ToString();
    var outputDir = videoDir+req.Query["outputDir"].ToString();
    var outputDir2 = req.Query["outputDir"].ToString();
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    if (!Directory.Exists(outputDir))
        Directory.CreateDirectory(outputDir);
    var result = await processor.ConvertToHlsAsync(url, filename, outputDir2, req.Scheme, req.Host.ToString());
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Message, statusCode: 500);
});

app.MapPost("/video/fetch", async (HttpRequest req, IVideoProcessor processor) =>
{
    var url = req.Query["url"].ToString();
    var filename = req.Query["filename"].ToString();
    var outputDir = req.Query["outputDir"].ToString();

    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required" });

    var result = await processor.FetchAsHlsAsync(url, filename, outputDir, req.Scheme, req.Host.ToString());
    return result.Success
        ? Results.Ok(result)
        : Results.Problem(result.Message, statusCode: 500);
});

app.Run();

#region VideoSettings
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
    Task<ResultModel> DownloadVideoAsync(string url, string? filename);
    Task<ResultModel> ConvertToHlsAsync(string url, string? filename, string? outputDir, string scheme, string host);
    Task<ResultModel> FetchAsHlsAsync(string url, string? filename, string? outputDir, string scheme, string host);
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

    public async Task<ResultModel> DownloadVideoAsync(string url, string? filename)
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
            return ResultModel.Failure($"Download failed: {ex.Message}");
        }
    }

    public async Task<ResultModel> ConvertToHlsAsync(string url, string? filename, string? outputDir, string scheme, string host)
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
            return ResultModel.Failure($"Conversion failed: {ex.Message}");
        }
    }

    public async Task<ResultModel> FetchAsHlsAsync(string url, string? filename, string? outputDir, string scheme, string host)
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
            return ResultModel.Failure($"Fetch failed: {ex.Message}");
        }
    }

    private async Task<ResultModel> RunFfmpegAsync(string arguments)
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
            return ResultModel.Ok();
        }

        _logger.LogError("FFmpeg failed with exit code {ExitCode}: {Error}", process.ExitCode, stderr);
        return ResultModel.Failure($"FFmpeg failed with exit code {process.ExitCode}");
    }

    private (string outputPath, string segmentPattern, string relativePath) PrepareHlsOutput(string? filename, string? outputDir)
    {
        var name = NormalizeFilename(filename);

        var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var directory = videoDir + outputDir;

        if (!Directory.Exists(directory))
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

#endregion

#region ImageSettings
// Helper methods

public class ImageSettings
{
    public string RootDirectory { get; set; } = "wwwroot";
}
public interface IimageProcessor
{
    public string Host { get; set; }
    Task<ResultModel> UploadImageAsync(IFormFile file, string? fileName = "", string? formPath = "");
}

public class ImageProcessor : IimageProcessor
{
    public string Host { get; set; }
    private readonly ImageSettings _settings;
    private readonly ILogger<ImageProcessor> _logger;
    private readonly IWebHostEnvironment _env;

    public ImageProcessor(IOptions<ImageSettings> settings, ILogger<ImageProcessor> logger, IWebHostEnvironment env)
    {
        _settings = settings.Value;
        _logger = logger;
        _env = env;
    }

    public async Task<ResultModel> UploadImageAsync(IFormFile file, string? fileName = null, string? formPath = "/default")
    {
        try
        {
            if (file == null) return ResultModel.Failure("No file provided");
            if (!string.IsNullOrEmpty(fileName))
                fileName = $"{DateTime.Now:yyMMddHHmmssfff}";
            formPath ??= "";
            formPath = formPath.Trim().Replace('/', Path.DirectorySeparatorChar);

            var wwwRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var ext = Path.GetExtension(file.FileName);
            fileName = fileName + ext;
            var dir = wwwRoot + formPath;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var path = dir + fileName;
            await using (var fs = new FileStream(path, FileMode.Create))
                await file.CopyToAsync(fs);

            var relativePath = Path.Combine(formPath, fileName).Replace("\\", "/").TrimStart('/');
            var result = ResultModel.Ok();
            result.Data = Host+relativePath;
            result.Success = true;
            result.Message = "File uploaded successfully";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image upload failed");
            return ResultModel.Failure($"Upload failed: {ex.Message}");
        }
    }
}
#endregion