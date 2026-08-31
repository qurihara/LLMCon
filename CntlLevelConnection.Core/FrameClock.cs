using System;
using System.Diagnostics;
using System.Threading;

namespace CntlLevelConnection;

/// <summary>
/// フレーム精度で待つためのユーティリティ。累積した目標時刻に対して待つので、
/// 1ステップごとの誤差が積み重ならない。待機の分解能は HiResTimer に要求し
/// （Windows では timeBeginPeriod、他の環境では何もしない）、
/// 残りが2ミリ秒を切ったらスピンでフレーム境界に詰める。
/// </summary>
internal static class FrameClock
{
    /// <summary>
    /// 細かい分解能を要求する。戻り値を破棄すると元へ戻る。
    /// これまでの BeginHiRes と EndHiRes の対を、破棄で戻す形に置き換えたもの。
    /// </summary>
    public static IDisposable HiRes() => HiResTimer.Request();

    public static void WaitUntil(Stopwatch sw, double targetMs, CancellationToken ct)
    {
        while (true)
        {
            double remaining = targetMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            ct.ThrowIfCancellationRequested();
            if (remaining > 2) Thread.Sleep(1);
            else Thread.SpinWait(100);
        }
    }
}
