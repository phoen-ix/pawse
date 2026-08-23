using System.IO;
using System.IO.Compression;
using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>
/// The portable self-replace. The rename dance is driven over real temp files with the
/// "start the successor" step injected, so the rollback paths - the ones that decide whether
/// a failed update leaves a working Pawse behind - are exercised without a second process.
/// </summary>
public class SelfReplaceSwapTests : IDisposable
{
    private readonly string _dir;
    private readonly string _exe;
    private readonly string _staged;
    private readonly string _previous;

    public SelfReplaceSwapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pawse-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Pawse.exe");
        _staged = _exe + ".new";
        _previous = _exe + ".old";
        File.WriteAllText(_exe, "old build");
        File.WriteAllText(_staged, "new build");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_successful_swap_hands_over_and_keeps_the_old_exe()
    {
        var outcome = SelfReplace.Swap(_exe, _staged, _previous, _ => true);

        Assert.Equal(ReplaceResult.Handover, outcome.Result);
        Assert.Equal("new build", File.ReadAllText(_exe));
        // Kept deliberately: the successor sweeps it once it actually starts, so the only
        // copy of the old build outlives the swap until the new one is proven to run.
        Assert.Equal("old build", File.ReadAllText(_previous));
        Assert.False(File.Exists(_staged));
    }

    [Fact]
    public void The_successor_is_started_with_the_replace_handshake()
    {
        string? started = null;
        SelfReplace.Swap(_exe, _staged, _previous, exe => { started = exe; return true; });

        // Started before this process exits, and at the real name - the --replace argument
        // itself is applied by StartSuccessor, which this seam stands in for.
        Assert.Equal(_exe, started);
    }

    /// <summary>If the replacement cannot even be launched, the old exe goes back.</summary>
    [Fact]
    public void A_successor_that_will_not_start_is_rolled_back()
    {
        var outcome = SelfReplace.Swap(_exe, _staged, _previous, _ => false);

        Assert.Equal(ReplaceResult.RolledBack, outcome.Result);
        Assert.Equal("old build", File.ReadAllText(_exe));
        Assert.False(File.Exists(_previous));
        Assert.False(File.Exists(_staged));
    }

    /// <summary>Being allowed to create files in the folder says nothing about renaming the
    /// running exe, so that rename is the real permission test - and it is first, because
    /// failing there has changed nothing at all.</summary>
    [Fact]
    public void A_locked_exe_refuses_before_anything_moves()
    {
        using (File.Open(_exe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = SelfReplace.Swap(_exe, _staged, _previous, _ => true);

            Assert.Equal(ReplaceResult.Refused, outcome.Result);
            Assert.True(File.Exists(_exe));
            Assert.False(File.Exists(_previous));
        }
        Assert.Equal("old build", File.ReadAllText(_exe));
    }

    [Fact]
    public void A_writable_folder_is_writable_and_a_bogus_one_is_not()
    {
        Assert.True(SelfReplace.CanWriteTo(_dir));
        Assert.False(SelfReplace.CanWriteTo(Path.Combine(_dir, "no", "such", "place")));
    }
}

/// <summary>
/// Unpacking the release zip. Release zips are Compress-Archive over exactly one file, so
/// insisting on "one flat entry" is both the real contract and what makes zip-slip and zip
/// bombs unrepresentable.
/// </summary>
public class SelfReplaceExtractTests : IDisposable
{
    private readonly string _dir;

    public SelfReplaceExtractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pawse-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>An MZ header, padded to a size that reads as the requested build.</summary>
    private static byte[] FakeExe(bool full)
    {
        var bytes = new byte[full ? UpdateCheck.FullBuildMinBytes + 1024 : 4096];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private string Zip(params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private bool Extract(string zip, InstallKind kind, out string? error) =>
        SelfReplace.ExtractSingleExe(zip, Path.Combine(_dir, "out.exe"), kind, "0.8.0", out error);

    [Fact]
    public void Unpacks_the_single_exe()
    {
        Assert.True(Extract(Zip(("Pawse.exe", FakeExe(full: true))), InstallKind.PortableFull, out var error));
        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(_dir, "out.exe")));
    }

    [Fact]
    public void The_minimal_zip_holds_the_launcher()
        => Assert.True(Extract(Zip(("Pawse-min.exe", FakeExe(full: false))), InstallKind.PortableMin, out _));

    /// <summary>Unpacking the launcher over a self-contained copy would leave a Pawse that
    /// cannot start for want of a runtime, so the sizes have to agree with the kind.</summary>
    [Fact]
    public void The_wrong_build_for_this_copy_is_refused()
    {
        Assert.False(Extract(Zip(("Pawse-min.exe", FakeExe(full: false))), InstallKind.PortableFull, out var error));
        Assert.Contains("wrong Pawse build", error);
    }

    [Fact]
    public void More_than_one_entry_is_not_one_of_ours()
    {
        Assert.False(Extract(
            Zip(("Pawse.exe", FakeExe(full: true)), ("readme.txt", new byte[] { 1 })),
            InstallKind.PortableFull, out var error));
        Assert.Contains("2 files", error);
    }

    [Fact]
    public void An_empty_archive_is_refused()
        => Assert.False(Extract(Zip(), InstallKind.PortableFull, out _));

    /// <summary>Zip-slip, unrepresentable rather than sanitised.</summary>
    [Theory]
    [InlineData("../Pawse.exe")]
    [InlineData("nested/Pawse.exe")]
    public void An_entry_carrying_a_path_is_refused(string name)
    {
        Assert.False(Extract(Zip((name, FakeExe(full: true))), InstallKind.PortableFull, out var error));
        Assert.Contains("plain Pawse exe", error);
    }

    [Fact]
    public void A_non_exe_entry_is_refused()
        => Assert.False(Extract(Zip(("Pawse.dll", FakeExe(full: true))), InstallKind.PortableFull, out _));

    [Fact]
    public void An_empty_entry_is_refused()
        => Assert.False(Extract(Zip(("Pawse.exe", Array.Empty<byte>())), InstallKind.PortableFull, out _));

    /// <summary>The checksum already bound the bytes; this catches a file that is not a
    /// program at all.</summary>
    [Fact]
    public void Something_that_is_not_a_windows_program_is_refused()
    {
        var notAnExe = new byte[UpdateCheck.FullBuildMinBytes + 1024];   // no MZ header
        Assert.False(Extract(Zip(("Pawse.exe", notAnExe)), InstallKind.PortableFull, out var error));
        Assert.Contains("not a Windows program", error);
    }
}
