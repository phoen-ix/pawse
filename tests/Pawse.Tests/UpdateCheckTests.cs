using System.IO;
using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.5.0", "0.6.0", true)]
    [InlineData("0.5.0", "0.5.1", true)]
    [InlineData("0.5.0", "v0.5.1", true)]   // release tags carry the v
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.6.0", "0.5.0", false)]   // never offer a downgrade
    [InlineData("0.5.0", "", false)]
    [InlineData("0.5.0", "unreleased", false)]
    [InlineData("", "0.6.0", false)]
    [InlineData(null, "0.6.0", false)]
    [InlineData("0.5.0", null, false)]
    public void Only_a_parseable_higher_version_counts(string? current, string? latest, bool expected)
        => Assert.Equal(expected, UpdateCheck.IsNewer(current, latest));
}

public class UpdateFeedTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string FullUrl = "https://github.com/phoen-ix/pawse/releases/download/v0.6.0/Pawse-Setup-0.6.0-full.exe";

    private static string Feed(string url = FullUrl, string sha = Sha) => $$"""
        {
          "version": "0.6.0",
          "notes": "https://github.com/phoen-ix/pawse/releases/tag/v0.6.0",
          "installers": {
            "full": { "url": "{{url}}", "sha256": "{{sha}}" },
            "min":  { "url": "https://github.com/phoen-ix/pawse/releases/download/v0.6.0/Pawse-Setup-0.6.0-min.exe", "sha256": "{{Sha}}" }
          }
        }
        """;

    [Fact]
    public void Reads_version_notes_and_both_installers()
    {
        var info = UpdateCheck.Parse(Feed());

        Assert.NotNull(info);
        Assert.Equal("0.6.0", info!.Version);
        Assert.Equal("https://github.com/phoen-ix/pawse/releases/tag/v0.6.0", info.NotesUrl);
        Assert.Equal(FullUrl, info.Full!.Url);
        Assert.Equal(Sha, info.Full.Sha256);
        Assert.EndsWith("-min.exe", info.Min!.Url);
    }

    [Fact]
    public void A_checksum_is_compared_lower_case()
        => Assert.Equal(Sha, UpdateCheck.Parse(Feed(sha: Sha.ToUpperInvariant()))!.Full!.Sha256);

    [Theory]
    [InlineData("http://example.com/Pawse-Setup.exe")] // plain http - nothing to trust
    [InlineData("file:///C:/Pawse-Setup.exe")]
    [InlineData("not a url")]
    public void An_asset_that_is_not_an_https_url_is_dropped(string url)
        => Assert.Null(UpdateCheck.Parse(Feed(url: url))!.Full);

    [Theory]
    [InlineData("abc")]                     // too short
    [InlineData("zzz9abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456")] // not hex
    public void An_asset_without_a_real_sha256_is_dropped(string sha)
        => Assert.Null(UpdateCheck.Parse(Feed(sha: sha))!.Full);

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"notes":"https://pawse.at"}""")]              // no version
    [InlineData("""{"version":"soon"}""")]                        // unparseable version
    [InlineData("""{"version":"0.6.0","installers":42}""")]       // wrong shape
    public void A_broken_feed_reads_as_no_answer_not_as_an_update(string json)
    {
        var info = UpdateCheck.Parse(json);
        // The last case still parses - it just has no installers, which the caller turns
        // into "open the downloads page" rather than a download.
        Assert.True(info is null || (info.Full is null && info.Min is null));
    }

    [Fact]
    public void A_feed_without_notes_falls_back_to_the_releases_page()
    {
        var info = UpdateCheck.Parse("""{"version":"0.6.0"}""");
        Assert.Equal(UpdateCheck.ReleasesUrl, info!.NotesUrl);
    }
}

public class UpdateInstallKindTests
{
    private const long FullExe = 63L * 1024 * 1024;
    private const long MinExe = 200L * 1024;

    [Fact]
    public void No_uninstall_entry_means_portable()
        => Assert.Equal(InstallKind.Portable,
            UpdateCheck.DetectInstall(@"C:\Users\me\Downloads\pawse", FullExe, () => null));

    [Fact]
    public void An_entry_pointing_somewhere_else_means_portable()
        => Assert.Equal(InstallKind.Portable,
            UpdateCheck.DetectInstall(@"C:\Users\me\Downloads\pawse", FullExe, () => @"C:\Program Files\Pawse"));

    [Theory]
    [InlineData(@"C:\Program Files\Pawse")]
    [InlineData(@"C:\Program Files\Pawse\")]        // trailing separator
    [InlineData(@"c:\program files\pawse")]         // Windows paths are case-insensitive
    public void An_entry_for_this_folder_means_installed(string installLocation)
        => Assert.Equal(InstallKind.InstalledFull,
            UpdateCheck.DetectInstall(@"C:\Program Files\Pawse", FullExe, () => installLocation));

    [Fact]
    public void The_launcher_sized_exe_is_the_minimal_build()
        => Assert.Equal(InstallKind.InstalledMin,
            UpdateCheck.DetectInstall(@"C:\Program Files\Pawse", MinExe, () => @"C:\Program Files\Pawse"));
}

public class UpdateScheduleTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Never_checked_is_due()
        => Assert.True(UpdateCheck.IsCheckDue(null, Now));

    [Fact]
    public void Checked_an_hour_ago_is_not_due()
        => Assert.False(UpdateCheck.IsCheckDue(Now.AddHours(-1), Now));

    [Fact]
    public void Checked_a_day_ago_is_due()
        => Assert.True(UpdateCheck.IsCheckDue(Now - UpdateCheck.AutoCheckInterval, Now));

    [Fact]
    public void A_stamp_in_the_future_is_due_rather_than_parked()
        => Assert.True(UpdateCheck.IsCheckDue(Now.AddYears(1), Now));
}

public class UpdateHashTests
{
    [Fact]
    public void Hashes_a_file_as_lower_case_hex()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                UpdateCheck.Sha256File(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
