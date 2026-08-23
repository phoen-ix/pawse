using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>One downloadable file from the update feed.</summary>
public sealed record UpdateAsset(string Url, string Sha256);

/// <summary>What the feed says the newest release is.</summary>
public sealed record UpdateInfo(string Version, string NotesUrl, UpdateAsset? Full, UpdateAsset? Min);

/// <summary>How this copy of Pawse got onto the machine - it decides whether an update can
/// install itself (run the matching installer) or only be pointed at (portable).</summary>
public enum InstallKind
{
    Portable,
    InstalledFull,
    InstalledMin,
}

/// <summary>
/// The only code in Pawse that touches the network. It runs when the user presses
/// "Check now" in Settings → About, or - only with the off-by-default
/// <see cref="Config.UpdateCfg.AutoCheck"/> switched on - once a day while Pawse is open,
/// and that daily check does no more than notify (installing still takes a deliberate
/// yes). The request carries a version number and nothing else.
///
/// <para>The feed lives on pawse.at (so checking never depends on the GitHub repo being
/// public) while the installers come from GitHub Releases. That split is deliberate: the
/// checksum arrives from a different host than the binary, so it actually cross-checks the
/// download instead of vouching for itself.</para>
///
/// <para>Everything above <see cref="FetchAsync"/> is pure and unit-tested; the network and
/// registry sit behind it.</para>
/// </summary>
public static class UpdateCheck
{
    public const string FeedUrl = "https://pawse.at/latest.json";
    public const string ReleasesUrl = "https://github.com/phoen-ix/pawse/releases/latest";

    /// <summary>The version a local build reports (Pawse.csproj ships 0.0.0-dev; CI injects
    /// the real one). Checking it against a release would always claim an update.</summary>
    public const string DevVersion = "0.0.0";

    /// <summary>Also hard-coded in packaging/pawse.nsi as UNINST_KEY - change both or
    /// neither, or an installed Pawse starts reading as portable.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pawse";

    /// <summary>The self-contained exe is ~63 MB, the launcher ~0.2 MB - anything in
    /// between is a build nobody ships, so the midpoint is a safe divider.</summary>
    private const long FullBuildMinBytes = 20L * 1024 * 1024;

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How long the opt-in automatic check waits between attempts.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

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
            return new UpdateInfo(version!, notes!, full, min);
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

    /// <summary>Installed (and which build) or portable - see <see cref="InstallKind"/>.</summary>
    public static InstallKind DetectInstall() =>
        DetectInstall(Log.ExeDir(), ProcessSizeBytes(), ReadInstallLocation);

    /// <summary>Test seam for <see cref="DetectInstall()"/>: the registry and the exe on disk
    /// are the only things it consults.</summary>
    internal static InstallKind DetectInstall(string exeDir, long exeBytes, Func<string?> installLocation)
    {
        var installed = installLocation();
        if (string.IsNullOrWhiteSpace(installed) || !SameFolder(installed, exeDir))
            return InstallKind.Portable;
        return exeBytes >= FullBuildMinBytes ? InstallKind.InstalledFull : InstallKind.InstalledMin;
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

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
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

    private static string? ReadInstallLocation()
    {
        // Per-user install first, then per-machine - the same order the installer's own
        // previous-install detection uses.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string path && path.Length > 0) return path;
            }
            catch { /* an unreadable key just means "not installed here" */ }
        }
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
