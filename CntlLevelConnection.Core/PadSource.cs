using System;
using System.Collections.Generic;

namespace CntlLevelConnection;

/// <summary>接続している物理パッドの情報。</summary>
public sealed record PadInfo(string Id, string Name);

/// <summary>
/// パッドの素性（製造者と製品の識別子と、表示名）。
/// ゲームから隠す相手を機器の一覧から探すために使う（Issue #12）。
/// </summary>
public sealed record PadHardware(ushort Vid, ushort Pid, string Name);

/// <summary>
/// 物理のゲームパッドの読み取り。Windows では Windows.Gaming.Input の実装を渡す。
/// mac のように物理パッドを扱わない環境では、何も渡さなくてよい（人間入力はウェブ版コントローラから入る）。
/// 一覧と選択も、環境ごとに違うのでここに含める。
/// </summary>
public interface IPadSource
{
    /// <summary>いま接続しているパッドの一覧。自分が作った仮想パッドは除いてよい。</summary>
    IReadOnlyList<PadInfo> List();

    /// <summary>読み取りの対象を選ぶ。null で「選ばない」。</summary>
    void Select(string? id);

    /// <summary>いま選んでいるパッドの識別子。</summary>
    string? SelectedId { get; }

    /// <summary>選んでいるパッドの状態を読む。読めなければ false（このとき state は既定値）。</summary>
    bool TryRead(out PadState state);

    /// <summary>
    /// 自分の出力が作った仮想パッドの識別子を伝える。一覧から除くために使う。
    /// 出力先が用意できたあとに呼ばれる。
    /// </summary>
    void ExcludeOwnPad(string? id);

    /// <summary>
    /// 自分の出力が作った仮想コントローラが乗っている XInput のスロットを伝える。
    /// 一覧から除くために使う。扱えない環境では何もしなくてよい。
    /// </summary>
    void ExcludeOwnSlot(int slot) { }

    /// <summary>いま使っている軸の割り当て。</summary>
    PadAxisMap AxisMap { get; }

    /// <summary>軸の割り当てを差し替える。選んでいるパッドに対して覚えておく。</summary>
    void SetAxisMap(PadAxisMap map);

    /// <summary>いま使っているボタンの割り当て。</summary>
    PadButtonMap ButtonMap { get; }

    /// <summary>ボタンの割り当てを差し替える。選んでいるパッドに対して覚えておく。</summary>
    void SetButtonMap(PadButtonMap map);

    /// <summary>
    /// 機体ごとの割り当てをプロファイルから受け取る。パッドを選んだときに、素性が合うものを当てる。
    /// 起動時に一度だけ呼ぶ。扱えない環境では何もしなくてよい。
    /// </summary>
    void UsePadProfiles(IReadOnlyList<PadProfile>? profiles) { }

    /// <summary>
    /// いま選んでいるパッドの割り当てを、プロファイルへ貼り付けられる JSON の断片にする。
    /// 実測で決めた割り当てを次の起動でも使えるようにするために出す。選んでいなければ null。
    /// </summary>
    string? DescribeSelectedAsProfile() => null;

    /// <summary>
    /// いま選んでいるパッドの素性。ゲームから隠す相手を特定するために使う。
    /// 分からない環境や、選んでいないときは null。
    /// </summary>
    PadHardware? SelectedHardware() => null;

    /// <summary>
    /// 生の軸とボタンの値を読む。どの軸がどの操作に当たるかを、人が見て決めるために使う。
    /// id を省略すると、いま選んでいるパッドを読む。読めなければ null。
    /// </summary>
    PadRawReading? ReadRaw(string? id = null);
}
