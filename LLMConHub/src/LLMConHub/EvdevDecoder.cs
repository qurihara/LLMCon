using System;
using System.Collections.Generic;
using System.Linq;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>Linux の入力の仕組み（evdev）で使う番号。include/uapi/linux/input-event-codes.h による。</summary>
public static class EvCodes
{
    public const ushort EV_SYN = 0x00, EV_KEY = 0x01, EV_ABS = 0x03;
    public const ushort SYN_REPORT = 0, SYN_DROPPED = 3;

    // ゲームパッドのボタン（位置で名前が決まっている。Documentation/input/gamepad.rst）
    public const int BTN_SOUTH = 0x130, BTN_EAST = 0x131, BTN_C = 0x132, BTN_NORTH = 0x133, BTN_WEST = 0x134, BTN_Z = 0x135;
    public const int BTN_TL = 0x136, BTN_TR = 0x137, BTN_TL2 = 0x138, BTN_TR2 = 0x139;
    public const int BTN_SELECT = 0x13a, BTN_START = 0x13b, BTN_MODE = 0x13c, BTN_THUMBL = 0x13d, BTN_THUMBR = 0x13e;
    public const int BTN_DPAD_UP = 0x220, BTN_DPAD_DOWN = 0x221, BTN_DPAD_LEFT = 0x222, BTN_DPAD_RIGHT = 0x223;

    // ジョイスティックのボタン（番号だけで、位置の意味は無い）
    public const int BTN_JOYSTICK = 0x120;          // BTN_TRIGGER。ここから 0x12f までの16個
    public const int BTN_TRIGGER_HAPPY = 0x2c0;     // 17個目以降のボタン

    public const int ABS_X = 0x00, ABS_Y = 0x01, ABS_Z = 0x02, ABS_RX = 0x03, ABS_RY = 0x04, ABS_RZ = 0x05;
    public const int ABS_HAT0X = 0x10, ABS_HAT0Y = 0x11;
    public const int ABS_CNT = 0x40;
    public const int KEY_CNT = 0x300;
}

/// <summary>機器が持つボタンと軸の一覧（sysfs の capabilities から作る）。</summary>
public sealed record EvdevCaps(IReadOnlyList<int> Keys, IReadOnlyList<int> Abs)
{
    /// <summary>位置で名前の決まったゲームパッドか（BTN_SOUTH を持つか）。</summary>
    public bool IsGamepad => Keys.Contains(EvCodes.BTN_SOUTH);

    /// <summary>番号だけのボタンを持つジョイスティックか（BTN_JOYSTICK を持つか）。格闘ゲーム用の機体の多くはこちらで報告される。</summary>
    public bool IsJoystick => Keys.Contains(EvCodes.BTN_JOYSTICK);

    /// <summary>LLMCon の入力として扱えるか。</summary>
    public bool IsPad => IsGamepad || IsJoystick;

    /// <summary>
    /// sysfs の capabilities のファイルの中身（16進の語を空白で区切り、最上位の語が先頭）を、番号の一覧にする。
    /// 1語のビット数は、カーネルの long の大きさ（64ビットの機械では64）である。
    /// </summary>
    public static List<int> ParseBitmap(string text, int bitsPerWord = 64)
    {
        var words = text.Trim().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>();
        for (int w = 0; w < words.Length; w++)
        {
            // 最後の語が、番号 0 から始まる語である
            int wordIndex = words.Length - 1 - w;
            ulong v = Convert.ToUInt64(words[w], 16);
            for (int bit = 0; bit < bitsPerWord; bit++)
                if ((v & (1UL << bit)) != 0) result.Add(wordIndex * bitsPerWord + bit);
        }
        result.Sort();
        return result;
    }
}

/// <summary>軸の範囲（EVIOCGABS で得る input_absinfo の一部）。</summary>
public readonly record struct AbsRange(int Min, int Max)
{
    public static readonly AbsRange Unknown = new(-32768, 32767);
    public bool Valid => Max > Min;
}

/// <summary>
/// evdev の入力の事象の列を、LLMCon の PadState に変える。機器も OS の呼び出しも使わないので、試験で確かめられる。
///
/// evdev は、ボタンと軸の変化を1つずつ事象として送り、まとまりの終わりに SYN_REPORT を送る。
/// この変換器は SYN_REPORT を受けた時点で、それまでの変化をまとめて確定させる。途中の半端な状態
/// （横の軸だけ変わって縦の軸がまだ変わっていない状態）を出さないためである。
///
/// 割り当ては2通りある。
///   ゲームパッド（BTN_SOUTH を持つ機体）では、カーネルが位置で名前を決めているので、その名前で割り当てる。
///   下が A、右が B、左が X、上が Y である（Xbox のボタンの位置に合わせる）。
///   ジョイスティック（BTN_JOYSTICK から始まる番号だけのボタンの機体）では、ボタンの番号を
///   PadButtonMap で割り当てる。Windows の素の HID の経路と同じ既定（PS4 系の並び）を使うので、
///   同じ機体なら、どちらの OS でも同じ位置のボタンが同じ名前になるはずである。ただし、これは実機で確かめていない。
///
/// 外部の入力装置を読む経路は、エラーではなく「もっともらしい値」を返して静かに壊れることがある
/// （2026/8 の Windows.Gaming.Input の件）。そのため、一度も事象を受け取っていないあいだは、
/// HasData を偽にし、TryRead が値を返さないようにする（Issue #7 と同じ考え方）。
/// </summary>
public sealed class EvdevDecoder
{
    private readonly EvdevCaps _caps;
    private readonly AbsRange[] _range = new AbsRange[EvCodes.ABS_CNT];
    private readonly int[] _abs = new int[EvCodes.ABS_CNT];
    private readonly bool[] _key = new bool[EvCodes.KEY_CNT];
    private readonly int[] _axisOrder;     // ジョイスティックの軸の並び（ハットを除く ABS の番号の昇順）
    private readonly int[] _keyOrder;      // 生のボタンの並び（ReadRaw で見せる順）
    private readonly int[] _sdlKeyOrder;   // SDL の Linux の数え方のボタンの並び（BTN_JOYSTICK 以上を昇順、そのあとそれより小さいものを昇順）

    /// <summary>確定した状態。</summary>
    public PadState Current { get; private set; }

    /// <summary>一度でも状態を確定させたか。偽のあいだは、値を信用してはならない。</summary>
    public bool HasData { get; private set; }

    /// <summary>SYN_DROPPED を受けた（カーネルの側で事象を取りこぼした）ので、状態を読み直す必要がある。</summary>
    public bool NeedsResync { get; private set; }
    private bool _dropping;

    // 割り当てを変えたら、その場で状態を計算し直す。次の事象を待つと、古い割り当ての状態が残る
    // （2026-11-03、十字キーの軸を割り当て直したあとも、左スティックが左上の端のまま Steam に届いた）。
    private PadAxisMap _axisMap = PadAxisMap.Default;
    private PadButtonMap _buttonMap = PadButtonMap.Default;
    public PadAxisMap AxisMap { get => _axisMap; set { _axisMap = value; if (HasData) Current = Compute(); } }
    public PadButtonMap ButtonMap { get => _buttonMap; set { _buttonMap = value; if (HasData) Current = Compute(); } }

    /// <summary>
    /// 機体から SYN_REPORT を一度でも受け取ったか。安いパッドには、最初にボタンが押されるまで何も報告せず、
    /// そのあいだカーネルが軸の値を 0（範囲の端）のまま持つものがある（BUFFALO BGC-FC801）。
    /// 偽のあいだは、スティックと十字キーの軸を中央として扱う。
    /// </summary>
    private bool _seenReport;

    /// <summary>
    /// ゲームパッドの機体で、カーネルの名前の割り当てを上書きする。キーはボタンの番号（0x133 など）、値は LLMCon のボタンの名前。
    /// ドライバによって、ボタンの名前と位置の対応が違う場合に使う（xpad の X と Y の件など。実機で確かめる必要がある）。
    /// </summary>
    public Dictionary<int, string> GamepadOverrides { get; } = new();

    /// <summary>
    /// 位置で名前の決まったゲームパッドとして報告される機体を、番号だけのボタンの機体として読む（プロファイルの asJoystick）。
    /// ボタンの番号は、機体が持つボタンの並びの順（_keyOrder の位置）にする。軸は AxisMap で割り当てる。
    /// </summary>
    public bool ForceJoystick { get => _forceJoystick; set { _forceJoystick = value; if (HasData) Current = Compute(); } }

    /// <summary>SDL の対応表の割り当て。null でなければ、ほかの割り当て（ゲームパッドの名前、AxisMap、ButtonMap）より優先して使う。</summary>
    public SdlMapping? Sdl { get => _sdl; set { _sdl = value; if (HasData) Current = Compute(); } }
    private SdlMapping? _sdl;
    private bool _forceJoystick;

    public EvdevDecoder(EvdevCaps caps)
    {
        _caps = caps;
        for (int i = 0; i < _range.Length; i++) _range[i] = AbsRange.Unknown;
        _axisOrder = caps.Abs.Where(a => a < EvCodes.ABS_HAT0X || a > 0x17).Where(a => a < EvCodes.ABS_CNT).ToArray();
        _keyOrder = caps.Keys.Where(k => k < EvCodes.KEY_CNT).ToArray();
        _sdlKeyOrder = _keyOrder.Where(k => k >= EvCodes.BTN_JOYSTICK).Concat(_keyOrder.Where(k => k < EvCodes.BTN_JOYSTICK)).ToArray();
    }

    public EvdevCaps Caps => _caps;

    /// <summary>軸の範囲と、いまの値を与える（開いた直後に EVIOCGABS で読んだもの）。</summary>
    public void SetAbsInfo(int code, AbsRange range, int value)
    {
        if (code < 0 || code >= EvCodes.ABS_CNT) return;
        if (range.Valid) _range[code] = range;
        _abs[code] = value;
    }

    /// <summary>ボタンのいまの状態を与える（開いた直後に EVIOCGKEY で読んだもの）。</summary>
    public void SetKey(int code, bool down)
    {
        if (code >= 0 && code < EvCodes.KEY_CNT) _key[code] = down;
    }

    /// <summary>開いた直後の読み取りを済ませたあとに呼ぶ。この時点の値で状態を確定させる。</summary>
    public void CommitInitial()
    {
        Current = Compute();
        HasData = true;
        NeedsResync = false;
        _dropping = false;
    }

    /// <summary>事象を1つ与える。SYN_REPORT で状態を確定させたときに true を返す。</summary>
    public bool Feed(ushort type, ushort code, int value)
    {
        switch (type)
        {
            case EvCodes.EV_SYN:
                if (code == EvCodes.SYN_DROPPED)
                {
                    // 次の SYN_REPORT までの事象は捨て、その後で状態を読み直す（evdev の取り決め）
                    _dropping = true;
                    NeedsResync = true;
                    return false;
                }
                if (code == EvCodes.SYN_REPORT)
                {
                    if (_dropping) { _dropping = false; return false; }
                    _seenReport = true;
                    Current = Compute();
                    HasData = true;
                    return true;
                }
                return false;
            case EvCodes.EV_KEY:
                if (_dropping) return false;
                if (code < EvCodes.KEY_CNT) _key[code] = value != 0;   // 1 が押下、2 が押しっぱなしの繰り返し、0 が解放
                return false;
            case EvCodes.EV_ABS:
                if (_dropping) return false;
                if (code < EvCodes.ABS_CNT) _abs[code] = value;
                return false;
            default:
                return false;
        }
    }

    // ── 変換 ──

    private PadState Compute()
    {
        var s = new PadState();
        if (_sdl != null) { ComputeSdl(ref s, _sdl); return s; }
        if (_caps.IsGamepad && !_forceJoystick) ComputeGamepad(ref s);
        else ComputeJoystick(ref s);

        // ハット（十字キーを軸で報告する機体）は、どちらの種類でも十字キーにする
        if (_caps.Abs.Contains(EvCodes.ABS_HAT0X))
        {
            int hx = _abs[EvCodes.ABS_HAT0X];
            if (hx < 0) s.Buttons |= Bit("DLeft");
            if (hx > 0) s.Buttons |= Bit("DRight");
        }
        if (_caps.Abs.Contains(EvCodes.ABS_HAT0Y))
        {
            int hy = _abs[EvCodes.ABS_HAT0Y];
            if (hy < 0) s.Buttons |= Bit("DUp");
            if (hy > 0) s.Buttons |= Bit("DDown");
        }
        return s;
    }

    private static readonly (int code, string name)[] GamepadDefault =
    {
        (EvCodes.BTN_SOUTH, "A"), (EvCodes.BTN_EAST, "B"), (EvCodes.BTN_WEST, "X"), (EvCodes.BTN_NORTH, "Y"),
        (EvCodes.BTN_TL, "LB"), (EvCodes.BTN_TR, "RB"), (EvCodes.BTN_TL2, "LT"), (EvCodes.BTN_TR2, "RT"),
        (EvCodes.BTN_SELECT, "Back"), (EvCodes.BTN_START, "Start"), (EvCodes.BTN_MODE, "Guide"),
        (EvCodes.BTN_THUMBL, "LS"), (EvCodes.BTN_THUMBR, "RS"),
        (EvCodes.BTN_DPAD_UP, "DUp"), (EvCodes.BTN_DPAD_DOWN, "DDown"), (EvCodes.BTN_DPAD_LEFT, "DLeft"), (EvCodes.BTN_DPAD_RIGHT, "DRight"),
    };

    private void ComputeGamepad(ref PadState s)
    {
        foreach (var (code, defName) in GamepadDefault)
        {
            if (!_key[code]) continue;
            var name = GamepadOverrides.TryGetValue(code, out var o) ? o : defName;
            ApplyButton(ref s, name);
        }
        foreach (var (code, name) in GamepadOverrides)
            if (_key[code] && !GamepadDefault.Any(d => d.code == code)) ApplyButton(ref s, name);

        // 軸。Linux の縦の軸は下が正なので、上が正の XInput に合わせて反転する。
        s.LX = Stick(EvCodes.ABS_X, invert: false);
        s.LY = Stick(EvCodes.ABS_Y, invert: true);
        s.RX = Stick(EvCodes.ABS_RX, invert: false);
        s.RY = Stick(EvCodes.ABS_RY, invert: true);
        // アナログのトリガー（ABS_Z と ABS_RZ）があれば、ボタンとしてのトリガーより優先して値を使う
        if (_caps.Abs.Contains(EvCodes.ABS_Z)) s.LT = Math.Max(s.LT, Trigger(EvCodes.ABS_Z));
        if (_caps.Abs.Contains(EvCodes.ABS_RZ)) s.RT = Math.Max(s.RT, Trigger(EvCodes.ABS_RZ));
    }

    private void ComputeJoystick(ref PadState s)
    {
        foreach (var code in _keyOrder)
        {
            if (!_key[code]) continue;
            int idx = _forceJoystick ? Array.IndexOf(_keyOrder, code) : RawButtonIndex(code);
            if (idx < 0) continue;
            var name = ButtonMap.TargetOf(idx);
            if (name != null) ApplyButton(ref s, name);
        }
        var m = AxisMap;
        s.LX = AxisAsStick(m.LX, invert: false);
        s.LY = AxisAsStick(m.LY, invert: m.InvertY);
        s.RX = AxisAsStick(m.RX, invert: false);
        s.RY = AxisAsStick(m.RY, invert: m.InvertY);
        if (m.LT >= 0) s.LT = Math.Max(s.LT, AxisAsTrigger(m.LT));
        if (m.RT >= 0) s.RT = Math.Max(s.RT, AxisAsTrigger(m.RT));
        if (m.SharedTrigger >= 0)
        {
            double v = Unit(AxisCode(m.SharedTrigger));
            if (v > 0.55) s.RT = Math.Max(s.RT, (byte)Math.Round((v - 0.5) * 2 * 255));
            if (v < 0.45) s.LT = Math.Max(s.LT, (byte)Math.Round((0.5 - v) * 2 * 255));
        }
        // 十字キーを2本の軸で報告するパッド（スーパーファミコン型の安いパッドに多い）。
        // 軸の両端の4分の1を押したと見なす。縦は Linux の取り決めどおり小さい側が上である。
        if (_seenReport && m.DpadX >= 0 && AxisCode(m.DpadX) >= 0)
        {
            double u = Unit(AxisCode(m.DpadX));
            if (u < 0.25) s.Buttons |= Bit("DLeft");
            if (u > 0.75) s.Buttons |= Bit("DRight");
        }
        if (_seenReport && m.DpadY >= 0 && AxisCode(m.DpadY) >= 0)
        {
            double u = Unit(AxisCode(m.DpadY));
            if (u < 0.25) s.Buttons |= Bit("DUp");
            if (u > 0.75) s.Buttons |= Bit("DDown");
        }
    }

    // ── SDL の対応表による変換 ──

    private void ComputeSdl(ref PadState s, SdlMapping m)
    {
        foreach (var (target, src) in m.Targets)
        {
            switch (target)
            {
                case "LX": s.LX = SdlStick(src, false); break;
                case "LY": s.LY = SdlStick(src, true); break;
                case "RX": s.RX = SdlStick(src, false); break;
                case "RY": s.RY = SdlStick(src, true); break;
                case "LT": s.LT = Math.Max(s.LT, SdlTrigger(src)); break;
                case "RT": s.RT = Math.Max(s.RT, SdlTrigger(src)); break;
                default: if (SdlPressed(src)) s.Buttons |= Bit(target); break;
            }
        }
    }

    /// <summary>SDL の軸の値を -1..1 にする（半分の軸と反転を含む）。スティックは最初の報告まで中央とみなす。</summary>
    private double SdlAxis(SdlMapping.Source src)
    {
        int code = AxisCode(src.Index);
        if (code < 0 || !_caps.Abs.Contains(code)) return 0;
        double v = Unit(code) * 2.0 - 1.0;
        if (src.Invert) v = -v;
        return v;
    }

    private bool SdlPressed(SdlMapping.Source src)
    {
        switch (src.Kind)
        {
            case 'b':
                return src.Index >= 0 && src.Index < _sdlKeyOrder.Length && _key[_sdlKeyOrder[src.Index]];
            case 'h':
            {
                int hx = EvCodes.ABS_HAT0X + 2 * src.Index, hy = hx + 1;
                if (hy >= EvCodes.ABS_CNT) return false;
                int x = _abs[hx], y = _abs[hy];
                return ((src.HatMask & 1) != 0 && y < 0) || ((src.HatMask & 2) != 0 && x > 0)
                    || ((src.HatMask & 4) != 0 && y > 0) || ((src.HatMask & 8) != 0 && x < 0);
            }
            case 'a':
            {
                if (!_seenReport) return false;
                double v = SdlAxis(src);
                return src.Half < 0 ? v < -0.5 : v > 0.5;
            }
        }
        return false;
    }

    private short SdlStick(SdlMapping.Source src, bool invertY)
    {
        double v;
        if (src.Kind == 'a')
        {
            if (!_seenReport) return 0;
            v = SdlAxis(src);
            if (src.Half > 0) v = Math.Max(0, v); else if (src.Half < 0) v = Math.Min(0, v);
        }
        else v = SdlPressed(src) ? 1 : 0;
        if (invertY) v = -v;   // SDL も Linux も下が正。LLMCon（XInput）は上が正
        return (short)Math.Clamp((int)Math.Round(v * 32767.0), -32768, 32767);
    }

    private byte SdlTrigger(SdlMapping.Source src)
    {
        if (src.Kind != 'a') return SdlPressed(src) ? (byte)255 : (byte)0;
        double v = SdlAxis(src);                 // -1..1
        double u = src.Half != 0 ? Math.Max(0, src.Half * v) : (v + 1) / 2;   // 0..1
        return (byte)Math.Clamp((int)Math.Round(u * 255.0), 0, 255);
    }

    /// <summary>ジョイスティックのボタンの番号を、Windows の素の HID と同じ通し番号にする。BTN_JOYSTICK を 0 とする。</summary>
    public static int RawButtonIndex(int code)
    {
        if (code >= EvCodes.BTN_JOYSTICK && code < EvCodes.BTN_JOYSTICK + 16) return code - EvCodes.BTN_JOYSTICK;
        if (code >= EvCodes.BTN_TRIGGER_HAPPY && code < EvCodes.BTN_TRIGGER_HAPPY + 40) return 16 + (code - EvCodes.BTN_TRIGGER_HAPPY);
        return -1;
    }

    private static void ApplyButton(ref PadState s, string name)
    {
        if (string.Equals(name, "LT", StringComparison.OrdinalIgnoreCase)) { s.LT = 255; return; }
        if (string.Equals(name, "RT", StringComparison.OrdinalIgnoreCase)) { s.RT = 255; return; }
        s.Buttons |= Bit(name);
    }

    private static ushort Bit(string name) => ControllerEngine.MaskOf(name);

    private int AxisCode(int index) => index >= 0 && index < _axisOrder.Length ? _axisOrder[index] : -1;

    private short AxisAsStick(int index, bool invert)
    {
        int code = AxisCode(index);
        return code < 0 ? (short)0 : Stick(code, invert);
    }

    private byte AxisAsTrigger(int index)
    {
        int code = AxisCode(index);
        return code < 0 ? (byte)0 : Trigger(code);
    }

    /// <summary>軸の値を 0 から 1 にする。</summary>
    private double Unit(int code)
    {
        if (code < 0) return 0.5;
        var r = _range[code];
        double u = (_abs[code] - (double)r.Min) / (r.Max - (double)r.Min);
        return Math.Clamp(u, 0.0, 1.0);
    }

    private short Stick(int code, bool invert)
    {
        if (!_caps.Abs.Contains(code)) return 0;
        if (!_seenReport) return 0;
        double u = Unit(code);                 // 0..1
        double v = u * 2.0 - 1.0;              // -1..1
        if (invert) v = -v;
        int iv = (int)Math.Round(v * 32767.0);
        return (short)Math.Clamp(iv, -32768, 32767);
    }

    private byte Trigger(int code)
    {
        if (!_caps.Abs.Contains(code)) return 0;
        return (byte)Math.Round(Unit(code) * 255.0);
    }

    // ── 生の値（どの軸とボタンが何に当たるかを、人が見て決めるため）──

    public PadRawReading Raw(string id, string name, ulong timestampMicros)
    {
        var axes = _axisOrder.Select(Unit).ToArray();
        var buttons = _keyOrder.Select(k => _key[k]).ToArray();
        var hats = $"hat0x={_abs[EvCodes.ABS_HAT0X]} hat0y={_abs[EvCodes.ABS_HAT0Y]}";
        var pressed = string.Join(" ", _keyOrder.Where(k => _key[k]).Select(k => $"0x{k:x3}"));
        return new PadRawReading(id, name, buttons.Length, 1, axes.Length, axes, buttons,
            $"{hats} pressed=[{pressed}] axes=[{string.Join(" ", _axisOrder.Select(a => $"0x{a:x2}"))}]",
            HasData ? Math.Max(1UL, timestampMicros) : 0);
    }
}
