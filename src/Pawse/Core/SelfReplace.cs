using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Pawse.Core;

/// <summary>What came of trying to replace the running exe.</summary>
public enum ReplaceResult
{
    /// <summary>The new exe is in place and running - the caller must shut down now.</summary>
    Handover,

    /// <summary>Nothing was touched. Offer the downloads page instead.</summary>
    Refused,

    /// <summary>Something failed mid-swap and the running exe was put back.</summary>
    RolledBack,

    /// <summary>The swap failed AND so did the rollback. This process is now the only working
    /// copy of Pawse on the machine - it must NOT exit, and the user has to be told.</summary>
    Stranded,
}

public sealed record ReplaceOutcome(ReplaceResult Result, string Message);

/// <summary>
/// Updating a portable copy, which has no installer to run: replace the exe in place.
///
/// <para>Windows refuses to delete or overwrite a running exe but allows it to be
/// <em>renamed</em>, and a rename inside one directory is atomic. So the new build is
/// unpacked <em>next to</em> the old one - never into %TEMP% and moved, which across volumes
/// degrades into a non-atomic copy+delete - and three renames do the swap. The old exe is
/// kept until the new one has actually started, so there is a way back from every step.</para>
/// </summary>
public static class SelfReplace
{
    internal const string StagedSuffix = ".new";
    internal const string PreviousSuffix = ".old";

    /// <summary>A Pawse exe is ~63 MB at the very most; a zip claiming far more is not one of
    /// ours, and unpacking it could fill the disk pawse.json lives on.</summary>
    private const long MaxExeBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Replace the running exe with the one inside <paramref name="zipPath"/>, which is
    /// consumed on the way. <see cref="ReplaceResult.Handover"/> is the only result that
    /// means "shut down now".
    /// </summary>
    public static ReplaceOutcome Run(string zipPath, InstallKind kind, string version)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return new(ReplaceResult.Refused, "Pawse could not find its own exe.");
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir))
            return new(ReplaceResult.Refused, "Pawse could not find its own folder.");
        if (!CanWriteTo(dir))
            return new(ReplaceResult.Refused,
                $"Pawse cannot write to {dir}, so it can't replace itself there.");

        var staged = exe + StagedSuffix;
        var previous = exe + PreviousSuffix;

        // Leftovers first. A .new from an interrupted attempt is stale by definition, and a
        // .old still sitting here means the last sweep could not remove it - which means the
        // name we need to move the running exe aside to is occupied.
        TryDelete(staged);
        TryDelete(previous);
        if (File.Exists(previous))
            return new(ReplaceResult.Refused,
                $"{previous} is still in use, so there is nowhere to move the current Pawse aside to.");

        if (!ExtractSingleExe(zipPath, staged, kind, version, out var error))
        {
            TryDelete(staged);
            return new(ReplaceResult.Refused, error!);
        }
        TryDelete(zipPath);

        return Swap(exe, staged, previous, StartSuccessor);
    }

    /// <summary>
    /// The three renames and their undo. <paramref name="start"/> is injected so the whole
    /// dance is testable without a second Pawse actually appearing.
    /// </summary>
    internal static ReplaceOutcome Swap(string exe, string staged, string previous, Func<string, bool> start)
    {
        // 1) Move the running exe aside. This is the real permission test - being allowed to
        //    create a file in the folder says nothing about renaming THIS file - and failing
        //    here has changed nothing at all.
        try { File.Move(exe, previous); }
        catch (Exception ex)
        {
            Log.Error("update: moving the current exe aside", ex);
            TryDelete(staged);
            return new(ReplaceResult.Refused,
                "Pawse could not move its current exe aside, so nothing was changed.");
        }

        // 2) The swap. Every failure from here has to be undone.
        try { File.Move(staged, exe); }
        catch (Exception ex)
        {
            Log.Error("update: moving the new exe into place", ex);
            return Undo(exe, previous, staged, newExeInPlace: false,
                "The update could not be put in place, so the previous Pawse was restored.");
        }

        // 3) Start the successor BEFORE this process exits. If it cannot even be started we
        //    still have a running process and a .old to restore from; having exited first we
        //    would have neither.
        if (!start(exe))
            return Undo(exe, previous, staged, newExeInPlace: true,
                "The updated Pawse could not be started, so the previous one was restored.");

        // .old survives until the successor's own startup sweeps it - so the only copy of the
        // old build is deleted by the new one, which proves the new one runs.
        Log.Info($"update: handed over to {exe}; keeping {previous} until it starts");
        return new(ReplaceResult.Handover, "");
    }

    /// <summary>Put the running exe back. <paramref name="newExeInPlace"/> means the new exe
    /// currently holds the real name and has to be moved out of the way first.</summary>
    private static ReplaceOutcome Undo(string exe, string previous, string staged,
                                       bool newExeInPlace, string message)
    {
        try
        {
            if (newExeInPlace && File.Exists(exe)) File.Move(exe, staged);
            File.Move(previous, exe);
            TryDelete(staged);
            Log.Warn("update: rolled back - " + message);
            return new(ReplaceResult.RolledBack, message);
        }
        catch (Exception ex)
        {
            // This process is still mapped from the file now called .old, so Pawse keeps
            // working until it exits - after which there is no Pawse.exe at all.
            Log.Error($"update: ROLLBACK FAILED - {previous} could not be moved back to {exe}", ex);
            return new(ReplaceResult.Stranded,
                "The update failed and Pawse could not put its previous exe back.\n\n" +
                $"Do not close Pawse yet. Rename\n\n{previous}\n\nback to\n\n{exe}\n\n" +
                "and it will start normally again.");
        }
    }

    /// <summary>Launch the replacement, telling it to wait for our single-instance mutex -
    /// the same --replace handshake the elevated relaunch uses. Without it the successor
    /// races us and says "Pawse is already running" instead of starting.</summary>
    private static bool StartSuccessor(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Elevation.ReplaceArg,
                // false, not true: no shell association lookup on the way. An HttpClient
                // download carries no mark of the web, so SmartScreen has nothing to react
                // to - keep it that way.
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update: starting the replacement", ex);
            return false;
        }
    }

    /// <summary>
    /// Can we write into the folder the exe lives in? A portable copy under Program Files, on
    /// a read-only share, or in a folder Controlled Folder Access guards - Desktop, Documents,
    /// Downloads, which is exactly where portable copies land - cannot replace itself, and
    /// finding that out after the download and the first rename is how you end up with no
    /// working exe at all.
    ///
    /// <para>DeleteOnClose so a crash between the write and the delete leaves nothing behind.
    /// This is a necessary condition, not a sufficient one: renaming the running exe needs
    /// DELETE on the file itself, which only the rename in <see cref="Swap"/> can prove.</para>
    /// </summary>
    internal static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".pawse-update-test");
            using var file = new FileStream(probe, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            file.WriteByte(0);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Unpack the single exe a release zip holds. Release zips are Compress-Archive over one
    /// file, so "exactly one flat entry" is the whole contract - and insisting on it disposes
    /// of zip-slip and zip bombs without a single path-normalisation call.
    /// </summary>
    internal static bool ExtractSingleExe(string zipPath, string destPath, InstallKind kind,
                                          string version, out string? error)
    {
        error = null;
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count != 1)
                {
                    error = $"The download holds {zip.Entries.Count} files, not the single Pawse exe it should.";
                    return false;
                }

                var entry = zip.Entries[0];
                // FullName == Name means the entry sits at the root and carries no path.
                if (entry.FullName != entry.Name
                    || entry.Name.Length == 0
                    || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || !entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = "The download does not contain a plain Pawse exe.";
                    return false;
                }

                if (entry.Length is <= 0 or > MaxExeBytes)
                {
                    error = "The exe inside the download is not a plausible size.";
                    return false;
                }

                // The full zip must hold the self-contained exe and the min zip the launcher.
                // The wrong way round leaves a portable minimal copy that cannot start at all
                // for want of a runtime - caught here, while nothing on disk has moved.
                bool wantFull = kind == InstallKind.PortableFull;
                if (wantFull != entry.Length >= UpdateCheck.FullBuildMinBytes)
                {
                    error = "The download is the wrong Pawse build for this copy.";
                    return false;
                }

                var dir = Path.GetDirectoryName(destPath)!;
                if (FreeSpace(dir) is { } free && free < entry.Length + 16L * 1024 * 1024)
                {
                    error = $"There is not enough free space in {dir} to unpack the update.";
                    return false;
                }

                entry.ExtractToFile(destPath, overwrite: true);
            }

            if (!LooksLikeWindowsExe(destPath))
            {
                error = "The unpacked file is not a Windows program.";
                return false;
            }

            // The checksum already binds these bytes to what the feed named; this catches the
            // feed and the release tag disagreeing about which version that was. A build whose
            // version resource cannot be read is accepted with a warning - refusing it would
            // hold updates hostage to a resource format we do not control.
            var found = FileVersionOf(destPath);
            if (found is not null && !UpdateCheck.SameVersion(found, version))
            {
                error = $"The unpacked Pawse reports version {found}, not {version}.";
                return false;
            }
            if (found is null) Log.Warn("update: the unpacked exe carries no readable version resource");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update: unpacking the download", ex);
            error = "The download could not be unpacked.";
            return false;
        }
    }

    /// <summary>
    /// Remove what a completed self-replace left behind. Called at every start: the previous
    /// exe can only go once the process that ran from it has fully exited and Windows has
    /// unmapped its image, which is a moment AFTER it released the mutex. Returns true when
    /// there is nothing left to remove.
    /// </summary>
    public static bool SweepLeftovers()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return true;
        bool clean = TryDelete(exe + PreviousSuffix);
        clean &= TryDelete(exe + StagedSuffix);   // an attempt that never reached the swap
        return clean;
    }

    private static long? FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }   // a UNC path or an odd volume - let the write itself decide
    }

    private static bool LooksLikeWindowsExe(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static string? FileVersionOf(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch { return null; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
