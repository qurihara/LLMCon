using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CntlLevelConnection;

/// <summary>
/// 物理のパッドの割り当てを、プレイヤにボタンの「位置」を順に押してもらって決める手順（MCP の pad_setup_*）。
///
/// なぜ要るか：SDL の対応表やプロファイルに載っていない機種は、ボタンの番号と位置の対応が分からない。
/// 実践向けのアーケードコントローラは天板に A や B の印字が無いので、「A を押して」とは頼めない。
/// そこで「上の段のいちばん左」「右手の4つのボタンのうち、いちばん下」のように位置で頼み、
/// 押された番号（または動いた軸）を記録して、LLMCon の名前（Xbox の位置）に割り当てる。
///
/// LLM は、Prompt の文をそのままプレイヤに伝え、pad_setup_next で押されるのを待つ。
/// 結果は PadProfile（asJoystick を真にし、ボタンを並びの順の番号で読む）にして当てる。
/// </summary>
public sealed class PadSetup
{
    /// <summary>1つの手順。Target は LLMCon の名前（A、DUp、LT、LX など）。Prompt はプレイヤに伝える文。</summary>
    public sealed record Step(string Target, string Prompt);

    /// <summary>1つの手順で記録したもの。Kind は button、axis、hat、skip のどれか。</summary>
    public sealed record Captured(string Target, string Kind, int Index, int Sign);

    public string Kind { get; }
    public IReadOnlyList<Step> Steps { get; }
    public List<Captured> Results { get; } = new();
    public int Position => Results.Count;
    public bool Done => Position >= Steps.Count;
    public Step? Current => Done ? null : Steps[Position];

    private PadSetup(string kind, IReadOnlyList<Step> steps) { Kind = kind; Steps = steps; }

    public static PadSetup? Create(string kind)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind is "arcade" or "stick" or "アケコン") return new PadSetup("arcade", ArcadeSteps);
        if (kind is "pad" or "gamepad" or "パッド") return new PadSetup("pad", PadSteps);
        return null;
    }

    // アーケードコントローラ：8つのボタンが2段に4つずつ並ぶ、格闘ゲームの標準の並び。
    // 上の段は左から X、Y、RB、LB、下の段は左から A、B、RT、LT（Switch では Y、X、R、L と B、A、ZR、ZL）。
    private static readonly Step[] ArcadeSteps =
    {
        new("DUp", "レバーを上に倒してください（レバーの無い機体は、上に当たるボタン）。"),
        new("DDown", "レバーを下に倒してください。"),
        new("DLeft", "レバーを左に倒してください。"),
        new("DRight", "レバーを右に倒してください。"),
        new("X", "8つのボタンのうち、上の段のいちばん左を押してください。"),
        new("Y", "上の段の、左から2つ目を押してください。"),
        new("RB", "上の段の、左から3つ目を押してください。"),
        new("LB", "上の段の、左から4つ目（いちばん右）を押してください。"),
        new("A", "下の段のいちばん左を押してください。"),
        new("B", "下の段の、左から2つ目を押してください。"),
        new("RT", "下の段の、左から3つ目を押してください。"),
        new("LT", "下の段の、左から4つ目（いちばん右）を押してください。"),
        new("Start", "ゲームを止める（ポーズやメニューを開く）のに使うボタンを押してください。無ければ待ってください。"),
        new("Back", "セレクトや「戻る」に使うボタンを押してください。無ければ待ってください。"),
    };

    // 両手で持つパッド：右手の4つのボタンは、下、右、左、上の位置で頼む。
    private static readonly Step[] PadSteps =
    {
        new("DUp", "左手の十字キーの上を押してください。"),
        new("DDown", "十字キーの下を押してください。"),
        new("DLeft", "十字キーの左を押してください。"),
        new("DRight", "十字キーの右を押してください。"),
        new("A", "右手の4つのボタンのうち、いちばん下を押してください。"),
        new("B", "右手の4つのボタンのうち、いちばん右を押してください。"),
        new("X", "右手の4つのボタンのうち、いちばん左を押してください。"),
        new("Y", "右手の4つのボタンのうち、いちばん上を押してください。"),
        new("LB", "左の肩の、手前のボタンを押してください。"),
        new("RB", "右の肩の、手前のボタンを押してください。"),
        new("LT", "左の肩の、奥のボタン（トリガー）を押してください。無ければ待ってください。"),
        new("RT", "右の肩の、奥のボタン（トリガー）を押してください。無ければ待ってください。"),
        new("Start", "スタートやメニューを開くボタンを押してください。"),
        new("Back", "セレクトや「戻る」に使うボタンを押してください。"),
        new("LS", "左のスティックを押し込んでください。無ければ待ってください。"),
        new("RS", "右のスティックを押し込んでください。無ければ待ってください。"),
        new("LX", "左のスティックを右に倒してください。無ければ待ってください。"),
        new("LY", "左のスティックを下に倒してください。無ければ待ってください。"),
        new("RX", "右のスティックを右に倒してください。無ければ待ってください。"),
        new("RY", "右のスティックを下に倒してください。無ければ待ってください。"),
    };

    /// <summary>
    /// 押されるのを待ち、押されたものを記録して返す。押されなければ「飛ばした」と記録する。
    /// 待つ前の値（押していない状態）と比べ、新しく押されたボタン、大きく動いた軸、変わったハットを探す。
    /// 記録したあとは、離されるのを最大2秒待つ（次の手順に押したままの状態を持ち込まないため）。
    /// </summary>
    public Captured WaitAndCapture(Func<PadRawReading?> read, int timeoutMs)
    {
        var step = Current ?? throw new InvalidOperationException("手順はもう終わっています。");
        var base0 = read();
        Captured? got = null;
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && got == null)
        {
            Thread.Sleep(15);
            var r = read();
            if (r == null || base0 == null || !r.IsValid) { base0 ??= r; continue; }
            for (int i = 0; i < r.Buttons.Length && got == null; i++)
                if (r.Buttons[i] && !(i < base0.Buttons.Length && base0.Buttons[i]))
                    got = new Captured(step.Target, "button", i, 0);
            for (int i = 0; i < r.Axes.Length && got == null; i++)
            {
                double d = r.Axes[i] - (i < base0.Axes.Length ? base0.Axes[i] : 0.5);
                if (Math.Abs(d) > 0.35) got = new Captured(step.Target, "axis", i, Math.Sign(d));
            }
            if (got == null && HatText(r) != HatText(base0)) got = new Captured(step.Target, "hat", 0, 0);
        }
        got ??= new Captured(step.Target, "skip", -1, 0);
        Results.Add(got);

        // 離されるのを待つ
        var relEnd = Environment.TickCount64 + 2000;
        while (got.Kind != "skip" && Environment.TickCount64 < relEnd)
        {
            Thread.Sleep(15);
            var r = read();
            if (r == null || base0 == null) break;
            bool held = got.Kind switch
            {
                "button" => got.Index < r.Buttons.Length && r.Buttons[got.Index],
                "axis" => Math.Abs(r.Axes[got.Index] - (got.Index < base0.Axes.Length ? base0.Axes[got.Index] : 0.5)) > 0.2,
                "hat" => HatText(r) != HatText(base0),
                _ => false,
            };
            if (!held) break;
        }
        return got;
    }

    private static string HatText(PadRawReading r)
    {
        // Switches には "hat0x=0 hat0y=0 pressed=[...]" のような文字列が入る。ハットの部分だけを比べる
        var s = r.Switches ?? "";
        int p = s.IndexOf("pressed", StringComparison.Ordinal);
        return p >= 0 ? s[..p] : s;
    }

    public static string DescribeCaptured(Captured c) => c.Kind switch
    {
        "button" => $"{c.Target} ← ボタン b{c.Index}",
        "axis" => $"{c.Target} ← 軸 a{c.Index}（{(c.Sign > 0 ? "大きい側" : "小さい側")}）",
        "hat" => $"{c.Target} ← ハット（十字キーとしてそのまま読む）",
        _ => $"{c.Target} ← 押されなかったので飛ばした",
    };

    /// <summary>記録から PadProfile を作る。ボタンは並びの順の番号で読む（asJoystick）。</summary>
    public PadProfile BuildProfile(PadHardware? hw, out List<string> notes)
    {
        notes = new List<string>();
        var buttons = new List<string>();
        int lx = -1, ly = -1, rx = -1, ry = -1, lt = -1, rt = -1, dpadX = -1, dpadY = -1;
        var used = new HashSet<int>();
        foreach (var c in Results)
        {
            if (c.Kind == "button")
            {
                if (!used.Add(c.Index)) notes.Add($"ボタン b{c.Index} を2つの手順で押したので、あとの {c.Target} を優先した。");
                buttons.Add($"{c.Index}={c.Target}");
            }
            else if (c.Kind == "axis")
            {
                switch (c.Target)
                {
                    case "DUp" or "DDown": dpadY = c.Index; if ((c.Target == "DUp") != (c.Sign < 0)) notes.Add("十字キーの縦の軸が、上で大きくなる向きでした。上下が逆になります。"); break;
                    case "DLeft" or "DRight": dpadX = c.Index; if ((c.Target == "DLeft") != (c.Sign < 0)) notes.Add("十字キーの横の軸が、左で大きくなる向きでした。左右が逆になります。"); break;
                    case "LX": lx = c.Index; break;
                    case "LY": ly = c.Index; break;
                    case "RX": rx = c.Index; break;
                    case "RY": ry = c.Index; break;
                    case "LT": lt = c.Index; break;
                    case "RT": rt = c.Index; break;
                    default: notes.Add($"{c.Target} は軸として動いたが、軸には割り当てられない名前なので飛ばした。"); break;
                }
            }
        }
        // 割り当てなかった番号は、既定の割り当て（PS4 の並び）に任せず、空けておく
        var assigned = new HashSet<int>(Results.Where(r => r.Kind == "button").Select(r => r.Index));
        for (int i = 0; i < 40; i++)
            if (!assigned.Contains(i) && PadButtonMap.Default.TargetOf(i) != null) buttons.Add($"{i}=-");
        return new PadProfile
        {
            Label = $"pad_setup で決めた割り当て（{(Kind == "arcade" ? "アーケードコントローラ" : "パッド")}、{hw?.Name ?? "名前なし"}）",
            Vid = hw?.Vid,
            Pid = hw?.Pid,
            AsJoystick = true,
            Axes = new PadAxisMap(lx, ly, rx, ry, lt, rt, true, -1, dpadX, dpadY),
            Buttons = string.Join(",", buttons),
        };
    }
}
