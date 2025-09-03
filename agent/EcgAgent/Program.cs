using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
var cfg = builder.Configuration;

// --- Services (DI) ---
builder.Services.AddHostedService<WatcherService>();
builder.Services.AddSingleton<IConfiguration>(cfg);

// Register Blob client only if using Azurite
if ((cfg["Storage:Mode"] ?? "Local") == "Azurite")
{
    builder.Services.AddSingleton(sp =>
        new BlobContainerClient(
            cfg["Storage:AzuriteConnectionString"],
            cfg["Storage:AzuriteContainer"]));
}

// Add Windows Service integration only when running on Windows
// and ONLY if you've installed the WindowsServices package.
if (OperatingSystem.IsWindows())
{
    // Requires: dotnet add package Microsoft.Extensions.Hosting.WindowsServices
    builder.Services.AddWindowsService();
}

// Optionally bind to a specific URL/port from config
var url = cfg["Http:Url"] ?? "http://localhost:5000";
builder.WebHost.UseUrls(url);

var app = builder.Build();

// --- Minimal API: POST /demographics -> write PatientFile.ini ---
// NOTE: Replace the INI keys with the vendor-confirmed schema once you have a sample.
var worklistIniPath = cfg["WorklistIniPath"]!;
app.MapPost("/demographics", async (Demographics d) =>
{
    var sb = new StringBuilder()
        .AppendLine("[Patient]")
        .AppendLine($"PatientID={d.PatientId}")
        .AppendLine($"PatientName={d.PatientName}");
    if (!string.IsNullOrWhiteSpace(d.AccessionNumber))
        sb.AppendLine($"AccessionNumber={d.AccessionNumber}");

    Directory.CreateDirectory(Path.GetDirectoryName(worklistIniPath)!);
    await File.WriteAllTextAsync(worklistIniPath, sb.ToString(), Encoding.UTF8);
    return Results.Ok(new { wrote = worklistIniPath });
});

app.Run();

// --- Models ---
record Demographics(string PatientId, string PatientName, string? AccessionNumber);

// --- Background watcher ---
public class WatcherService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly BlobContainerClient? _blob;

    public WatcherService(IConfiguration cfg, IServiceProvider sp)
    {
        _cfg = cfg;
        if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
            _blob = sp.GetService<BlobContainerClient>();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = _cfg["PdfWatchDir"]!;
        Directory.CreateDirectory(dir);

        var fsw = new FileSystemWatcher(dir, "*.pdf")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        fsw.Created += async (_, e) =>
        {
            try
            {
                // Allow the ECG app to finish writing the file
                await Task.Delay(2000, stoppingToken);

                if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
                {
                    await _blob!.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    var client = _blob.GetBlobClient(Path.GetFileName(e.FullPath));
                    await using var s = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await client.UploadAsync(s, overwrite: true, cancellationToken: stoppingToken);
                }
                else
                {
                    var destDir = Path.GetFullPath(_cfg["Storage:LocalIngestDir"]!);
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, Path.GetFileName(e.FullPath));
                    File.Copy(e.FullPath, dest, overwrite: true);
                }
            }
            catch
            {
                // TODO: add logging
            }
        };

        return Task.CompletedTask;
    }
}
