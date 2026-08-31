using System;
using System.Globalization;
using System.Text;

namespace CntlLevelConnection;

/// <summary>
/// エンジンが確定させたコントローラの状態の出力先。ループの各周回で Write が呼ばれる。
/// Windows の仮想コントローラ（ViGEm）に限らず、別の出力先へ差し替えられるようにするための境界である。
/// 将来、Raspberry Pi での Bluetooth や USB の HID ガジェットも、3つ目の実装としてここに収まる。
/// Write はエンジンのループのスレッドから、非常に高い頻度で呼ばれるので、重い処理をしてはならない。
/// </summary>
public interface IPadSink : IDisposable
{
    /// <summary>出力先を用意する。使える状態になったら true。失敗したときは Error に理由を入れる。</summary>
    bool Start();

    /// <summary>この周回の状態を出力する。ループのスレッドから毎周回呼ばれる。</summary>
    void Write(in PadState s);

    /// <summary>この出力先が使える状態か。</summary>
    bool Connected { get; }

    /// <summary>Start に失敗した理由。</summary>
    string? Error { get; }

    /// <summary>画面や get_info に出す短い名前。</summary>
    string Name { get; }
}

/// <summary>
/// 出力先が、自分で仮想のコントローラを作る種類のものであることを示す。
/// その識別子を物理パッドの一覧から除かないと、自分の出力を自分で読んでしまう。
/// ViGEm の出力先がこれを実装する。
/// </summary>
public interface IOwnPadIdentity
{
    /// <summary>この出力が作った仮想コントローラの識別子（分からなければ null）。</summary>
    string? OwnPadId { get; }

    /// <summary>
    /// この出力が作った仮想コントローラが乗っている XInput のスロット（分からなければ -1）。
    /// XInput の機体は XInput で直接読むので、識別子とは別にスロットの番号も要る（Issue #15）。
    /// </summary>
    int OwnXInputSlot => -1;
}

/// <summary>
/// コントローラの状態を、つながっているページへ WebSocket で配る出力先。
/// オペレーティングシステムの仮想コントローラを介さないので、どの環境でも動き、
/// ブラウザのゲームは「どのゲームパッドを読むか」という問題から解放される。
///
/// 送る量を抑えるため、状態が前回と変わったときだけ送る。さらに毎秒60回を上限とする。
/// エンジンのループは毎秒数百回まわるので、そのまま全部送ると多すぎるためである。
/// 変化が無いあいだも、受け手が「つながっているか」を判断できるように、1秒に1回だけ同じ状態を送る。
///
/// 送信そのものは待たずに投げる（ループを止めないため）。同時に何本も飛ばないよう、
/// 直前の送信が終わるまでは次を出さない。
/// </summary>
public sealed class WebSocketSink : IPadSink
{
    private readonly WebController _web;
    private PadState _last;
    private bool _hasLast;
    private long _lastSendTicks;
    private long _lastAnyTicks;
    private int _sending;   // 0 = 送信していない、1 = 送信中

    private static readonly long MinIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 60;   // 毎秒60回まで
    private static readonly long HeartbeatTicks = System.Diagnostics.Stopwatch.Frequency;          // 1秒ごとの生存の合図

    public WebSocketSink(WebController web) => _web = web;

    public string Name => "websocket";
    public bool Connected => true;      // 相手がいなくても、出力先としては常に使える
    public string? Error => null;

    public bool Start() => true;

    public void Write(in PadState s)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool changed = !_hasLast || !Same(s, _last);

        if (!changed && now - _lastAnyTicks < HeartbeatTicks) return;      // 変化が無く、合図の時刻でもない
        if (changed && now - _lastSendTicks < MinIntervalTicks) return;    // 変化はあるが、上限の頻度を超える

        // 直前の送信がまだ終わっていなければ、この周回は見送る（ループを詰まらせないため）。
        // このとき _last を更新してはならない。更新すると、送っていない状態を「送った」と見なして
        // しまい、次の周回で変化なしと判断されて、その入力が二度と送られなくなる。
        if (System.Threading.Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

        _last = s; _hasLast = true;
        _lastSendTicks = now; _lastAnyTicks = now;

        var json = ToJson(s);
        _ = _web.BroadcastTextAsync(json).ContinueWith(_ => System.Threading.Volatile.Write(ref _sending, 0));
    }

    private static bool Same(in PadState a, in PadState b)
        => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;

    /// <summary>
    /// ページが読みやすい形にする。種別は "pad" とし、ウェブ版コントローラへの配信（reload や miclvl）と混ざらないようにする。
    /// ボタンは名前の配列で送る（受け手が番号の対応を知らなくてよい）。軸は -1..1、トリガーは 0..1 に正規化する。
    /// </summary>
    private static string ToJson(in PadState s)
    {
        var sb = new StringBuilder(220);
        sb.Append("{\"t\":\"pad\",\"buttons\":[");
        bool first = true;
        foreach (var name in ControllerEngine.NamesFromMask(s.Buttons))
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(name).Append('"');
            first = false;
        }
        sb.Append("],\"lx\":").Append(F(s.LX / 32767.0))
          .Append(",\"ly\":").Append(F(s.LY / 32767.0))
          .Append(",\"rx\":").Append(F(s.RX / 32767.0))
          .Append(",\"ry\":").Append(F(s.RY / 32767.0))
          .Append(",\"lt\":").Append(F(s.LT / 255.0))
          .Append(",\"rt\":").Append(F(s.RT / 255.0))
          .Append('}');
        return sb.ToString();
    }

    private static string F(double v) => Math.Round(Math.Clamp(v, -1.0, 1.0), 3).ToString(CultureInfo.InvariantCulture);

    public void Dispose() { /* 保持している資源は無い */ }
}
