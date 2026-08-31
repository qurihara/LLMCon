using System;

namespace CntlLevelConnection;

/// <summary>
/// マイクのしきい値でボタンを操作する仕組みの、環境に依存しない口。
/// Windows では WASAPI を使う実装（MicInput）を渡す。マイクを扱わない環境では、
/// 何もしない実装（NullMicTrigger）を渡す。MCP のツールは、この口だけを知っている。
/// </summary>
public interface IMicTrigger
{
    /// <summary>いまの設定を短い文で表す（get_info の表示に使う）。</summary>
    string Describe();

    /// <summary>設定を差し替える。結果を表す文字列を返す（失敗の理由もここに入れる）。</summary>
    string Configure(bool enabled, string? button, double? threshold, double? low, string? mode);

    /// <summary>有効になっているか。</summary>
    bool Enabled { get; }

    /// <summary>直近の音量（0..1）。メーターの表示に使う。</summary>
    double Level { get; }
}

/// <summary>マイクを扱わない環境で使う実装。設定を求められたら、使えない旨を返す。</summary>
public sealed class NullMicTrigger : IMicTrigger
{
    public static readonly NullMicTrigger Instance = new();
    public string Describe() => "mic not available on this platform";
    public string Configure(bool enabled, string? button, double? threshold, double? low, string? mode)
        => "この環境ではマイクのしきい値ボタンを使えません（マイクの取り込みは Windows の実装だけにあります）";
    public bool Enabled => false;
    public double Level => 0;
}
