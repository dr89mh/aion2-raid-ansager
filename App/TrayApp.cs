using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CubeRadar.Core.Capture;
using RaidAnsager.Analysis;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

internal static class TrayApp
{
    public static string Version =>
        typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0";

    public static int Run(string? replayFile, double speed)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write("Oberfläche", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("Absturz", (Exception)e.ExceptionObject);
        using var single = new Mutex(true, replayFile is null ? "RaidAnsager.Live" : "RaidAnsager.Replay." + Environment.ProcessId,
            out var first);
        if (!first)
        {
            MessageBox.Show("Der Raid-Ansager läuft bereits (Symbol im Infobereich unten rechts).", "Raid-Ansager",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (!LiveSniffer.IsNpcapInstalled())
        {
            var answer = MessageBox.Show(
                "Npcap ist nicht installiert. Der Raid-Ansager liest damit den Netzwerkverkehr von Aion 2 mit " +
                "(nur lesen, wie Wireshark).\n\nJetzt npcap.com öffnen? Installer mit den Standardoptionen ausführen, " +
                "danach den Raid-Ansager neu starten.", "Raid-Ansager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) Open("https://npcap.com/#download");
            return 3;
        }
        if (replayFile is not null && !File.Exists(replayFile))
        {
            MessageBox.Show("Datei nicht gefunden: " + replayFile, "Raid-Ansager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        using var ctx = new AnsagerContext(replayFile, speed);
        Application.Run(ctx);
        return 0;
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

internal sealed class AnsagerContext : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly GameData _data = GameData.Load();
    private readonly Speaker _speaker;
    private readonly Announcer _engine;
    private readonly Pipeline _pipe;
    private readonly FightLogWriter _logs;
    private readonly ReplayClock _clock;
    private readonly LiveSniffer? _sniffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _modsItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _moveItem, _muteItem;
    private readonly long _startedMs = Environment.TickCount64;
    private volatile string _capture = "starte …";
    private volatile bool _connected;
    private string _lastFight = "";
    private List<string> _modProblems = new();
    private int _tooltipTicks;

    public AnsagerContext(string? replayFile, double speed)
    {
        _speaker = new Speaker(_settings);
        var mods = LoadMods();
        _engine = new Announcer(_data, mods, _speaker,
            new AnnouncerOptions
            {
                Role = _settings.Role, SayUnknown = _settings.SayUnknown, AllBosses = _settings.AllBosses, InstanceBossesOnly = true,
            });
        _logs = new FightLogWriter(replayFile is null ? AppPaths.Fights : Path.Combine(AppPaths.Fights, "wiedergabe"), _data);
        _engine.FightEnded += f =>
        {
            try { _logs.Write(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _lastFight = $"{f.Def.Name}: {f.Outcome} nach {FightReport.Clock((f.EndMs - f.PullMs) / 1000.0)}";
        };
        _pipe = new Pipeline(_engine) { RawFolder = replayFile is null && _settings.RecordRaw ? AppPaths.Raw : null };
        _pipe.FlowLocked += _ => _connected = true;
        _clock = new ReplayClock(live: replayFile is null);

        _overlay = new OverlayForm(_settings);
        _overlay.Show();

        _moveItem = new ToolStripMenuItem("Overlay verschieben", null, (_, _) => ToggleMove()) { ShortcutKeyDisplayString = "Strg+Alt+O" };
        _muteItem = new ToolStripMenuItem("Stumm", null, (_, _) => ToggleMute()) { ShortcutKeyDisplayString = "Strg+Alt+M" };
        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Raid-Ansager",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ToggleMove();

        _hotkeys = new HotkeyWindow();
        _hotkeys.Register(1, 'O', ToggleMove);
        _hotkeys.Register(2, 'M', ToggleMute);
        _hotkeys.Register(3, 'T', () => _engine.Test(_clock.Now()));

        if (replayFile is null)
        {
            _sniffer = new LiveSniffer(new LiveSnifferOptions());
            _sniffer.PacketArrived += (raw, dev) => _pipe.OnPacket(raw, dev.Name, LiveSniffer.Describe(dev));
            _sniffer.StatusChanged += s => _capture = Translate(s);
            _sniffer.Start();
            _tray.ShowBalloonTip(4000, "Raid-Ansager läuft",
                "Ansagen kommen automatisch, sobald ein Dungeon- oder Raid-Boss gepullt wird. Rechtsklick auf das Symbol für Einstellungen.",
                ToolTipIcon.Info);
        }
        else
        {
            _capture = $"Wiedergabe ×{speed:0.#}: {Path.GetFileName(replayFile)}";
            _ = Pipeline.Replay(_pipe, replayFile, speed, _clock, _cts.Token).ContinueWith(t =>
            {
                _capture = t.IsFaulted ? "Wiedergabe-Fehler: " + t.Exception?.GetBaseException().Message : "Wiedergabe beendet";
            }, TaskScheduler.Default);
        }

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private List<BossMod> LoadMods()
    {
        _modProblems = new List<string>();
        var mods = ModLoader.LoadAll(AppPaths.ModFolders, _modProblems);
        _modsItem.Text = mods.Count == 0 ? "Keine Boss-Mods geladen"
            : "Mods: " + string.Join(", ", mods.Select(m => $"{m.Id} {m.Version}{(m.Source == "eingebaut" ? "" : " (Datei)")}"));
        return mods;
    }

    private static string Translate(string s) => s switch
    {
        "starting" => "starte …",
        "stopped" => "gestoppt",
        "AION 2 is not running" => "Aion 2 läuft nicht",
        "waiting for the game to connect" => "warte auf die Spielverbindung",
        _ when s.StartsWith("AION 2 not running", StringComparison.Ordinal) => "Aion 2 läuft nicht",
        _ when s.StartsWith("no game connection", StringComparison.Ordinal) => "keine Spielverbindung",
        _ when s.StartsWith("capturing on", StringComparison.Ordinal) => "liest mit (" + s["capturing on ".Length..] + ")",
        _ when s.StartsWith("Npcap is not installed", StringComparison.Ordinal) => "Npcap fehlt (npcap.com)",
        _ when s.StartsWith("capture error", StringComparison.Ordinal) => "Fehler beim Mitlesen: " + s["capture error: ".Length..],
        _ when s.StartsWith("no Npcap adapter", StringComparison.Ordinal) => "kein Netzwerkadapter mit Spielverbindung (VPN?)",
        _ => s,
    };

    private void OnTick()
    {
        _engine.Tick(_clock.Now());
        var st = _engine.Snapshot();
        string? status = null;
        var early = Environment.TickCount64 - _startedMs < 12_000;
        var problem = _capture.StartsWith("Npcap", StringComparison.Ordinal) || _capture.StartsWith("Fehler", StringComparison.Ordinal) ||
                      _capture.StartsWith("kein Netzwerk", StringComparison.Ordinal);
        if (problem || early || !_clock.Live)
            status = "Raid-Ansager: " + (_connected && !problem && _clock.Live ? "verbunden" : _capture) +
                     (_modProblems.Count > 0 ? $" · {_modProblems.Count} Mod-Problem(e)" : "");
        if (_settings.Muted) status = (status is null ? "" : status + " · ") + "STUMM";
        _overlay.Render(st, status);

        if (++_tooltipTicks >= 20)
        {
            _tooltipTicks = 0;
            _statusItem.Text = (_connected ? "Verbunden" : _capture) + (st.InModZone ? $" · Raid-Zone {st.Zone}" : "");
            var tip = $"Raid-Ansager – {(_connected ? "verbunden" : _capture)}";
            if (_lastFight.Length > 0) tip += "\n" + _lastFight;
            _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Raid-Ansager {TrayApp.Version}") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_moveItem);
        _muteItem.Checked = _settings.Muted;
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripMenuItem("Test-Ansage", null, (_, _) => _engine.Test(_clock.Now())) { ShortcutKeyDisplayString = "Strg+Alt+T" });
        menu.Items.Add(new ToolStripSeparator());

        var role = new ToolStripMenuItem("Meine Rolle");
        foreach (var (key, label) in new[] { ("alle", "Alle Ansagen"), ("tank", "Tank"), ("heiler", "Heiler"), ("dd", "Schaden (DD)") })
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.Role == key };
            item.Click += (_, _) =>
            {
                _settings.Role = key;
                _engine.Options.Role = key;
                foreach (ToolStripMenuItem i in role.DropDownItems) i.Checked = i == item;
                _settings.Save();
            };
            role.DropDownItems.Add(item);
        }
        menu.Items.Add(role);
        menu.Items.Add(Toggle("Alle Dungeon-Bosse (auch ohne Mod)", () => _settings.AllBosses, v =>
        {
            _settings.AllBosses = v;
            _engine.Options.AllBosses = v;
        }));
        menu.Items.Add(Toggle("Unbekannte Boss-Fähigkeiten vorlesen", () => _settings.SayUnknown, v =>
        {
            _settings.SayUnknown = v;
            _engine.Options.SayUnknown = v;
        }));
        menu.Items.Add(Toggle("Warntöne", () => _settings.Tones, v => _settings.Tones = v));

        var voice = new ToolStripMenuItem("Stimme");
        foreach (var name in Speaker.Voices())
        {
            var item = new ToolStripMenuItem(name) { Checked = name == _speaker.VoiceName };
            item.Click += (_, _) =>
            {
                _settings.Voice = name;
                _speaker.Apply();
                foreach (ToolStripMenuItem i in voice.DropDownItems) i.Checked = i.Text == _speaker.VoiceName;
                _settings.Save();
                _speaker.Say("So klinge ich", urgent: true);
            };
            voice.DropDownItems.Add(item);
        }
        menu.Items.Add(voice);
        menu.Items.Add(Choice("Lautstärke", [25, 50, 75, 100], v => $"{v} %", () => _settings.Volume, v =>
        {
            _settings.Volume = v;
            _speaker.Apply();
            _speaker.Sound("info");
        }));
        menu.Items.Add(Choice("Sprechtempo", [0, 2, 4, 6], v => v switch { 0 => "normal", 2 => "etwas schneller", 4 => "schnell", _ => "sehr schnell" },
            () => _settings.Rate, v =>
            {
                _settings.Rate = v;
                _speaker.Apply();
                _speaker.Say("Purpurkreis weitergeben", urgent: true);
            }));
        menu.Items.Add(Choice("Overlay-Größe", [80, 100, 125, 150], v => $"{v} %", () => (int)Math.Round(_settings.Scale * 100), v =>
        {
            _settings.Scale = v / 100.0;
            _engine.Test(_clock.Now());
        }));
        menu.Items.Add(Toggle("Raid-Mitschnitt speichern (für die Auswertung)", () => _settings.RecordRaw, v =>
        {
            _settings.RecordRaw = v;
            if (_clock.Live) _pipe.RawFolder = v ? AppPaths.Raw : null;
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_modsItem);
        menu.Items.Add(new ToolStripMenuItem("Mods neu laden", null, (_, _) => ReloadMods()));
        menu.Items.Add(new ToolStripMenuItem("Mods-Ordner öffnen", null, (_, _) => OpenFolder(AppPaths.Mods, exportMods: true)));
        menu.Items.Add(new ToolStripMenuItem("Kampfprotokolle öffnen", null, (_, _) => OpenFolder(AppPaths.Fights)));
        menu.Items.Add(new ToolStripMenuItem("Raid-Mitschnitte öffnen", null, (_, _) => OpenFolder(AppPaths.Raw)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => ExitThread()));
        return menu;
    }

    private ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get() };
        item.Click += (_, _) =>
        {
            set(!get());
            item.Checked = get();
            _settings.Save();
        };
        return item;
    }

    private ToolStripMenuItem Choice(string text, int[] values, Func<int, string> label, Func<int> get, Action<int> set)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var v in values)
        {
            var item = new ToolStripMenuItem(label(v)) { Checked = get() == v, Tag = v };
            item.Click += (_, _) =>
            {
                set(v);
                foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = (int)i.Tag! == get();
                _settings.Save();
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private void ToggleMove()
    {
        _overlay.MoveMode = !_overlay.MoveMode;
        _moveItem.Checked = _overlay.MoveMode;
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        _muteItem.Checked = _settings.Muted;
        _settings.Save();
        if (!_settings.Muted) _speaker.Say("Ton an", urgent: true);
    }

    private void ReloadMods()
    {
        _engine.SetMods(LoadMods());
        var msg = _modProblems.Count == 0 ? _modsItem.Text : _modsItem.Text + "\n\nProbleme:\n" + string.Join("\n", _modProblems.Take(12));
        _tray.ShowBalloonTip(5000, "Boss-Mods neu geladen", msg ?? "", _modProblems.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private static void OpenFolder(string dir, bool exportMods = false)
    {
        Directory.CreateDirectory(dir);
        if (exportMods)
        {
            // a copy of every built-in mod to edit; a file wins as long as its "version" is not older than the built-in one
            var asm = typeof(AnsagerContext).Assembly;
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("mods/", StringComparison.Ordinal)))
            {
                var target = Path.Combine(dir, Path.GetFileName(res));
                if (File.Exists(target)) continue;
                using var s = asm.GetManifestResourceStream(res)!;
                using var f = File.Create(target);
                s.CopyTo(f);
            }
        }
        TrayApp.Open(dir);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _cts.Cancel();
        _sniffer?.Stop();
        _engine.Flush("Programm beendet");
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _hotkeys.Dispose();
            _tray.Dispose();
            _overlay.Dispose();
            _sniffer?.Dispose();
            _pipe.Dispose();
            _speaker.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Strg+Alt+key hotkeys through RegisterHotKey (no keyboard hook; the game is not touched).</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    private readonly Dictionary<int, Action> _actions = new();

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public void Register(int id, char key, Action action)
    {
        if (RegisterHotKey(Handle, id, MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, key)) _actions[id] = action;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action)) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
