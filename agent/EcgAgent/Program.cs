using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// config & DI
var cfg = builder.Configuration;
builder.Services.AddWindowsService(); // allows Windows Service hosting later
builder.Services.AddHostedService<WatcherService>();
builder.Services.AddSingleton<IConfiguration>(cfg);

// If Storage:Mode == Azurite, register Blob client
if ((cfg["Storage:Mode"] ?? "Local") == "Azurite")
{
    builder.Services.AddSingleton(sp =>
        new BlobContainerClient(
            cfg["Storage:AzuriteConnectionString"],
            cfg["Storage:AzuriteContainer"]));
}

var app = builder.Build();

// --- Minimal API: POST /demographics -> write PatientFile.ini ---
// NOTE: Replace keys after you capture a sample INI from the ECG app.
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

var url = cfg["Http:Url"] ?? "http://localhost:5000";
app.Urls.Clear();
app.Urls.Add(url);

await app.RunAsync();

// --- models ---
record Demographics(string PatientId, string PatientName, string? AccessionNumber);

// --- background watcher ---
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
                // wait for ECG app to finish writing PDF
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
                // TODO: add logging for production
            }
        };

        return Task.CompletedTask;
    }
}
