using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CntlLevelConnection;

/// <summary>
/// マクロの1ステップ。指定した状態を Frames フレームのあいだ保持する。
/// 1フレームの長さは、エンジンに設定された現在のフレームレートで決まる。
/// 省略したスティックやトリガーは中立（0）になる。
/// </summary>
public sealed record MacroStep(
    [property: Description("number of frames this state is held (at the current fps)")] int Frames,
    [property: Description("buttons held during this step: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight")] string[]? Buttons = null,
    [property: Description("left stick X -32768..32767")] int? Lx = null,
    [property: Description("left stick Y -32768..32767 (up positive)")] int? Ly = null,
    [property: Description("right stick X -32768..32767")] int? Rx = null,
    [property: Description("right stick Y -32768..32767 (up positive)")] int? Ry = null,
    [property: Description("left trigger 0..255")] int? Lt = null,
    [property: Description("right trigger 0..255")] int? Rt = null);

/// <summary>
/// フレームを単位にしたマクロを保管して、フレーム精度で実行する。
/// 実行は専用スレッド上で行い、各ステップの状態をエンジンのLLM注入経路へ書き込む。
/// 実行が終わると、LLM注入を中立に戻す。
/// </summary>
public sealed class MacroEngine(ControllerEngine engine)
{
    private readonly Dictionary<string, MacroStep[]> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationTokenSource? _running;

    public void Define(string name, MacroStep[] steps)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("macro name is required");
        if (steps is null || steps.Length == 0) throw new ArgumentException("at least one step is required");
        foreach (var s in steps)
            foreach (var b in s.Buttons ?? Array.Empty<string>())
                if (!ControllerEngine.IsKnownButton(b))
                    throw new ArgumentException($"unknown button '{b}' in macro '{name}'");
        lock (_lock) _macros[name] = steps;
    }

    public IReadOnlyList<string> List()
    {
        lock (_lock) return _macros.Keys.ToList();
    }

    public void Stop()
    {
        lock (_lock) _running?.Cancel();
    }

    public async Task<string> RunAsync(string name)
    {
        MacroStep[] steps;
        CancellationToken ct;
        lock (_lock)
        {
            if (!_macros.TryGetValue(name, out var s))
                throw new ArgumentException($"unknown macro '{name}'. defined: {string.Join(", ", _macros.Keys)}");
            steps = s;
            _running?.Cancel();
            _running = new CancellationTokenSource();
            ct = _running.Token;
        }

        double frameMs = 1000.0 / engine.Fps;
        int totalFrames = steps.Sum(s => Math.Max(0, s.Frames));

        try { await Task.Run(() => Execute(steps, frameMs, ct), ct); }
        catch (OperationCanceledException) { return $"macro '{name}' was canceled"; }

        return $"ran macro '{name}': {steps.Length} steps, {totalFrames} frames at {engine.Fps:F0}fps (about {Math.Round(totalFrames * frameMs)}ms)";
    }

    private void Execute(MacroStep[] steps, double frameMs, CancellationToken ct)
    {
        using var hiRes = FrameClock.HiRes();
        try
        {
            var sw = Stopwatch.StartNew();
            double targetMs = 0;
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                Apply(step);
                targetMs += Math.Max(0, step.Frames) * frameMs;
                FrameClock.WaitUntil(sw, targetMs, ct);
            }
        }
        finally
        {
            engine.LlmNeutral();
        }
    }

    private void Apply(MacroStep step)
    {
        engine.SetLlmSnapshot(
            step.Buttons ?? Array.Empty<string>(),
            Clamp16(step.Lx), Clamp16(step.Ly), Clamp16(step.Rx), Clamp16(step.Ry),
            ClampByte(step.Lt), ClampByte(step.Rt));
    }

    private static short Clamp16(int? v) => (short)Math.Clamp(v ?? 0, short.MinValue, short.MaxValue);
    private static byte ClampByte(int? v) => (byte)Math.Clamp(v ?? 0, 0, 255);
}
