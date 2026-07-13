using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Pawse.Core;

/// <summary>
/// Dead-simple file logger. Writes <c>pawse.log</c> NEXT TO THE EXE so it is
/// trivial to find (the previous app buried it under %LOCALAPPDATA%). Falls back
/// to %APPDATA%\Pawse only if the exe directory is not writable.
///
/// <para>Writing is done on a dedicated background thread: callers only enqueue a
/// preformatted line, never touch the file. This matters because <see cref="Info"/>
/// is called from <c>LockController.Engage/Disengage</c>, which run inline on the
/// WH_KEYBOARD_LL hook callback - a blocking file write there could exceed the OS
/// LowLevelHooksTimeout and get the hook silently removed.</para>
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static Thread? _writer;
    private static string? _path;

    public static string? FilePath => _path;

    public static void Init(string version)
    {
        _path = ResolvePath("pawse.log");
        try
        {
            // Keep the log small: start fresh if it grew past ~1 MB.
            if (_path != null && File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                File.Delete(_path);
        }
        catch { /* logging must never throw */ }

        if (_writer == null)
        {
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "pawse-log" };
            _writer.Start();
        }

        Info(new string('=', 60));
        Info($"Pawse v{version} starting - log at {_path}");
    }

    /// <summary>Directory the running exe lives in.</summary>
    public static string ExeDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p))
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d)) return d;
            }
        }
        catch { /* ignore */ }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// A path for <paramref name="filename"/> next to the exe, or under
    /// %APPDATA%\Pawse if the exe directory can't be written to.
    /// </summary>
    public static string ResolvePath(string filename)
    {
        var exeDir = ExeDir();
        try
        {
            var probe = Path.Combine(exeDir, ".pawse-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(exeDir, filename);
        }
        catch
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Pawse");
            try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
            return Path.Combine(dir, filename);
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg} :: {ex}");

    private static void Write(string level, string msg)
    {
        // Timestamp at enqueue time so lines stay ordered even though the write is deferred.
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}";
        try { System.Diagnostics.Debug.WriteLine(line); } catch { /* ignore */ }
        // Never touch the file on the caller's thread (may be the keyboard hook).
        try { Queue.Add(line); } catch { /* adding disabled - ignore */ }
    }

    private static void WriterLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (_path != null) File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
    }
}
