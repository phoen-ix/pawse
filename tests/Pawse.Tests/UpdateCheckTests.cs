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
    [InlineData("zzz3456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // 64 chars, not hex
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
        => Assert.Equal(InstallKind.PortableFull,
            UpdateCheck.DetectInstall(@"C:\Users\me\Downloads\pawse", FullExe, () => null));

    [Fact]
    public void An_entry_pointing_somewhere_else_means_portable()
        => Assert.Equal(InstallKind.PortableFull,
            UpdateCheck.DetectInstall(@"C:\Users\me\Downloads\pawse", FullExe, () => @"C:\Program Files\Pawse"));

    [Fact]
    public void A_launcher_sized_portable_copy_is_the_minimal_zip()
        => Assert.Equal(InstallKind.PortableMin,
            UpdateCheck.DetectInstall(@"C:\Users\me\Downloads\pawse", MinExe, () => null));

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

    /// <summary>What the installer recorded beats the size heuristic - the whole reason
    /// BuildVariant exists.</summary>
    [Theory]
    [InlineData("full", InstallKind.InstalledFull)]
    [InlineData("min", InstallKind.InstalledMin)]
    public void The_recorded_build_variant_wins(string variant, InstallKind expected)
        => Assert.Equal(expected, UpdateCheck.DetectInstall(
            @"C:\Program Files\Pawse", 0, () => @"C:\Program Files\Pawse", () => variant));

    /// <summary>ProcessSizeBytes answers 0 when it cannot stat the exe, and 0 is below the
    /// 20 MB divider - so without a recorded variant a full install reads as minimal, and
    /// would be offered an installer whose runtime the machine may not have. Pinned so the
    /// fallback's sharp edge stays visible.</summary>
    [Fact]
    public void An_unreadable_size_falls_back_to_the_small_build()
        => Assert.Equal(InstallKind.InstalledMin, UpdateCheck.DetectInstall(
            @"C:\Program Files\Pawse", 0, () => @"C:\Program Files\Pawse"));

    [Fact]
    public void A_portable_copy_ignores_a_stray_build_variant()
        => Assert.Equal(InstallKind.PortableFull, UpdateCheck.DetectInstall(
            @"C:\Users\me\Downloads\pawse", FullExe, () => null, () => "min"));
}

public class UpdateScopeTests
{
    private const string Dir = @"C:\Program Files\Pawse";

    [Fact]
    public void A_per_user_entry_wins_over_a_per_machine_one()
        => Assert.Equal(InstallScope.PerUser, UpdateCheck.ScopeOf(Dir, Dir, Dir));

    [Fact]
    public void Only_a_machine_entry_reads_as_per_machine()
        => Assert.Equal(InstallScope.PerMachine, UpdateCheck.ScopeOf(Dir, null, Dir));

    [Fact]
    public void An_entry_for_another_folder_does_not_count()
        => Assert.Equal(InstallScope.None, UpdateCheck.ScopeOf(Dir, @"D:\Elsewhere", null));

    [Fact]
    public void No_entry_at_all_is_no_scope()
        => Assert.Equal(InstallScope.None, UpdateCheck.ScopeOf(Dir, null, null));

    [Fact]
    public void The_comparison_tolerates_case_and_a_trailing_separator()
        => Assert.Equal(InstallScope.PerUser, UpdateCheck.ScopeOf(Dir, @"c:\program files\pawse\", null));
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

/// <summary>The GitHub side: the version comes out of where /releases/latest redirects to,
/// which costs no API rate limit. Anything that is not a release tag page must read as "no
/// answer" so the check falls back to the feed rather than inventing a version.</summary>
public class UpdateGitHubProbeTests
{
    [Theory]
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/v0.8.0", "0.8.0")]
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/0.8.0", "0.8.0")]   // no v
    [InlineData("/phoen-ix/pawse/releases/tag/v0.8.0", "0.8.0")]                    // relative
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/v0.8.0.1", "0.8.0")] // 4 parts
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/v01.2.3", "1.2.3")]
    [InlineData("https://github.com/other/repo/releases/tag/v9.9.9", "9.9.9")]      // after a rename
    public void The_tag_in_the_redirect_is_the_version(string location, string expected)
        => Assert.Equal(expected, UpdateCheck.VersionFromTagUrl(location));

    [Theory]
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/v0.8")]   // ToString(3) would throw
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/nightly")]
    [InlineData("https://github.com/phoen-ix/pawse/releases/latest")]     // never resolved
    [InlineData("https://github.com/phoen-ix/pawse/releases")]
    [InlineData("https://github.com/login?return_to=%2Fphoen-ix%2Fpawse")]
    [InlineData("https://example.com/phoen-ix/pawse/releases/tag/v0.8.0")] // wrong host
    [InlineData("http://github.com/phoen-ix/pawse/releases/tag/v0.8.0")]   // not https
    [InlineData("https://github.com/phoen-ix/pawse/releases/tag/")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_no_answer(string? location)
        => Assert.Null(UpdateCheck.VersionFromTagUrl(location));
}

/// <summary>SHA256SUMS.txt is the fallback checksum source, and it is fetched from the same
/// host as the binary - so parsing it has to be strict about what it accepts.</summary>
public class UpdateSumsTests
{
    private const string Hash = "ec59f5f4127ac5073da011179945414c01a5d5411141a624543c04a92b3e697d";
    private const string Other = "7f5be3ab90b13e3ac24461e1fda377e3d47f47ba2bf301b6bf19134e5f41da7f";

    [Fact]
    public void Reads_a_hash_out_of_the_crlf_file_ci_writes()
        => Assert.Equal(Hash, UpdateCheck.Sha256From(
            $"{Other}  Pawse-0.8.0-min.zip\r\n{Hash}  Pawse-0.8.0.zip\r\n", "Pawse-0.8.0.zip"));

    [Fact]
    public void Reads_lf_endings_too()
        => Assert.Equal(Hash, UpdateCheck.Sha256From($"{Hash}  Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));

    [Fact]
    public void Accepts_the_binary_mode_marker()
        => Assert.Equal(Hash, UpdateCheck.Sha256From($"{Hash} *Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));

    [Fact]
    public void Uppercase_hex_comes_back_lowercased()
        => Assert.Equal(Hash, UpdateCheck.Sha256From(
            $"{Hash.ToUpperInvariant()}  Pawse-0.8.0.zip", "Pawse-0.8.0.zip"));

    [Fact]
    public void Skips_blank_and_comment_lines()
        => Assert.Equal(Hash, UpdateCheck.Sha256From(
            $"# generated by CI\n\n{Hash}  Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));

    /// <summary>Two lines for one file that disagree is a rejection, not a coin toss.</summary>
    [Fact]
    public void A_file_listed_twice_with_different_hashes_is_refused()
        => Assert.Null(UpdateCheck.Sha256From(
            $"{Hash}  Pawse-0.8.0.zip\n{Other}  Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));

    [Fact]
    public void A_file_listed_twice_with_the_same_hash_is_fine()
        => Assert.Equal(Hash, UpdateCheck.Sha256From(
            $"{Hash}  Pawse-0.8.0.zip\n{Hash}  Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));

    [Theory]
    [InlineData("Pawse-0.8.0-min.zip")]                       // a name that isn't listed
    [InlineData("Pawse-0.9.0.zip")]
    public void An_absent_name_has_no_hash(string name)
        => Assert.Null(UpdateCheck.Sha256From($"{Hash}  Pawse-0.8.0.zip\n", name));

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>404</body></html>")]  // a GitHub error page
    [InlineData("abc  Pawse-0.8.0.zip")]                          // too short to be a hash
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_sums_file_yields_nothing(string? text)
        => Assert.Null(UpdateCheck.Sha256From(text, "Pawse-0.8.0.zip"));

    /// <summary>A path in the name field must not match a bare leaf name.</summary>
    [Fact]
    public void A_path_is_not_the_leaf_name()
        => Assert.Null(UpdateCheck.Sha256From($"{Hash}  dist/Pawse-0.8.0.zip\n", "Pawse-0.8.0.zip"));
}

/// <summary>The decision table: what to do given what each source said. All of it pure, so
/// the awkward combinations - especially the window right after a release, where GitHub has
/// the new version and the feed has not caught up - are testable without a network.</summary>
public class UpdatePlanTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Current = "0.7.1";

    private static UpdateInfo Feed(string version, bool auto = true, bool withAssets = true) =>
        new(version,
            $"https://github.com/phoen-ix/pawse/releases/tag/v{version}",
            withAssets ? new UpdateAsset($"https://github.com/x/y/releases/download/v{version}/f.exe", Sha) : null,
            withAssets ? new UpdateAsset($"https://github.com/x/y/releases/download/v{version}/m.exe", Sha) : null,
            withAssets ? new UpdateAsset($"https://github.com/x/y/releases/download/v{version}/f.zip", Sha) : null,
            withAssets ? new UpdateAsset($"https://github.com/x/y/releases/download/v{version}/m.zip", Sha) : null,
            auto);

    private static string Sums(string version, InstallKind kind) =>
        $"{Sha}  {UpdateCheck.AssetName(kind, version)}\r\n";

    [Fact]
    public void Neither_source_answering_is_unknown()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, null, null, null, "offline");
        Assert.Equal(UpdateVerdict.Unknown, plan.Verdict);
        Assert.Equal("offline", plan.Error);
    }

    [Fact]
    public void Both_agreeing_installs_from_the_feed_checksum()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, "0.8.0", Feed("0.8.0"), null, null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.Equal(ChecksumSource.Feed, plan.Checksum);
        Assert.True(UpdateCheck.MayInstallUnattended(plan));
    }

    /// <summary>And in that case the sums file is never even needed.</summary>
    [Fact]
    public void Both_agreeing_needs_no_sums_request()
        => Assert.False(UpdateCheck.NeedsSums(Current, InstallKind.InstalledFull, "0.8.0", Feed("0.8.0")));

    /// <summary>The feed is committed after the release publishes, so this window is real.</summary>
    [Fact]
    public void A_lagging_feed_falls_back_to_the_release_sums()
    {
        Assert.True(UpdateCheck.NeedsSums(Current, InstallKind.InstalledFull, "0.8.0", Feed(Current)));

        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, "0.8.0", Feed(Current),
                                    Sums("0.8.0", InstallKind.InstalledFull), null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.Equal(ChecksumSource.GitHubSums, plan.Checksum);
        // Same host as the binary, so a check nobody watched must not act on it.
        Assert.False(UpdateCheck.MayInstallUnattended(plan));
    }

    [Fact]
    public void A_lagging_feed_and_no_sums_only_notifies()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, "0.8.0", Feed(Current), null, null);
        Assert.Equal(UpdateVerdict.Available, plan.Verdict);
        Assert.Equal(ChecksumSource.None, plan.Checksum);
        Assert.Null(plan.Asset);
    }

    [Fact]
    public void Github_alone_still_installs_via_the_sums()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledMin, "0.8.0", null,
                                    Sums("0.8.0", InstallKind.InstalledMin), null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.Equal(ChecksumSource.GitHubSums, plan.Checksum);
        Assert.Equal("Pawse-Setup-0.8.0-min.exe", plan.FileName);
    }

    [Fact]
    public void The_feed_alone_installs_from_its_own_checksum()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, null, Feed("0.8.0"), null, null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.Equal(ChecksumSource.Feed, plan.Checksum);
    }

    /// <summary>GitHub decides the version even when it says there is nothing new. Falling
    /// back to a feed that still advertises a YANKED release would resurrect it.</summary>
    [Fact]
    public void Github_saying_nothing_is_newer_beats_a_feed_that_disagrees()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, Current, Feed("0.9.0"), null, null);
        Assert.Equal(UpdateVerdict.UpToDate, plan.Verdict);
    }

    [Fact]
    public void A_feed_ahead_of_github_does_not_lend_its_checksum_to_githubs_version()
    {
        // Feed says 0.9.0, GitHub says 0.8.0: the version is 0.8.0, and the feed's 0.9.0
        // hashes must not be used for it.
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, "0.8.0", Feed("0.9.0"), null, null);
        Assert.Equal("0.8.0", plan.Version);
        Assert.NotEqual(ChecksumSource.Feed, plan.Checksum);
    }

    [Fact]
    public void An_older_release_is_never_offered()
        => Assert.Equal(UpdateVerdict.UpToDate,
            UpdateCheck.Plan("0.9.0", InstallKind.InstalledFull, "0.8.0", Feed("0.8.0"), null, null).Verdict);

    [Fact]
    public void A_feed_that_paused_auto_installs_still_offers_the_update()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.InstalledFull, "0.8.0", Feed("0.8.0", auto: false), null, null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.False(plan.FeedAllowsAuto);
        Assert.False(UpdateCheck.MayInstallUnattended(plan));   // ...but not on its own
    }

    [Fact]
    public void A_portable_copy_uses_the_portable_zip()
    {
        var plan = UpdateCheck.Plan(Current, InstallKind.PortableFull, "0.8.0", Feed("0.8.0"), null, null);
        Assert.Equal(UpdateVerdict.Installable, plan.Verdict);
        Assert.Equal("Pawse-0.8.0.zip", plan.FileName);
    }

    /// <summary>A feed with no portable block - every feed written before this change.</summary>
    [Fact]
    public void A_feed_without_portable_entries_falls_back_to_the_sums()
    {
        var old = new UpdateInfo("0.8.0", UpdateCheck.ReleasesUrl,
            new UpdateAsset("https://github.com/x/y/f.exe", Sha), null);
        Assert.True(UpdateCheck.NeedsSums(Current, InstallKind.PortableMin, "0.8.0", old));

        var plan = UpdateCheck.Plan(Current, InstallKind.PortableMin, "0.8.0", old,
                                    Sums("0.8.0", InstallKind.PortableMin), null);
        Assert.Equal(ChecksumSource.GitHubSums, plan.Checksum);
        Assert.Equal("Pawse-0.8.0-min.zip", plan.FileName);
    }

    [Theory]
    [InlineData(InstallKind.InstalledFull, "Pawse-Setup-0.8.0-full.exe")]
    [InlineData(InstallKind.InstalledMin, "Pawse-Setup-0.8.0-min.exe")]
    [InlineData(InstallKind.PortableFull, "Pawse-0.8.0.zip")]
    [InlineData(InstallKind.PortableMin, "Pawse-0.8.0-min.zip")]
    public void Each_kind_names_its_own_asset(InstallKind kind, string expected)
        => Assert.Equal(expected, UpdateCheck.AssetName(kind, "0.8.0"));

    [Fact]
    public void Asset_urls_are_predictable_from_the_version()
        => Assert.Equal("https://github.com/phoen-ix/pawse/releases/download/v0.8.0/SHA256SUMS.txt",
            UpdateCheck.SumsUrl("0.8.0"));
}

/// <summary>Backing off from an automatic install that did not take.</summary>
public class UpdateAutoRetryTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_version_never_tried_may_be_installed()
        => Assert.True(UpdateCheck.MayRetryAutoInstall("0.8.0", null, null, Now));

    [Fact]
    public void The_same_version_is_not_retried_the_next_day()
        => Assert.False(UpdateCheck.MayRetryAutoInstall("0.8.0", "0.8.0", Now.AddDays(-1), Now));

    [Fact]
    public void A_different_version_is_always_worth_a_try()
        => Assert.True(UpdateCheck.MayRetryAutoInstall("0.9.0", "0.8.0", Now.AddDays(-1), Now));

    [Fact]
    public void The_same_version_comes_round_again_after_a_week()
        => Assert.True(UpdateCheck.MayRetryAutoInstall(
            "0.8.0", "0.8.0", Now - UpdateCheck.AutoRetryInterval, Now));

    [Fact]
    public void A_stamp_in_the_future_is_not_a_reason_to_wait()
        => Assert.True(UpdateCheck.MayRetryAutoInstall("0.8.0", "0.8.0", Now.AddYears(1), Now));
}

/// <summary>The feed keys added for portable self-replace and the fleet-wide auto brake.
/// Both must be optional: a feed written before they existed has to keep working.</summary>
public class UpdateFeedPortableTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Feed(string extra) => $$"""
        {
          "version": "0.8.0",
          "installers": { "full": { "url": "https://github.com/x/y/f.exe", "sha256": "{{Sha}}" } }
          {{extra}}
        }
        """;

    [Fact]
    public void Reads_the_portable_zips()
    {
        var info = UpdateCheck.Parse(Feed($$"""
            , "portable": {
                "full": { "url": "https://github.com/x/y/Pawse-0.8.0.zip", "sha256": "{{Sha}}" },
                "min":  { "url": "https://github.com/x/y/Pawse-0.8.0-min.zip", "sha256": "{{Sha}}" }
              }
            """));

        Assert.EndsWith("Pawse-0.8.0.zip", info!.PortableFull!.Url);
        Assert.EndsWith("Pawse-0.8.0-min.zip", info.PortableMin!.Url);
    }

    [Fact]
    public void A_feed_with_no_portable_block_still_parses()
    {
        var info = UpdateCheck.Parse(Feed(""));
        Assert.NotNull(info);
        Assert.Null(info!.PortableFull);
        Assert.NotNull(info.Full);   // the rest is untouched
    }

    [Fact]
    public void Automatic_installs_are_allowed_unless_the_feed_says_false()
    {
        Assert.True(UpdateCheck.Parse(Feed(""))!.AllowsAuto);
        Assert.True(UpdateCheck.Parse(Feed(""", "auto": true"""))!.AllowsAuto);
        Assert.False(UpdateCheck.Parse(Feed(""", "auto": false"""))!.AllowsAuto);
    }

    /// <summary>Only a literal false pauses them - a typo must not silently disable the
    /// feature for everyone.</summary>
    [Theory]
    [InlineData(", \"auto\": \"no\"")]
    [InlineData(", \"auto\": 0")]
    [InlineData(", \"auto\": null")]
    public void Anything_other_than_false_leaves_them_allowed(string extra)
        => Assert.True(UpdateCheck.Parse(Feed(extra))!.AllowsAuto);

    [Fact]
    public void A_portable_asset_without_a_real_checksum_is_dropped()
        => Assert.Null(UpdateCheck.Parse(Feed("""
            , "portable": { "full": { "url": "https://github.com/x/y/p.zip", "sha256": "nope" } }
            """))!.PortableFull);
}

/// <summary>When a check retries. The attempt cap alone is not enough: two hosts timing out
/// costs ~20 s an attempt, so five of those would sit on "Checking…" for nearly two minutes -
/// hence a wall-clock budget as well.</summary>
public class UpdateRetryPolicyTests
{
    [Fact]
    public void A_first_failure_is_worth_another_go()
        => Assert.True(UpdateCheck.ShouldRetryCheck(completed: 1, TimeSpan.Zero));

    [Fact]
    public void It_stops_at_the_attempt_cap()
    {
        Assert.True(UpdateCheck.ShouldRetryCheck(UpdateCheck.MaxCheckAttempts - 1, TimeSpan.Zero));
        Assert.False(UpdateCheck.ShouldRetryCheck(UpdateCheck.MaxCheckAttempts, TimeSpan.Zero));
    }

    /// <summary>Slow failures run out of budget before they run out of attempts.</summary>
    [Fact]
    public void It_stops_once_the_budget_is_spent()
        => Assert.False(UpdateCheck.ShouldRetryCheck(completed: 2, UpdateCheck.CheckRetryBudget));

    /// <summary>And it won't start an attempt it has no room for: the gap alone would take it
    /// past the budget.</summary>
    [Fact]
    public void It_does_not_start_an_attempt_it_cannot_fit()
        => Assert.False(UpdateCheck.ShouldRetryCheck(
            completed: 1, UpdateCheck.CheckRetryBudget - TimeSpan.FromMilliseconds(1)));

    /// <summary>A fast failure - offline, DNS answering immediately - gets every attempt.</summary>
    [Fact]
    public void Fast_failures_get_the_full_five()
    {
        var elapsed = TimeSpan.Zero;
        int attempts = 1;
        while (UpdateCheck.ShouldRetryCheck(attempts, elapsed))
        {
            elapsed += UpdateCheck.CheckRetryGap + TimeSpan.FromMilliseconds(100);
            attempts++;
        }
        Assert.Equal(UpdateCheck.MaxCheckAttempts, attempts);
    }

    /// <summary>A slow one - both hosts timing out - still spans long enough to outlast someone
    /// answering a firewall prompt, without turning into a two-minute stare. Note the budget
    /// gates whether a new attempt STARTS, so the last one admitted still runs its full timeout
    /// and the total overshoots the budget by roughly one attempt.</summary>
    [Fact]
    public void Slow_failures_still_get_a_useful_window()
    {
        var perAttempt = TimeSpan.FromSeconds(20);
        var elapsed = perAttempt;
        int attempts = 1;
        while (UpdateCheck.ShouldRetryCheck(attempts, elapsed))
        {
            elapsed += UpdateCheck.CheckRetryGap + perAttempt;
            attempts++;
        }
        Assert.Equal(3, attempts);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(70));
    }
}
