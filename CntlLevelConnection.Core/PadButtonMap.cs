using System;
using System.Collections.Generic;
using System.Linq;

namespace CntlLevelConnection;

/// <summary>
/// 物理パッドの生のボタンを、コントローラのどの出力に割り当てるかの設定。
///
/// 軸と同じく、ボタンの並びも機種ごとに違う。とくに困るのは、機種によってトリガーが
/// アナログの軸ではなくボタンとして報告されることである。2026/8/6 に ROG Ally の内蔵
/// コントローラで実際にそうなっていた（この機体は、いまは XInput から読んでいる）。
///
/// 割り当て先には LT と RT も選べる。ボタンは押した・押さないの2値なので、
/// 割り当てるとトリガーの出力は 0 か 255 になる。アナログの値は得られない。
/// </summary>
public sealed class PadButtonMap
{
    /// <summary>生のボタンの番号から、割り当て先の名前へ。名前は ControllerEngine のボタン名か LT か RT。</summary>
    private readonly Dictionary<int, string> _map;

    /// <summary>割り当て先として使える名前。ボタンに加えて、トリガーの2つを受け付ける。</summary>
    public static readonly string[] Targets =
    {
        "A", "B", "X", "Y", "LB", "RB", "LS", "RS", "Start", "Back", "Guide",
        "DUp", "DDown", "DLeft", "DRight", "LT", "RT",
    };

    /// <summary>
    /// 素の HID の機体の標準的な並び。
    ///
    /// この割り当ては、いまや素の HID の機体にしか使われない。XInput の機体は Issue #15 で
    /// XInput から直接読むようになり、そちらは軸もボタンも名前が付いているためである。
    /// したがって、ここで合わせるべき相手は PS4 系の HID の並びである。
    ///
    /// 2026/8/6 の実測（Issue #9）で、PunkWorkshop（Razer の市販品）と導電性コントローラ
    /// （GP2040-CE の Switch のモード）の並びが完全に一致した。上段が左から b0 b3 b5 b4、
    /// 下段が左から b1 b2 b7 b6 である。これは PS4 の標準の並び（□ ✕ ○ △ が b0 b1 b2 b3、
    /// L1 R1 L2 R2 が b4 b5 b6 b7）と同じで、格闘ゲーム用の機体はこれを踏襲している。
    ///
    /// 以前の割り当ては b0 を A、b6 を Back、b7 を Start にしていた。これは Xbox 系の並びで、
    /// 素の HID の機体には当たらない。実機では、上段の左端が X ではなく A になり、下段の
    /// 右2つを押すと Start と Back が出てしまう（対戦の最中にメニューが開く）。
    ///
    /// 名前の対応は、Hitbox を XInput で読んだときの並び（上段が X Y RB LB、下段が A B RT LT）に
    /// 合わせてある。どの経路で読んでも、同じ位置のボタンが同じ名前になる。
    /// </summary>
    public static readonly PadButtonMap Default = new(new Dictionary<int, string>
    {
        [0] = "X", [1] = "A", [2] = "B", [3] = "Y",
        [4] = "LB", [5] = "RB",
        [6] = "LT", [7] = "RT",
        [8] = "Back", [9] = "Start",
        [10] = "LS", [11] = "RS",
        [12] = "Guide",
    });

    private PadButtonMap(Dictionary<int, string> map) => _map = map;

    /// <summary>生のボタンの番号に対する割り当て先。割り当てが無ければ null。</summary>
    public string? TargetOf(int rawIndex)
        => _map.TryGetValue(rawIndex, out var t) ? t : null;

    /// <summary>
    /// 既定の割り当てに、指定された分だけ上書きを重ねた新しい割り当てを作る。
    /// 指定は "8=LT,9=RT" のような形で、割り当てを外すときは "8=-" と書く。
    /// 全部を書き並べる必要は無く、変えたいものだけ書けばよい。
    /// </summary>
    public static PadButtonMap FromSpec(string? spec, out string error)
    {
        error = "";
        var map = new Dictionary<int, string>(Default._map);
        if (string.IsNullOrWhiteSpace(spec)) return new PadButtonMap(map);

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                error = $"\"{part.Trim()}\" の形が違います。\"8=LT\" のように書いてください";
                return new PadButtonMap(map);
            }
            if (!int.TryParse(kv[0].Trim(), out var idx) || idx < 0)
            {
                error = $"\"{kv[0].Trim()}\" は生のボタンの番号として読めません";
                return new PadButtonMap(map);
            }
            var target = kv[1].Trim();
            if (target == "-" || target.Length == 0) { map.Remove(idx); continue; }

            var known = Targets.FirstOrDefault(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                error = $"\"{target}\" という割り当て先はありません。使えるのは {string.Join(" ", Targets)} です";
                return new PadButtonMap(map);
            }
            map[idx] = known;
        }
        return new PadButtonMap(map);
    }

    /// <summary>既定と違うところだけを、人が読める形で示す。</summary>
    public string Describe()
    {
        var changes = new List<string>();
        foreach (var idx in _map.Keys.Union(Default._map.Keys).OrderBy(i => i))
        {
            var mine = TargetOf(idx);
            var def = Default.TargetOf(idx);
            if (mine != def) changes.Add($"b{idx}={mine ?? "-"}");
        }
        return changes.Count == 0 ? "既定のまま" : string.Join(" ", changes);
    }

    /// <summary>いまの割り当てを全部並べる。</summary>
    public string DescribeAll()
        => string.Join(" ", _map.OrderBy(kv => kv.Key).Select(kv => $"b{kv.Key}={kv.Value}"));
}
