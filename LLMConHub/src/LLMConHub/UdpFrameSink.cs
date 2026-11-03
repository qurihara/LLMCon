using System;
using System.Net;
using System.Net.Sockets;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// 物理の入力が届いた時刻を知っている入力源が実装する。出力の電文に入力の時刻を入れ、遅延を測るために使う。
/// EvdevPadSource がこれを実装する。
/// </summary>
public interface IInputTimestamp
{
    /// <summary>最後に状態が変わった入力の時刻（PadFrame.NowMicros と同じ時計で、マイクロ秒）。分からなければ 0。</summary>
    ulong LastInputMicros { get; }
}

/// <summary>
/// 確定したコントローラの状態を、PadFrame の電文にして UDP で送る出力先。IPadSink の3つ目の実装である。
///
/// 送り先は、同じ機械で動く橋渡しのプログラムである。橋渡しのプログラムが、Bluetooth のコントローラとして
/// ゲーム機や PC へ出力する。Bluetooth の手続き（ペアリング、ゲーム機ごとの応答）は既存の実装
/// （Switch なら nxbt）に任せ、LLMCon の側は状態を時間どおりに渡すことだけを受け持つ。
///
/// Write はエンジンのループのスレッドから、毎秒約1000回呼ばれる。そのため次のようにした。
///   状態が前回と変わったときだけ、その周回で送る。頻度の上限は設けない。同じ機械の中の UDP は
///   1回あたり数十マイクロ秒で済み、上限を設けると、その分だけ入力が遅れるからである。
///   変化が無いあいだも、受け手が「つながっているか」を判断できるように、100ミリ秒に1回だけ同じ状態を送る。
///   電文の領域は最初に1つだけ確保し、周回ごとにメモリを確保しない。
///   送り先がまだ起動していないとき、Linux では送信が「接続を拒否された」という例外になる。
///   これは受け手が後から起動する普通の状況なので、数えるだけで、ループには伝えない。
/// </summary>
public sealed class UdpFrameSink : IPadSink
{
    private readonly IPEndPoint _target;
    private readonly IInputTimestamp? _inputClock;
    private Socket? _socket;
    private readonly byte[] _buf = new byte[PadFrame.Size];
    private PadState _last;
    private bool _hasLast;
    private uint _seq;
    private long _lastAnyMicros;

    private const long HeartbeatMicros = 100_000;

    /// <summary>送れなかった回数（受け手が起動していないときなど）。診断のために出す。</summary>
    public long SendErrors { get; private set; }

    /// <summary>送った電文の数。</summary>
    public long Sent { get; private set; }

    public UdpFrameSink(IPEndPoint target, IInputTimestamp? inputClock = null)
    {
        _target = target;
        _inputClock = inputClock;
    }

    /// <summary>"127.0.0.1:9100" のような指定から作る。ポートを省けば 9100 にする。</summary>
    public static UdpFrameSink FromSpec(string? spec, IInputTimestamp? inputClock = null)
    {
        var host = "127.0.0.1";
        int port = 9100;
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var t = spec.Trim();
            int colon = t.LastIndexOf(':');
            if (colon > 0)
            {
                host = t[..colon];
                if (!int.TryParse(t[(colon + 1)..], out port)) throw new ArgumentException($"UDP の送り先のポートが読めません：{spec}");
            }
            else if (int.TryParse(t, out var onlyPort)) port = onlyPort;
            else host = t;
        }
        var addr = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host)[0];
        return new UdpFrameSink(new IPEndPoint(addr, port), inputClock);
    }

    public string Name => $"udp({_target})";
    public bool Connected => _socket != null;
    public string? Error { get; private set; }

    public bool Start()
    {
        try
        {
            _socket = new Socket(_target.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            _socket.Connect(_target);   // 送り先を固定しておくと、送るたびに宛先を組み立てずに済む
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _socket = null;
            return false;
        }
    }

    public void Write(in PadState s)
    {
        var sock = _socket;
        if (sock == null) return;

        ulong now = PadFrame.NowMicros();
        bool changed = !_hasLast || !Same(s, _last);
        if (!changed && (long)now - _lastAnyMicros < HeartbeatMicros) return;

        _last = s; _hasLast = true; _lastAnyMicros = (long)now;
        ulong input = _inputClock?.LastInputMicros ?? 0;
        PadFrame.Encode(_buf, s, unchecked(++_seq), now, input, heartbeat: !changed);
        try
        {
            sock.Send(_buf, SocketFlags.None);
            Sent++;
        }
        catch (SocketException)
        {
            // 受け手が起動していない、など。送り直しは周回ごとには行わない。
            // 周回ごとに送り直すと、受け手が止まっているあいだ毎秒約1000回の例外が起き、ループが重くなる。
            // 受け手が起動すれば、100ミリ秒ごとの定期の電文で、いまの状態が届く。
            SendErrors++;
        }
    }

    private static bool Same(in PadState a, in PadState b)
        => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;

    public void Dispose()
    {
        try { _socket?.Dispose(); } catch { /* ignore */ }
        _socket = null;
    }
}
