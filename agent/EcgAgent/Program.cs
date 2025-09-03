using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent; // added

var builder = WebApplication.CreateBuilder(args);

// Enable console logging detail (optional tweak)
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

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
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService();
}

// Optionally bind to a specific URL/port from config
var url = cfg["Http:Url"] ?? "http://localhost:5000";
builder.WebHost.UseUrls(url);

var app = builder.Build();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("Agent starting on {Url} | Mode={Mode} | PdfWatchDir={PdfDir} | WorklistIni={Ini}", url, cfg["Storage:Mode"], cfg["PdfWatchDir"], cfg["WorklistIniPath"]);

// --- Minimal API: POST /demographics -> write PatientFile.ini ---
// NOTE: Replace the INI keys with the vendor-confirmed schema once you have a sample.
var worklistIniPath = cfg["WorklistIniPath"]!;
app.MapPost("/demographics", async (Demographics d, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Demographics");
    log.LogInformation("Received demographics PatientId={PatientId} LastName={LastName} FirstName={FirstName}", d.PatientId, d.LastName, d.FirstName);
    try
    {
        var sb = new StringBuilder()
            .AppendLine("[PatientData001]")
            .AppendLine($"ID={d.PatientId}")
            .AppendLine($"LastName={d.LastName}")
            .AppendLine($"FirstName={d.FirstName}")
            .AppendLine($"BirthDay={d.BirthDay}")
            .AppendLine($"BirthMonth={d.BirthMonth}")
            .AppendLine($"BirthYear={d.BirthYear}")
            .AppendLine($"Sex={d.Sex}")
            .AppendLine($"Weight={d.Weight}")
            .AppendLine($"Height={d.Height}")
            .AppendLine($"Address={d.Address}")
            .AppendLine($"Phone1={d.Phone1}")
            .AppendLine($"Phone2={d.Phone2}")
            .AppendLine($"Fax={d.Fax}")
            .AppendLine($"E-Mail={d.Email}")
            .AppendLine($"Medications={d.Medications}")
            .AppendLine($"Other={d.Other}");

        Directory.CreateDirectory(Path.GetDirectoryName(worklistIniPath)!);
        await File.WriteAllTextAsync(worklistIniPath, sb.ToString(), Encoding.UTF8);
        log.LogInformation("Wrote INI file {Path} (length {Len} bytes)", worklistIniPath, sb.Length);
        return Results.Ok(new { wrote = worklistIniPath });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed writing INI at {Path}", worklistIniPath);
        return Results.Problem($"Failed writing INI: {ex.Message}");
    }
});

app.Run();

// --- Models ---
record Demographics(
    string PatientId,
    string LastName,
    string FirstName,
    int BirthDay,
    int BirthMonth,
    int BirthYear,
    int Sex,
    int Weight,
    int Height,
    string Address,
    string Phone1,
    string Phone2,
    string Fax,
    string Email,
    string Medications,
    string Other);

// --- Background watcher ---
public class WatcherService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly BlobContainerClient? _blob;
    private readonly ILogger<WatcherService> _log;
    private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _pollTimer;

    public WatcherService(IConfiguration cfg, IServiceProvider sp, ILogger<WatcherService> log)
    {
        _cfg = cfg;
        _log = log;
        if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
            _blob = sp.GetService<BlobContainerClient>();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir = _cfg["PdfWatchDir"]!;
        Directory.CreateDirectory(dir);
        _log.LogInformation("Watcher started. Mode={Mode} Watching={Dir}", _cfg["Storage:Mode"], dir);

        var fsw = new FileSystemWatcher(dir)
        {
            Filter = "*.*", // we'll filter ourselves
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        fsw.Created += async (_, e) => await OnEvent("Created", e.FullPath, stoppingToken);
        fsw.Changed += async (_, e) => await OnEvent("Changed", e.FullPath, stoppingToken);
        fsw.Renamed += async (_, e) => await OnEvent("Renamed", e.FullPath, stoppingToken);

        // Poll fallback every 15s to catch missed writes (WSL inotify edge cases)
        _pollTimer = new Timer(async _ => await Poll(dir, stoppingToken), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    private async Task OnEvent(string kind, string path, CancellationToken ct)
    {
        try
        {
            if (!IsPdf(path)) return;
            if (_processed.ContainsKey(path)) return; // already done
            _log.LogDebug("FS event {Kind} for {File}", kind, path);
            await Task.Delay(1500, ct); // debounce & allow write completion
            if (!File.Exists(path)) { _log.LogDebug("Skipped {File} (disappeared)", path); return; }
            if (_processed.ContainsKey(path)) return;
            await ProcessFile(path, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error handling event for {File}", path);
        }
    }

    private async Task Poll(string dir, CancellationToken ct)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf"))
            {
                if (_processed.ContainsKey(f)) continue;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                if (age < TimeSpan.FromSeconds(2)) continue; // still possibly writing
                _log.LogDebug("Poll picked up {File}", f);
                await ProcessFile(f, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Poll error");
        }
    }

    private static bool IsPdf(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private async Task ProcessFile(string path, CancellationToken ct)
    {
        if (!_processed.TryAdd(path, 1)) return;
        _log.LogInformation("Processing PDF {File}", path);
        try
        {
            // Retry open until stable
            const int maxAttempts = 5;
            var lastLen = -1L;
            for (int i = 0; i < maxAttempts; i++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 0 && fi.Length == lastLen) break; // stable size two checks apart
                    lastLen = fi.Length;
                }
                catch { }
                await Task.Delay(400, ct);
            }

            if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
            {
                await _blob!.CreateIfNotExistsAsync(cancellationToken: ct);
                var client = _blob.GetBlobClient(Path.GetFileName(path));
                await using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await client.UploadAsync(s, overwrite: true, cancellationToken: ct);
                _log.LogInformation("Uploaded {Name} ({Bytes} bytes) to container {Container}", client.Name, new FileInfo(path).Length, _blob.Name);
            }
            else
            {
                var destDir = Path.GetFullPath(_cfg["Storage:LocalIngestDir"]!);
                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, Path.GetFileName(path));
                File.Copy(path, dest, overwrite: true);
                _log.LogInformation("Copied {Src} -> {Dest}", path, dest);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed processing {File}", path);
            _processed.TryRemove(path, out _); // allow retry via poll
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _pollTimer?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
