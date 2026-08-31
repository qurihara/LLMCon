using System;
using System.ComponentModel;

namespace CntlLevelConnection;

/// <summary>
/// 人間入力(物理+ソフト+マイク)に適用する改変ルール1件。データ駆動の「改変の語彙」。
/// デジタルボタン向け: "disable"{Button} / "remap"{From,To} / "turbo"{Button,Hz} /
/// "invert"{Button}（反転。押されていないとき On、押されているとき Off）。
/// 時間方向: "delay"{DelayMs}。人間入力の全体を DelayMs ミリ秒だけ遅らせる（反応遅延。スキル差調整）。
/// アナログ向け（スティックとトリガー）: "gain"{Axis,Amount=倍率} / "deadzone"{Axis,Amount=0..1} /
/// "invert"{Axis} / "clamp"{Axis,Amount=0..1} / "curve"{Axis,Amount=指数} / "rate"{Axis,Amount=毎秒の最大変化量} /
/// "swap"{Axis=スティック} / "rotate"{Axis=スティック,Amount=度}。Axis は LX,LY,RX,RY,LT,RT または LS,RS,sticks,triggers,all。
/// swap と rotate はスティック単位（XとYをまとめて扱う）。
/// StartSec/EndSec を付けると、set_mapping 呼び出しからの経過秒で その窓だけ有効（時間変化改変）。
/// </summary>
public sealed record MappingRule(
    [property: Description("disable | remap | turbo | delay | gain | deadzone | invert | clamp | curve | rate | swap | rotate")] string Op,
    [property: Description("target button for disable/turbo")] string? Button = null,
    [property: Description("source button for remap")] string? From = null,
    [property: Description("destination button for remap")] string? To = null,
    [property: Description("turbo frequency in Hz (default 15)")] double? Hz = null,
    [property: Description("delay in milliseconds for op=delay (delays the whole human input)")] double? DelayMs = null,
    [property: Description("target axis for analog ops: LX,LY,RX,RY,LT,RT or LS,RS,sticks,triggers,all")] string? Axis = null,
    [property: Description("amount for analog ops: gain factor / deadzone 0..1 / clamp max 0..1 / curve exponent / rate units-per-second / rotate degrees")] double? Amount = null,
    [property: Description("active from this many seconds after set (optional)")] double? StartSec = null,
    [property: Description("active until this many seconds after set (optional)")] double? EndSec = null);

/// <summary>GUIプリセット名 → ルールセット（GUIとLLMが同じ仕組みを共有）。</summary>
public static class MappingPresets
{
    public static MappingRule[] Build(Preset p) => p switch
    {
        Preset.DisableA => new[] { new MappingRule("disable", Button: "A") },
        Preset.SwapAB   => new[] { new MappingRule("remap", From: "A", To: "B"),
                                   new MappingRule("remap", From: "B", To: "A") },
        Preset.TurboB   => new[] { new MappingRule("turbo", Button: "B", Hz: 15) },
        _ => Array.Empty<MappingRule>(),
    };
}
