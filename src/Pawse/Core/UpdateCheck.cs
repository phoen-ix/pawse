using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>One downloadable file from the update feed.</summary>
public sealed record UpdateAsset(string Url, string Sha256);

/// <summary>What the feed says the newest release is. The portable entries and
/// <paramref name="AllowsAuto"/> carry defaults so a feed written before they existed - and
/// every existing caller and test - still works unchanged.</summary>
public sealed record UpdateInfo(
    string Version,
    string NotesUrl,
    UpdateAsset? Full,
    UpdateAsset? Min,
    UpdateAsset? PortableFull = null,
    UpdateAsset? PortableMin = null,
    bool AllowsAuto = true);

/// <summary>How this copy of Pawse got onto the machine - it decides whether an update runs
/// the matching installer, replaces the exe in place, or can only be pointed at.</summary>
public enum InstallKind
{
    PortableFull,
    PortableMin,
    InstalledFull,
    InstalledMin,
}

/// <summary>Which hive an install's Add/Remove entry sits in. Per-machine cannot be updated
/// without a UAC prompt, so an unattended update declines it rather than surprising anyone.</summary>
public enum InstallScope
{
    None,
    PerUser,
    PerMachine,
}

/// <summary>Where the SHA-256 an update is verified against came from.</summary>
public enum ChecksumSource
{
    /// <summary>No usable checksum, so there is nothing safe to install.</summary>
    None,

    /// <summary>pawse.at - a different host than the binary, which is the point.</summary>
    Feed,

    /// <summary>The release's own SHA256SUMS.txt. Same host as the download, so it proves the
    /// transfer was not corrupted and nothing more. Never enough for an unattended install.</summary>
    GitHubSums,
}

/// <summary>What a finished check concluded.</summary>
public enum UpdateVerdict
{
    /// <summary>Neither source answered.</summary>
    Unknown,
    UpToDate,

    /// <summary>Newer, but nothing this copy can safely install by itself.</summary>
    Available,

    /// <summary>Newer, with a verified asset for this exact install kind.</summary>
    Installable,
}

/// <summary>The outcome of a check, and everything acting on it needs.</summary>
public sealed record UpdatePlan(
    UpdateVerdict Verdict,
    string? Version,
    string NotesUrl,
    UpdateAsset? Asset,
    string? FileName,
    InstallKind Kind,
    ChecksumSource Checksum,
    bool FeedAllowsAuto,
    string? Error)
{
    public override string ToString() =>
        $"{Verdict} version={Version ?? "?"} kind={Kind} checksum={Checksum}"
        + (FeedAllowsAuto ? "" : " feed-paused-auto")
        + (Error is null ? "" : $" error=\"{Error}\"");
}

/// <summary>
/// The only code in Pawse that touches the network. It runs when the user presses
/// "Check now" in Settings → About, or - only above the default
/// <see cref="Config.UpdateMode.Manual"/> - once a day while Pawse is open. The request
/// carries a version number and nothing else.
///
/// <para>GitHub is asked first - it is where the releases actually are, so a check keeps
/// working if pawse.at ever goes away - and the feed on pawse.at is the fallback. The
/// version comes from whichever answered; the CHECKSUM is preferred from pawse.at, because
/// a hash served by the same host as the binary only proves the transfer was not corrupted.
/// Two hosts means two TLS chains and two delivery paths, which is real protection against a
/// compromised edge or a MITM - but not against a compromised source, since this repository
/// serves both. Authenticode signing is the answer to that, and Pawse does not have it yet.</para>
///
/// <para>Everything above <see cref="FetchAsync"/> is pure and unit-tested; the network and
/// registry sit behind it.</para>
/// </summary>
public static class UpdateCheck
{
    public const string FeedUrl = "https://pawse.at/latest.json";
    public const string RepoUrl = "https://github.com/phoen-ix/pawse";
    public const string ReleasesUrl = RepoUrl + "/releases/latest";

    /// <summary>The host every GitHub URL must be on. A redirect that leaves it (a login
    /// wall, a rename to somewhere else) is not an answer about a release.</summary>
    private const string GitHubHost = "github.com";

    /// <summary>The version a local build reports (Pawse.csproj ships 0.0.0-dev; CI injects
    /// the real one). Checking it against a release would always claim an update.</summary>
    public const string DevVersion = "0.0.0";

    /// <summary>Also hard-coded in packaging/pawse.nsi as UNINST_KEY - change both or
    /// neither, or an installed Pawse starts reading as portable.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pawse";

    /// <summary>The self-contained exe is ~63 MB, the launcher ~0.2 MB - anything in
    /// between is a build nobody ships, so the midpoint is a safe divider. Only a fallback
    /// now: installs made by a current installer record BuildVariant instead.</summary>
    internal const long FullBuildMinBytes = 20L * 1024 * 1024;

    /// <summary>How long to wait before re-attempting an unattended install of the same
    /// version. A silent installer that refuses the job leaves the old version in place, and
    /// the next daily check would otherwise find the same update and try again forever.</summary>
    public static readonly TimeSpan AutoRetryInterval = TimeSpan.FromDays(7);

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How long the opt-in automatic check waits between attempts.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    /// <summary>How many times one check will ask before giving up. A first attempt fails far
    /// more often than a second: a firewall that prompts per-application blocks the connection
    /// while its dialog waits, and a cold first HTTPS request pays for DNS, TLS and building
    /// the certificate chain - all inside the same timeout.</summary>
    public const int MaxCheckAttempts = 5;

    /// <summary>Breathing room between attempts.</summary>
    public static readonly TimeSpan CheckRetryGap = TimeSpan.FromSeconds(1);

    /// <summary>How late a NEW attempt may start - not a hard ceiling, since the attempt it
    /// admits still runs to its own timeout. The count alone is not enough: two hosts timing out
    /// costs ~20 s each time, so five of those would leave "Checking…" up for nearly two
    /// minutes. With this, fast failures get all five attempts, and slow ones get three spanning
    /// roughly a minute - long enough to outlast someone answering a firewall prompt, and
    /// bounded enough to stay honest with a counter on screen.</summary>
    public static readonly TimeSpan CheckRetryBudget = TimeSpan.FromSeconds(45);

    /// <summary>Whether to make attempt number <paramref name="completed"/> + 1.</summary>
    public static bool ShouldRetryCheck(int completed, TimeSpan elapsed) =>
        completed < MaxCheckAttempts && elapsed + CheckRetryGap < CheckRetryBudget;

    /// <summary>Outcome of a feed fetch: either <paramref name="Info"/> or a human-readable
    /// <paramref name="Error"/> - never both, never neither.</summary>
    public sealed record FetchResult(UpdateInfo? Info, string? Error);

    /// <summary>Whether the automatic check is due. Never checked = due. A stamp in the
    /// future means the clock moved (or the file was edited), which also counts as due
    /// rather than parking the check until that date arrives.</summary>
    public static bool IsCheckDue(DateTime? lastUtc, DateTime nowUtc) =>
        lastUtc is not { } last || last > nowUtc || nowUtc - last >= AutoCheckInterval;

    /// <summary>True when <paramref name="latest"/> is a strictly higher version than
    /// <paramref name="current"/>. Anything unparseable answers false: an update prompt off
    /// the back of a version string we don't understand would be worse than silence.</summary>
    public static bool IsNewer(string? current, string? latest) =>
        TryParseVersion(current, out var now) && TryParseVersion(latest, out var next) && next > now;

    /// <summary>True when both strings name the same release. Used to decide whether the
    /// feed is talking about the version GitHub just reported, or lagging behind it.</summary>
    public static bool SameVersion(string? a, string? b) =>
        TryParseVersion(a, out var x) && TryParseVersion(b, out var y) && x == y;

    /// <summary>
    /// The version out of the Location a <c>/releases/latest</c> request redirects to,
    /// e.g. <c>https://github.com/phoen-ix/pawse/releases/tag/v0.8.0</c> -> <c>0.8.0</c>.
    /// Null for anything that is not that: a login wall, a rename that left the host, a
    /// tag that is not a version. This is the whole reason the API is not used - the
    /// redirect costs no rate limit, and api.github.com allows 60 requests an hour per IP,
    /// which everyone behind one corporate NAT shares.
    /// </summary>
    /// <param name="location">The raw Location header, which may be relative.</param>
    /// <param name="requestUrl">What was requested, to resolve a relative Location against.</param>
    internal static string? VersionFromTagUrl(string? location, string requestUrl = ReleasesUrl)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var baseUri)) return null;
        if (!Uri.TryCreate(baseUri, location, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase)) return null;

        // /{owner}/{repo}/releases/tag/{tag} - anything shorter or shaped differently
        // (/login, /{owner}/{repo}/releases with no tag) is not an answer.
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;
        if (!parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[3].Equals("tag", StringComparison.OrdinalIgnoreCase)) return null;

        var tag = Uri.UnescapeDataString(parts[4]);
        if (!TryParseVersion(tag, out var version)) return null;
        // A two-component tag (v0.8) would make ToString(3) throw.
        return version.Build < 0 ? null : version.ToString(3);
    }

    /// <summary>
    /// The SHA-256 for one file out of a <c>SHA256SUMS.txt</c> - lines of
    /// <c>&lt;64 hex&gt;␠␠&lt;leaf name&gt;</c>, CRLF-terminated because CI writes it on Windows.
    /// Null when the name is absent or the file is not what it claims to be (a 404 page,
    /// say). Two lines naming the same file with DIFFERENT hashes is a rejection, not a
    /// coin toss.
    /// </summary>
    internal static string? Sha256From(string? sumsText, string fileName)
    {
        if (string.IsNullOrEmpty(sumsText)) return null;
        string? found = null;
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int split = line.IndexOf(' ');
            if (split != 64) continue;                       // the hash is the first field
            var hash = line[..split];
            if (!IsSha256(hash)) continue;

            // "  name" (text mode) or " *name" (binary mode) - both are sha256sum output.
            var name = line[split..].TrimStart(' ', '*');
            if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;

            hash = hash.ToLowerInvariant();
            if (found is not null && found != hash)
            {
                Log.Error($"update: SHA256SUMS.txt lists {fileName} twice with different hashes");
                return null;
            }
            found = hash;
        }
        return found;
    }

    /// <summary>The release asset this install kind updates from.</summary>
    internal static string AssetName(InstallKind kind, string version) => kind switch
    {
        InstallKind.InstalledFull => $"Pawse-Setup-{version}-full.exe",
        InstallKind.InstalledMin => $"Pawse-Setup-{version}-min.exe",
        InstallKind.PortableFull => $"Pawse-{version}.zip",
        _ => $"Pawse-{version}-min.zip",
    };

    /// <summary>Release assets sit at a predictable URL, which is what lets the redirect
    /// probe replace the API: the version is the only thing that has to be discovered.</summary>
    internal static string AssetUrl(string version, string name) =>
        $"{RepoUrl}/releases/download/v{version}/{name}";

    internal static string SumsUrl(string version) => AssetUrl(version, "SHA256SUMS.txt");

    internal static string NotesUrlFor(string version) => $"{RepoUrl}/releases/tag/v{version}";

    /// <summary>The feed's asset for this install kind, if it has one.</summary>
    internal static UpdateAsset? FeedAssetFor(UpdateInfo feed, InstallKind kind) => kind switch
    {
        InstallKind.InstalledFull => feed.Full,
        InstallKind.InstalledMin => feed.Min,
        InstallKind.PortableFull => feed.PortableFull,
        _ => feed.PortableMin,
    };

    /// <summary>Whether resolving this needs the release's SHA256SUMS.txt - i.e. the feed is
    /// missing, lagging, or has nothing for this kind. Lets the caller skip a whole request
    /// in the common case where the feed already agrees with GitHub.</summary>
    internal static bool NeedsSums(string current, InstallKind kind, string? githubVersion, UpdateInfo? feed)
    {
        var version = githubVersion ?? feed?.Version;
        if (version is null || !IsNewer(current, version)) return false;
        if (feed is not null && SameVersion(feed.Version, version) && FeedAssetFor(feed, kind) is not null)
            return false;
        return true;
    }

    /// <summary>
    /// Decide what to do, from what each source said. Pure, so the whole matrix is testable
    /// without a network: GitHub alone, feed alone, both, neither, and the window right after
    /// a release where GitHub already has vN+1 and the feed still says vN.
    /// </summary>
    /// <param name="githubVersion">From the redirect probe, or null if it did not answer.</param>
    /// <param name="feed">Parsed pawse.at feed, or null if it did not answer.</param>
    /// <param name="sumsText">SHA256SUMS.txt, when it was needed and fetched.</param>
    internal static UpdatePlan Plan(string current, InstallKind kind, string? githubVersion,
                                    UpdateInfo? feed, string? sumsText, string? error)
    {
        // GitHub wins on the version whenever it answered, even to say "nothing newer".
        // Falling back to a lagging feed on a successful probe would resurrect a release
        // that was deliberately yanked from GitHub but is still named by a stale feed.
        var version = githubVersion ?? feed?.Version;
        if (version is null)
            return new(UpdateVerdict.Unknown, null, ReleasesUrl, null, null, kind,
                       ChecksumSource.None, true, error ?? "The update check failed.");

        var notes = githubVersion is not null ? NotesUrlFor(version) : feed!.NotesUrl;
        bool allowsAuto = feed?.AllowsAuto ?? true;

        if (!IsNewer(current, version))
            return new(UpdateVerdict.UpToDate, version, notes, null, null, kind,
                       ChecksumSource.None, allowsAuto, null);

        // The feed's checksum only counts if the feed is talking about THIS release.
        if (feed is not null && SameVersion(feed.Version, version)
            && FeedAssetFor(feed, kind) is { } feedAsset)
            return new(UpdateVerdict.Installable, version, notes, feedAsset,
                       AssetName(kind, version), kind, ChecksumSource.Feed, allowsAuto, null);

        var name = AssetName(kind, version);
        if (Sha256From(sumsText, name) is { } sha)
            return new(UpdateVerdict.Installable, version, notes,
                       new UpdateAsset(AssetUrl(version, name), sha), name, kind,
                       ChecksumSource.GitHubSums, allowsAuto, null);

        // Newer, but nothing verifiable - say so rather than downloading on trust.
        return new(UpdateVerdict.Available, version, notes, null, null, kind,
                   ChecksumSource.None, allowsAuto, null);
    }

    /// <summary>Whether a check nobody is watching may install this on its own. Requires a
    /// checksum from the OTHER host: a same-host hash vouches for its own download.</summary>
    public static bool MayInstallUnattended(UpdatePlan plan) =>
        plan.Verdict == UpdateVerdict.Installable
        && plan.Checksum == ChecksumSource.Feed
        && plan.FeedAllowsAuto;

    /// <summary>Whether an unattended install of this version may be attempted again. Mirrors
    /// <see cref="IsCheckDue"/> on a moved clock.</summary>
    public static bool MayRetryAutoInstall(string version, string? lastVersion, DateTime? lastUtc, DateTime nowUtc) =>
        !string.Equals(version, lastVersion, StringComparison.OrdinalIgnoreCase)
        || lastUtc is not { } last || last > nowUtc || nowUtc - last >= AutoRetryInterval;

    /// <summary>Parse the feed. Returns null for anything malformed - a broken feed must
    /// read as "couldn't check", never as an update.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var version = Text(root, "version");
            if (!TryParseVersion(version, out _)) return null;

            var notes = Text(root, "notes");
            if (!IsHttpsUrl(notes)) notes = ReleasesUrl;

            UpdateAsset? full = null, min = null;
            if (root.TryGetProperty("installers", out var installers) && installers.ValueKind == JsonValueKind.Object)
            {
                full = ReadAsset(installers, "full");
                min = ReadAsset(installers, "min");
            }

            UpdateAsset? portableFull = null, portableMin = null;
            if (root.TryGetProperty("portable", out var portable) && portable.ValueKind == JsonValueKind.Object)
            {
                portableFull = ReadAsset(portable, "full");
                portableMin = ReadAsset(portable, "min");
            }

            // Absent means allowed, so every feed written before this key existed keeps
            // working. Only a literal false pauses unattended installs - a fleet-wide brake
            // for a bad release, since an installed copy has no way back on its own.
            bool allowsAuto = !(root.TryGetProperty("auto", out var auto)
                                && auto.ValueKind == JsonValueKind.False);

            return new UpdateInfo(version!, notes!, full, min, portableFull, portableMin, allowsAuto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetch and parse the feed. The one outbound request Pawse ever makes.</summary>
    public static async Task<FetchResult> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient(FeedTimeout);
            var json = await http.GetStringAsync(FeedUrl, ct).ConfigureAwait(false);
            var info = Parse(json);
            return info is null
                ? new FetchResult(null, "The update feed could not be read.")
                : new FetchResult(info, null);
        }
        catch (TaskCanceledException)
        {
            return new FetchResult(null, "The update check timed out.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("update feed", ex);
            return new FetchResult(null, "Pawse could not reach pawse.at.");
        }
        catch (Exception ex)
        {
            Log.Error("update feed", ex);
            return new FetchResult(null, "The update check failed.");
        }
    }

    /// <summary>
    /// Ask GitHub what the newest release is, by following <c>/releases/latest</c> one hop and
    /// reading the tag out of where it points. No API, so no rate limit and no JSON; and
    /// <c>/releases/latest</c> already skips drafts and prereleases. Null when GitHub did not
    /// give a straight answer - the caller then falls back to the feed.
    /// </summary>
    public static async Task<string?> FetchGitHubVersionAsync(CancellationToken ct = default)
    {
        // No retry loop here, ever: github.com's web frontend has its own abuse throttling,
        // and one look per user per day is nothing to it.
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = NewClient(FeedTimeout, handler);

            var url = ReleasesUrl;
            // A repo rename adds a 301 in front of the 302, so allow a couple of hops -
            // bounded, because a redirect loop must not become the update check.
            for (int hop = 0; hop < 3; hop++)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode is < 300 or > 399) break;

                var location = response.Headers.Location?.OriginalString;
                if (string.IsNullOrEmpty(location)) break;

                if (VersionFromTagUrl(location, url) is { } version) return version;
                if (!Uri.TryCreate(new Uri(url), location, out var next)) break;
                url = next.AbsoluteUri;
            }
            Log.Warn("update: github did not redirect to a release tag");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("update: asking github", ex);
            return null;
        }
    }

    /// <summary>The release's own SHA256SUMS.txt, or null. Only fetched when the feed cannot
    /// supply the checksum - see <see cref="NeedsSums"/>.</summary>
    public static async Task<string?> FetchSumsAsync(string version, CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient(FeedTimeout);
            return await http.GetStringAsync(SumsUrl(version), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("update: fetching SHA256SUMS.txt", ex);
            return null;
        }
    }

    /// <summary>
    /// One whole check: GitHub for the version, the feed for the checksum, and SHA256SUMS.txt
    /// only when the feed cannot supply one. The result says what may be done about it.
    /// </summary>
    /// <summary>
    /// One check, retried while nothing at all answers. Only the <see cref="UpdateVerdict.Unknown"/>
    /// verdict is retried: every other one already has a usable answer, and GitHub timing out
    /// while the feed replies is the fallback doing its job, not a failure.
    /// </summary>
    /// <param name="onAttempt">Called with the attempt number as each one starts, so the caller
    /// can say so on screen - a 20 s wait with no sign of life reads as a hang.</param>
    public static async Task<UpdatePlan> CheckAsync(string current, InstallKind kind,
                                                    CancellationToken ct = default,
                                                    IProgress<int>? onAttempt = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        UpdatePlan plan;
        for (int attempt = 1; ; attempt++)
        {
            onAttempt?.Report(attempt);
            plan = await CheckOnceAsync(current, kind, ct).ConfigureAwait(false);
            if (plan.Verdict != UpdateVerdict.Unknown) return plan;

            if (ct.IsCancellationRequested || !ShouldRetryCheck(attempt, started.Elapsed)) break;
            Log.Info($"update check: attempt {attempt} reached nobody, trying again");
            try { await Task.Delay(CheckRetryGap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        Log.Warn($"update check: gave up after {started.Elapsed.TotalSeconds:F0}s - {plan.Error}");
        return plan;
    }

    private static async Task<UpdatePlan> CheckOnceAsync(string current, InstallKind kind, CancellationToken ct)
    {
        var githubVersion = await FetchGitHubVersionAsync(ct).ConfigureAwait(false);

        // Nothing newer, straight from the source of truth: there is no download to verify,
        // so there is no reason to ask pawse.at anything. The overwhelmingly common check
        // therefore touches exactly one host.
        if (githubVersion is not null && !IsNewer(current, githubVersion))
            return Plan(current, kind, githubVersion, null, null, null);

        // Past here there is either an update or no answer, and both want the feed - it
        // carries the checksum from the other host, the only kind an unattended install acts on.
        var feed = await FetchAsync(ct).ConfigureAwait(false);
        if (githubVersion is null && feed.Info is null)
            // Both were tried and both failed, so name both: reporting only the feed's error
            // points at pawse.at when github.com was asked first and is just as unreachable.
            return Plan(current, kind, null, null, null,
                        "Pawse could not reach github.com or pawse.at.");

        string? sums = null;
        if (NeedsSums(current, kind, githubVersion, feed.Info))
        {
            var version = githubVersion ?? feed.Info!.Version;
            Log.Info($"update: the feed cannot vouch for {version}, asking the release's SHA256SUMS.txt");
            sums = await FetchSumsAsync(version, ct).ConfigureAwait(false);
        }
        return Plan(current, kind, githubVersion, feed.Info, sums, feed.Error);
    }

    /// <summary>Installed (and which build) or portable - see <see cref="InstallKind"/>.</summary>
    public static InstallKind DetectInstall() =>
        DetectInstall(Log.ExeDir(), ProcessSizeBytes(), ReadInstallLocation, ReadBuildVariant);

    /// <summary>Test seam for <see cref="DetectInstall()"/>: the registry and the exe on disk
    /// are the only things it consults.</summary>
    /// <param name="buildVariant">The installer records "full" or "min" next to DisplayVersion.
    /// Absent for installs made before that existed, and always absent for a portable copy -
    /// then, and only then, the exe's size decides.</param>
    internal static InstallKind DetectInstall(string exeDir, long exeBytes,
                                              Func<string?> installLocation,
                                              Func<string?>? buildVariant = null)
    {
        var installed = installLocation();
        bool isInstalled = !string.IsNullOrWhiteSpace(installed) && SameFolder(installed, exeDir);

        // Prefer what the installer recorded. Guessing from the size gets it wrong whenever
        // the size cannot be read (ProcessSizeBytes answers 0, which reads as the small
        // build), and offering a self-contained install the minimal installer leaves a copy
        // that needs a runtime the machine may not have.
        var variant = isInstalled ? buildVariant?.Invoke() : null;
        bool full = variant switch
        {
            "full" => true,
            "min" => false,
            _ => exeBytes >= FullBuildMinBytes,
        };

        return (isInstalled, full) switch
        {
            (true, true) => InstallKind.InstalledFull,
            (true, false) => InstallKind.InstalledMin,
            (false, true) => InstallKind.PortableFull,
            (false, false) => InstallKind.PortableMin,
        };
    }

    /// <summary>True when this copy came from an installer rather than a zip.</summary>
    public static bool IsInstalled(InstallKind kind) =>
        kind is InstallKind.InstalledFull or InstallKind.InstalledMin;

    /// <summary>Which hive this install is registered in. Per-machine needs administrator
    /// rights to update, which an unattended check must never demand on its own.</summary>
    public static InstallScope DetectScope() =>
        ScopeOf(Log.ExeDir(), ReadInstallLocationIn(Registry.CurrentUser),
                ReadInstallLocationIn(Registry.LocalMachine));

    /// <summary>Test seam for <see cref="DetectScope()"/>. HKCU first, the same order the
    /// installer's own previous-install probe uses.</summary>
    internal static InstallScope ScopeOf(string exeDir, string? hkcuLocation, string? hklmLocation)
    {
        if (!string.IsNullOrWhiteSpace(hkcuLocation) && SameFolder(hkcuLocation, exeDir))
            return InstallScope.PerUser;
        if (!string.IsNullOrWhiteSpace(hklmLocation) && SameFolder(hklmLocation, exeDir))
            return InstallScope.PerMachine;
        return InstallScope.None;
    }

    /// <summary>Download to %TEMP% and verify the SHA-256 from the feed. Returns the file's
    /// path, or null when anything at all went wrong (already logged). A file that fails the
    /// checksum is deleted rather than left lying around next to a working one.</summary>
    public static async Task<string?> DownloadVerifiedAsync(UpdateAsset asset, string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            using var http = NewClient(DownloadTimeout);
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var file = File.Create(path))
                await source.CopyToAsync(file, ct).ConfigureAwait(false);

            var actual = Sha256File(path);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"update download rejected: sha256 {actual} != {asset.Sha256}");
                TryDelete(path);
                return null;
            }
            Log.Info($"update downloaded and verified: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("update download", ex);
            TryDelete(path);
            return null;
        }
    }

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---- internals -----------------------------------------------------------

    private static HttpClient NewClient(TimeSpan timeout, HttpClientHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        // The whole payload Pawse sends about you: which version is asking.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Pawse/{App.Version}");
        return http;
    }

    private static UpdateAsset? ReadAsset(JsonElement installers, string name)
    {
        if (!installers.TryGetProperty(name, out var asset) || asset.ValueKind != JsonValueKind.Object)
            return null;
        var url = Text(asset, "url");
        var sha = Text(asset, "sha256");
        // https only, and a real SHA-256: without both there is nothing to verify a
        // download against, and an unverifiable installer must never be run.
        if (!IsHttpsUrl(url) || !IsSha256(sha)) return null;
        return new UpdateAsset(url!, sha!.ToLowerInvariant());
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsSha256(string? hex)
    {
        if (hex is not { Length: 64 }) return false;
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[1..];
        if (!Version.TryParse(trimmed, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? ReadInstallLocation() =>
        // Per-user install first, then per-machine - the same order the installer's own
        // previous-install detection uses.
        ReadInstallLocationIn(Registry.CurrentUser) ?? ReadInstallLocationIn(Registry.LocalMachine);

    private static string? ReadInstallLocationIn(RegistryKey root) => ReadValue(root, "InstallLocation");

    /// <summary>"full" | "min", written by the installer since v0.8. Absent for anything
    /// older, and for a portable copy.</summary>
    private static string? ReadBuildVariant() =>
        (ReadValue(Registry.CurrentUser, "BuildVariant") ?? ReadValue(Registry.LocalMachine, "BuildVariant"))
            ?.Trim().ToLowerInvariant();

    private static string? ReadValue(RegistryKey root, string name)
    {
        try
        {
            using var key = root.OpenSubKey(UninstallKey);
            if (key?.GetValue(name) is string value && value.Length > 0) return value;
        }
        catch { /* an unreadable key just means "not installed here" */ }
        return null;
    }

    private static long ProcessSizeBytes()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? 0 : new FileInfo(exe).Length;
        }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
