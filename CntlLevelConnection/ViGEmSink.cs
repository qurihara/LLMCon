using System;
using System.Collections.Generic;
using System.Linq;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace CntlLevelConnection;

/// <summary>
/// これまでどおりの出力先。ViGEm の仮想 Xbox 360 コントローラへ書き、1周回につき1回だけ送信する。
/// Windows でだけ動く。実際の市販ゲームに効かせたいときは、これを使う。
/// </summary>
public sealed class ViGEmSink : IPadSink, IOwnPadIdentity
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;

    public string Name => "vigem";
    public bool Connected => _controller != null;
    public string? Error { get; private set; }

    /// <summary>この出力が作った仮想コントローラを、物理パッドの一覧から除くために使う識別子。</summary>
    public string? OwnPadId { get; private set; }

    /// <summary>この出力が作った仮想コントローラが乗っている XInput のスロット（-1 は不明）。</summary>
    public int OwnXInputSlot { get; private set; } = -1;

    /// <summary>
    /// 自分の仮想コントローラが XInput のどのスロットに乗っているかを、自分で動かして見つける。
    /// 左トリガーを目一杯にして、その値が出ているスロットを探す。人手は要らない。
    /// </summary>
    private static int FindOwnSlot(IXbox360Controller controller)
    {
        try
        {
            controller.SetSliderValue(Xbox360Slider.LeftTrigger, 255);
            controller.SubmitReport();

            // XInput のスロットの割り当ては、作った直後にはまだ落ち着いていないことがある。
            // 見つかるまで少し待つ。
            for (int attempt = 0; attempt < 12; attempt++)
            {
                System.Threading.Thread.Sleep(150);
                int found = -1;
                bool ambiguous = false;
                for (int i = 0; i < 4; i++)
                    if (XInputInterop.TryRead(i, out var s) && s.LT == 255)
                    { if (found < 0) found = i; else ambiguous = true; }
                if (ambiguous) return -1;         // 見分けがつかないなら除外しない
                if (found >= 0) return found;
            }
            return -1;
        }
        catch { return -1; }
        finally
        {
            try { controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0); controller.SubmitReport(); } catch { }
        }
    }

    private static readonly (ushort bit, Xbox360Button btn)[] OutBits =
    {
        (0x1000,Xbox360Button.A),(0x2000,Xbox360Button.B),(0x4000,Xbox360Button.X),(0x8000,Xbox360Button.Y),
        (0x0100,Xbox360Button.LeftShoulder),(0x0200,Xbox360Button.RightShoulder),
        (0x0040,Xbox360Button.LeftThumb),(0x0080,Xbox360Button.RightThumb),
        (0x0010,Xbox360Button.Start),(0x0020,Xbox360Button.Back),(0x0400,Xbox360Button.Guide),
        (0x0001,Xbox360Button.Up),(0x0002,Xbox360Button.Down),(0x0004,Xbox360Button.Left),(0x0008,Xbox360Button.Right),
    };

    public bool Start()
    {
        try
        {
            // 仮想コントローラを先に作る。順序が重要である。
            //
            // Windows.Gaming.Input は、処理が起動して機器を把握したあとに機体が増えると、
            // その処理への読み取りの配達を止めてしまう。新しい機体も、それまで読めていた機体も
            // 読めなくなる。機体が去ると回復する。2026/8/6 に実測で確かめた。
            //
            // 以前はここで、作成の前後の一覧の差から自分のパッドを見分けていた。そのためには
            // 作成の前に列挙する必要があり、その結果 LLMCon は自分の仮想コントローラによって
            // 自分の読み取りを壊していた。物理パッドが読めなかった原因はこれである。
            //
            // いまは先に作り、そのあとで列挙する。自分のパッドは、実際に動かして反応するものを
            // 探すことで見分ける。前後の差に頼らないので、順序を入れ替えられる。
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.AutoSubmitReport = false;   // ループで 1周回 = 1送信
            _controller.Connect();

            WgiPad.WaitUntilEnumerationSettles();
            OwnPadId = FindOwnPad(_controller);
            OwnXInputSlot = FindOwnSlot(_controller);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _controller = null;
            return false;
        }
    }

    /// <summary>
    /// 自分が作った仮想コントローラがどれかを見分ける。
    ///
    /// Windows.Gaming.Input は、自分の処理が作った仮想コントローラの読み取りを、その処理自身
    /// には配らない。時刻印が 0 のままになる。2026/8/6 に実測で確かめた。他の処理が作った
    /// 仮想パッドは普通に読めるので、読めないものが自分のものである、という形で見分けられる。
    ///
    /// 作成の前後の一覧の差には頼らない。差を取るには作成の前に列挙する必要があり、それをすると
    /// 自分の仮想コントローラで自分の読み取りを壊してしまうためである。
    /// </summary>
    private static string? FindOwnPad(IXbox360Controller controller)
    {
        var candidates = WgiPad.CurrentIds().Where(WgiPad.LooksLikeVirtualPad).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];   // 迷いようがない（通常はこちら）

        // 候補が複数あるのは、他にも ViGEm を使うソフトウェアが動いている場合である。
        // 他の仮想パッドは、動きがあってはじめて読み取りが届くので、少し待って何度か見る。
        try
        {
            controller.SetButtonState(Xbox360Button.A, false);
            controller.SubmitReport();

            for (int attempt = 0; attempt < 10; attempt++)
            {
                System.Threading.Thread.Sleep(200);
                var unreadable = candidates.Where(id => !WgiPad.IsReadable(id)).ToList();
                if (unreadable.Count == 1) return unreadable[0];
                if (unreadable.Count == 0) return null;   // 全部読めるなら、自分のものが見当たらない
            }
        }
        catch { /* 見分けられなくても、出力そのものは動かしたい */ }
        // 見分けがつかないときは、諦めて除外しない。自分の出力を物理パッドとして読み込む
        // 恐れは残るが、本物の物理パッドを一覧から消すよりは害が小さい。
        return null;
    }

    public void Write(in PadState s)
    {
        var c = _controller;
        if (c is null) return;
        foreach (var (bit, btn) in OutBits) c.SetButtonState(btn, (s.Buttons & bit) != 0);
        c.SetSliderValue(Xbox360Slider.LeftTrigger, s.LT);
        c.SetSliderValue(Xbox360Slider.RightTrigger, s.RT);
        c.SetAxisValue(Xbox360Axis.LeftThumbX, s.LX);
        c.SetAxisValue(Xbox360Axis.LeftThumbY, s.LY);
        c.SetAxisValue(Xbox360Axis.RightThumbX, s.RX);
        c.SetAxisValue(Xbox360Axis.RightThumbY, s.RY);
        c.SubmitReport();
    }

    public void Dispose()
    {
        try { _controller?.ResetReport(); _controller?.SubmitReport(); } catch { /* ignore */ }
        try { _controller?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _controller = null; _client = null;
    }
}
