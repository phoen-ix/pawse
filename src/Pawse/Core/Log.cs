using System.Collections.Concurrent;
using System.IO;

namespace Pawse.Core;

/// <summary>
/// Opt-in, buffered file logger. Resolves <c>pawse.log</c> next to the exe so it is trivial
/// to find, falling back to %APPDATA%\Pawse when that folder cannot be written; buffers every
/// line until <see cref="Enable"/> has read Config.General.Logging, then writes on a dedicated
/// thread - or drops the buffer when the answer is no.
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

    /// <summary>Whether anything reaches the file. Off until <see cref="Enable"/> says
    /// otherwise, and off is what a config that never mentions it means.</summary>
    private static volatile bool _enabled;

    /// <summary><see cref="Enable"/> has been called, so silence is now a decision rather
    /// than "the config hasn't been read yet".</summary>
    private static volatile bool _decided;

    /// <summary>
    /// Resolve where the log would go and record the banner - but write nothing yet. Whether
    /// a log exists at all is a user setting, and the config carrying it is not loaded for
    /// another twenty-odd lines of startup; buffering until <see cref="Enable"/> decides is
    /// what keeps those early lines when the answer turns out to be yes.
    /// </summary>
    public static void Init(string version)
    {
        _path = ResolvePath("pawse.log");
        Info(new string('=', 60));
        Info($"Pawse v{version} starting - log at {_path}");
    }

    /// <summary>
    /// Turn the log on or off. Called once the config is known and again whenever the setting
    /// changes. Off by default: Pawse sees every keystroke, so a file recording what it did
    /// is something to opt into rather than find later.
    /// </summary>
    public static void Enable(bool on)
    {
        if (!on)
        {
            // Order matters: _decided first, so a concurrent Write drops rather than
            // enqueueing behind the drain below and surviving into a log nobody asked for.
            _decided = true;
            _enabled = false;
            while (Queue.TryTake(out _)) { /* discard what was buffered pre-decision */ }
            return;
        }

        // ...and the other way round here, so a Write racing this is buffered, not dropped.
        _enabled = true;
        _decided = true;
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
        // that motivated elevating. Wherever the config already lives, that's home -
        // with two refinements for the per-machine install whose first run was elevated
        // and so left a pawse.json in Program Files: a file next to the exe that THIS
        // token cannot write is copied to %APPDATA% once and used from there (or every
        // later normal run would load it and never manage to save), and when both
        // places have one, %APPDATA% wins because every token can write it.
        var exeDir = ExeDir();
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pawse");
        var exeConfig = Path.Combine(exeDir, "pawse.json");
        var appDataConfig = Path.Combine(appDataDir, "pawse.json");
        try
        {
            if (File.Exists(appDataConfig)) return appDataDir;
            if (File.Exists(exeConfig))
            {
                if (CanWrite(exeDir)) return exeDir;
                try
                {
                    Directory.CreateDirectory(appDataDir);
                    File.Copy(exeConfig, appDataConfig);
                    Warn($"pawse.json next to the exe is read-only for this account - copied it to {appDataDir}");
                }
                catch { /* best effort; defaults will be written there below */ }
                return appDataDir;
            }
        }
        catch { /* fall through to the probe */ }

        if (CanWrite(exeDir)) return exeDir;
        try { Directory.CreateDirectory(appDataDir); } catch { /* ignore */ }
        return appDataDir;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".pawse-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
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
        // Decided and off: nothing is kept. Before the decision, lines are buffered - see Init.
        if (_decided && !_enabled) return;
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
