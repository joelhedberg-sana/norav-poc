var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// optional Azurite client (if you later want to stream from blob)
if ((builder.Configuration["Storage:Mode"] ?? "Local") == "Azurite")
{
    builder.Services.AddSingleton(sp =>
        new Azure.Storage.Blobs.BlobContainerClient(
            builder.Configuration["Storage:AzuriteConnectionString"],
            builder.Configuration["Storage:AzuriteContainer"]));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// endpoints for pure-local folder viewing
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
