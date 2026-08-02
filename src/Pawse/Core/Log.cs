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
    private static string? _baseDir;

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
    /// %APPDATA%\Pawse if the exe directory can't be written to. The writability
    /// probe runs once per process - Config calls this on every save/load, and the
    /// answer can't meaningfully change mid-run.
    /// </summary>
    public static string ResolvePath(string filename) =>
        Path.Combine(_baseDir ??= ResolveBaseDir(), filename);

    private static string ResolveBaseDir()
    {
        // An existing pawse.json decides first: the writability probe depends on the
        // process token, so an elevated relaunch from e.g. Program Files would
        // otherwise resolve a DIFFERENT directory than the run that launched it -
        // and silently start from defaults, dropping the very settings (Win+L block)
        // that motivated elevating. Wherever the config already lives, that's home.
        var exeDir = ExeDir();
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pawse");
        try
        {
            if (File.Exists(Path.Combine(exeDir, "pawse.json"))) return exeDir;
            if (File.Exists(Path.Combine(appDataDir, "pawse.json"))) return appDataDir;
        }
        catch { /* fall through to the probe */ }

        try
        {
            var probe = Path.Combine(exeDir, ".pawse-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            try { Directory.CreateDirectory(appDataDir); } catch { /* ignore */ }
            return appDataDir;
        }
    }

    /// <summary>
    /// Drain the queue before the process ends. The writer is a background thread, so
    /// without this the last lines - shutdown, exactly the ones a support log needs -
    /// are killed mid-queue. Bounded by a timeout: exiting matters more than logging.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            Queue.CompleteAdding(); // Write() already ignores add-after-complete
            _writer?.Join(TimeSpan.FromSeconds(2));
        }
        catch { /* logging must never throw */ }
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
