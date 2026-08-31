using System;

namespace CntlLevelConnection;

/// <summary>
/// 待機の分解能を細かくすることを要求する仕組み。
/// Windows では winmm の timeBeginPeriod を呼ぶ実装を渡す。それ以外の環境では何もしない。
/// Request() が返すものを破棄した時点で、元の分解能へ戻す。
/// </summary>
public interface IHiResTimer
{
    /// <summary>細かい分解能を要求する。戻り値を破棄すると元へ戻る。</summary>
    IDisposable Request();
}

/// <summary>何もしない実装。分解能を変えられない環境（mac など）で使う。</summary>
public sealed class NullHiResTimer : IHiResTimer
{
    public static readonly NullHiResTimer Instance = new();
    private sealed class Nothing : IDisposable { public static readonly Nothing Instance = new(); public void Dispose() { } }
    public IDisposable Request() => Nothing.Instance;
}

/// <summary>
/// 現在の高分解能タイマーの実装を保持する。既定は何もしない実装で、
/// Windows のアプリが起動時に自分の実装を差し込む。
/// FrameClock のように、どこからでも使いたい箇所があるので、静的に持つ。
/// </summary>
public static class HiResTimer
{
    private static IHiResTimer _impl = NullHiResTimer.Instance;

    /// <summary>実装を差し替える（Windows 側の起動時に呼ぶ）。</summary>
    public static void Use(IHiResTimer impl) => _impl = impl ?? NullHiResTimer.Instance;

    /// <summary>細かい分解能を要求する。戻り値を破棄すると元へ戻る。</summary>
    public static IDisposable Request() => _impl.Request();
}
