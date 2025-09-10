using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent; // added
using FellowOakDicom.Network; // DICOM server

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

// DICOM server settings & create server (fo-dicom v5)
var dicomPort = builder.Configuration.GetValue<int>("Dicom:Port", 11112);
var aet = builder.Configuration.GetValue<string>("Dicom:AET", "NEKO_ECG");
var server = DicomServerFactory.Create<EcgDicomService>(dicomPort);

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
startupLogger.LogInformation("Agent starting on {Url} | Mode={Mode} | PdfWatchDir={PdfDir} | WorklistIni={Ini} | DICOM AET={AET} Port={Port}", url, cfg["Storage:Mode"], cfg["PdfWatchDir"], cfg["WorklistIniPath"], aet, dicomPort);

// --- Session-based single-patient workflow ---
var worklistIniPath = cfg["WorklistIniPath"]!;
var defaultTtlMinutes = cfg.GetValue<int?>("Session:TtlMinutes") ?? 15;
var autoClearOnFirstPdf = cfg.GetValue<bool?>("Session:AutoClearOnFirstPdf") ?? true;
var patientSectionName = cfg["Worklist:SectionName"] ?? "PatientData007"; // vendor expects this index

app.MapPost("/session/start", (Demographics d, int? ttlMinutes, bool? force, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Session");
    var ttl = TimeSpan.FromMinutes(ttlMinutes ?? defaultTtlMinutes);
    var (ok, session, error) = EcgAgent.PatientSessionStore.Start(d, worklistIniPath, ttl, force == true, patientSectionName);
    if (ok)
    {
        log.LogInformation("Started session {SessionId} PatientId={Pid} Expires={Exp}", session!.SessionId, d.PatientId, session.ExpiresAt);
        return Results.Ok(new { sessionId = session.SessionId, expiresAt = session.ExpiresAt });
    }
    log.LogWarning("Session start rejected error={Error}", error);
    return Results.Conflict(new { error, active = session?.SessionId });
});

app.MapGet("/session/status", () =>
{
    var s = EcgAgent.PatientSessionStore.GetActive();
    return s == null
        ? Results.Ok(new { active = false })
        : Results.Ok(new
        {
            active = true,
            sessionId = s.SessionId,
            patientId = s.Demographics.PatientId,
            lastName = s.Demographics.LastName,
            firstName = s.Demographics.FirstName,
            expiresAt = s.ExpiresAt,
            cleared = s.Cleared,
            clearReason = s.ClearReason
        });
});

app.MapPost("/session/clear", (string? reason, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Session");
    var (prev, changed) = EcgAgent.PatientSessionStore.Clear(reason ?? "manual");
    if (changed)
        log.LogInformation("Cleared session {SessionId} reason={Reason}", prev!.SessionId, prev.ClearReason);
    return Results.Ok(new { cleared = changed, previous = prev?.SessionId });
});

// Backward compatibility: /demographics delegates to session start (will conflict if active)
app.MapPost("/demographics", (Demographics d) =>
{
    var (ok, session, error) = EcgAgent.PatientSessionStore.Start(d, worklistIniPath, TimeSpan.FromMinutes(defaultTtlMinutes), force: false, patientSectionName);
    return ok
        ? Results.Ok(new { wrote = worklistIniPath, sessionId = session!.SessionId })
        : Results.Conflict(new { error, active = session?.SessionId });
});

// --- MWL: add worklist item ---
app.MapPost("/mwl/items", (Demographics d) =>
{
    // Use provided PatientName if supplied; otherwise construct from Last^First per DICOM PN format
    var patientName = d.PatientName ?? $"{d.LastName}^{d.FirstName}";
    var ds = WorklistStore.FromOrder(d.PatientId, patientName, d.AccessionNumber);
    WorklistStore.Add(ds);
    return Results.Ok(new { added = d.PatientId, ae = aet, port = dicomPort });
});

app.Run();

// --- Models ---
public record Demographics(
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
    string Other,
    string? PatientName = null,
    string? AccessionNumber = null);

// --- Background watcher ---
public class WatcherService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly BlobContainerClient? _blob;
    private readonly ILogger<WatcherService> _log;
    private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _pollTimer;
    private readonly bool _autoClear;

    public WatcherService(IConfiguration cfg, IServiceProvider sp, ILogger<WatcherService> log)
    {
        _cfg = cfg;
        _log = log;
        _autoClear = cfg.GetValue<bool?>("Session:AutoClearOnFirstPdf") ?? true;
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
            // Capture active session (if any) at processing start so we can tag the file
            var session = EcgAgent.PatientSessionStore.GetActive();
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

            // Build destination (optionally patient-prefixed) name
            string originalName = Path.GetFileName(path);
            string destName = originalName;
            if (session != null)
            {
                var safePid = new string(session.Demographics.PatientId.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                destName = $"{safePid}_{stamp}_{originalName}";
            }

            if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
            {
                await _blob!.CreateIfNotExistsAsync(cancellationToken: ct);
                var client = _blob.GetBlobClient(destName);
                await using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await client.UploadAsync(s, overwrite: true, cancellationToken: ct);
                _log.LogInformation("Uploaded {Name} ({Bytes} bytes) to container {Container}", client.Name, new FileInfo(path).Length, _blob.Name);
            }
            else
            {
                var destDir = Path.GetFullPath(_cfg["Storage:LocalIngestDir"]!);
                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, destName);
                File.Copy(path, dest, overwrite: true);
                _log.LogInformation("Copied {Src} -> {Dest}", path, dest);
            }

            if (_autoClear)
            {
                EcgAgent.PatientSessionStore.AutoClearAfterMeasurement();
                _log.LogInformation("Auto-cleared active patient session after PDF {File}", path);
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
