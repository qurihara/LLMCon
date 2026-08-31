using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace CntlLevelConnection;

/// <summary>
/// Windows の高分解能タイマー。winmm の timeBeginPeriod を呼び、破棄したときに戻す。
/// Core は IHiResTimer だけを知っており、この実装は Windows のアプリが起動時に差し込む。
/// </summary>
public sealed class WinMmHiResTimer : IHiResTimer
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

    public IDisposable Request() => new Period();

    private sealed class Period : IDisposable
    {
        private bool _done;
        public Period() { timeBeginPeriod(1); }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            timeEndPeriod(1);
        }
    }
}

/// <summary>
/// Windows.Gaming.Input による物理パッドの読み取り。既存の WgiPad を、Core の抽象に合わせて包んだもの。
/// 自分の出力が作った仮想コントローラは、一覧からも読み取りからも除く。
/// </summary>
public sealed class WgiPadSource : IPadSource
{
    private volatile RawGameController? _pad;
    private string? _ownPadId;

    // XInput の機体を選んでいるときの、そのスロットの番号（-1 は選んでいない）。
    // Windows.Gaming.Input は前面にあることを要求するので、常駐したままでは XInput の
    // 機体を読めない。そちらは XInputGetState で直接読む（Issue #15）。
    private volatile int _slot = -1;

    /// <summary>自分が作った仮想コントローラが乗っている XInput のスロット（-1 は不明）。</summary>
    private volatile int _ownSlot = -1;
    private volatile PadAxisMap _map = PadAxisMap.Default;
    private volatile PadButtonMap _buttons = PadButtonMap.Default;

    // パッドごとの割り当てを覚えておく（別のパッドを選び直しても、前の設定が生きる）
    private readonly Dictionary<string, PadAxisMap> _perPad = new();
    private readonly Dictionary<string, PadButtonMap> _perPadButtons = new();

    // 読み取りのたびに配列を作らないよう、使い回す（既存の実装と同じ考え方）
    private bool[] _btn = Array.Empty<bool>();
    private GameControllerSwitchPosition[] _sw = Array.Empty<GameControllerSwitchPosition>();
    private double[] _ax = Array.Empty<double>();

    public string? SelectedId => _slot >= 0 ? XInputInterop.SlotId(_slot) : _pad?.NonRoamableId;
    public PadAxisMap AxisMap => _map;
    public PadButtonMap ButtonMap => _buttons;

    public void ExcludeOwnPad(string? id) => _ownPadId = id;

    /// <summary>
    /// 自分の出力が乗っている XInput のスロットを受け取る。特定は出力先（ViGEmSink）が
    /// 自分を動かして行う。こちらでは推測しない。
    /// </summary>
    public void ExcludeOwnSlot(int slot) => _ownSlot = slot;

    /// <summary>
    /// 一覧を出す。XInput の機体はスロットとして、素の HID の機体は
    /// Windows.Gaming.Input の機体として並べる。利用者は経路の違いを意識しない。
    /// </summary>
    public IReadOnlyList<PadInfo> List()
    {
        var result = new List<PadInfo>();

        // XInput の機体。前面を失っても読めるので、こちらを先に出す。
        var connected = XInputInterop.ConnectedSlots();
        for (int i = 0; i < connected.Length; i++)
        {
            if (!connected[i] || i == _ownSlot) continue;
            result.Add(new PadInfo(XInputInterop.SlotId(i), NameForSlot(i)));
        }

        // 素の HID の機体。XInput の機体は上で出しているので、ここでは除く。
        foreach (var p in WgiPad.List(_ownPadId))
        {
            var c = WgiPad.Find(p.Id);
            if (c != null && XInputClass.IsXInputDevice(c.HardwareVendorId, c.HardwareProductId)) continue;
            result.Add(new PadInfo(p.Id, p.Name));
        }
        return result;
    }

    /// <summary>
    /// スロットに人が読める名前を付ける。Windows.Gaming.Input の側に XInput の機体が
    /// ひとつしか無ければ、その表示名を借りる。複数あると取り違えるので、そのときは番号だけにする。
    /// </summary>
    private string NameForSlot(int slot)
    {
        var xs = WgiPad.List(_ownPadId)
            .Where(p => { var c = WgiPad.Find(p.Id); return c != null && XInputClass.IsXInputDevice(c.HardwareVendorId, c.HardwareProductId); })
            .ToList();
        return xs.Count == 1 ? $"{xs[0].Name} (XInput {slot})" : $"XInput スロット {slot}";
    }

    private IReadOnlyList<PadProfile>? _profiles;

    public void UsePadProfiles(IReadOnlyList<PadProfile>? profiles) => _profiles = profiles;

    public void Select(string? id)
    {
        // 識別子が xinput:N ならスロット、そうでなければ Windows.Gaming.Input の機体である。
        int slot = XInputInterop.SlotOf(id);
        _slot = slot;
        _pad = (slot < 0 && id != null) ? WgiPad.Find(id) : null;
        lock (_perPad)
        {
            // 順に見る。この処理の中で前に設定したものがあれば、それがいちばん優先される。
            // 無ければプロファイルの機体ごとの設定を当てる。それも無ければ既定にする。
            if (id != null && _perPad.TryGetValue(id, out var m)) _map = m;
            else _map = ProfileFor(id)?.Axes ?? PadAxisMap.Default;

            if (id != null && _perPadButtons.TryGetValue(id, out var b)) _buttons = b;
            else
            {
                var spec = ProfileFor(id)?.Buttons;
                _buttons = string.IsNullOrEmpty(spec)
                    ? PadButtonMap.Default
                    : PadButtonMap.FromSpec(spec, out _);
            }
        }
    }

    /// <summary>その識別子の機体に当てはまるプロファイルの設定。無ければ null。</summary>
    private PadProfile? ProfileFor(string? id)
    {
        if (id == null || _profiles == null) return null;
        var c = WgiPad.Find(id);
        if (c == null) return null;
        string name; try { name = c.DisplayName ?? ""; } catch { name = ""; }
        return PadProfile.FindFor(_profiles, c.HardwareVendorId, c.HardwareProductId, name);
    }

    /// <summary>
    /// いま選んでいるパッドの素性。ゲームから隠す相手を機器の一覧から探すために使う。
    /// XInput のスロットは xinput1_4.dll の序数 108 から、素の HID の機体は
    /// Windows.Gaming.Input から取る。
    /// </summary>
    public PadHardware? SelectedHardware()
    {
        int slot = _slot;
        if (slot >= 0)
        {
            var ids = XInputInterop.HardwareIds(slot);
            return ids is null ? null : new PadHardware(ids.Value.Vid, ids.Value.Pid, NameForSlot(slot));
        }
        var pad = _pad;
        if (pad == null) return null;
        string name; try { name = pad.DisplayName ?? "pad"; } catch { name = "pad"; }
        return new PadHardware(pad.HardwareVendorId, pad.HardwareProductId, name);
    }

    public string? DescribeSelectedAsProfile()
    {
        var pad = _pad;
        if (pad == null) return null;
        string name; try { name = pad.DisplayName ?? "pad"; } catch { name = "pad"; }
        return PadProfile.ToJsonSnippet(name, pad.HardwareVendorId, pad.HardwareProductId, _map, _buttons);
    }

    public void SetAxisMap(PadAxisMap map)
    {
        _map = map ?? PadAxisMap.Default;
        var id = SelectedId;
        if (id != null) lock (_perPad) _perPad[id] = _map;
    }

    public void SetButtonMap(PadButtonMap map)
    {
        _buttons = map ?? PadButtonMap.Default;
        var id = SelectedId;
        if (id != null) lock (_perPad) _perPadButtons[id] = _buttons;
    }

    public bool TryRead(out PadState state)
    {
        // XInput の機体は XInputGetState で直接読む。前面も窓も要らないので、
        // タスクトレイに引っ込んだままでも読める（Issue #15）。
        // 軸に名前が付いているので、軸の割り当ての変換も要らない。
        int slot = _slot;
        if (slot >= 0) return XInputInterop.TryRead(slot, out state);

        var pad = _pad;
        if (pad == null) { state = default; return false; }
        // 戻り値を捨ててはならない。読めなかったときに state をそのまま渡すと、
        // 中身の無い既定値が「スティックが端に倒れている」と解釈される（Issue #7）。
        if (!WgiPad.TryRead(pad, _map, _buttons, ref _btn, ref _sw, ref _ax, out state))
        {
            state = default;
            return false;
        }
        return true;
    }

    public PadRawReading? ReadRaw(string? id = null)
    {
        // 指定がスロットなら XInput から読む。指定が無ければ、いま選んでいるものに従う。
        int slot = (id != null) ? XInputInterop.SlotOf(id) : _slot;
        if (slot >= 0) return XInputInterop.ReadRaw(slot, NameForSlot(slot));

        var pad = (id != null) ? WgiPad.Find(id) : _pad;
        // どれも選ばれていないときは、繋がっている最初のパッド（自分の仮想出力は除く）を見る。
        // 割り当てを調べる場面では、まだパッドを選んでいないことが多いため。
        if (pad == null && id == null && _slot < 0)
        {
            var first = List().FirstOrDefault();
            if (first != null)
            {
                int s2 = XInputInterop.SlotOf(first.Id);
                if (s2 >= 0) return XInputInterop.ReadRaw(s2, first.Name);
                pad = WgiPad.Find(first.Id);
            }
        }
        return pad == null ? null : WgiPad.ReadRaw(pad);
    }
}
