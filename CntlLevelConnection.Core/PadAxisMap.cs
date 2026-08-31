using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace CntlLevelConnection;

/// <summary>
/// 物理パッドの生の軸を、コントローラのどの出力に割り当てるかの設定。
/// ゲームパッドは機種ごとに軸の数も並びも違うので、固定の割り当てでは全部に対応できない。
/// 既定はよくある並び（軸0と軸1が左スティック、軸2と軸3が右スティック）で、
/// 合わないパッドは set_pad_axes で指定し直せる。
///
/// 値の範囲の違いに注意する。生の軸はどれも 0 から 1 で返るが、
/// スティックは中央が 0.5 の両側の範囲、トリガーは踏んでいないときが 0 の片側の範囲である。
/// </summary>
public sealed record PadAxisMap(
    [property: Description("左スティックの横。生の軸の番号。-1 で未割り当て")] int LX = 0,
    [property: Description("左スティックの縦")] int LY = 1,
    [property: Description("右スティックの横")] int RX = 2,
    [property: Description("右スティックの縦")] int RY = 3,
    [property: Description("左トリガー。-1 で未割り当て")] int LT = -1,
    [property: Description("右トリガー。-1 で未割り当て")] int RT = -1,
    [property: Description("縦の軸を反転するか（多くのパッドは下が大きい値なので既定は真）")] bool InvertY = true,
    [property: Description("左右のトリガーが1本の軸を共有する場合、その軸の番号。中央より上下でどちらかが踏まれたと見なす。-1 で使わない")] int SharedTrigger = -1)
{
    /// <summary>よくある並び。左スティックが軸0と軸1、右スティックが軸2と軸3、トリガーは未割り当て。</summary>
    public static readonly PadAxisMap Default = new();

    /// <summary>この割り当てが参照する最大の軸の番号（軸の数が足りているかの確認に使う）。</summary>
    public int MaxAxisIndex => new[] { LX, LY, RX, RY, LT, RT, SharedTrigger }.Max();

    public string Describe()
        => $"LX={Show(LX)} LY={Show(LY)} RX={Show(RX)} RY={Show(RY)} LT={Show(LT)} RT={Show(RT)}"
         + (SharedTrigger >= 0 ? $" sharedTrigger={SharedTrigger}" : "")
         + $" invertY={InvertY}";

    private static string Show(int i) => i < 0 ? "-" : i.ToString();
}

/// <summary>生の軸とボタンの値。どの軸がどの操作に当たるかを、人が見て決めるために使う。</summary>
public sealed record PadRawReading(
    string Id,
    string Name,
    int ButtonCount,
    int SwitchCount,
    int AxisCount,
    double[] Axes,
    bool[] Buttons,
    string Switches,
    ulong Timestamp = 0)
{
    /// <summary>
    /// この読み取りが本物かどうか。
    /// 時刻印が 0 のときは、その機体から報告をまだ一度も受け取っていないという意味であり、
    /// 軸もボタンも中身の無い既定値である。値がたまたま 0 だったのとは区別しなければならない。
    /// 2026/8/6 に、これを取り違えて仮想コントローラを暴走させていたことが分かった（Issue #7）。
    /// </summary>
    public bool IsValid => Timestamp != 0;

    /// <summary>人が読みやすい1行の形にする。軸は番号と値、押されているボタンは番号で示す。</summary>
    public string Describe()
    {
        var ax = string.Join(" ", Axes.Select((v, i) => $"a{i}={v:F3}"));
        var bt = string.Join(",", Buttons.Select((v, i) => (v, i)).Where(x => x.v).Select(x => $"b{x.i}"));
        var head = $"{Name} (axes={AxisCount} buttons={ButtonCount} switches={SwitchCount})";
        if (!IsValid)
            head += "\n  ** この機体からはまだ一度も報告が届いていません（時刻印が 0）。"
                  + "下の値は中身がありません。読み取りは無効として捨てています **";
        return head + "\n"
             + $"  {ax}\n"
             + $"  pressed: {(string.IsNullOrEmpty(bt) ? "-" : bt)}   switch: {Switches}   ts={Timestamp}";
    }
}
