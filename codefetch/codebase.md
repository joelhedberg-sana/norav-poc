ecg-stage1-poc.sln
```
1 | Microsoft Visual Studio Solution File, Format Version 12.00
2 | # Visual Studio Version 17
3 | VisualStudioVersion = 17.5.2.0
4 | MinimumVisualStudioVersion = 10.0.40219.1
5 | Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "agent", "agent", "{A0596E84-2522-6F31-0F43-77167BFA80D0}"
6 | EndProject
7 | Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "EcgAgent", "agent\EcgAgent\EcgAgent.csproj", "{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6}"
8 | EndProject
9 | Global
10 | 	GlobalSection(SolutionConfigurationPlatforms) = preSolution
11 | 		Debug|Any CPU = Debug|Any CPU
12 | 		Release|Any CPU = Release|Any CPU
13 | 	EndGlobalSection
14 | 	GlobalSection(ProjectConfigurationPlatforms) = postSolution
15 | 		{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
16 | 		{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6}.Debug|Any CPU.Build.0 = Debug|Any CPU
17 | 		{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6}.Release|Any CPU.ActiveCfg = Release|Any CPU
18 | 		{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6}.Release|Any CPU.Build.0 = Release|Any CPU
19 | 	EndGlobalSection
20 | 	GlobalSection(SolutionProperties) = preSolution
21 | 		HideSolutionNode = FALSE
22 | 	EndGlobalSection
23 | 	GlobalSection(NestedProjects) = preSolution
24 | 		{FB57F5AC-AD5D-8D8E-D2C3-F92DB84BABA6} = {A0596E84-2522-6F31-0F43-77167BFA80D0}
25 | 	EndGlobalSection
26 | 	GlobalSection(ExtensibilityGlobals) = postSolution
27 | 		SolutionGuid = {10B2D685-46C7-4BE1-9085-73115BAC7546}
28 | 	EndGlobalSection
29 | EndGlobal
```

agent/EcgAgent/DicomServices.cs
```
1 | using FellowOakDicom;
2 | using FellowOakDicom.Network;
3 | using FellowOakDicom.Network.Client; // not strictly needed here
4 | using Microsoft.Extensions.Logging;
5 | using System.Text; // needed for Encoding
6 | 
7 | public class EcgDicomService :
8 |     DicomService,
9 |     IDicomServiceProvider,
10 |     IDicomCEchoProvider,
11 |     IDicomCFindProvider,
12 |     IDicomCStoreProvider
13 | {
14 |     private readonly ILogger<EcgDicomService> _log;
15 |     private static readonly HashSet<DicomUID> _acceptedStorage = new()
16 |     {
17 |         DicomUID.EncapsulatedPDFStorage,
18 |         DicomUID.TwelveLeadECGWaveformStorage,
19 |         DicomUID.SecondaryCaptureImageStorage
20 |     };
21 | 
22 |     public EcgDicomService(INetworkStream stream, Encoding fallback, ILogger log,
23 |                            DicomServiceDependencies deps)
24 |         : base(stream, fallback, log, deps)
25 |     {
26 |         _log = deps.LoggerFactory.CreateLogger<EcgDicomService>();
27 |     }
28 | 
29 |     // --- Association negotiation (v5 async interface) ---
30 |     public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
31 |     {
32 |         foreach (var pc in association.PresentationContexts)
33 |         {
34 |             if (pc.AbstractSyntax == DicomUID.Verification ||
35 |                 pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
36 |                 _acceptedStorage.Contains(pc.AbstractSyntax))
37 |             {
38 |                 pc.SetResult(DicomPresentationContextResult.Accept);
39 |             }
40 |             else
41 |             {
42 |                 pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
43 |             }
44 |         }
45 |         await SendAssociationAcceptAsync(association);
46 |     }
47 | 
48 |     public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
49 |     public void OnConnectionClosed(Exception exception) { }
50 |     public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
51 | 
52 |     // --- C-ECHO ---
53 |     public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest req)
54 |         => Task.FromResult(new DicomCEchoResponse(req, DicomStatus.Success));
55 | 
56 |     // --- MWL C-FIND ---
57 |     public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
58 |     {
59 |         foreach (var match in WorklistStore.Query(request.Dataset))
60 |         {
61 |             yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = match };
62 |         }
63 |         yield return new DicomCFindResponse(request, DicomStatus.Success);
64 |         await Task.CompletedTask;
65 |     }
66 | 
67 |     // --- C-STORE ---
68 |     public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest req)
69 |     {
70 |         try
71 |         {
72 |             var file = GetCStoreDicomFile(); // parameterless protected method returns DicomFile
73 |             var studyUid = file.Dataset.GetString(DicomTag.StudyInstanceUID);
74 |             var seriesUid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
75 |             var sopUid = file.Dataset.GetString(DicomTag.SOPInstanceUID);
76 | 
77 |             var root = Path.Combine("C:\\ecg-poc\\dicom-in");
78 |             Directory.CreateDirectory(root);
79 |             var dcmPath = Path.Combine(root, $"{studyUid}_{seriesUid}_{sopUid}.dcm");
80 |             file.Save(dcmPath);
81 | 
82 |             if (req.SOPClassUID == DicomUID.EncapsulatedPDFStorage)
83 |             {
84 |                 var pdfBytes = file.Dataset.GetValue<byte[]>(DicomTag.EncapsulatedDocument, 0);
85 |                 var pdfName = $"{studyUid}_{seriesUid}_{sopUid}.pdf";
86 |                 var pdfOut = Path.Combine("C:\\ecg-poc\\ingest", pdfName);
87 |                 Directory.CreateDirectory(Path.GetDirectoryName(pdfOut)!);
88 |                 File.WriteAllBytes(pdfOut, pdfBytes);
89 |             }
90 | 
91 |             return Task.FromResult(new DicomCStoreResponse(req, DicomStatus.Success));
92 |         }
93 |         catch (Exception ex)
94 |         {
95 |             _log?.LogError(ex, "C-STORE failed");
96 |             return Task.FromResult(new DicomCStoreResponse(req, DicomStatus.ProcessingFailure));
97 |         }
98 |     }
99 | 
100 |     public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
101 |         => Task.CompletedTask;
102 | }
```

agent/EcgAgent/EcgAgent.csproj
```
1 | <Project Sdk="Microsoft.NET.Sdk.Web">
2 | 
3 |   <PropertyGroup>
4 |     <TargetFramework>net9.0</TargetFramework>
5 |     <Nullable>enable</Nullable>
6 |     <ImplicitUsings>enable</ImplicitUsings>
7 |     <UserSecretsId>dotnet-EcgAgent-64edfb93-318b-4b30-b23a-0172868389f3</UserSecretsId>
8 |   </PropertyGroup>
9 | 
10 |   <ItemGroup>
11 |     <PackageReference Include="Azure.Storage.Blobs" Version="12.25.0" />
12 |     <PackageReference Include="fo-dicom" Version="5.2.2" />
13 |     <!-- Hosting comes from Microsoft.AspNetCore.App with Web SDK; keep WindowsServices for service integration -->
14 |     <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="9.0.8" />
15 |     <PackageReference Include="Microsoft.Extensions.Logging" Version="9.0.8" />
16 |   </ItemGroup>
17 | </Project>
```

agent/EcgAgent/Program.cs
```
1 | using System.Text;
2 | using Azure.Storage.Blobs;
3 | using Microsoft.Extensions.Hosting;
4 | using System.Collections.Concurrent; // added
5 | using FellowOakDicom.Network; // DICOM server
6 | 
7 | var builder = WebApplication.CreateBuilder(args);
8 | 
9 | // Enable console logging detail (optional tweak)
10 | builder.Logging.ClearProviders();
11 | builder.Logging.AddSimpleConsole(o =>
12 | {
13 |     o.SingleLine = true;
14 |     o.TimestampFormat = "HH:mm:ss.fff ";
15 | });
16 | 
17 | // --- Configuration ---
18 | var cfg = builder.Configuration;
19 | 
20 | // DICOM server settings & create server (fo-dicom v5)
21 | var dicomPort = builder.Configuration.GetValue<int>("Dicom:Port", 11112);
22 | var aet = builder.Configuration.GetValue<string>("Dicom:AET", "NEKO_ECG");
23 | var server = DicomServerFactory.Create<EcgDicomService>(dicomPort);
24 | 
25 | // --- Services (DI) ---
26 | builder.Services.AddHostedService<WatcherService>();
27 | builder.Services.AddSingleton<IConfiguration>(cfg);
28 | 
29 | // Register Blob client only if using Azurite
30 | if ((cfg["Storage:Mode"] ?? "Local") == "Azurite")
31 | {
32 |     builder.Services.AddSingleton(sp =>
33 |         new BlobContainerClient(
34 |             cfg["Storage:AzuriteConnectionString"],
35 |             cfg["Storage:AzuriteContainer"]));
36 | }
37 | 
38 | // Add Windows Service integration only when running on Windows
39 | if (OperatingSystem.IsWindows())
40 | {
41 |     builder.Services.AddWindowsService();
42 | }
43 | 
44 | // Optionally bind to a specific URL/port from config
45 | var url = cfg["Http:Url"] ?? "http://localhost:5000";
46 | builder.WebHost.UseUrls(url);
47 | 
48 | var app = builder.Build();
49 | var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
50 | startupLogger.LogInformation("Agent starting on {Url} | Mode={Mode} | PdfWatchDir={PdfDir} | WorklistIni={Ini} | DICOM AET={AET} Port={Port}", url, cfg["Storage:Mode"], cfg["PdfWatchDir"], cfg["WorklistIniPath"], aet, dicomPort);
51 | 
52 | // --- Minimal API: POST /demographics -> write PatientFile.ini ---
53 | // NOTE: Replace the INI keys with the vendor-confirmed schema once you have a sample.
54 | var worklistIniPath = cfg["WorklistIniPath"]!;
55 | app.MapPost("/demographics", async (Demographics d, ILoggerFactory lf) =>
56 | {
57 |     var log = lf.CreateLogger("Demographics");
58 |     log.LogInformation("Received demographics PatientId={PatientId} LastName={LastName} FirstName={FirstName}", d.PatientId, d.LastName, d.FirstName);
59 |     try
60 |     {
61 |         var sb = new StringBuilder()
62 |             .AppendLine("[PatientData001]")
63 |             .AppendLine($"ID={d.PatientId}")
64 |             .AppendLine($"LastName={d.LastName}")
65 |             .AppendLine($"FirstName={d.FirstName}")
66 |             .AppendLine($"BirthDay={d.BirthDay}")
67 |             .AppendLine($"BirthMonth={d.BirthMonth}")
68 |             .AppendLine($"BirthYear={d.BirthYear}")
69 |             .AppendLine($"Sex={d.Sex}")
70 |             .AppendLine($"Weight={d.Weight}")
71 |             .AppendLine($"Height={d.Height}")
72 |             .AppendLine($"Address={d.Address}")
73 |             .AppendLine($"Phone1={d.Phone1}")
74 |             .AppendLine($"Phone2={d.Phone2}")
75 |             .AppendLine($"Fax={d.Fax}")
76 |             .AppendLine($"E-Mail={d.Email}")
77 |             .AppendLine($"Medications={d.Medications}")
78 |             .AppendLine($"Other={d.Other}");
79 | 
80 |         Directory.CreateDirectory(Path.GetDirectoryName(worklistIniPath)!);
81 |         await File.WriteAllTextAsync(worklistIniPath, sb.ToString(), Encoding.UTF8);
82 |         log.LogInformation("Wrote INI file {Path} (length {Len} bytes)", worklistIniPath, sb.Length);
83 |         return Results.Ok(new { wrote = worklistIniPath });
84 |     }
85 |     catch (Exception ex)
86 |     {
87 |         log.LogError(ex, "Failed writing INI at {Path}", worklistIniPath);
88 |         return Results.Problem($"Failed writing INI: {ex.Message}");
89 |     }
90 | });
91 | 
92 | // --- MWL: add worklist item ---
93 | app.MapPost("/mwl/items", (Demographics d) =>
94 | {
95 |     // Use provided PatientName if supplied; otherwise construct from Last^First per DICOM PN format
96 |     var patientName = d.PatientName ?? $"{d.LastName}^{d.FirstName}";
97 |     var ds = WorklistStore.FromOrder(d.PatientId, patientName, d.AccessionNumber);
98 |     WorklistStore.Add(ds);
99 |     return Results.Ok(new { added = d.PatientId, ae = aet, port = dicomPort });
100 | });
101 | 
102 | app.Run();
103 | 
104 | // --- Models ---
105 | record Demographics(
106 |     string PatientId,
107 |     string LastName,
108 |     string FirstName,
109 |     int BirthDay,
110 |     int BirthMonth,
111 |     int BirthYear,
112 |     int Sex,
113 |     int Weight,
114 |     int Height,
115 |     string Address,
116 |     string Phone1,
117 |     string Phone2,
118 |     string Fax,
119 |     string Email,
120 |     string Medications,
121 |     string Other,
122 |     string? PatientName = null,
123 |     string? AccessionNumber = null);
124 | 
125 | // --- Background watcher ---
126 | public class WatcherService : BackgroundService
127 | {
128 |     private readonly IConfiguration _cfg;
129 |     private readonly BlobContainerClient? _blob;
130 |     private readonly ILogger<WatcherService> _log;
131 |     private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.OrdinalIgnoreCase);
132 |     private Timer? _pollTimer;
133 | 
134 |     public WatcherService(IConfiguration cfg, IServiceProvider sp, ILogger<WatcherService> log)
135 |     {
136 |         _cfg = cfg;
137 |         _log = log;
138 |         if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
139 |             _blob = sp.GetService<BlobContainerClient>();
140 |     }
141 | 
142 |     protected override Task ExecuteAsync(CancellationToken stoppingToken)
143 |     {
144 |         var dir = _cfg["PdfWatchDir"]!;
145 |         Directory.CreateDirectory(dir);
146 |         _log.LogInformation("Watcher started. Mode={Mode} Watching={Dir}", _cfg["Storage:Mode"], dir);
147 | 
148 |         var fsw = new FileSystemWatcher(dir)
149 |         {
150 |             Filter = "*.*", // we'll filter ourselves
151 |             NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.LastWrite,
152 |             IncludeSubdirectories = false,
153 |             EnableRaisingEvents = true
154 |         };
155 | 
156 |         fsw.Created += async (_, e) => await OnEvent("Created", e.FullPath, stoppingToken);
157 |         fsw.Changed += async (_, e) => await OnEvent("Changed", e.FullPath, stoppingToken);
158 |         fsw.Renamed += async (_, e) => await OnEvent("Renamed", e.FullPath, stoppingToken);
159 | 
160 |         // Poll fallback every 15s to catch missed writes (WSL inotify edge cases)
161 |         _pollTimer = new Timer(async _ => await Poll(dir, stoppingToken), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
162 |         return Task.CompletedTask;
163 |     }
164 | 
165 |     private async Task OnEvent(string kind, string path, CancellationToken ct)
166 |     {
167 |         try
168 |         {
169 |             if (!IsPdf(path)) return;
170 |             if (_processed.ContainsKey(path)) return; // already done
171 |             _log.LogDebug("FS event {Kind} for {File}", kind, path);
172 |             await Task.Delay(1500, ct); // debounce & allow write completion
173 |             if (!File.Exists(path)) { _log.LogDebug("Skipped {File} (disappeared)", path); return; }
174 |             if (_processed.ContainsKey(path)) return;
175 |             await ProcessFile(path, ct);
176 |         }
177 |         catch (Exception ex)
178 |         {
179 |             _log.LogError(ex, "Error handling event for {File}", path);
180 |         }
181 |     }
182 | 
183 |     private async Task Poll(string dir, CancellationToken ct)
184 |     {
185 |         try
186 |         {
187 |             foreach (var f in Directory.EnumerateFiles(dir, "*.pdf"))
188 |             {
189 |                 if (_processed.ContainsKey(f)) continue;
190 |                 var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
191 |                 if (age < TimeSpan.FromSeconds(2)) continue; // still possibly writing
192 |                 _log.LogDebug("Poll picked up {File}", f);
193 |                 await ProcessFile(f, ct);
194 |             }
195 |         }
196 |         catch (Exception ex)
197 |         {
198 |             _log.LogError(ex, "Poll error");
199 |         }
200 |     }
201 | 
202 |     private static bool IsPdf(string path) =>
203 |         path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
204 | 
205 |     private async Task ProcessFile(string path, CancellationToken ct)
206 |     {
207 |         if (!_processed.TryAdd(path, 1)) return;
208 |         _log.LogInformation("Processing PDF {File}", path);
209 |         try
210 |         {
211 |             // Retry open until stable
212 |             const int maxAttempts = 5;
213 |             var lastLen = -1L;
214 |             for (int i = 0; i < maxAttempts; i++)
215 |             {
216 |                 if (ct.IsCancellationRequested) return;
217 |                 try
218 |                 {
219 |                     var fi = new FileInfo(path);
220 |                     if (fi.Length > 0 && fi.Length == lastLen) break; // stable size two checks apart
221 |                     lastLen = fi.Length;
222 |                 }
223 |                 catch { }
224 |                 await Task.Delay(400, ct);
225 |             }
226 | 
227 |             if ((_cfg["Storage:Mode"] ?? "Local") == "Azurite")
228 |             {
229 |                 await _blob!.CreateIfNotExistsAsync(cancellationToken: ct);
230 |                 var client = _blob.GetBlobClient(Path.GetFileName(path));
231 |                 await using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
232 |                 await client.UploadAsync(s, overwrite: true, cancellationToken: ct);
233 |                 _log.LogInformation("Uploaded {Name} ({Bytes} bytes) to container {Container}", client.Name, new FileInfo(path).Length, _blob.Name);
234 |             }
235 |             else
236 |             {
237 |                 var destDir = Path.GetFullPath(_cfg["Storage:LocalIngestDir"]!);
238 |                 Directory.CreateDirectory(destDir);
239 |                 var dest = Path.Combine(destDir, Path.GetFileName(path));
240 |                 File.Copy(path, dest, overwrite: true);
241 |                 _log.LogInformation("Copied {Src} -> {Dest}", path, dest);
242 |             }
243 |         }
244 |         catch (Exception ex)
245 |         {
246 |             _log.LogError(ex, "Failed processing {File}", path);
247 |             _processed.TryRemove(path, out _); // allow retry via poll
248 |         }
249 |     }
250 | 
251 |     public override Task StopAsync(CancellationToken cancellationToken)
252 |     {
253 |         _pollTimer?.Dispose();
254 |         return base.StopAsync(cancellationToken);
255 |     }
256 | }
```

agent/EcgAgent/Worker.cs
```
1 | namespace EcgAgent;
2 | 
3 | public class Worker : BackgroundService
4 | {
5 |     private readonly ILogger<Worker> _logger;
6 | 
7 |     public Worker(ILogger<Worker> logger)
8 |     {
9 |         _logger = logger;
10 |     }
11 | 
12 |     protected override async Task ExecuteAsync(CancellationToken stoppingToken)
13 |     {
14 |         while (!stoppingToken.IsCancellationRequested)
15 |         {
16 |             if (_logger.IsEnabled(LogLevel.Information))
17 |             {
18 |                 _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
19 |             }
20 |             await Task.Delay(1000, stoppingToken);
21 |         }
22 |     }
23 | }
```

agent/EcgAgent/WorklistStore.cs
```
1 | using FellowOakDicom;
2 | 
3 | public static class WorklistStore
4 | {
5 |     // Simple list; replace with DB later. Each item is already a fully populated MWL dataset.
6 |     private static readonly List<DicomDataset> _items = new();
7 | 
8 |     public static void Add(DicomDataset ds)
9 |     {
10 |         lock (_items) _items.Add(ds);
11 |     }
12 | 
13 |     // naive filter: match on PatientID / Accession
14 |     public static IEnumerable<DicomDataset> Query(DicomDataset keys)
15 |     {
16 |         var pid = keys.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
17 |         var acc = keys.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
18 | 
19 |         lock (_items)
20 |         {
21 |             return _items.Where(i =>
22 |                 (string.IsNullOrEmpty(pid) || i.GetString(DicomTag.PatientID) == pid) &&
23 |                 (string.IsNullOrEmpty(acc) || i.GetString(DicomTag.AccessionNumber) == acc)
24 |             ).ToList();
25 |         }
26 |     }
27 | 
28 |     // Convert your app's demographics to a proper MWL item dataset
29 |     public static DicomDataset FromOrder(string patientId, string name, string? accession)
30 |     {
31 |         var now = DateTime.UtcNow;
32 |         return new DicomDataset
33 |         {
34 |             { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
35 |             { DicomTag.PatientID, patientId },
36 |             { DicomTag.PatientName, name },
37 |             { DicomTag.AccessionNumber, accession ?? string.Empty },
38 |             { DicomTag.Modality, "ECG" }, // not standardized modality token, but acceptable for MWL
39 |             { DicomTag.ScheduledProcedureStepSequence, new DicomSequence(
40 |                 DicomTag.ScheduledProcedureStepSequence, // FIX: supply tag for sequence ctor
41 |                 new DicomDataset {
42 |                     { DicomTag.ScheduledStationAETitle, "NEKO_ECG" },
43 |                     { DicomTag.ScheduledProcedureStepStartDate, now.ToString("yyyyMMdd") },
44 |                     { DicomTag.ScheduledProcedureStepStartTime, now.ToString("HHmmss") },
45 |                     { DicomTag.ScheduledPerformingPhysicianName, "Auto" },
46 |                     { DicomTag.ScheduledProcedureStepDescription, "Resting ECG" }
47 |                 })
48 |             }
49 |         };
50 |     }
51 | }
```

agent/EcgAgent/appsettings.Development.json
```
1 | {
2 |   "Logging": {
3 |     "LogLevel": {
4 |       "Default": "Information",
5 |       "Microsoft.Hosting.Lifetime": "Information"
6 |     }
7 |   },
8 |   "WorklistIniPath": "/mnt/c/ProgramData/NoravMedical/PCECG/Worklist/PatientFile.ini",
9 |   "PdfWatchDir": "/mnt/c/ProgramData/NoravMedical/PCECG/PDF",
10 |   "Storage": {
11 |     "Mode": "Azurite",
12 |     "LocalIngestDir": "./localdata/ingest",
13 |     "AzuriteConnectionString": "UseDevelopmentStorage=true",
14 |     "AzuriteContainer": "ecg-reports"
15 |   },
16 |   "Http": { "Url": "http://localhost:5000" }
17 | }
```

agent/EcgAgent/appsettings.json
```
1 | {
2 |   "WorklistIniPath": "C:\\ProgramData\\NoravMedical\\PCECG\\Worklist\\PatientFile.ini",
3 |   "PdfWatchDir": "C:\\ProgramData\\NoravMedical\\PCECG\\PDF",
4 |   "Storage": {
5 |     "Mode": "Azurite",                       
6 |     "LocalIngestDir": "..\\..\\..\\..\\localdata\\ingest",
7 |     "AzuriteConnectionString": "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFeq...;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;",
8 |     "AzuriteContainer": "ecg-reports"
9 |   },
10 |   "Http": { "Url": "http://localhost:5000" }
11 | }
```

docker/azurite/docker-compose.yml
```
1 | version: "3.8"
2 | services:
3 |   azurite:
4 |     image: mcr.microsoft.com/azure-storage/azurite
5 |     ports:
6 |       - "10000:10000"  # Blob
7 |       - "10001:10001"  # Queue
8 |       - "10002:10002"  # Table
9 |     command: "azurite --loose --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0"
10 |     volumes:
11 |       - ../../localdata/azurite:/data
12 |     working_dir: /data
```

ui/EcgUi/EcgUi.csproj
```
1 | <Project Sdk="Microsoft.NET.Sdk.Web">
2 | 
3 |   <PropertyGroup>
4 |     <TargetFramework>net9.0</TargetFramework>
5 |     <Nullable>enable</Nullable>
6 |     <ImplicitUsings>enable</ImplicitUsings>
7 |   </PropertyGroup>
8 | 
9 |   <ItemGroup>
10 |     <PackageReference Include="Azure.Storage.Blobs" Version="12.25.0" />
11 |     <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="9.0.0" />
12 |   </ItemGroup>
13 | </Project>
```

ui/EcgUi/Program.cs
```
1 | using Azure.Storage.Blobs;
2 | using Microsoft.AspNetCore.Components;
3 | using Microsoft.AspNetCore.Components.Web;
4 | using Azure.Storage.Blobs.Models;
5 | 
6 | var builder = WebApplication.CreateBuilder(args);
7 | 
8 | // Blazor Server services
9 | builder.Services.AddRazorPages();
10 | builder.Services.AddServerSideBlazor();
11 | 
12 | // HttpClient with BaseAddress from NavigationManager
13 | builder.Services.AddScoped(sp =>
14 | {
15 |     var nav = sp.GetRequiredService<NavigationManager>();
16 |     return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
17 | });
18 | 
19 | if ((builder.Configuration["Storage:Mode"] ?? "Local") == "Azurite")
20 | {
21 |     builder.Services.AddSingleton(sp =>
22 |         new BlobContainerClient(
23 |             builder.Configuration["Storage:AzuriteConnectionString"],
24 |             builder.Configuration["Storage:AzuriteContainer"]));
25 | }
26 | 
27 | var app = builder.Build();
28 | 
29 | if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
30 | app.UseStaticFiles();
31 | app.UseRouting();
32 | 
33 | app.MapBlazorHub();
34 | app.MapFallbackToPage("/_Host");
35 | 
36 | var storageMode = builder.Configuration["Storage:Mode"] ?? "Local";
37 | if (storageMode == "Azurite")
38 | {
39 |     // Blob listing endpoint
40 |     app.MapGet("/api/reports", async (BlobContainerClient container) =>
41 |     {
42 |         await container.CreateIfNotExistsAsync();
43 |         var names = new List<string>();
44 |         await foreach (BlobItem b in container.GetBlobsAsync(prefix: null))
45 |         {
46 |             if (b.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
47 |                 names.Add(b.Name);
48 |         }
49 |         return names.OrderByDescending(n => n);
50 |     });
51 | 
52 |     // Blob download endpoint
53 |     app.MapGet("/api/reports/{name}", async (string name, BlobContainerClient container) =>
54 |     {
55 |         var client = container.GetBlobClient(name);
56 |         if (!await client.ExistsAsync()) return Results.NotFound();
57 |         var dl = await client.DownloadStreamingAsync();
58 |         return Results.Stream(dl.Value.Content, contentType: "application/pdf", fileDownloadName: name, enableRangeProcessing: true);
59 |     });
60 | }
61 | else
62 | {
63 |     // Local file system endpoints (existing behavior)
64 |     var ingestDir = builder.Configuration["Storage:LocalIngestDir"] ?? "../localdata/ingest";
65 |     Directory.CreateDirectory(ingestDir);
66 |     app.MapGet("/api/reports", () =>
67 |     {
68 |         var files = Directory.GetFiles(ingestDir, "*.pdf");
69 |         return files.Select(Path.GetFileName).OrderByDescending(x => x);
70 |     });
71 |     app.MapGet("/api/reports/{name}", (string name) =>
72 |     {
73 |         var path = Path.Combine(ingestDir, name);
74 |         return File.Exists(path)
75 |             ? Results.File(path, "application/pdf", enableRangeProcessing: true)
76 |             : Results.NotFound();
77 |     });
78 | }
79 | 
80 | app.Run();
```

ui/EcgUi/_Imports.razor
```
1 | @using System.Net.Http
2 | @using System.Net.Http.Json
3 | @using Microsoft.AspNetCore.Components
4 | @using Microsoft.AspNetCore.Components.Forms
5 | @using Microsoft.AspNetCore.Components.Routing
6 | @using Microsoft.AspNetCore.Components.Web
7 | @using Microsoft.AspNetCore.Components.Web.Virtualization
8 | @using Microsoft.JSInterop
9 | @using EcgUi
10 | @using EcgUi.Components
```

ui/EcgUi/appsettings.Development.json
```
1 | {
2 |   "Logging": {
3 |     "LogLevel": {
4 |       "Default": "Information",
5 |       "Microsoft.AspNetCore": "Warning"
6 |     }
7 |   },
8 |   "Storage": {
9 |     "Mode": "Azurite",
10 |     "LocalIngestDir": "./localdata/ingest",
11 |     "AzuriteConnectionString": "UseDevelopmentStorage=true",
12 |     "AzuriteContainer": "ecg-reports"
13 |   },
14 |   "AgentUrl": "http://localhost:5000"
15 | }
```

ui/EcgUi/appsettings.json
```
1 | {
2 |   "Storage": {
3 |     "Mode": "Azurite",  
4 |     "LocalIngestDir": "..\\..\\localdata\\ingest",
5 |     "AzuriteConnectionString": "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFeq...;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;",
6 |     "AzuriteContainer": "ecg-reports"
7 |   },
8 |   "AgentUrl": "http://localhost:5000"
9 | }
```

agent/EcgAgent/Properties/launchSettings.json
```
1 | ﻿{
2 |   "$schema": "https://json.schemastore.org/launchsettings.json",
3 |   "profiles": {
4 |     "EcgAgent": {
5 |       "commandName": "Project",
6 |       "dotnetRunMessages": true,
7 |       "environmentVariables": {
8 |         "DOTNET_ENVIRONMENT": "Development"
9 |       }
10 |     }
11 |   }
12 | }
```

ui/EcgUi/Components/App.razor
```
1 | ﻿<Router AppAssembly="typeof(Program).Assembly">
2 |     <Found Context="routeData">
3 |         <RouteView RouteData="routeData" DefaultLayout="typeof(EcgUi.Components.Layout.MainLayout)" />
4 |     </Found>
5 |     <NotFound>
6 |         <p>Sorry, page not found.</p>
7 |     </NotFound>
8 | </Router>
```

ui/EcgUi/Components/Routes.razor
```
1 | ﻿<Router AppAssembly="typeof(Program).Assembly">
2 |     <Found Context="routeData">
3 |         <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
4 |         <FocusOnNavigate RouteData="routeData" Selector="h1" />
5 |     </Found>
6 | </Router>
```

ui/EcgUi/Components/_Imports.razor
```
1 | ﻿@using System.Net.Http
2 | @using System.Net.Http.Json
3 | @using Microsoft.AspNetCore.Components
4 | @using Microsoft.AspNetCore.Components.Forms
5 | @using Microsoft.AspNetCore.Components.Routing
6 | @using Microsoft.AspNetCore.Components.Web
7 | @using Microsoft.AspNetCore.Components.Web.Virtualization
8 | @using Microsoft.JSInterop
9 | @using EcgUi
10 | @using EcgUi.Components
11 | @using Microsoft.AspNetCore.Components.Routing
```

ui/EcgUi/Pages/Nurse.razor
```
1 | @page "/nurse"
2 | @using Microsoft.AspNetCore.Components.Forms
3 | @inject HttpClient Http
4 | @inject IConfiguration Cfg
5 | 
6 | <h3>ECG Demographics → Worklist (INI)</h3>
7 | 
8 | <EditForm Model="@m" OnValidSubmit="Submit" FormName="demoForm">
9 |   <DataAnnotationsValidator />
10 |   <ValidationSummary />
11 |   <InputText @bind-Value="m.PatientId" placeholder="ID" />
12 |   <InputText @bind-Value="m.LastName" placeholder="Last Name" />
13 |   <InputText @bind-Value="m.FirstName" placeholder="First Name" />
14 |   <InputNumber @bind-Value="m.BirthDay" placeholder="Birth Day" />
15 |   <InputNumber @bind-Value="m.BirthMonth" placeholder="Birth Month" />
16 |   <InputNumber @bind-Value="m.BirthYear" placeholder="Birth Year" />
17 |   <InputNumber @bind-Value="m.Sex" placeholder="Sex (1=Male,2=Female)" />
18 |   <InputNumber @bind-Value="m.Weight" placeholder="Weight" />
19 |   <InputNumber @bind-Value="m.Height" placeholder="Height" />
20 |   <InputText @bind-Value="m.Address" placeholder="Address" />
21 |   <InputText @bind-Value="m.Phone1" placeholder="Phone1" />
22 |   <InputText @bind-Value="m.Phone2" placeholder="Phone2" />
23 |   <InputText @bind-Value="m.Fax" placeholder="Fax" />
24 |   <InputText @bind-Value="m.Email" placeholder="E-Mail" />
25 |   <InputText @bind-Value="m.Medications" placeholder="Medications" />
26 |   <InputText @bind-Value="m.Other" placeholder="Other" />
27 |   <button type="submit">Write Worklist</button>
28 |   <button type="button" @onclick="SendToMwl">Send to MWL</button>
29 | </EditForm>
30 | 
31 | <p>@status</p>
32 | 
33 | @code {
34 |   Demographics m = new() { BirthDay = 1, BirthMonth = 1, BirthYear = 1970 }; // defaults
35 |   string status = "";
36 |   public class Demographics
37 |   {
38 |     public string PatientId { get; set; } = "";
39 |     public string LastName { get; set; } = "";
40 |     public string FirstName { get; set; } = "";
41 |     public int BirthDay { get; set; }
42 |     public int BirthMonth { get; set; }
43 |     public int BirthYear { get; set; }
44 |     public int Sex { get; set; } = 1;
45 |     public int Weight { get; set; }
46 |     public int Height { get; set; }
47 |     public string Address { get; set; } = "";
48 |     public string Phone1 { get; set; } = "";
49 |     public string Phone2 { get; set; } = "";
50 |     public string Fax { get; set; } = "";
51 |     public string Email { get; set; } = "";
52 |     public string Medications { get; set; } = "";
53 |     public string Other { get; set; } = "";
54 |   }
55 | 
56 |   private async Task Submit()
57 |   {
58 |     var agent = Cfg["AgentUrl"]!;
59 |     var resp = await Http.PostAsJsonAsync($"{agent}/demographics", m);
60 |     status = resp.IsSuccessStatusCode ? "INI written." : "Failed.";
61 |   }
62 | 
63 |   private async Task SendToMwl()
64 |   {
65 |     var agent = Cfg["AgentUrl"]!;
66 |     var resp = await Http.PostAsJsonAsync($"{agent}/mwl/items", m);
67 |     status = resp.IsSuccessStatusCode ? "MWL item added." : "MWL add failed.";
68 |   }
69 | }
```

ui/EcgUi/Pages/Reports.razor
```
1 | @page "/reports"
2 | @inject HttpClient Http
3 | 
4 | <h3>ECG Reports</h3>
5 | 
6 | <select @onchange="OnPick">
7 |   <option value="">-- choose --</option>
8 |   @foreach (var f in files) { <option value="@f">@f</option> }
9 | </select>
10 | 
11 | @if (!string.IsNullOrEmpty(selected))
12 | {
13 |   <iframe src="@($"/api/reports/{selected}")" style="width:100%;height:75vh;"></iframe>
14 | }
15 | 
16 | @code {
17 |   List<string> files = new();
18 |   string selected = "";
19 | 
20 |   protected override async Task OnInitializedAsync()
21 |   {
22 |     files = (await Http.GetFromJsonAsync<List<string>>("/api/reports")) ?? new();
23 |   }
24 |   async Task OnPick(ChangeEventArgs e) => selected = e.Value?.ToString() ?? "";
25 | }
```

ui/EcgUi/Pages/_Host.cshtml
```
1 | @page "/"
2 | @namespace EcgUi.Pages
3 | @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
4 | <!DOCTYPE html>
5 | <html lang="en">
6 | <head>
7 |     <meta charset="utf-8" />
8 |     <title>ECG UI</title>
9 |     <base href="~/" />
10 |     <link href="css/bootstrap/bootstrap.min.css" rel="stylesheet" />
11 |     <link href="app.css" rel="stylesheet" />
12 | </head>
13 | <body>
14 |     <app>
15 |         <component type="typeof(EcgUi.Components.App)" render-mode="ServerPrerendered" />
16 |     </app>
17 |     <script src="_framework/blazor.server.js"></script>
18 | </body>
19 | </html>
```

ui/EcgUi/Properties/launchSettings.json
```
1 | {
2 |   "$schema": "https://json.schemastore.org/launchsettings.json",
3 |     "profiles": {
4 |       "http": {
5 |         "commandName": "Project",
6 |         "dotnetRunMessages": true,
7 |         "launchBrowser": true,
8 |         "applicationUrl": "http://localhost:5256",
9 |         "environmentVariables": {
10 |           "ASPNETCORE_ENVIRONMENT": "Development"
11 |         }
12 |       },
13 |       "https": {
14 |         "commandName": "Project",
15 |         "dotnetRunMessages": true,
16 |         "launchBrowser": true,
17 |         "applicationUrl": "https://localhost:7152;http://localhost:5256",
18 |         "environmentVariables": {
19 |           "ASPNETCORE_ENVIRONMENT": "Development"
20 |         }
21 |       }
22 |     }
23 |   }
```

ui/EcgUi/wwwroot/app.css
```
1 | html, body {
2 |     font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;
3 | }
4 | 
5 | a, .btn-link {
6 |     color: #006bb7;
7 | }
8 | 
9 | .btn-primary {
10 |     color: #fff;
11 |     background-color: #1b6ec2;
12 |     border-color: #1861ac;
13 | }
14 | 
15 | .btn:focus, .btn:active:focus, .btn-link.nav-link:focus, .form-control:focus, .form-check-input:focus {
16 |   box-shadow: 0 0 0 0.1rem white, 0 0 0 0.25rem #258cfb;
17 | }
18 | 
19 | .content {
20 |     padding-top: 1.1rem;
21 | }
22 | 
23 | h1:focus {
24 |     outline: none;
25 | }
26 | 
27 | .valid.modified:not([type=checkbox]) {
28 |     outline: 1px solid #26b050;
29 | }
30 | 
31 | .invalid {
32 |     outline: 1px solid #e50000;
33 | }
34 | 
35 | .validation-message {
36 |     color: #e50000;
37 | }
38 | 
39 | .blazor-error-boundary {
40 |     background: url(data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNTYiIGhlaWdodD0iNDkiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiIG92ZXJmbG93PSJoaWRkZW4iPjxkZWZzPjxjbGlwUGF0aCBpZD0iY2xpcDAiPjxyZWN0IHg9IjIzNSIgeT0iNTEiIHdpZHRoPSI1NiIgaGVpZ2h0PSI0OSIvPjwvY2xpcFBhdGg+PC9kZWZzPjxnIGNsaXAtcGF0aD0idXJsKCNjbGlwMCkiIHRyYW5zZm9ybT0idHJhbnNsYXRlKC0yMzUgLTUxKSI+PHBhdGggZD0iTTI2My41MDYgNTFDMjY0LjcxNyA1MSAyNjUuODEzIDUxLjQ4MzcgMjY2LjYwNiA1Mi4yNjU4TDI2Ny4wNTIgNTIuNzk4NyAyNjcuNTM5IDUzLjYyODMgMjkwLjE4NSA5Mi4xODMxIDI5MC41NDUgOTIuNzk1IDI5MC42NTYgOTIuOTk2QzI5MC44NzcgOTMuNTEzIDI5MSA5NC4wODE1IDI5MSA5NC42NzgyIDI5MSA5Ny4wNjUxIDI4OS4wMzggOTkgMjg2LjYxNyA5OUwyNDAuMzgzIDk5QzIzNy45NjMgOTkgMjM2IDk3LjA2NTEgMjM2IDk0LjY3ODIgMjM2IDk0LjM3OTkgMjM2LjAzMSA5NC4wODg2IDIzNi4wODkgOTMuODA3MkwyMzYuMzM4IDkzLjAxNjIgMjM2Ljg1OCA5Mi4xMzE0IDI1OS40NzMgNTMuNjI5NCAyNTkuOTYxIDUyLjc5ODUgMjYwLjQwNyA1Mi4yNjU4QzI2MS4yIDUxLjQ4MzcgMjYyLjI5NiA1MSAyNjMuNTA2IDUxWk0yNjMuNTg2IDY2LjAxODNDMjYwLjczNyA2Ni4wMTgzIDI1OS4zMTMgNjcuMTI0NSAyNTkuMzEzIDY5LjMzNyAyNTkuMzEzIDY5LjYxMDIgMjU5LjMzMiA2OS44NjA4IDI1OS4zNzEgNzAuMDg4N0wyNjEuNzk1IDg0LjAxNjEgMjY1LjM4IDg0LjAxNjEgMjY3LjgyMSA2OS43NDc1QzI2Ny44NiA2OS43MzA5IDI2Ny44NzkgNjkuNTg3NyAyNjcuODc5IDY5LjMxNzkgMjY3Ljg3OSA2Ny4xMTgyIDI2Ni40NDggNjYuMDE4MyAyNjMuNTg2IDY2LjAxODNaTTI2My41NzYgODYuMDU0N0MyNjEuMDQ5IDg2LjA1NDcgMjU5Ljc4NiA4Ny4zMDA1IDI1OS43ODYgODkuNzkyMSAyNTkuNzg2IDkyLjI4MzcgMjYxLjA0OSA5My41Mjk1IDI2My41NzYgOTMuNTI5NSAyNjYuMTE2IDkzLjUyOTUgMjY3LjM4NyA5Mi4yODM3IDI2Ny4zODcgODkuNzkyMSAyNjcuMzg3IDg3LjMwMDUgMjY2LjExNiA4Ni4wNTQ3IDI2My41NzYgODYuMDU0N1oiIGZpbGw9IiNGRkU1MDAiIGZpbGwtcnVsZT0iZXZlbm9kZCIvPjwvZz48L3N2Zz4=) no-repeat 1rem/1.8rem, #b32121;
41 |     padding: 1rem 1rem 1rem 3.7rem;
42 |     color: white;
43 | }
44 | 
45 |     .blazor-error-boundary::after {
46 |         content: "An error has occurred."
47 |     }
48 | 
49 | .darker-border-checkbox.form-check-input {
50 |     border-color: #929292;
51 | }
52 | 
53 | .form-floating > .form-control-plaintext::placeholder, .form-floating > .form-control::placeholder {
54 |     color: var(--bs-secondary-color);
55 |     text-align: end;
56 | }
57 | 
58 | .form-floating > .form-control-plaintext:focus::placeholder, .form-floating > .form-control:focus::placeholder {
59 |     text-align: start;
60 | }
```

agent/EcgAgent/localdata/Worklist/PatientFile.ini
```
1 | ﻿[PatientData001]
2 | ID=aasdf
3 | LastName=asdff
4 | FirstName=asölkjs
5 | BirthDay=1
6 | BirthMonth=1
7 | BirthYear=1970
8 | Sex=1
9 | Weight=0
10 | Height=0
11 | Address=ölkjasdf
12 | Phone1=08098
13 | Phone2=098990
14 | Fax=asdfj
15 | E-Mail=lökasjdf
16 | Medications=ölkajsdf
17 | Other=asdfölkj
```

ui/EcgUi/Components/Layout/MainLayout.razor
```
1 | ﻿@inherits LayoutComponentBase
2 | 
3 | <div class="page">
4 |     <div class="sidebar">
5 |         <NavMenu />
6 |     </div>
7 | 
8 |     <main>
9 |         <div class="top-row px-4">
10 |             <a href="https://learn.microsoft.com/aspnet/core/" target="_blank">About</a>
11 |         </div>
12 | 
13 |         <article class="content px-4">
14 |             @Body
15 |         </article>
16 |     </main>
17 | </div>
18 | 
19 | <div id="blazor-error-ui" data-nosnippet>
20 |     An unhandled error has occurred.
21 |     <a href="." class="reload">Reload</a>
22 |     <span class="dismiss">🗙</span>
23 | </div>
```

ui/EcgUi/Components/Layout/MainLayout.razor.css
```
1 | .page {
2 |     position: relative;
3 |     display: flex;
4 |     flex-direction: column;
5 | }
6 | 
7 | main {
8 |     flex: 1;
9 | }
10 | 
11 | .sidebar {
12 |     background-image: linear-gradient(180deg, rgb(5, 39, 103) 0%, #3a0647 70%);
13 | }
14 | 
15 | .top-row {
16 |     background-color: #f7f7f7;
17 |     border-bottom: 1px solid #d6d5d5;
18 |     justify-content: flex-end;
19 |     height: 3.5rem;
20 |     display: flex;
21 |     align-items: center;
22 | }
23 | 
24 |     .top-row ::deep a, .top-row ::deep .btn-link {
25 |         white-space: nowrap;
26 |         margin-left: 1.5rem;
27 |         text-decoration: none;
28 |     }
29 | 
30 |     .top-row ::deep a:hover, .top-row ::deep .btn-link:hover {
31 |         text-decoration: underline;
32 |     }
33 | 
34 |     .top-row ::deep a:first-child {
35 |         overflow: hidden;
36 |         text-overflow: ellipsis;
37 |     }
38 | 
39 | @media (max-width: 640.98px) {
40 |     .top-row {
41 |         justify-content: space-between;
42 |     }
43 | 
44 |     .top-row ::deep a, .top-row ::deep .btn-link {
45 |         margin-left: 0;
46 |     }
47 | }
48 | 
49 | @media (min-width: 641px) {
50 |     .page {
51 |         flex-direction: row;
52 |     }
53 | 
54 |     .sidebar {
55 |         width: 250px;
56 |         height: 100vh;
57 |         position: sticky;
58 |         top: 0;
59 |     }
60 | 
61 |     .top-row {
62 |         position: sticky;
63 |         top: 0;
64 |         z-index: 1;
65 |     }
66 | 
67 |     .top-row.auth ::deep a:first-child {
68 |         flex: 1;
69 |         text-align: right;
70 |         width: 0;
71 |     }
72 | 
73 |     .top-row, article {
74 |         padding-left: 2rem !important;
75 |         padding-right: 1.5rem !important;
76 |     }
77 | }
78 | 
79 | #blazor-error-ui {
80 |     color-scheme: light only;
81 |     background: lightyellow;
82 |     bottom: 0;
83 |     box-shadow: 0 -1px 2px rgba(0, 0, 0, 0.2);
84 |     box-sizing: border-box;
85 |     display: none;
86 |     left: 0;
87 |     padding: 0.6rem 1.25rem 0.7rem 1.25rem;
88 |     position: fixed;
89 |     width: 100%;
90 |     z-index: 1000;
91 | }
92 | 
93 |     #blazor-error-ui .dismiss {
94 |         cursor: pointer;
95 |         position: absolute;
96 |         right: 0.75rem;
97 |         top: 0.5rem;
98 |     }
```

ui/EcgUi/Components/Layout/NavMenu.razor
```
1 | ﻿<div class="top-row ps-3 navbar navbar-dark">
2 |     <div class="container-fluid">
3 |         <a class="navbar-brand" href="">EcgUi</a>
4 |     </div>
5 | </div>
6 | 
7 | <input type="checkbox" title="Navigation menu" class="navbar-toggler" />
8 | 
9 | <div class="nav-scrollable" onclick="document.querySelector('.navbar-toggler').click()">
10 |     <nav class="nav flex-column">
11 |         <div class="nav-item px-3">
12 |             <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
13 |                 <span class="bi bi-house-door-fill-nav-menu" aria-hidden="true"></span> Home
14 |             </NavLink>
15 |         </div>
16 | 
17 |         <div class="nav-item px-3">
18 |             <NavLink class="nav-link" href="counter">
19 |                 <span class="bi bi-plus-square-fill-nav-menu" aria-hidden="true"></span> Counter
20 |             </NavLink>
21 |         </div>
22 | 
23 |         <div class="nav-item px-3">
24 |             <NavLink class="nav-link" href="weather">
25 |                 <span class="bi bi-list-nested-nav-menu" aria-hidden="true"></span> Weather
26 |             </NavLink>
27 |         </div>
28 |     </nav>
29 | </div>
30 | 
```

ui/EcgUi/Components/Layout/NavMenu.razor.css
```
1 | .navbar-toggler {
2 |     appearance: none;
3 |     cursor: pointer;
4 |     width: 3.5rem;
5 |     height: 2.5rem;
6 |     color: white;
7 |     position: absolute;
8 |     top: 0.5rem;
9 |     right: 1rem;
10 |     border: 1px solid rgba(255, 255, 255, 0.1);
11 |     background: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 30 30'%3e%3cpath stroke='rgba%28255, 255, 255, 0.55%29' stroke-linecap='round' stroke-miterlimit='10' stroke-width='2' d='M4 7h22M4 15h22M4 23h22'/%3e%3c/svg%3e") no-repeat center/1.75rem rgba(255, 255, 255, 0.1);
12 | }
13 | 
14 | .navbar-toggler:checked {
15 |     background-color: rgba(255, 255, 255, 0.5);
16 | }
17 | 
18 | .top-row {
19 |     min-height: 3.5rem;
20 |     background-color: rgba(0,0,0,0.4);
21 | }
22 | 
23 | .navbar-brand {
24 |     font-size: 1.1rem;
25 | }
26 | 
27 | .bi {
28 |     display: inline-block;
29 |     position: relative;
30 |     width: 1.25rem;
31 |     height: 1.25rem;
32 |     margin-right: 0.75rem;
33 |     top: -1px;
34 |     background-size: cover;
35 | }
36 | 
37 | .bi-house-door-fill-nav-menu {
38 |     background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='white' class='bi bi-house-door-fill' viewBox='0 0 16 16'%3E%3Cpath d='M6.5 14.5v-3.505c0-.245.25-.495.5-.495h2c.25 0 .5.25.5.5v3.5a.5.5 0 0 0 .5.5h4a.5.5 0 0 0 .5-.5v-7a.5.5 0 0 0-.146-.354L13 5.793V2.5a.5.5 0 0 0-.5-.5h-1a.5.5 0 0 0-.5.5v1.293L8.354 1.146a.5.5 0 0 0-.708 0l-6 6A.5.5 0 0 0 1.5 7.5v7a.5.5 0 0 0 .5.5h4a.5.5 0 0 0 .5-.5Z'/%3E%3C/svg%3E");
39 | }
40 | 
41 | .bi-plus-square-fill-nav-menu {
42 |     background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='white' class='bi bi-plus-square-fill' viewBox='0 0 16 16'%3E%3Cpath d='M2 0a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V2a2 2 0 0 0-2-2H2zm6.5 4.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3a.5.5 0 0 1 1 0z'/%3E%3C/svg%3E");
43 | }
44 | 
45 | .bi-list-nested-nav-menu {
46 |     background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='white' class='bi bi-list-nested' viewBox='0 0 16 16'%3E%3Cpath fill-rule='evenodd' d='M4.5 11.5A.5.5 0 0 1 5 11h10a.5.5 0 0 1 0 1H5a.5.5 0 0 1-.5-.5zm-2-4A.5.5 0 0 1 3 7h10a.5.5 0 0 1 0 1H3a.5.5 0 0 1-.5-.5zm-2-4A.5.5 0 0 1 1 3h10a.5.5 0 0 1 0 1H1a.5.5 0 0 1-.5-.5z'/%3E%3C/svg%3E");
47 | }
48 | 
49 | .nav-item {
50 |     font-size: 0.9rem;
51 |     padding-bottom: 0.5rem;
52 | }
53 | 
54 |     .nav-item:first-of-type {
55 |         padding-top: 1rem;
56 |     }
57 | 
58 |     .nav-item:last-of-type {
59 |         padding-bottom: 1rem;
60 |     }
61 | 
62 |     .nav-item ::deep .nav-link {
63 |         color: #d7d7d7;
64 |         background: none;
65 |         border: none;
66 |         border-radius: 4px;
67 |         height: 3rem;
68 |         display: flex;
69 |         align-items: center;
70 |         line-height: 3rem;
71 |         width: 100%;
72 |     }
73 | 
74 | .nav-item ::deep a.active {
75 |     background-color: rgba(255,255,255,0.37);
76 |     color: white;
77 | }
78 | 
79 | .nav-item ::deep .nav-link:hover {
80 |     background-color: rgba(255,255,255,0.1);
81 |     color: white;
82 | }
83 | 
84 | .nav-scrollable {
85 |     display: none;
86 | }
87 | 
88 | .navbar-toggler:checked ~ .nav-scrollable {
89 |     display: block;
90 | }
91 | 
92 | @media (min-width: 641px) {
93 |     .navbar-toggler {
94 |         display: none;
95 |     }
96 | 
97 |     .nav-scrollable {
98 |         /* Never collapse the sidebar for wide screens */
99 |         display: block;
100 | 
101 |         /* Allow sidebar to scroll for tall menus */
102 |         height: calc(100vh - 3.5rem);
103 |         overflow-y: auto;
104 |     }
105 | }
```

ui/EcgUi/Components/Pages/Counter.razor
```
1 | ﻿@page "/counter"
2 | 
3 | <PageTitle>Counter</PageTitle>
4 | 
5 | <h1>Counter</h1>
6 | 
7 | <p role="status">Current count: @currentCount</p>
8 | 
9 | <button class="btn btn-primary" @onclick="IncrementCount">Click me</button>
10 | 
11 | @code {
12 |     private int currentCount = 0;
13 | 
14 |     private void IncrementCount()
15 |     {
16 |         currentCount++;
17 |     }
18 | }
```

ui/EcgUi/Components/Pages/Error.razor
```
1 | ﻿@page "/Error"
2 | @using System.Diagnostics
3 | 
4 | <PageTitle>Error</PageTitle>
5 | 
6 | <h1 class="text-danger">Error.</h1>
7 | <h2 class="text-danger">An error occurred while processing your request.</h2>
8 | 
9 | @if (ShowRequestId)
10 | {
11 |     <p>
12 |         <strong>Request ID:</strong> <code>@RequestId</code>
13 |     </p>
14 | }
15 | 
16 | <h3>Development Mode</h3>
17 | <p>
18 |     Swapping to <strong>Development</strong> environment will display more detailed information about the error that occurred.
19 | </p>
20 | <p>
21 |     <strong>The Development environment shouldn't be enabled for deployed applications.</strong>
22 |     It can result in displaying sensitive information from exceptions to end users.
23 |     For local debugging, enable the <strong>Development</strong> environment by setting the <strong>ASPNETCORE_ENVIRONMENT</strong> environment variable to <strong>Development</strong>
24 |     and restarting the app.
25 | </p>
26 | 
27 | @code{
28 |     [CascadingParameter]
29 |     private HttpContext? HttpContext { get; set; }
30 | 
31 |     private string? RequestId { get; set; }
32 |     private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
33 | 
34 |     protected override void OnInitialized() =>
35 |         RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
36 | }
```

ui/EcgUi/Components/Pages/Home.razor
```
1 | ﻿@page "/"
2 | 
3 | <PageTitle>Home</PageTitle>
4 | 
5 | <h1>Hello, world!</h1>
6 | 
7 | Welcome to your new app.
```

ui/EcgUi/Components/Pages/Weather.razor
```
1 | ﻿@page "/weather"
2 | @attribute [StreamRendering]
3 | 
4 | <PageTitle>Weather</PageTitle>
5 | 
6 | <h1>Weather</h1>
7 | 
8 | <p>This component demonstrates showing data.</p>
9 | 
10 | @if (forecasts == null)
11 | {
12 |     <p><em>Loading...</em></p>
13 | }
14 | else
15 | {
16 |     <table class="table">
17 |         <thead>
18 |             <tr>
19 |                 <th>Date</th>
20 |                 <th aria-label="Temperature in Celsius">Temp. (C)</th>
21 |                 <th aria-label="Temperature in Farenheit">Temp. (F)</th>
22 |                 <th>Summary</th>
23 |             </tr>
24 |         </thead>
25 |         <tbody>
26 |             @foreach (var forecast in forecasts)
27 |             {
28 |                 <tr>
29 |                     <td>@forecast.Date.ToShortDateString()</td>
30 |                     <td>@forecast.TemperatureC</td>
31 |                     <td>@forecast.TemperatureF</td>
32 |                     <td>@forecast.Summary</td>
33 |                 </tr>
34 |             }
35 |         </tbody>
36 |     </table>
37 | }
38 | 
39 | @code {
40 |     private WeatherForecast[]? forecasts;
41 | 
42 |     protected override async Task OnInitializedAsync()
43 |     {
44 |         // Simulate asynchronous loading to demonstrate streaming rendering
45 |         await Task.Delay(500);
46 | 
47 |         var startDate = DateOnly.FromDateTime(DateTime.Now);
48 |         var summaries = new[] { "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching" };
49 |         forecasts = Enumerable.Range(1, 5).Select(index => new WeatherForecast
50 |         {
51 |             Date = startDate.AddDays(index),
52 |             TemperatureC = Random.Shared.Next(-20, 55),
53 |             Summary = summaries[Random.Shared.Next(summaries.Length)]
54 |         }).ToArray();
55 |     }
56 | 
57 |     private class WeatherForecast
58 |     {
59 |         public DateOnly Date { get; set; }
60 |         public int TemperatureC { get; set; }
61 |         public string? Summary { get; set; }
62 |         public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
63 |     }
64 | }
```
