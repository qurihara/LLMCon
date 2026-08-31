using System;

namespace CntlLevelConnection;

/// <summary>
/// 物理パッドをゲームから隠す仕組み。Windows では HidHide を使う。
///
/// なぜ要るのか。LLMCon は物理パッドを読み、改変ルールを適用して仮想コントローラとして出す。
/// しかしゲームからは物理パッドと仮想コントローラの両方が見えるので、生の入力も届いてしまう。
/// 反転のような改変は、生の入力に打ち消される（Issue #12）。物理パッドを LLMCon にだけ見せれば、
/// 改変後の入力だけがゲームに届く。
///
/// 扱えない環境（mac、画面を持たないホスト）では、何も渡さなくてよい。
/// </summary>
public interface IPadHider
{
    /// <summary>使えるか。HidHide が導入されていなければ false。</summary>
    bool Available { get; }

    /// <summary>使えないときの理由。使えるときは null。</summary>
    string? Unavailable { get; }

    /// <summary>いま隠すことを望んでいるか（パッドを選んでいなければ、望んでいても隠すものが無い）。</summary>
    bool Requested { get; }

    /// <summary>いま何を隠しているかを1行で。get_info と get_state に出す。</summary>
    string Describe();

    /// <summary>隠す・隠さないを切り替える。結果を人が読める形で返す。</summary>
    string SetHiding(bool on);

    /// <summary>読み取るパッドが変わったことを伝える。隠すことを望んでいれば、対象を移す。</summary>
    void OnSelectionChanged(string? padId);

    /// <summary>すべての隠蔽をやめる。終了時に必ず呼ぶ。</summary>
    void Release();
}
