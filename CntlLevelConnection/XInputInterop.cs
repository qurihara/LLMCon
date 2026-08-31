using System;
using System.Runtime.InteropServices;

namespace CntlLevelConnection;

/// <summary>XInput からの物理パッドの読み取り（ポーリング）。Windows 固有。</summary>
internal static class XInputInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, ref XINPUT_STATE pState);

    // ── スロットの機体の素性 ─────────────────────────────
    // XInput の公開された関数は、機体の製造者と製品の識別子を返さない。しかし
    // xinput1_4.dll の序数 108（XInputGetCapabilitiesEx）が返す。文書化されていないが、
    // 2026/8/7 にこの機械で実物を確かめた（内蔵 VID_0B05 PID_1B4C、ViGEm の仮想パッド
    // VID_045E PID_028E）。ゲームから隠す相手を機器の一覧から探すために要る（Issue #12）。

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION { public ushort Left, Right; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES
    {
        public byte Type, SubType;
        public ushort Flags;
        public XINPUT_GAMEPAD Gamepad;
        public XINPUT_VIBRATION Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES_EX
    {
        public XINPUT_CAPABILITIES Capabilities;
        public ushort VendorId, ProductId, ProductVersion, Unknown1;
        public uint Unknown2;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern int XInputGetCapabilitiesEx(int reserved, int dwUserIndex, int dwFlags, ref XINPUT_CAPABILITIES_EX caps);

    /// <summary>そのスロットの機体の製造者と製品の識別子。取れなければ null。</summary>
    public static (ushort Vid, ushort Pid)? HardwareIds(int slot)
    {
        if (slot < 0 || slot > 3) return null;
        try
        {
            var caps = new XINPUT_CAPABILITIES_EX();
            // 第1引数は 1 を渡す決まりである（他の値では失敗する）。
            if (XInputGetCapabilitiesEx(1, slot, 0, ref caps) != ErrorSuccess) return null;
            if (caps.VendorId == 0 && caps.ProductId == 0) return null;
            return (caps.VendorId, caps.ProductId);
        }
        catch { return null; }   // 文書化されていない関数なので、無い環境でも落とさない
    }

    public const int ErrorSuccess = 0;

    /// <summary>指定スロットを読む。未接続なら false。</summary>
    public static bool TryRead(int slot, out PadState s)
    {
        s = default;
        if (slot < 0 || slot > 3) return false;
        var st = new XINPUT_STATE();
        if (XInputGetState(slot, ref st) != ErrorSuccess) return false;
        var g = st.Gamepad;
        s = new PadState
        {
            Buttons = g.wButtons,
            LT = g.bLeftTrigger, RT = g.bRightTrigger,
            LX = g.sThumbLX, LY = g.sThumbLY, RX = g.sThumbRX, RY = g.sThumbRY,
        };
        return true;
    }

    /// <summary>スロット0..3の接続状況。</summary>
    public static bool[] ConnectedSlots()
    {
        var r = new bool[4];
        var st = new XINPUT_STATE();
        for (int i = 0; i < 4; i++) r[i] = XInputGetState(i, ref st) == ErrorSuccess;
        return r;
    }

    /// <summary>
    /// 生の値をそのまま読む。どのボタンがどう出るかを人が見るために使う。
    /// XInput は軸に名前が付いているので、生の軸の番号を人が判定する必要は無い。
    /// 未接続なら null。
    /// </summary>
    public static PadRawReading? ReadRaw(int slot, string name)
    {
        if (slot < 0 || slot > 3) return null;
        var st = new XINPUT_STATE();
        if (XInputGetState(slot, ref st) != ErrorSuccess) return null;
        var g = st.Gamepad;

        // 軸は Windows.Gaming.Input と同じく 0 から 1 に正規化して見せる。
        // 見る人が2つの経路を同じ物差しで比べられるようにするためである。
        double Bip(short v) => (v + 32768.0) / 65535.0;
        var axes = new[] { Bip(g.sThumbLX), Bip(g.sThumbLY), Bip(g.sThumbRX), Bip(g.sThumbRY),
                           g.bLeftTrigger / 255.0, g.bRightTrigger / 255.0 };

        var names = ControllerEngine.NamesFromMask(g.wButtons);
        var buttons = new bool[16];
        foreach (var n in names) { int i = Array.IndexOf(ButtonOrder, n); if (i >= 0) buttons[i] = true; }

        // 時刻印はパケット番号を使う。0 は「まだ一度も更新されていない」を意味するので、
        // Windows.Gaming.Input のときと同じ守り（Issue #7）がそのまま働く。
        return new PadRawReading(SlotId(slot), name, ButtonOrder.Length, 0, axes.Length,
                                 axes, buttons, "-", st.dwPacketNumber);
    }

    /// <summary>生のボタンの並び。ReadRaw が返す番号の意味である。</summary>
    public static readonly string[] ButtonOrder =
    {
        "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "LS", "RS",
        "DUp", "DRight", "DDown", "DLeft", "Guide",
    };

    /// <summary>スロットを表す識別子。Windows.Gaming.Input の識別子と混ざらない形にする。</summary>
    public static string SlotId(int slot) => $"xinput:{slot}";

    /// <summary>識別子がスロットを指しているなら、その番号。違えば -1。</summary>
    public static int SlotOf(string? id)
        => id != null && id.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase) &&
           int.TryParse(id.AsSpan(7), out var n) && n >= 0 && n <= 3 ? n : -1;
}
