using System;
using System.Collections.Generic;
using System.Linq;

namespace CntlLevelConnection;

/// <summary>
/// 機体ごとの割り当ての設定。プロファイル（JSON）の pads に並べる。
///
/// 2026/8/6 の実測で、手持ちの4台とも既定の割り当てが合わないことが分かった。
/// 軸の並びも、十字キーがボタンかハットスイッチかも、機種ごとに違う。
/// 毎回 MCP から入れ直すのは現実的でないので、プロファイルに書けるようにする。
///
/// 見分けは製造者と製品の識別子（VID と PID）で行う。表示名は一意でないことがあり
/// （たとえば「XBOX 360 Controller For Windows」は複数の機体が名乗る）、
/// Windows.Gaming.Input の識別子は処理ごとに変わるので、どちらも鍵に向かない。
/// </summary>
public sealed class PadProfile
{
    /// <summary>人が読むための呼び名。動作には影響しない。</summary>
    public string? Label { get; set; }

    /// <summary>製造者の識別子。null なら製造者では絞らない。</summary>
    public int? Vid { get; set; }

    /// <summary>製品の識別子。null なら製品では絞らない。</summary>
    public int? Pid { get; set; }

    /// <summary>表示名の一部。VID と PID が分からないときの補助。null なら名前では絞らない。</summary>
    public string? NameContains { get; set; }

    /// <summary>軸の割り当て。null ならそのままにする。</summary>
    public PadAxisMap? Axes { get; set; }

    /// <summary>ボタンの割り当ての指定（"10=DUp,11=DRight" の形）。null ならそのままにする。</summary>
    public string? Buttons { get; set; }

    /// <summary>この設定が、その素性の機体に当てはまるか。</summary>
    public bool Matches(ushort vid, ushort pid, string name)
    {
        if (Vid is int v && v != vid) return false;
        if (Pid is int p && p != pid) return false;
        if (!string.IsNullOrEmpty(NameContains) &&
            (name == null || name.IndexOf(NameContains, StringComparison.OrdinalIgnoreCase) < 0)) return false;
        // 何も条件が無いものは、取り違えのもとなので当てはめない
        return Vid != null || Pid != null || !string.IsNullOrEmpty(NameContains);
    }

    /// <summary>並びの中から、その機体に当てはまる最初のものを返す。無ければ null。</summary>
    public static PadProfile? FindFor(IEnumerable<PadProfile>? profiles, ushort vid, ushort pid, string name)
        => profiles?.FirstOrDefault(p => p.Matches(vid, pid, name));

    /// <summary>人が読める1行にする。</summary>
    public string Describe()
    {
        var who = Label ?? NameContains ?? "(名前なし)";
        var ids = (Vid is int v ? $"vid=0x{v:X4} " : "") + (Pid is int p ? $"pid=0x{p:X4}" : "");
        var what = (Axes != null ? $" axes[{Axes.Describe()}]" : "") +
                   (!string.IsNullOrEmpty(Buttons) ? $" buttons[{Buttons}]" : "");
        return $"{who} {ids}{what}".Trim();
    }

    /// <summary>
    /// いまの割り当てを、プロファイルへ貼り付けられる JSON の断片にする。
    /// 実測で決めた割り当てを、次の起動でも使えるようにするために出す。
    /// </summary>
    public static string ToJsonSnippet(string label, ushort vid, ushort pid, PadAxisMap axes, PadButtonMap buttons)
    {
        var b = buttons.Describe();
        var buttonsLine = b == "既定のまま" ? "" :
            $",\n      \"buttons\": \"{string.Join(",", b.Split(' ').Select(x => x.Replace("b", "")))}\"";
        return "{\n" +
               $"  \"pads\": [\n" +
               $"    {{\n" +
               $"      \"label\": \"{label}\",\n" +
               $"      \"vid\": {vid},\n" +
               $"      \"pid\": {pid},\n" +
               $"      \"axes\": {{ \"lx\": {axes.LX}, \"ly\": {axes.LY}, \"rx\": {axes.RX}, \"ry\": {axes.RY}, " +
               $"\"lt\": {axes.LT}, \"rt\": {axes.RT}, \"invertY\": {(axes.InvertY ? "true" : "false")} }}" +
               buttonsLine + "\n" +
               $"    }}\n" +
               $"  ]\n" +
               "}";
    }
}
