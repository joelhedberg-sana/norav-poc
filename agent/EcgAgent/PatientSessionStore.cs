using System.Text;

namespace EcgAgent;

public static class PatientSessionStore
{
    private static readonly object _lock = new();
    private static PatientSession? _current;

    public static PatientSession? GetActive()
    {
        lock (_lock)
        {
            if (_current is { } c && c.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _current = null;
                TryDeleteIni(c);
                return null;
            }
            return _current;
        }
    }

    public static (bool ok, PatientSession? session, string? error) Start(Demographics d, string iniPath, TimeSpan ttl, bool force, string sectionName)
    {
        lock (_lock)
        {
            if (_current != null && !_current.IsExpired && !force)
                return (false, _current, "active_session_exists");

            var now = DateTimeOffset.UtcNow;
            _current = new PatientSession(
                SessionId: Guid.NewGuid().ToString("n"),
                CreatedAt: now,
                ExpiresAt: now.Add(ttl),
                Demographics: d,
                IniPath: iniPath,
                Cleared: false);

            try
            {
                WriteIniAtomic(d, iniPath, sectionName);
            }
            catch (Exception ex)
            {
                _current = null;
                return (false, null, $"ini_write_failed:{ex.Message}");
            }

            return (true, _current, null);
        }
    }

    public static (PatientSession? previous, bool changed) Clear(string reason = "manual")
    {
        lock (_lock)
        {
            if (_current == null) return (null, false);
            if (_current.Cleared) return (_current, false);
            var prev = _current = _current with { Cleared = true, ClearedAt = DateTimeOffset.UtcNow, ClearReason = reason };
            TryDeleteIni(prev);
            return (prev, true);
        }
    }

    public static void AutoClearAfterMeasurement()
    {
        lock (_lock)
        {
            if (_current == null || _current.Cleared) return;
            var prev = _current = _current with { Cleared = true, ClearedAt = DateTimeOffset.UtcNow, ClearReason = "pdf-processed" };
            TryDeleteIni(prev);
        }
    }

    private static void WriteIniAtomic(Demographics d, string iniPath, string sectionName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        var tmp = iniPath + ".tmp";
        // Build with explicit CRLF regardless of platform; some Windows software requires CRLF
        var lines = new[]
        {
            $"[{sectionName}]",
            $"ID={d.PatientId}",
            $"LastName={d.LastName}",
            $"FirstName={d.FirstName}",
            $"BirthDay={d.BirthDay}",
            $"BirthMonth={d.BirthMonth}",
            $"BirthYear={d.BirthYear}",
            $"Sex={d.Sex}",
            $"Weight={d.Weight}",
            $"Height={d.Height}",
            $"Address={d.Address}",
            $"Phone1={d.Phone1}",
            $"Phone2={d.Phone2}",
            $"Fax={d.Fax}",
            $"E-Mail={d.Email}",
            $"Medications={d.Medications}",
            $"Other={d.Other}"
        };
        var text = string.Join("\r\n", lines) + "\r\n"; // ensure trailing newline
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, iniPath, true);
    }

    private static void TryDeleteIni(PatientSession session)
    {
        try
        {
            if (File.Exists(session.IniPath))
                File.Delete(session.IniPath);
        }
        catch { }
    }

    public record PatientSession(
        string SessionId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        Demographics Demographics,
        string IniPath,
        bool Cleared,
        DateTimeOffset? ClearedAt = null,
        string? ClearReason = null)
    {
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}