using System;

namespace CntlLevelConnection;

/// <summary>
/// 正規化したコントローラの状態。物理パッド・ソフト入力・出力で共通に使う。
/// ボタンのビットの並びは XInput に合わせてあるが、この型自体はどの環境にも依存しない。
/// </summary>
public struct PadState
{
    public ushort Buttons;   // ボタンのビット（XInput と同じ並び）
    public byte LT, RT;      // 0..255
    public short LX, LY, RX, RY;

    /// <summary>2つの入力源を合成する（ボタンはOR、トリガーは大きい方、軸は絶対値が大きい方）。</summary>
    public static PadState Merge(in PadState a, in PadState b) => new()
    {
        Buttons = (ushort)(a.Buttons | b.Buttons),
        LT = Math.Max(a.LT, b.LT),
        RT = Math.Max(a.RT, b.RT),
        LX = PickAxis(a.LX, b.LX),
        LY = PickAxis(a.LY, b.LY),
        RX = PickAxis(a.RX, b.RX),
        RY = PickAxis(a.RY, b.RY),
    };

    private static short PickAxis(short x, short y) => Math.Abs((int)x) >= Math.Abs((int)y) ? x : y;
}
