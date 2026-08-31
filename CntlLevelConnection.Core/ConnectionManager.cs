using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CntlLevelConnection;

/// <summary>
/// コントローラをまたいだ接続の「事象」。ソース側の人間入力（物理とソフトを合成したもの）から検出する。
/// Type が "press" または "release" のときは Button を1つ見る。
/// Type が "sequence" のときは Buttons を順に押す並び（コマンド）を見る。
/// </summary>
public sealed record ConnEvent(
    [property: Description("press | release | sequence")] string Type,
    [property: Description("button for press/release: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight")] string? Button = null,
    [property: Description("ordered button presses for a sequence, e.g. [\"DDown\",\"DRight\",\"A\"]")] string[]? Buttons = null,
    [property: Description("max gap between consecutive sequence inputs, in ms (default 500)")] double? WindowMs = null);

/// <summary>作用を加える相手の LLMCon。同一ネットワーク前提なので host と port を直接渡す。</summary>
public sealed record ConnTarget(
    [property: Description("target LLMCon host, e.g. 127.0.0.1")] string Host,
    [property: Description("target LLMCon MCP port, e.g. 8778")] int Port);

/// <summary>
/// 事象が起きたときに相手へ加える作用。改変と注入の両方に対応する。
/// Kind が "mapping" のときは Rules を相手の人間入力へ DurationSec 秒だけ重ねる（既存マッピングは壊さない）。
/// Kind が "inject_tap" のときは Buttons を Frames フレームだけ相手にタップさせる。
/// Kind が "inject_macro" のときは相手に定義済みの Macro を実行させる。
/// </summary>
public sealed record ConnAction(
    [property: Description("mapping | inject_tap | inject_macro")] string Kind,
    [property: Description("for mapping: modification rules applied to the target's human input (same shape as set_mapping)")] MappingRule[]? Rules = null,
    [property: Description("for mapping: how long the rules stay active on the target, in seconds (default 1)")] double? DurationSec = null,
    [property: Description("for inject_tap: buttons to tap on the target")] string[]? Buttons = null,
    [property: Description("for inject_tap: frames to hold at the target's fps (default 3)")] int? Frames = null,
    [property: Description("for inject_macro: name of a macro already defined on the target")] string? Macro = null);

/// <summary>受け口 /connect/apply が受け取るワイヤ形式。ConnAction をそのまま運ぶ。From は送り手の識別。</summary>
public sealed record ConnApply(
    string Kind,
    MappingRule[]? Rules = null,
    double? DurationSec = null,
    string[]? Buttons = null,
    int? Frames = null,
    string? Macro = null,
    string? From = null);

/// <summary>登録された接続1件。実行時の状態（並びの進み・直近の発火時刻）も持つ。</summary>
public sealed class Connection
{
    public required string Id { get; init; }
    public required ConnEvent Event { get; init; }
    public required ConnTarget Target { get; init; }
    public required ConnAction Action { get; init; }
    public double? CooldownMs { get; init; }

    // 実行時の状態（書き換えはループスレッドのみ）
    public ushort EventBit;                 // press / release 用
    public ushort[] SeqBits = Array.Empty<ushort>();   // sequence 用
    public int SeqIndex;
    public double SeqLastSec;
    public double LastFiredSec = double.NegativeInfinity;
    public long FireCount;

    public string EventShort() => (Event.Type ?? "").ToLowerInvariant() switch
    {
        "sequence" => $"seq[{string.Join(",", Event.Buttons ?? Array.Empty<string>())}]",
        _ => $"{Event.Type} {Event.Button}",
    };

    public string Describe()
    {
        string ev = Event.Type.ToLowerInvariant() switch
        {
            "sequence" => $"sequence [{string.Join(",", Event.Buttons ?? Array.Empty<string>())}] (window {Event.WindowMs ?? 500}ms)",
            _ => $"{Event.Type} {Event.Button}",
        };
        string act = Action.Kind.ToLowerInvariant() switch
        {
            "mapping" => $"mapping {Action.Rules?.Length ?? 0} rule(s) for {Action.DurationSec ?? 1}s",
            "inject_tap" => $"inject_tap [{string.Join("+", Action.Buttons ?? Array.Empty<string>())}] {Action.Frames ?? 3}f",
            "inject_macro" => $"inject_macro '{Action.Macro}'",
            _ => Action.Kind,
        };
        string cd = CooldownMs is double c ? $", cooldown {c}ms" : "";
        return $"{Id}: when {ev} -> {Target.Host}:{Target.Port} do {act}{cd} (fired {FireCount}x)";
    }
}

/// <summary>
/// このLLMConが持つ接続ルールの集まり。高速ループから人間入力のエッジを受け取り、
/// 事象に一致したら相手のLLMConの受け口（/connect/apply）へ作用を直接送る。
/// 大規模言語モデルは事象の検出と送信の途中には入らない（低頻度の設定だけを担う）。
/// </summary>
public sealed class ConnectionManager
{
    private readonly object _lock = new();
    private readonly List<Connection> _connections = new();
    private int _seq;
    private readonly EventLog _log;

    /// <summary>自分の識別（name@port）。受け手側の記録に「誰から来たか」を残すために送る。</summary>
    public string SelfLabel { get; set; } = "?";

    public ConnectionManager(EventLog log) => _log = log;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string Add(ConnEvent ev, ConnTarget target, ConnAction action, double? cooldownMs)
    {
        if (ev is null) throw new ArgumentException("event is required");
        if (target is null || string.IsNullOrWhiteSpace(target.Host) || target.Port <= 0)
            throw new ArgumentException("target host and port are required");
        ValidateAction(action);

        var c = new Connection
        {
            Id = "",  // 後で採番
            Event = ev,
            Target = target,
            Action = action,
            CooldownMs = cooldownMs,
        };

        switch ((ev.Type ?? "").ToLowerInvariant())
        {
            case "press":
            case "release":
                if (string.IsNullOrWhiteSpace(ev.Button)) throw new ArgumentException("event.button is required for press/release");
                c.EventBit = ControllerEngine.MaskOf(ev.Button!);
                if (c.EventBit == 0) throw new ArgumentException($"unknown event button '{ev.Button}'");
                break;
            case "sequence":
                if (ev.Buttons is null || ev.Buttons.Length == 0) throw new ArgumentException("event.buttons is required for sequence");
                c.SeqBits = ev.Buttons.Select(b =>
                {
                    var bit = ControllerEngine.MaskOf(b);
                    if (bit == 0) throw new ArgumentException($"unknown sequence button '{b}'");
                    return bit;
                }).ToArray();
                break;
            default:
                throw new ArgumentException($"unknown event type '{ev.Type}' (use press, release, or sequence)");
        }

        lock (_lock)
        {
            var id = $"c{++_seq}";
            var added = new Connection
            {
                Id = id, Event = c.Event, Target = c.Target, Action = c.Action, CooldownMs = c.CooldownMs,
                EventBit = c.EventBit, SeqBits = c.SeqBits,
            };
            _connections.Add(added);
            return id;
        }
    }

    private static void ValidateAction(ConnAction a)
    {
        if (a is null) throw new ArgumentException("action is required");
        switch ((a.Kind ?? "").ToLowerInvariant())
        {
            case "mapping":
                if (a.Rules is null || a.Rules.Length == 0) throw new ArgumentException("action.rules is required for mapping");
                break;
            case "inject_tap":
                if (a.Buttons is null || a.Buttons.Length == 0) throw new ArgumentException("action.buttons is required for inject_tap");
                foreach (var b in a.Buttons)
                    if (!ControllerEngine.IsKnownButton(b)) throw new ArgumentException($"unknown inject button '{b}'");
                break;
            case "inject_macro":
                if (string.IsNullOrWhiteSpace(a.Macro)) throw new ArgumentException("action.macro is required for inject_macro");
                break;
            default:
                throw new ArgumentException($"unknown action kind '{a.Kind}' (use mapping, inject_tap, or inject_macro)");
        }
    }

    public bool Remove(string id)
    {
        lock (_lock) return _connections.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public void Clear() { lock (_lock) _connections.Clear(); }

    public IReadOnlyList<Connection> List()
    {
        lock (_lock) return _connections.ToList();
    }

    /// <summary>
    /// 高速ループから、人間入力の押下エッジ(pressed)と解放エッジ(released)を受け取る。
    /// エッジが立った時にだけ呼ばれる（毎ティックではない）。一致した接続の作用を相手へ送る。
    /// </summary>
    public void OnHumanEdges(ushort pressed, ushort released, double now)
    {
        List<Connection> snap;
        lock (_lock)
        {
            if (_connections.Count == 0) return;
            snap = _connections.ToList();
        }

        foreach (var c in snap)
        {
            bool fire = (c.Event.Type ?? "").ToLowerInvariant() switch
            {
                "press" => (pressed & c.EventBit) != 0,
                "release" => (released & c.EventBit) != 0,
                "sequence" => AdvanceSequence(c, pressed, now),
                _ => false,
            };
            if (fire) TryFire(c, now);
        }
    }

    // 押下エッジで並びを1歩進める。最後まで一致したら true（発火）。
    private static bool AdvanceSequence(Connection c, ushort pressed, double now)
    {
        if (pressed == 0 || c.SeqBits.Length == 0) return false;
        double win = (c.Event.WindowMs ?? 500.0) / 1000.0;

        foreach (var name in ControllerEngine.NamesFromMask(pressed))
        {
            ushort bit = ControllerEngine.MaskOf(name);
            if (c.SeqIndex > 0 && (now - c.SeqLastSec) > win) c.SeqIndex = 0;

            if (bit == c.SeqBits[c.SeqIndex])
            {
                c.SeqIndex++;
                c.SeqLastSec = now;
                if (c.SeqIndex >= c.SeqBits.Length) { c.SeqIndex = 0; return true; }
            }
            else if (bit == c.SeqBits[0]) { c.SeqIndex = 1; c.SeqLastSec = now; }
            else { c.SeqIndex = 0; }
        }
        return false;
    }

    private void TryFire(Connection c, double now)
    {
        if (c.CooldownMs is double cd && (now - c.LastFiredSec) * 1000.0 < cd)
        {
            _log.Add("skip", $"{c.Id} {c.EventShort()} matched but suppressed by cooldown ({cd:F0}ms)");
            return;
        }
        c.LastFiredSec = now;
        c.FireCount++;

        _log.Add("send", $"{c.Id} {c.EventShort()} -> {c.Target.Host}:{c.Target.Port} [{c.Action.Kind}]");

        var payload = JsonSerializer.Serialize(
            new ConnApply(c.Action.Kind, c.Action.Rules, c.Action.DurationSec ?? 1.0, c.Action.Buttons, c.Action.Frames ?? 3, c.Action.Macro, SelfLabel),
            Json);
        var url = $"http://{c.Target.Host}:{c.Target.Port}/connect/apply";
        _ = SendAsync(url, payload);   // ループを止めないため、送信は投げっぱなしにする
    }

    private static async Task SendAsync(string url, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = await Http.PostAsync(url, content).ConfigureAwait(false);
        }
        catch { /* 相手が起動していない・到達不能などは無視する */ }
    }
}
