using System.Net.Http.Json;

namespace EcgUi.Services;

public sealed class AgentClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    public AgentClient(HttpClient http, IConfiguration cfg) { _http = http; _cfg = cfg; }

    private string Base => _cfg["AgentUrl"] ?? "http://localhost:5000";

    public Task<HttpResponseMessage> OpenAsync() => _http.PostAsync($"{Base}/sdk/open", null);
    public Task<HttpResponseMessage> InitAsync(int sampleRate) => _http.PostAsync($"{Base}/sdk/init?sampleRate={sampleRate}", null);
    public Task<HttpResponseMessage> StartAsync() => _http.PostAsync($"{Base}/sdk/start", null);
    public Task<HttpResponseMessage> StopAsync() => _http.PostAsync($"{Base}/sdk/stop", null);
    public async Task<short[][]?> GetSamplesAsync(int n) => await _http.GetFromJsonAsync<short[][]>($"{Base}/sdk/samples?n={n}");
    public async Task<Dictionary<string,string>?> StatusAsync() => await _http.GetFromJsonAsync<Dictionary<string,string>>($"{Base}/sdk/status");
}
