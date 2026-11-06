using System.Diagnostics;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;
using RaidAnsager.Engine;
using RaidAnsager.Protocol;
using SharpPcap;

namespace RaidAnsager.App;

/// <summary>
/// Packets → TCP streams → game frames (Cube Radar core) → <see cref="GameEvent"/>s → <see cref="Announcer"/>.
/// Optionally keeps a raw pcapng of the time spent in a mod zone (with 20 s of history before entering it).
/// </summary>
public sealed class Pipeline : IDisposable
{
    private readonly Announcer _engine;
    private readonly DumpSession _session;
    private readonly List<GameEvent> _events = new();
    private readonly object _rawGate = new();
    private readonly Queue<(RawCapture Raw, string Iface, string? Desc, long Ms)> _history = new();
    private PcapNgWriter? _raw;

    public Pipeline(Announcer engine, bool tickWithPackets = false)
    {
        _engine = engine;
        TickWithPackets = tickWithPackets;
        _session = new DumpSession(OpcodeBook.Empty, new DumpOptions { WriteHeartbeats = false }, jsonlPath: null);
        _session.Tap = (GameFlow flow, in FrameMeta meta, ReadOnlySpan<byte> body) =>
        {
            _events.Clear();
            CombatDecoder.Decode(meta.Opcode, body, flow.TimeMs, _events);
            foreach (var e in _events) _engine.OnEvent(e);
        };
        _session.FlowLocked += f => FlowLocked?.Invoke(f.Key.ToString());
    }

    /// <summary>Analysis: advance the engine clock with every packet instead of a wall clock.</summary>
    public bool TickWithPackets { get; }

    /// <summary>Folder for raw recordings while in a mod zone; null = off.</summary>
    public string? RawFolder { get; set; }

    public string? RawFile { get; private set; }

    public long Packets => _session.Packets;

    public long LastPacketMs => _session.LastPacketMs;

    public event Action<string>? FlowLocked;

    public void OnPacket(RawCapture raw, string iface, string? desc = null)
    {
        try
        {
            _session.OnPacket(raw, iface, desc);
            var ms = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
            if (TickWithPackets) _engine.Tick(ms);
            if (RawFolder is not null) Raw(raw, iface, desc, ms);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("Paket", ex); // one bad packet must not stop the capture thread
        }
    }

    private void Raw(RawCapture raw, string iface, string? desc, long ms)
    {
        lock (_rawGate)
        {
            var inZone = _engine.InModZone;
            if (inZone && _raw is null)
            {
                Directory.CreateDirectory(RawFolder!);
                RawFile = Path.Combine(RawFolder!, $"{DateTime.Now:yyyyMMdd_HHmmss}_zone{_engine.Zone}.pcapng");
                _raw = new PcapNgWriter(RawFile, "RaidAnsager");
                while (_history.Count > 0)
                {
                    var h = _history.Dequeue();
                    Write(h.Raw, h.Iface, h.Desc);
                }
            }
            else if (!inZone && _raw is not null)
            {
                _raw.Dispose();
                _raw = null;
            }

            if (_raw is not null)
            {
                Write(raw, iface, desc);
                return;
            }
            _history.Enqueue((raw, iface, desc, ms));
            while (_history.Count > 0 && ms - _history.Peek().Ms > 20_000) _history.Dequeue();
        }
    }

    private void Write(RawCapture raw, string iface, string? desc)
    {
        var id = _raw!.Interface(iface, (int)raw.LinkLayerType, desc);
        var t = raw.Timeval;
        _raw.WritePacket(id, (ulong)t.Seconds * 1_000_000 + (ulong)t.MicroSeconds, raw.Data, raw.PacketLength);
    }

    /// <summary>Feeds a capture file. <paramref name="speed"/> 0 = as fast as possible, 1 = real time.</summary>
    public static Task Replay(Pipeline p, string path, double speed, ReplayClock clock, CancellationToken ct) => Task.Run(() =>
    {
        var real = Stopwatch.StartNew();
        long? first = null;
        CaptureFileSource.ReadAll(path, raw =>
        {
            var ms = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
            if (first is null)
            {
                first = ms;
                clock.Start(ms, speed);
            }
            if (speed > 0)
            {
                var wait = (ms - first.Value) / speed - real.ElapsedMilliseconds;
                if (wait > 1) ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(wait, 60_000)));
            }
            p.OnPacket(raw, "replay");
        }, ct);
    }, ct);

    public void Dispose()
    {
        lock (_rawGate)
        {
            _raw?.Dispose();
            _raw = null;
        }
        _session.Dispose();
    }
}

/// <summary>The engine clock: wall clock live, capture time during a replay (0 until the first packet).</summary>
public sealed class ReplayClock(bool live)
{
    private readonly Stopwatch _real = new();
    private long _firstMs;
    private double _speed = 1;
    private bool _started;

    public bool Live { get; } = live;

    public void Start(long firstPacketMs, double speed)
    {
        _firstMs = firstPacketMs;
        _speed = speed <= 0 ? 1 : speed;
        _real.Restart();
        _started = true;
    }

    public long Now() => Live
        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        : _started ? _firstMs + (long)(_real.ElapsedMilliseconds * _speed) : 0;
}
