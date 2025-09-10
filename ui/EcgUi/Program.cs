using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Azure.Storage.Blobs.Models;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// HttpClient with BaseAddress from NavigationManager
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// Determine storage mode with a fast TCP connectivity check (no SDK retries)
var requestedMode = builder.Configuration["Storage:Mode"] ?? "Local";
var storageMode = requestedMode;
if (requestedMode == "Azurite")
{
    bool reachable = false;
    string host = "127.0.0.1";
    int port = 10000;
    var connStr = builder.Configuration["Storage:AzuriteConnectionString"] ?? string.Empty;
    // Rough parse: if the connection string has BlobEndpoint=... extract host:port
    var blobEndpointToken = connStr.Split(';').FirstOrDefault(s => s.StartsWith("BlobEndpoint=", StringComparison.OrdinalIgnoreCase));
    if (blobEndpointToken != null)
    {
        try
        {
            var uri = new Uri(blobEndpointToken.Substring("BlobEndpoint=".Length));
            host = uri.Host;
            port = uri.Port;
        }
        catch { }
    }
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        using var client = new System.Net.Sockets.TcpClient();
        var connectTask = client.ConnectAsync(host, port);
        var completed = Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token)).Result;
        reachable = connectTask.IsCompletedSuccessfully && client.Connected;
    }
    catch { reachable = false; }

    if (reachable)
    {
        var containerName = builder.Configuration["Storage:AzuriteContainer"];
        builder.Services.AddSingleton(sp => new BlobContainerClient(connStr, containerName));
        Console.WriteLine($"INFO: Azurite reachable at {host}:{port}. Using Azurite storage mode.");
    }
    else
    {
        Console.WriteLine($"WARN: Azurite not reachable at {host}:{port}. Falling back to Local storage mode.");
        storageMode = "Local";
    }
}

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

if (storageMode == "Azurite")
{
    // Blob listing endpoint with resilience
    app.MapGet("/api/reports", async (BlobContainerClient container, ILoggerFactory lf) =>
    {
        var log = lf.CreateLogger("Reports");
        try
        {
            await container.CreateIfNotExistsAsync();
            var names = new List<string>();
            await foreach (BlobItem b in container.GetBlobsAsync(prefix: null))
            {
                if (b.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    names.Add(b.Name);
            }
            return Results.Ok(names.OrderByDescending(n => n));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed listing blob reports (returning empty)");
            return Results.Ok(Array.Empty<string>());
        }
    });

    // Blob download endpoint with resilience
    app.MapGet("/api/reports/{name}", async (string name, BlobContainerClient container, ILoggerFactory lf) =>
    {
        var log = lf.CreateLogger("Reports");
        try
        {
            var client = container.GetBlobClient(name);
            if (!await client.ExistsAsync()) return Results.NotFound();
            var dl = await client.DownloadStreamingAsync();
            return Results.Stream(dl.Value.Content, contentType: "application/pdf", fileDownloadName: name, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed downloading report {Name}", name);
            return Results.NotFound();
        }
    });
}
else
{
    // Local file system endpoints (existing behavior)
    var ingestDir = builder.Configuration["Storage:LocalIngestDir"] ?? "../localdata/ingest";
    Directory.CreateDirectory(ingestDir);
    app.MapGet("/api/reports", () =>
    {
        var files = Directory.GetFiles(ingestDir, "*.pdf");
        return files.Select(Path.GetFileName).OrderByDescending(x => x);
    });
    app.MapGet("/api/reports/{name}", (string name) =>
    {
        var path = Path.Combine(ingestDir, name);
        return File.Exists(path)
            ? Results.File(path, "application/pdf", enableRangeProcessing: true)
            : Results.NotFound();
    });
}

app.Run();
