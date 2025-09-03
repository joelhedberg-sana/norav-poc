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

if ((builder.Configuration["Storage:Mode"] ?? "Local") == "Azurite")
{
    builder.Services.AddSingleton(sp =>
        new BlobContainerClient(
            builder.Configuration["Storage:AzuriteConnectionString"],
            builder.Configuration["Storage:AzuriteContainer"]));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

var storageMode = builder.Configuration["Storage:Mode"] ?? "Local";
if (storageMode == "Azurite")
{
    // Blob listing endpoint
    app.MapGet("/api/reports", async (BlobContainerClient container) =>
    {
        await container.CreateIfNotExistsAsync();
        var names = new List<string>();
        await foreach (BlobItem b in container.GetBlobsAsync(prefix: null))
        {
            if (b.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                names.Add(b.Name);
        }
        return names.OrderByDescending(n => n);
    });

    // Blob download endpoint
    app.MapGet("/api/reports/{name}", async (string name, BlobContainerClient container) =>
    {
        var client = container.GetBlobClient(name);
        if (!await client.ExistsAsync()) return Results.NotFound();
        var dl = await client.DownloadStreamingAsync();
        return Results.Stream(dl.Value.Content, contentType: "application/pdf", fileDownloadName: name, enableRangeProcessing: true);
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
