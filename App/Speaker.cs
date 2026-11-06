using System.Media;
using System.Speech.Synthesis;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

/// <summary>Speech (Windows voices, German preferred) and short warning tones.</summary>
public sealed class Speaker : IAnnouncerOutput, IDisposable
{
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly SpeechSynthesizer _tts = new();
    private readonly Dictionary<string, SoundPlayer> _tones = new();
    private int _pending;
    private int _toneVolume = -1;

    public Speaker(Settings settings)
    {
        _settings = settings;
        _tts.SetOutputToDefaultAudioDevice();
        _tts.SpeakCompleted += (_, _) => Interlocked.Decrement(ref _pending);
        Apply();
    }

    public string VoiceName { get; private set; } = "";

    public static List<string> Voices()
    {
        using var tts = new SpeechSynthesizer();
        return tts.GetInstalledVoices().Where(v => v.Enabled)
            .OrderBy(v => v.VoiceInfo.Culture.Name.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(v => v.VoiceInfo.Name).ToList();
    }

    /// <summary>Takes over voice, rate and volume from the settings.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            var voices = _tts.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            var pick = voices.FirstOrDefault(v => v.Name == _settings.Voice)
                       ?? voices.FirstOrDefault(v => v.Culture.Name.StartsWith("de", StringComparison.OrdinalIgnoreCase))
                       ?? voices.FirstOrDefault();
            if (pick is not null)
            {
                _tts.SelectVoice(pick.Name);
                VoiceName = pick.Name;
            }
            _tts.Rate = Math.Clamp(_settings.Rate, -10, 10);
            _tts.Volume = Math.Clamp(_settings.Volume, 0, 100);
        }
    }

    public void Say(string text, bool urgent)
    {
        if (_settings.Muted || string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (urgent) _tts.SpeakAsyncCancelAll();
            else if (Volatile.Read(ref _pending) >= 2) return; // do not pile up stale callouts
            Interlocked.Increment(ref _pending);
            _tts.SpeakAsync(text);
        }
    }

    public void Sound(string kind)
    {
        if (_settings.Muted || !_settings.Tones) return;
        lock (_gate)
        {
            if (_toneVolume != _settings.Volume)
            {
                foreach (var p in _tones.Values) p.Dispose();
                _tones.Clear();
                _toneVolume = _settings.Volume;
            }
            if (!_tones.TryGetValue(kind, out var player))
            {
                var wav = kind == "alarm"
                    ? Tones([(1046, 90), (0, 35), (784, 90), (0, 35), (1046, 110)], _toneVolume)
                    : Tones([(660, 110)], _toneVolume * 0.6);
                _tones[kind] = player = new SoundPlayer(new MemoryStream(wav));
                player.Load();
            }
            try { player.Play(); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>16-bit mono WAV of sine beeps (frequency 0 = pause), with short fades against clicks.</summary>
    private static byte[] Tones((int Hz, int Ms)[] parts, double volume)
    {
        const int rate = 22050;
        var samples = new List<short>();
        var amp = 0.55 * Math.Clamp(volume, 0, 100) / 100 * short.MaxValue;
        foreach (var (hz, ms) in parts)
        {
            var n = rate * ms / 1000;
            var fade = Math.Min(n / 4, rate * 8 / 1000);
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                samples.Add(hz == 0 ? (short)0 : (short)(amp * env * Math.Sin(2 * Math.PI * hz * i / rate)));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Count * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tts.SpeakAsyncCancelAll();
            _tts.Dispose();
            foreach (var p in _tones.Values) p.Dispose();
        }
    }
}
