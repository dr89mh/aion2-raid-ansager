using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

/// <summary>
/// One-click upload of the fight logs to the upload service (server/upload-worker, a Cloudflare Worker): only logs not
/// uploaded before, in zips of at most 15 MB, without player names. The raid lead collects them with
/// <c>RaidAnsager abholen</c>. For tests: RAIDANSAGER_UPLOAD_URL points at another service.
/// </summary>
public static class Uploader
{
    public const string DefaultService = "https://raidansager-upload.raidannouncer.workers.dev";

    /// <summary>Same value as UPLOAD_TOKEN in server/upload-worker/wrangler.toml; not a secret.</summary>
    public const string UploadToken = "ra1-StEqRMONlmt418Gtl69V-g3_";

    private const long MaxZip = 15L * 1024 * 1024;

    public static string Service =>
        (Environment.GetEnvironmentVariable("RAIDANSAGER_UPLOAD_URL") is { Length: > 0 } url ? url : DefaultService).TrimEnd('/');

    public static bool Configured => !Service.Contains("SUBDOMAIN", StringComparison.Ordinal);

    /// <summary>Logs already sent (file names relative to the fight folder).</summary>
    private static string SentList => Path.Combine(AppPaths.Root, "hochgeladen.txt");

    private static string Relative(string file) => Path.GetRelativePath(AppPaths.Fights, file);

    public static List<string> Pending()
    {
        var sent = File.Exists(SentList) ? File.ReadAllLines(SentList).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        return LogExport.LogFiles().Where(f => !sent.Contains(Relative(f))).ToList();
    }

    /// <returns>Number of fights sent, or the reason it failed (for the user).</returns>
    public static async Task<(int Fights, string? Error)> UploadAsync(Settings settings, CancellationToken ct = default)
    {
        if (!Configured) return (0, L.T("No upload service is set up in this version.", "In dieser Version ist kein Upload-Dienst eingerichtet."));
        var pending = Pending();
        if (pending.Count == 0) return (0, null);
        if (string.IsNullOrEmpty(settings.ClientId))
        {
            settings.ClientId = Guid.NewGuid().ToString("N")[..12]; // anonymous, only to tell uploads of one PC apart
            settings.Save();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaidAnsager", TrayApp.Version));
        var sent = 0;
        foreach (var batch in Batches(pending))
        {
            using var zip = new MemoryStream();
            LogExport.Write(batch, zip);
            zip.Position = 0;
            using var request = new HttpRequestMessage(HttpMethod.Post, Service + "/upload") { Content = new StreamContent(zip) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Headers.Add("X-Upload-Token", UploadToken);
            request.Headers.Add("X-App-Version", TrayApp.Version);
            request.Headers.Add("X-Fights", batch.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Client-Id", settings.ClientId);
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var why = (await response.Content.ReadAsStringAsync(ct)).Trim();
                    return (sent, response.StatusCode == HttpStatusCode.TooManyRequests
                        ? L.T("Daily upload limit reached, please try again tomorrow.", "Tageslimit erreicht, bitte morgen nochmal.")
                        : L.T($"The upload service refused it ({(int)response.StatusCode} {why}).", $"Der Upload-Dienst hat abgelehnt ({(int)response.StatusCode} {why})."));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ErrorLog.Write("upload", ex);
                return (sent, L.T("No connection to the upload service.", "Keine Verbindung zum Upload-Dienst."));
            }
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllLines(SentList, batch.Select(Relative));
            sent += batch.Count;
        }
        return (sent, null);
    }

    /// <summary>Groups the logs so that a zip stays well below the service's 20 MB (logs pack about 8:1).</summary>
    private static IEnumerable<List<string>> Batches(List<string> files)
    {
        var batch = new List<string>();
        long size = 0;
        foreach (var f in files)
        {
            var len = new FileInfo(f).Length / 6;
            if (batch.Count > 0 && size + len > MaxZip)
            {
                yield return batch;
                batch = [];
                size = 0;
            }
            batch.Add(f);
            size += len;
        }
        if (batch.Count > 0) yield return batch;
    }

    // ------------------------------------------------------------------ collecting (raid lead)

    /// <summary>Admin token of the upload service; only on the raid lead's PC.</summary>
    public static string AdminTokenFile => Path.Combine(AppPaths.Root, "admin-token.txt");

    public static string Inbox => Path.Combine(AppPaths.Root, "uploads");

    /// <summary>Downloads every upload not yet in <see cref="Inbox"/>; optionally deletes the downloaded ones on the service.</summary>
    public static async Task<(int New, int Total, string? Error)> CollectAsync(bool delete, TextWriter con)
    {
        if (!File.Exists(AdminTokenFile)) return (0, 0, L.T($"No admin token ({AdminTokenFile}).", $"Kein Admin-Schlüssel ({AdminTokenFile})."));
        var token = File.ReadAllText(AdminTokenFile).Trim();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaidAnsager", TrayApp.Version));
        Directory.CreateDirectory(Inbox);
        JsonElement list;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Service + "/admin/list"));
            list = doc.RootElement.GetProperty("uploads").Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            return (0, 0, ex.Message);
        }
        int fresh = 0, total = 0;
        foreach (var item in list.EnumerateArray())
        {
            total++;
            var key = item.GetProperty("key").GetString()!;
            var file = Path.Combine(Inbox, key.Replace("u/", "", StringComparison.Ordinal));
            if (!File.Exists(file))
            {
                var bytes = await http.GetByteArrayAsync(Service + "/admin/file/" + Uri.EscapeDataString(key));
                await File.WriteAllBytesAsync(file, bytes);
                fresh++;
                var fights = item.TryGetProperty("fights", out var n) ? n.ToString() : "?";
                var version = item.TryGetProperty("version", out var v) ? v.ToString() : "?";
                con.WriteLine($"  + {Path.GetFileName(file)}  ({fights} {L.T("fights", "Kämpfe")}, v{version})");
            }
            if (delete)
                using (await http.DeleteAsync(Service + "/admin/file/" + Uri.EscapeDataString(key))) { }
        }
        return (fresh, total, null);
    }
}
