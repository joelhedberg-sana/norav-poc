using System.Runtime.InteropServices;
using EcgAgent.Drivers;

public sealed class Norav1200HrService : IHostedService, IDisposable
{
    private readonly ILogger<Norav1200HrService> _log;
    private System.Threading.Timer? _poll;
    private bool _opened, _streaming;
    private int _sr = 1000;
    private const int LeadCount = 12;       // API expects 12 buffers (I, II, III, aVR, aVL, aVF, V1..V6)
    private const int Seconds = 10;         // ring length per doc
    private int _bufSamples => _sr * Seconds;
    private readonly IntPtr[] _ecg = new IntPtr[LeadCount];
    private readonly IntPtr[] _ecgOrig = new IntPtr[LeadCount];
    private IntPtr _info = IntPtr.Zero;     // optional flags buffer
    private double _microVoltPerUnit;

    public Norav1200HrService(ILogger<Norav1200HrService> log) => _log = log;

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("Norav1200HrService ready. Use /sdk/open, /sdk/init, /sdk/start to begin streaming.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopStreaming();
        Close();
        FreeBuffers();
    }

    public void Dispose()
    {
        FreeBuffers();
        GC.SuppressFinalize(this);
    }

    // ----- Control surface called from Minimal API -----
    public (bool ok, string msg) Open()
    {
        if (!OperatingSystem.IsWindows()) return (false, "Windows only");
        var rc = Norav1200HrNative.Open();
        _opened = (rc == 0);
        return _opened ? (true, "Open=OK") : (false, $"Open failed rc=0x{rc:X}");
    }

    public (bool ok, string msg) Init(int sampleRate = 1000, int numChannels = 8, int startLead = 1, int mode = 0)
    {
        if (!_opened) return (false, "Call /sdk/open first");
        _sr = sampleRate;

        AllocBuffers();
        var rc = Norav1200HrNative.Init(numChannels, startLead, _sr, out _microVoltPerUnit, mode);
        return rc == 0 ? (true, $"Init=OK sr={_sr} uVPerUnit={_microVoltPerUnit}") : (false, $"Init failed rc=0x{rc:X}");
    }

    public (bool ok, string msg) StartStreaming()
    {
        if (!_opened) return (false, "Not opened");
        var rc = Norav1200HrNative.Start();
        if (rc != 0) return (false, $"Start failed rc=0x{rc:X}");
        _streaming = true;
        _poll = new Timer(_ => PollOnce(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(25));
        return (true, "Streaming started");
    }

    public async Task StopStreaming()
    {
        if (!_streaming) return;
        _poll?.Dispose();
        _poll = null;
        _streaming = false;
        var rc = Norav1200HrNative.Stop();
        if (rc != 0) _log.LogWarning("Stop failed rc=0x{RC:X}", rc);
        await Task.CompletedTask;
    }

    public void Close()
    {
        if (_opened) Norav1200HrNative.Close();
        _opened = false;
    }

    // ----- Data access for preview -----
    // Returns last N samples per lead as int16 arrays (copying from ring)
    public short[][] GetSamples(int n = 500)
    {
        var outLeads = new short[LeadCount][];
        for (int i = 0; i < LeadCount; i++)
        {
            outLeads[i] = new short[Math.Min(n, _bufSamples)];
            if (_ecg[i] == IntPtr.Zero) continue;
            // naive copy of the tail of the buffer
            var bytes = outLeads[i].Length * sizeof(short);
            var src = _ecg[i] + ((_bufSamples - outLeads[i].Length) * sizeof(short));
            Marshal.Copy(src, outLeads[i], 0, outLeads[i].Length);
        }
        return outLeads;
    }

    // ----- Internals -----
    private void PollOnce()
    {
        try
        {
            if (!_streaming) return;
            var ok = Norav1200HrNative.ReadECGDataFull(_ecg, _ecgOrig, (short)LeadCount,
                        out int ringIndex,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        _info);
            if (ok == 0) return; // no fresh data
            // You can inspect ringIndex, lead-off counters, etc., if needed
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PollOnce error");
        }
    }

    private void AllocBuffers()
    {
        FreeBuffers();
        int bytes = _bufSamples * sizeof(short);
        var zero = new byte[bytes]; // reused zero array
        for (int i = 0; i < LeadCount; i++)
        {
            _ecg[i] = Marshal.AllocHGlobal(bytes);
            _ecgOrig[i] = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(zero, 0, _ecg[i], bytes);
            Marshal.Copy(zero, 0, _ecgOrig[i], bytes);
        }
        _info = Marshal.AllocHGlobal(_bufSamples * sizeof(int));
        Marshal.Copy(new byte[_bufSamples * sizeof(int)], 0, _info, _bufSamples * sizeof(int));
    }

    private void FreeBuffers()
    {
        for (int i = 0; i < LeadCount; i++)
        {
            if (_ecg[i] != IntPtr.Zero) Marshal.FreeHGlobal(_ecg[i]);
            if (_ecgOrig[i] != IntPtr.Zero) Marshal.FreeHGlobal(_ecgOrig[i]);
            _ecg[i] = _ecgOrig[i] = IntPtr.Zero;
        }
        if (_info != IntPtr.Zero) Marshal.FreeHGlobal(_info);
        _info = IntPtr.Zero;
    }
}
