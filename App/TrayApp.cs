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
        Application.ThreadException += (_, e) => ErrorLog.Write("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("crash", (Exception)e.ExceptionObject);
        var settings = Settings.Load();
        L.Lang = settings.Language;
        using var single = new Mutex(true, replayFile is null ? "RaidAnsager.Live" : "RaidAnsager.Replay." + Environment.ProcessId,
            out var first);
        if (!first)
        {
            MessageBox.Show(L.T("The raid announcer is already running (icon in the notification area, bottom right).",
                "Der Raid-Ansager läuft bereits (Symbol im Infobereich unten rechts)."), L.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (!LiveSniffer.IsNpcapInstalled())
        {
            var answer = MessageBox.Show(L.T(
                    "Npcap is not installed. The raid announcer uses it to read the network traffic of Aion 2 " +
                    "(read only, like Wireshark).\n\nOpen npcap.com now? Run the installer with its default options, " +
                    "then start the raid announcer again.",
                    "Npcap ist nicht installiert. Der Raid-Ansager liest damit den Netzwerkverkehr von Aion 2 mit " +
                    "(nur lesen, wie Wireshark).\n\nJetzt npcap.com öffnen? Installer mit den Standardoptionen ausführen, " +
                    "danach den Raid-Ansager neu starten."),
                L.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) Open("https://npcap.com/#download");
            return 3;
        }
        if (replayFile is not null && !File.Exists(replayFile))
        {
            MessageBox.Show(L.T("File not found: ", "Datei nicht gefunden: ") + replayFile, L.AppName, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        using var ctx = new AnsagerContext(settings, replayFile, speed);
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
    private readonly Settings _settings;
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
    private readonly string? _replayName;
    private readonly double _speed;
    private ToolStripMenuItem _statusItem = null!, _modsItem = null!, _moveItem = null!, _muteItem = null!;
    private readonly long _startedMs = Environment.TickCount64;
    private volatile string _captureRaw = "starting";
    private volatile string? _replayState;
    private volatile bool _connected;
    private Fight? _lastFight;
    private List<BossMod> _mods = new();
    private List<string> _modProblems = new();
    private int _tooltipTicks;

    public AnsagerContext(Settings settings, string? replayFile, double speed)
    {
        _settings = settings;
        _replayName = replayFile is null ? null : Path.GetFileName(replayFile);
        _speed = speed;
        _speaker = new Speaker(_settings);
        _mods = LoadMods();
        _engine = new Announcer(_data, _mods, _speaker,
            new AnnouncerOptions
            {
                Role = _settings.Role, SayUnknown = _settings.SayUnknown, AllBosses = _settings.AllBosses, InstanceBossesOnly = true,
            });
        _logs = new FightLogWriter(replayFile is null ? AppPaths.Fights : Path.Combine(AppPaths.Fights, "replay"), _data);
        _engine.FightEnded += f =>
        {
            try { _logs.Write(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("log", ex); }
            _lastFight = f;
        };
        _pipe = new Pipeline(_engine) { RawFolder = replayFile is null && _settings.RecordRaw ? AppPaths.Raw : null };
        _pipe.FlowLocked += _ => _connected = true;
        _clock = new ReplayClock(live: replayFile is null);

        _overlay = new OverlayForm(_settings);
        _overlay.Show();

        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = L.AppName,
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
            _sniffer.StatusChanged += s => _captureRaw = s;
            _sniffer.Start();
            _tray.ShowBalloonTip(4000, L.T("Raid announcer is running", "Raid-Ansager läuft"),
                L.T("Callouts start automatically when a dungeon or raid boss is pulled. Right-click the icon for settings.",
                    "Ansagen kommen automatisch, sobald ein Dungeon- oder Raid-Boss gepullt wird. Rechtsklick auf das Symbol für Einstellungen."),
                ToolTipIcon.Info);
        }
        else
        {
            _ = Pipeline.Replay(_pipe, replayFile, speed, _clock, _cts.Token).ContinueWith(t =>
            {
                _replayState = t.IsFaulted ? "error: " + t.Exception?.GetBaseException().Message : "done";
            }, TaskScheduler.Default);
        }

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private List<BossMod> LoadMods()
    {
        _modProblems = new List<string>();
        return ModLoader.LoadAll(AppPaths.ModFolders, _modProblems);
    }

    private string ModsSummary() => _mods.Count == 0
        ? L.T("No boss mods loaded", "Keine Boss-Mods geladen")
        : "Mods: " + string.Join(", ", _mods.Select(m => $"{m.Id} {m.Version}{(m.Source == "eingebaut" ? "" : L.T(" (file)", " (Datei)"))}"));

    /// <summary>The capture state in the current language.</summary>
    private string Capture()
    {
        if (_replayName is not null)
            return _replayState switch
            {
                null => L.T($"replay ×{_speed:0.#}: {_replayName}", $"Wiedergabe ×{_speed:0.#}: {_replayName}"),
                "done" => L.T("replay finished", "Wiedergabe beendet"),
                var e => L.T("replay ", "Wiedergabe-") + e,
            };
        var s = _captureRaw;
        return s switch
        {
            "starting" => L.T("starting …", "starte …"),
            "stopped" => L.T("stopped", "gestoppt"),
            "AION 2 is not running" => L.T("Aion 2 is not running", "Aion 2 läuft nicht"),
            "waiting for the game to connect" => L.T("waiting for the game connection", "warte auf die Spielverbindung"),
            _ when s.StartsWith("AION 2 not running", StringComparison.Ordinal) => L.T("Aion 2 is not running", "Aion 2 läuft nicht"),
            _ when s.StartsWith("no game connection", StringComparison.Ordinal) => L.T("no game connection", "keine Spielverbindung"),
            _ when s.StartsWith("capturing on", StringComparison.Ordinal) =>
                L.T("reading (", "liest mit (") + s["capturing on ".Length..] + ")",
            _ when s.StartsWith("Npcap is not installed", StringComparison.Ordinal) => L.T("Npcap missing (npcap.com)", "Npcap fehlt (npcap.com)"),
            _ when s.StartsWith("capture error", StringComparison.Ordinal) =>
                L.T("capture error: ", "Fehler beim Mitlesen: ") + s["capture error: ".Length..],
            _ when s.StartsWith("no Npcap adapter", StringComparison.Ordinal) =>
                L.T("no network adapter carries the game connection (VPN?)", "kein Netzwerkadapter mit Spielverbindung (VPN?)"),
            _ => s,
        };
    }

    private bool CaptureProblem()
    {
        var s = _captureRaw;
        return s.StartsWith("Npcap", StringComparison.Ordinal) || s.StartsWith("capture error", StringComparison.Ordinal) ||
               s.StartsWith("no Npcap adapter", StringComparison.Ordinal);
    }

    private void OnTick()
    {
        _engine.Tick(_clock.Now());
        var st = _engine.Snapshot();
        string? status = null;
        var early = Environment.TickCount64 - _startedMs < 12_000;
        var problem = CaptureProblem();
        if (problem || early || !_clock.Live)
            status = $"{L.AppName}: " + (_connected && !problem && _clock.Live ? L.T("connected", "verbunden") : Capture()) +
                     (_modProblems.Count > 0 ? L.T($" · {_modProblems.Count} mod problem(s)", $" · {_modProblems.Count} Mod-Problem(e)") : "");
        if (_settings.Muted) status = (status is null ? "" : status + " · ") + L.T("MUTED", "STUMM");
        _overlay.Render(st, status);

        if (++_tooltipTicks >= 20)
        {
            _tooltipTicks = 0;
            var state = _connected ? L.T("connected", "verbunden") : Capture();
            _statusItem.Text = char.ToUpper(state[0]) + state[1..] + (st.InModZone ? L.T($" · raid zone {st.Zone}", $" · Raid-Zone {st.Zone}") : "");
            var tip = $"{L.AppName} – {state}";
            if (_lastFight is { } f)
                tip += $"\n{FightReport.Boss(f, _data)}: {f.Outcome}, {FightReport.Clock((f.EndMs - f.PullMs) / 1000.0)}";
            _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var keys = L.T("Ctrl+Alt+", "Strg+Alt+");
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{L.AppName} {TrayApp.Version}") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        _statusItem = new ToolStripMenuItem(Capture()) { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        _moveItem = new ToolStripMenuItem(L.T("Move overlay", "Overlay verschieben"), null, (_, _) => ToggleMove())
            { ShortcutKeyDisplayString = keys + "O", Checked = _overlay.MoveMode };
        menu.Items.Add(_moveItem);
        _muteItem = new ToolStripMenuItem(L.T("Mute", "Stumm"), null, (_, _) => ToggleMute())
            { ShortcutKeyDisplayString = keys + "M", Checked = _settings.Muted };
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripMenuItem(L.T("Test callout", "Test-Ansage"), null, (_, _) => _engine.Test(_clock.Now()))
            { ShortcutKeyDisplayString = keys + "T" });
        menu.Items.Add(new ToolStripSeparator());

        var language = new ToolStripMenuItem("Language / Sprache");
        foreach (var (key, label) in new[] { ("en", "English"), ("de", "Deutsch") })
            language.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetLanguage(key)) { Checked = L.Lang == key });
        menu.Items.Add(language);

        var role = new ToolStripMenuItem(L.T("My role", "Meine Rolle"));
        foreach (var (key, label) in new[]
                 {
                     ("alle", L.T("All callouts", "Alle Ansagen")), ("tank", "Tank"), ("heiler", L.T("Healer", "Heiler")),
                     ("dd", L.T("Damage (DPS)", "Schaden (DD)")),
                 })
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
        menu.Items.Add(Toggle(L.T("All dungeon bosses (also without a mod)", "Alle Dungeon-Bosse (auch ohne Mod)"),
            () => _settings.AllBosses, v =>
            {
                _settings.AllBosses = v;
                _engine.Options.AllBosses = v;
            }));
        menu.Items.Add(Toggle(L.T("Read out unknown boss skills", "Unbekannte Boss-Fähigkeiten vorlesen"),
            () => _settings.SayUnknown, v =>
            {
                _settings.SayUnknown = v;
                _engine.Options.SayUnknown = v;
            }));
        menu.Items.Add(Toggle(L.T("Warning tones", "Warntöne"), () => _settings.Tones, v => _settings.Tones = v));

        var voice = new ToolStripMenuItem(L.T("Voice", "Stimme"));
        foreach (var name in Speaker.Voices())
        {
            var item = new ToolStripMenuItem(name) { Checked = name == _speaker.VoiceName };
            item.Click += (_, _) =>
            {
                _settings.Voice = name;
                _speaker.Apply();
                foreach (ToolStripMenuItem i in voice.DropDownItems) i.Checked = i.Text == _speaker.VoiceName;
                _settings.Save();
                _speaker.Say(L.T("This is my voice", "So klinge ich"), urgent: true);
            };
            voice.DropDownItems.Add(item);
        }
        menu.Items.Add(voice);
        menu.Items.Add(Choice(L.T("Volume", "Lautstärke"), [25, 50, 75, 100], v => $"{v} %", () => _settings.Volume, v =>
        {
            _settings.Volume = v;
            _speaker.Apply();
            _speaker.Sound("info");
        }));
        menu.Items.Add(Choice(L.T("Speech rate", "Sprechtempo"), [0, 2, 4, 6],
            v => v switch
            {
                0 => L.T("normal", "normal"), 2 => L.T("a bit faster", "etwas schneller"), 4 => L.T("fast", "schnell"),
                _ => L.T("very fast", "sehr schnell"),
            },
            () => _settings.Rate, v =>
            {
                _settings.Rate = v;
                _speaker.Apply();
                _speaker.Say(L.T("Spread out, then stack", "Verteilen, dann sammeln"), urgent: true);
            }));
        menu.Items.Add(Choice(L.T("Overlay size", "Overlay-Größe"), [80, 100, 125, 150], v => $"{v} %",
            () => (int)Math.Round(_settings.Scale * 100), v =>
            {
                _settings.Scale = v / 100.0;
                _engine.Test(_clock.Now());
            }));
        menu.Items.Add(Toggle(L.T("Save raid recordings (for improving the mods)", "Raid-Mitschnitt speichern (für die Auswertung)"),
            () => _settings.RecordRaw, v =>
            {
                _settings.RecordRaw = v;
                if (_clock.Live) _pipe.RawFolder = v ? AppPaths.Raw : null;
            }));
        menu.Items.Add(new ToolStripSeparator());
        _modsItem = new ToolStripMenuItem(ModsSummary()) { Enabled = false };
        menu.Items.Add(_modsItem);
        menu.Items.Add(new ToolStripMenuItem(L.T("Reload mods", "Mods neu laden"), null, (_, _) => ReloadMods()));
        menu.Items.Add(new ToolStripMenuItem(L.T("Open mods folder", "Mods-Ordner öffnen"), null, (_, _) => OpenFolder(AppPaths.Mods, exportMods: true)));
        menu.Items.Add(new ToolStripMenuItem(L.T("Open fight logs", "Kampfprotokolle öffnen"), null, (_, _) => OpenFolder(AppPaths.Fights)));
        menu.Items.Add(new ToolStripMenuItem(L.T("Open raid recordings", "Raid-Mitschnitte öffnen"), null, (_, _) => OpenFolder(AppPaths.Raw)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("Exit", "Beenden"), null, (_, _) => ExitThread()));
        return menu;
    }

    private void SetLanguage(string lang)
    {
        if (L.Lang == lang) return;
        L.Lang = lang;
        _settings.Language = lang;
        _settings.Save();
        _speaker.Apply(); // a voice of the new language
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
        if (!Speaker.HasVoiceFor(lang))
            _tray.ShowBalloonTip(8000, L.AppName, L.T(
                    "No English voice is installed. Windows Settings → Time & language → Speech → Add voices.",
                    "Keine deutsche Stimme installiert. Windows-Einstellungen → Zeit und Sprache → Sprache → Stimmen hinzufügen."),
                ToolTipIcon.Warning);
        _speaker.Say(L.T("English", "Deutsch"), urgent: true);
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
        if (!_settings.Muted) _speaker.Say(L.T("Sound on", "Ton an"), urgent: true);
    }

    private void ReloadMods()
    {
        _mods = LoadMods();
        _engine.SetMods(_mods);
        _modsItem.Text = ModsSummary();
        var msg = _modProblems.Count == 0 ? ModsSummary()
            : ModsSummary() + "\n\n" + L.T("Problems:", "Probleme:") + "\n" + string.Join("\n", _modProblems.Take(12));
        _tray.ShowBalloonTip(5000, L.T("Boss mods reloaded", "Boss-Mods neu geladen"), msg,
            _modProblems.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
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
        _engine.Flush(L.T("program closed", "Programm beendet"));
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

/// <summary>Ctrl+Alt+key hotkeys through RegisterHotKey (no keyboard hook; the game is not touched).</summary>
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
