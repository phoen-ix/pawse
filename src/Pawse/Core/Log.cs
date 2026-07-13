using System.IO;

namespace Pawse.Core;

/// <summary>
/// Dead-simple file logger. Writes <c>pawse.log</c> NEXT TO THE EXE so it is
/// trivial to find (the previous app buried it under %LOCALAPPDATA%). Falls back
/// to %APPDATA%\Pawse only if the exe directory is not writable.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
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
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}";
        lock (Gate)
        {
            try
            {
                if (_path != null) File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
        try { System.Diagnostics.Debug.WriteLine(line); } catch { /* ignore */ }
    }
}
