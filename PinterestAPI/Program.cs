using System.Diagnostics;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
// ?? Use your custom ffmpeg path
var ffmpegPath = builder.Configuration["FfmpegPath"] ?? "ffmpeg";
var videoDir = Path.Combine(Directory.GetCurrentDirectory(), "videos");
Directory.CreateDirectory(videoDir);

// Serve static files -> http://localhost:5000/videos/filename.mp4
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(videoDir),
    RequestPath = "/videos"
});

app.MapGet("/", () => Results.Ok("Video Downloader API is running. Use /download?url=...&filename=... & Use /upload?url=...&filename=..."));
//([FromQuery] string? url, [FromQuery] string? outputDir, [FromQuery] string? filename)
app.MapPost("/fetch", async (HttpRequest req) =>
{
    var url = req.Query["url"].ToString();
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "Missing ?url param" });

    // File name (without extension)
    var name = string.IsNullOrWhiteSpace(req.Query["filename"])
        ? Guid.NewGuid().ToString()
        : Path.GetFileNameWithoutExtension(req.Query["filename"].ToString());

    // Output directory
    var outputDir = req.Query["outputDir"].ToString();
    var rootDir = string.IsNullOrWhiteSpace(outputDir)
        ? Path.Combine(Directory.GetCurrentDirectory(), "videos")
        : Path.GetFullPath(outputDir);

    // Ensure directory exists
    Directory.CreateDirectory(rootDir);

    // Paths for output
    var outputM3u8 = Path.Combine(rootDir, $"{name}.m3u8");
    var segmentPattern = Path.Combine(rootDir, "segment_%03d.ts");

    // FFmpeg command
    var arguments =
        $"-i \"{url}\" -c copy -f hls -hls_time 6 -hls_playlist_type vod " +
        $"-hls_segment_filename \"{segmentPattern}\" \"{outputM3u8}\"";

    var startInfo = new ProcessStartInfo(ffmpegPath, arguments)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    try
    {
        using var ffmpeg = Process.Start(startInfo)!;

        string stderr = await ffmpeg.StandardError.ReadToEndAsync();
        string stdout = await ffmpeg.StandardOutput.ReadToEndAsync();

        await ffmpeg.WaitForExitAsync();

        if (ffmpeg.ExitCode == 0)
        {
            return Results.Ok(new
            {
                success = true,
                playlist = outputM3u8,
                folder = rootDir
            });
        }
        else
        {
            return Results.Problem($"FFmpeg failed with exit code {ffmpeg.ExitCode}", stderr);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Exception: {ex.Message}", ex.ToString());
    }
});


// API: POST /download?url=...&filename=... download?url=https://v1.pinimg.com/videos/iht/hls/0c/c1/e7/0cc1e72b25279e82237a29bcb5ff6232.m3u8&filename=newvideo123.mp4
app.MapPost("/download", async (HttpRequest req) =>
{
    try
    {
        var url = req.Query["url"].ToString();
        var filename = string.IsNullOrWhiteSpace(req.Query["filename"])
            ? $"{Guid.NewGuid()}.mp4"
            : req.Query["filename"].ToString();

        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "Missing ?url param" });

        var output = Path.Combine(videoDir, filename);

        var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, $"-i \"{url}\" -y -c copy \"{output}\"")
            {
                RedirectStandardError = true, // capture FFmpeg logs
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        ffmpeg.Start();

        // capture logs
        var stderr = await ffmpeg.StandardError.ReadToEndAsync();
        var stdout = await ffmpeg.StandardOutput.ReadToEndAsync();

        await ffmpeg.WaitForExitAsync();

        if (ffmpeg.ExitCode == 0)
        {
            var fileUrl = $"http://{req.Host}/videos/{filename}";
            return Results.Ok(new { success = true, url = fileUrl });
        }
        else
        {
            return Results.Problem($"FFmpeg failed with exit code {ffmpeg.ExitCode}",  stderr);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Exception: {ex.Message}", ex.ToString());
    }
});

// Upload ->/upload?url=C:\Users\rafay\Downloads\response.mp4&filename=robot&outputDir=C:\Users\rafay\Documents\Projects\personal\pinter\test\nwe\sam2
app.MapPost("/upload", async (HttpRequest req) =>
{
    try
    {
        var url = req.Query["url"].ToString();
        var name = string.IsNullOrWhiteSpace(req.Query["filename"])
            ? Guid.NewGuid().ToString()
            : Path.GetFileNameWithoutExtension(req.Query["filename"].ToString());

        var customOutputDir = req.Query["outputDir"].ToString();
        var rootDir = string.IsNullOrWhiteSpace(customOutputDir)
            ? videoDir
            : Path.GetFullPath(customOutputDir);

        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "Missing ?url param" });

        // Folder where this video will be saved
        var outputFolder = Path.Combine(rootDir);
        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);
        var outputM3U8 = Path.Combine(outputFolder, $"{name}.m3u8");

        // FFmpeg command: MP4 -> HLS
        var arguments =
            $"-i \"{url}\" -c:v h264 -c:a aac -strict -2 -f hls " +
            $"-hls_time 6 -hls_playlist_type vod " +
            $"-hls_segment_filename \"{Path.Combine(outputFolder, "segment_%03d.ts")}\" " +
            $"\"{outputM3U8}\"";

        var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        ffmpeg.Start();

        var stderr = await ffmpeg.StandardError.ReadToEndAsync();
        var stdout = await ffmpeg.StandardOutput.ReadToEndAsync();

        await ffmpeg.WaitForExitAsync();

        if (ffmpeg.ExitCode == 0)
        {
            return Results.Ok(new
            {
                success = true,
                path = outputM3U8,
                folder = outputFolder
            });
        }
        else
        {
            return Results.Problem($"FFmpeg failed with exit code {ffmpeg.ExitCode}", stderr);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem($"Exception: {ex.Message}", ex.ToString());
    }
});

app.Run();
