using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Gaming.Input;

namespace CntlLevelConnection;

public sealed class WgiPadInfo
{
    public string Id = "";
    public string Name = "";
    public override string ToString() => Name;
}

/// <summary>
/// Windows.Gaming.Input の RawGameController を使い、XInput/DirectInput を問わず
/// あらゆるゲームパッドを列挙・読み取る。raw ボタン/軸は既定マッピングで PadState に変換する
/// （デバイス個別の割り当て調整は将来対応）。
/// </summary>
internal static class WgiPad
{
    /// <summary>
    /// Windows.Gaming.Input は、追加と取り外しの事象に手を挙げておかないと、読み取りを配り続けない。
    /// 一覧を取るだけでは足りない。手を挙げていないと、起動した時点の値が1回取れるだけで、
    /// そのあと時刻印が進まなくなる。静止中に報告を送らない機体では、1回すら取れない。
    ///
    /// 2026/8/6 に、これで LLMCon が ROG Ally の内蔵コントローラを一度も読めていなかったことが
    /// 分かった。確認用のプログラムは最初から登録していたので読めており、その差が長く分からなかった。
    /// なお、これは窓を持つこととは別の条件で、両方が要る。窓を持たない処理では、事象に手を
    /// 挙げていても XInput の機体は読めない。
    ///
    /// 中身は空でよい。手を挙げること自体に意味がある。
    /// </summary>
    static WgiPad()
    {
        try
        {
            // あとから挿された機体は、事象で受け取ったその場で一度読んでおく。
            // これをしないと、一覧には出るのに読み取りが一度も届かない機体になる。
            // 2026/8/6 に実測で分かった。処理が起動した時点で居た機体は読めるのに、
            // あとから現れた機体だけが時刻印 0 のままだった。
            RawGameController.RawGameControllerAdded += (_, c) => Touch(c);
            RawGameController.RawGameControllerRemoved += (_, _) => { };
            Gamepad.GamepadAdded += (_, _) => { };
            Gamepad.GamepadRemoved += (_, _) => { };
            _ = Gamepad.Gamepads.Count;
        }
        catch { /* 事象に手を挙げられない環境でも、列挙だけは動かしたい */ }
    }

    /// <summary>その機体を一度読んで、この処理に対して読み取りが流れ始めるようにする。</summary>
    private static void Touch(RawGameController c)
    {
        try
        {
            var btn = new bool[c.ButtonCount];
            var sw = new GameControllerSwitchPosition[c.SwitchCount];
            var ax = new double[c.AxisCount];
            c.GetCurrentReading(btn, sw, ax);
        }
        catch { /* 読めなくても、あとの処理は続ける */ }
    }

    /// <summary>
    /// ViGEm が作る仮想 Xbox 360 コントローラの製造者と製品の識別子。
    /// 自分の出力を物理パッドと取り違えないための、最後の確認に使う。
    /// </summary>
    private const ushort VirtualVendorId = 0x045E;
    private const ushort VirtualProductId = 0x028E;

    public static IReadOnlyList<string> CurrentIds()
        => RawGameController.RawGameControllers.Select(c => c.NonRoamableId).ToList();

    /// <summary>
    /// 列挙が落ち着くまで待つ。Windows.Gaming.Input は、処理を始めた直後には一覧がまだ空か
    /// 途中までしか埋まっていない。この待ちを入れずに一覧の差を取ると、もとからあった機体が
    /// 「あとから増えたもの」に見え、自分の仮想パッドと取り違える。実際にそれが起きていた。
    /// 一覧の数が続けて変わらなくなったら、落ち着いたとみなす。
    /// </summary>
    public static void WaitUntilEnumerationSettles(int quietMs = 250, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -1;
        var stable = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int now = RawGameController.RawGameControllers.Count;
            if (now != last) { last = now; stable.Restart(); }
            else if (stable.ElapsedMilliseconds >= quietMs) return;
            System.Threading.Thread.Sleep(30);
        }
    }

    /// <summary>その識別子の機体が、ViGEm の作る仮想コントローラらしいかどうか。</summary>
    public static bool LooksLikeVirtualPad(string id)
    {
        var c = Find(id);
        return c != null && c.HardwareVendorId == VirtualVendorId && c.HardwareProductId == VirtualProductId;
    }

    /// <summary>
    /// その機体から、この処理へ読み取りが届いているか。時刻印が 0 でなければ届いている。
    /// 自分の処理が作った仮想コントローラは、自分には配られないので、これが false になる。
    /// 自分のパッドを見分けるのに使う。
    /// </summary>
    public static bool IsReadable(string id)
    {
        var c = Find(id);
        if (c == null) return false;
        try
        {
            var btn = new bool[c.ButtonCount];
            var sw = new GameControllerSwitchPosition[c.SwitchCount];
            var ax = new double[c.AxisCount];
            return c.GetCurrentReading(btn, sw, ax) != 0;
        }
        catch { return false; }
    }

    public static List<WgiPadInfo> List(string? excludeId)
    {
        var result = new List<WgiPadInfo>();
        foreach (var c in RawGameController.RawGameControllers)
        {
            if (c.NonRoamableId == excludeId) continue;
            result.Add(new WgiPadInfo { Id = c.NonRoamableId, Name = SafeName(c) });
        }
        return result;
    }

    public static RawGameController? Find(string id)
        => RawGameController.RawGameControllers.FirstOrDefault(c => c.NonRoamableId == id);

    private static string SafeName(RawGameController c)
    {
        try
        {
            var n = c.DisplayName;
            return string.IsNullOrWhiteSpace(n) ? "(game controller)" : n;
        }
        catch { return "(game controller)"; }
    }

    /// <summary>
    /// 選択中コントローラを、与えられた軸の割り当てで読む。バッファは呼び出し側で再利用（GC回避）。
    /// 軸の値はどれも 0 から 1 で返るが、スティックは中央が 0.5 の両側、トリガーは 0 が離した状態の
    /// 片側の範囲なので、変換の仕方を分ける。
    /// </summary>
    public static bool TryRead(RawGameController c, PadAxisMap map, PadButtonMap buttons, ref bool[] btn, ref GameControllerSwitchPosition[] sw, ref double[] ax, out PadState s)
    {
        s = default;
        try
        {
            int bc = c.ButtonCount, sc = c.SwitchCount, ac = c.AxisCount;
            if (btn.Length < bc) btn = new bool[bc];
            if (sw.Length < sc) sw = new GameControllerSwitchPosition[sc];
            if (ax.Length < ac) ax = new double[ac];
            ulong ts = c.GetCurrentReading(btn, sw, ax);

            // 時刻印が 0 なら、この機体からまだ一度も報告が届いていない。配列は既定値のままである。
            // これを読み取りとして扱うと、軸 0.000 が下の Stick() で「中央 0.5 から見て端」と解釈され、
            // -32767 になる。つまりスティックが左いっぱいかつ上いっぱいに張り付く。
            // 無効として捨て、呼び出し側には「読めなかった」と伝える（Issue #7）。
            if (ts == 0) return false;

            // 時刻印が付いていても、軸がすべてちょうど 0.000 のときはスティックの値を作らない。
            // 理由は2つある。ひとつは、報告が途中で止まって古い値が凍結したときに、この形になるのを
            // 実際に観測したこと。もうひとつは、レバーレスの格闘ゲーム用のようにアナログスティックを
            // 持たない機体が、本当に全軸 0.000 を返すことである。どちらの場合も、それを
            // 「両方のスティックが左いっぱいかつ上いっぱい」と読むのは誤りである。
            // スティックが中央 0.5 の両側を取る以上、全部がちょうど端に張り付くことは現実には起きない。
            // ボタンとハットスイッチは信用してよいので、そちらは通す。
            bool allAxesZero = true;
            for (int i = 0; i < ac; i++) if (ax[i] != 0.0) { allAxesZero = false; break; }

            // ref パラメータはローカル関数から参照できないため、配列参照をローカルへ退避
            var btnL = btn; var axL = ax;

            ushort b = 0;
            // トリガーへ割り当てられたボタンは、押されていれば目一杯の値にする。
            // ボタンは2値なので、アナログの値は得られない。
            byte btnLT = 0, btnRT = 0;
            for (int i = 0; i < bc; i++)
            {
                if (!btnL[i]) continue;
                var target = buttons.TargetOf(i);
                if (target == null) continue;
                if (target == "LT") { btnLT = 255; continue; }
                if (target == "RT") { btnRT = 255; continue; }
                b |= ControllerEngine.MaskOf(target);
            }

            // 十字キー（ハットスイッチ）
            if (sc > 0)
            {
                var p = sw[0];
                if (p is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight) b |= 0x0001;
                if (p is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight) b |= 0x0002;
                if (p is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft) b |= 0x0004;
                if (p is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight) b |= 0x0008;
            }
            s.Buttons = b;

            // スティック: 0..1（中央 0.5）を -32768..32767 へ。静止時のゆらぎは不感帯で落とす。
            const int Dz = 3000;
            bool axesUsable = !allAxesZero;
            short Stick(int i)
            {
                if (!axesUsable || i < 0 || i >= ac) return 0;
                short v = (short)Math.Clamp((axL[i] - 0.5) * 2.0 * 32767.0, -32768, 32767);
                return Math.Abs((int)v) < Dz ? (short)0 : v;
            }
            // トリガー: 0..1 をそのまま 0..255 へ。踏んでいないときのゆらぎは小さな不感帯で落とす。
            // トリガーは 0 が離した状態なので、全軸 0.000 でも 0 になるだけで害は無い。
            byte Trigger(int i)
            {
                if (i < 0 || i >= ac) return 0;
                double v = Math.Clamp(axL[i], 0.0, 1.0);
                return v < 0.06 ? (byte)0 : (byte)Math.Round(v * 255.0);
            }

            short y1 = Stick(map.LY), y2 = Stick(map.RY);
            s.LX = Stick(map.LX);
            s.LY = map.InvertY ? (short)Math.Clamp(-(int)y1, -32768, 32767) : y1;
            s.RX = Stick(map.RX);
            s.RY = map.InvertY ? (short)Math.Clamp(-(int)y2, -32768, 32767) : y2;

            if (map.SharedTrigger >= 0 && map.SharedTrigger < ac)
            {
                // 左右のトリガーが1本の軸を共有する型（多くは中央 0.5 が両方離した状態で、
                // 片方を踏むと 0 側、もう片方を踏むと 1 側へ動く）。
                double v = Math.Clamp(axL[map.SharedTrigger], 0.0, 1.0);
                double d = (v - 0.5) * 2.0;                       // -1..1
                if (d > 0.06) s.RT = (byte)Math.Round(Math.Min(1.0, d) * 255.0);
                else if (d < -0.06) s.LT = (byte)Math.Round(Math.Min(1.0, -d) * 255.0);
            }
            else
            {
                s.LT = Trigger(map.LT);
                s.RT = Trigger(map.RT);
            }

            // 軸から取れた値と、ボタンから来た値の、大きいほうを採る。
            // 軸に割り当てが無い機体（トリガーがボタンの機体）でも、これで効く。
            if (btnLT > s.LT) s.LT = btnLT;
            if (btnRT > s.RT) s.RT = btnRT;
            return true;
        }
        catch { return false; }
    }

    /// <summary>生の軸とボタンの値を読む。どの軸がどの操作に当たるかを、人が見て決めるために使う。</summary>
    public static PadRawReading? ReadRaw(RawGameController c)
    {
        try
        {
            int bc = c.ButtonCount, sc = c.SwitchCount, ac = c.AxisCount;
            var btn = new bool[bc];
            var sw = new GameControllerSwitchPosition[sc];
            var ax = new double[ac];
            // 時刻印は捨てずに持ち帰る。0 なら中身の無い読み取りだと、見る人に分かるようにするため。
            ulong ts = c.GetCurrentReading(btn, sw, ax);
            string switches = sc > 0 ? string.Join(",", sw.Select(p => p.ToString())) : "-";
            return new PadRawReading(c.NonRoamableId, SafeName(c), bc, sc, ac, ax, btn, switches, ts);
        }
        catch { return null; }
    }
}
