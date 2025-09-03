using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddHttpClient();

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

// Minimal API endpoints for PDF listing
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

app.Run();
