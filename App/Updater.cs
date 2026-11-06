using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Forms;
using CubeRadar.Core.Update;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

/// <summary>
/// Automatic updates from the GitHub releases: checks shortly after the start and every 6 hours, downloads the new
/// RaidAnsager.exe next to the running one, verifies size and SHA-256 against GitHub, starts it as installer and quits.
/// The installer waits until the old program has closed, copies itself over it and starts it (see
/// <see cref="SelfReplace"/> in the Cube Radar core for why the running exe is never touched itself).
/// Installs only while no boss fight runs and the player is not inside an instance, so a raid is never interrupted.
/// This is the program's only internet connection (api.github.com / github.com); it can be switched off.
/// For tests: RAIDANSAGER_UPDATE_API points the check at another URL.
/// </summary>
internal sealed class Updater : IDisposable
{
    /// <summary>The old program starts the downloaded exe with <c>--install-update &lt;installed exe&gt; [arguments]</c>.
    /// Every later version must keep understanding this, because the old version decides how it is called.</summary>
    public const string InstallFlag = "--install-update";
    public const string AfterUpdateFlag = "--after-update";

    public static readonly Uri Latest = new("https://api.github.com/repos/dr89mh/aion2-raid-ansager/releases/latest");
    public const string ReleasesPage = "https://github.com/dr89mh/aion2-raid-ansager/releases";
    public const string Asset = "RaidAnsager.exe";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(3), Interval = TimeSpan.FromHours(6),
        Retry = TimeSpan.FromMinutes(5), CheckTimeout = TimeSpan.FromSeconds(20), DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly Settings _settings;
    private readonly string _exe;
    private readonly Func<bool> _busy;
    private readonly Action _quit;
    private readonly HttpClient _http;
    private readonly UpdateClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Updater(Settings settings, string exe, Func<bool> busy, Action quit)
    {
        _settings = settings;
        _exe = exe;
        _busy = busy;
        _quit = quit;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RaidAnsager", Current.ToString(3)));
        var api = Environment.GetEnvironmentVariable("RAIDANSAGER_UPDATE_API") is { Length: > 0 } url ? new Uri(url) : Latest;
        _client = new UpdateClient(_http, api, Asset);
    }

    public static Version Current { get; } = UpdateClient.Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>A development build (inside bin\) never updates itself.</summary>
    public static bool Allowed(string exe) =>
        !exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>A newer release, once a check found one.</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised on a worker thread once per newly found version.</summary>
    public event Action<ReleaseInfo>? Found;

    /// <summary>Raised on a worker thread when an update could not be installed (message for the user).</summary>
    public event Action<string>? Failed;

    /// <summary>Raised on a worker thread right before the program quits for the installer.</summary>
    public event Action<ReleaseInfo>? Installing;

    public void Start() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(FirstCheck, _cts.Token);
            try { SelfReplace.CleanUp(_exe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("update cleanup", ex); }
            var lastCheck = DateTime.MinValue;
            while (true)
            {
                if (_settings.AutoUpdate)
                {
                    if (DateTime.UtcNow - lastCheck >= Interval)
                    {
                        await CheckAsync();
                        lastCheck = DateTime.UtcNow;
                    }
                    // install as soon as no fight runs and we are not inside a dungeon or raid
                    if (Available is { Installable: true } && !_busy() && await InstallAsync() is null) return;
                }
                await Task.Delay(Available is null ? Interval : Retry, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    /// <summary>Asks GitHub for the latest release; true when a newer one is available.</summary>
    public async Task<bool> CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return Available is not null;
        ReleaseInfo? found = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(CheckTimeout);
            var latest = await _client.LatestAsync(timeout.Token);
            if (latest is not null && UpdateClient.IsNewer(latest, Current))
            {
                if (Available?.Version != latest.Version) found = latest;
                Available = latest;
            }
            else Available = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!_cts.IsCancellationRequested) ErrorLog.Write("update check", ex);
        }
        finally
        {
            _gate.Release();
        }
        if (found is not null) Found?.Invoke(found);
        return Available is not null;
    }

    /// <summary>Downloads and verifies <see cref="Available"/>, starts it as installer and quits.</summary>
    /// <returns>Null when the installer was started; otherwise what went wrong, for the user.</returns>
    public async Task<string?> InstallAsync()
    {
        if (Available is not { Installable: true } release) return L.T("There is no installable update.", "Es gibt kein installierbares Update.");
        if (!await _gate.WaitAsync(0)) return L.T("An update is already running.", "Ein Update läuft bereits.");
        var staged = SelfReplace.StagingPath(_exe);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(DownloadTimeout);
                await _client.DownloadAsync(release, staged, timeout.Token);
            }
            Installing?.Invoke(release);
            var start = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exe)! };
            start.ArgumentList.Add(InstallFlag);
            start.ArgumentList.Add(_exe);
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => a != AfterUpdateFlag)) start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SelfReplace.CleanUp(_exe);
            ErrorLog.Write("update", ex);
            var reason = ex switch
            {
                UpdateException => ex.Message,
                UnauthorizedAccessException => L.T("The program may not write into its folder (e.g. under Program Files).",
                    "Im Ordner der exe darf das Programm nicht schreiben (z. B. unter „Programme“)."),
                OperationCanceledException => L.T("The download took too long.", "Der Download hat zu lange gedauert."),
                HttpRequestException => L.T("No connection to GitHub.", "Keine Verbindung zu GitHub."),
                _ => ex.Message,
            };
            Failed?.Invoke(reason);
            _gate.Release();
            return reason;
        }
        _quit();
        return null;
    }

    /// <summary>
    /// The downloaded exe, started with <see cref="InstallFlag"/>: waits until the old program has closed, puts itself in
    /// place of the installed exe and starts that. If it cannot, the old version starts again.
    /// </summary>
    public static int RunInstaller(string[] a)
    {
        if (a.Length == 0 || Environment.ProcessPath is not { } self) return 2;
        var installed = a[0];
        var args = a[1..];
        try
        {
            // the old program holds this lock until it has closed
            if (Mutex.TryOpenExisting(TrayApp.LiveMutex, out var running))
                using (running)
                {
                    try
                    {
                        if (running.WaitOne(TimeSpan.FromMinutes(1))) running.ReleaseMutex();
                    }
                    catch (AbandonedMutexException)
                    {
                        running.ReleaseMutex();
                    }
                }
            SelfReplace.Install(self, installed, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("installer", ex);
            MessageBox.Show(L.T($"The update could not be installed:\n\n{ex.Message}\n\nThe previous version starts again.",
                    $"Das Update konnte nicht installiert werden:\n\n{ex.Message}\n\nDie bisherige Version startet wieder."),
                L.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Launch(installed, args) ? 1 : 2;
        }
        return Launch(installed, [.. args, AfterUpdateFlag]) ? 0 : 2;
    }

    private static bool Launch(string exe, IEnumerable<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("installer start", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
