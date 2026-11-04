using System.Net;
using System.Net.Sockets;
using CntlLevelConnection;
using LLMConHub;
using Xunit;

namespace LLMConHub.Tests;

public class PadFrameTests
{
    [Fact]
    public void EncodeThenDecode_ReturnsSameValues()
    {
        var s = new PadState { Buttons = 0x1234, LT = 7, RT = 250, LX = -32768, LY = 32767, RX = 123, RY = -456 };
        var buf = new byte[PadFrame.Size];
        PadFrame.Encode(buf, s, seq: 42, sentMicros: 1_000_000_123, inputMicros: 999_999_000, heartbeat: true);

        Assert.True(PadFrame.TryDecode(buf, out var d, out var seq, out var sent, out var input, out var hb));
        Assert.Equal(s.Buttons, d.Buttons);
        Assert.Equal(s.LT, d.LT);
        Assert.Equal(s.RT, d.RT);
        Assert.Equal(s.LX, d.LX);
        Assert.Equal(s.LY, d.LY);
        Assert.Equal(s.RX, d.RX);
        Assert.Equal(s.RY, d.RY);
        Assert.Equal(42u, seq);
        Assert.Equal(1_000_000_123UL, sent);
        Assert.Equal(999_999_000UL, input);
        Assert.True(hb);
    }

    [Fact]
    public void Layout_MatchesDocumentedOffsets()
    {
        // Python の橋渡し（bridge/llmconhub_bridge.py）は、この並びを前提に読む。並びを変えたら両方を直すこと。
        var s = new PadState { Buttons = 0x1000, LT = 0x11, RT = 0x22, LX = 0x0102, LY = -2, RX = 0, RY = 0 };
        var buf = new byte[PadFrame.Size];
        PadFrame.Encode(buf, s, 1, 2, 3, heartbeat: false);
        Assert.Equal((byte)'L', buf[0]);
        Assert.Equal((byte)'B', buf[1]);
        Assert.Equal(PadFrame.Version, buf[2]);
        Assert.Equal(0, buf[3]);
        Assert.Equal(1, buf[4]);
        Assert.Equal(2, buf[8]);
        Assert.Equal(3, buf[16]);
        Assert.Equal(0x00, buf[24]);
        Assert.Equal(0x10, buf[25]);
        Assert.Equal(0x11, buf[26]);
        Assert.Equal(0x22, buf[27]);
        Assert.Equal(0x02, buf[28]);
        Assert.Equal(0x01, buf[29]);
        Assert.Equal(0xFE, buf[30]);
        Assert.Equal(0xFF, buf[31]);
    }

    [Fact]
    public void Decode_RejectsWrongMagic()
    {
        var buf = new byte[PadFrame.Size];
        buf[0] = (byte)'X';
        Assert.False(PadFrame.TryDecode(buf, out _, out _, out _, out _, out _));
    }
}

public class UdpFrameSinkTests
{
    [Fact]
    public void SendsOnChange_AndSkipsUnchangedUntilHeartbeat()
    {
        using var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)rx.Client.LocalEndPoint!).Port;
        using var sink = UdpFrameSink.FromSpec($"127.0.0.1:{port}");
        Assert.True(sink.Start());

        var a = new PadState { Buttons = ControllerEngine.MaskOf("A") };
        sink.Write(a);      // 初回は送る
        sink.Write(a);      // 変化が無いので送らない
        sink.Write(a);
        var b = new PadState { Buttons = 0 };
        sink.Write(b);      // 変化したので送る

        Assert.Equal(2, sink.Sent);
        rx.Client.ReceiveTimeout = 1000;
        IPEndPoint? from = null;
        var f1 = rx.Receive(ref from);
        var f2 = rx.Receive(ref from);
        Assert.True(PadFrame.TryDecode(f1, out var d1, out var s1, out _, out _, out var hb1));
        Assert.True(PadFrame.TryDecode(f2, out var d2, out var s2, out _, out _, out _));
        Assert.Equal(a.Buttons, d1.Buttons);
        Assert.Equal((ushort)0, d2.Buttons);
        Assert.Equal(s1 + 1, s2);
        Assert.False(hb1);
    }

    [Fact]
    public void ReceiverAbsent_DoesNotThrow()
    {
        // 受け手のいないポートへ送っても、Write が例外を投げてはならない（エンジンのループを止めないため）
        int port;
        using (var tmp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)tmp.Client.LocalEndPoint!).Port;
        using var sink = UdpFrameSink.FromSpec($"127.0.0.1:{port}");
        Assert.True(sink.Start());
        for (int i = 0; i < 5; i++) sink.Write(new PadState { Buttons = (ushort)i });
    }

    [Theory]
    [InlineData(null, "127.0.0.1", 9100)]
    [InlineData("9200", "127.0.0.1", 9200)]
    [InlineData("127.0.0.1:9300", "127.0.0.1", 9300)]
    public void FromSpec_ParsesTarget(string? spec, string host, int port)
    {
        using var sink = UdpFrameSink.FromSpec(spec);
        Assert.Equal($"udp({host}:{port})", sink.Name);
    }
}

public class EvdevCapsTests
{
    [Fact]
    public void ParseBitmap_ReadsMostSignificantWordFirst()
    {
        // 実際の sysfs の書き方の例：2語で、後ろの語が番号 0 から 63、前の語が 64 から 127
        var bits = EvdevCaps.ParseBitmap("3 8000000000000001", 64);
        Assert.Equal(new[] { 0, 63, 64, 65 }, bits);
    }

    [Fact]
    public void ParseBitmap_GamepadKeyWord()
    {
        // BTN_SOUTH（0x130 = 304）は、5語目（番号 256 から 319）の 48 ビット目に当たる
        // 0x130 のビットだけを立てた5語を作る
        var words = new string[5];
        for (int i = 0; i < 5; i++) words[i] = "0";
        words[0] = (1UL << (0x130 - 256)).ToString("x");   // 先頭が最上位の語
        var bits = EvdevCaps.ParseBitmap(string.Join(' ', words), 64);
        Assert.Equal(new[] { 0x130 }, bits);
        Assert.True(new EvdevCaps(bits, new List<int>()).IsGamepad);
    }
}

public class EvdevDecoderTests
{
    private static EvdevDecoder Gamepad()
    {
        var keys = new List<int> { EvCodes.BTN_SOUTH, EvCodes.BTN_EAST, EvCodes.BTN_NORTH, EvCodes.BTN_WEST,
            EvCodes.BTN_TL, EvCodes.BTN_TR, EvCodes.BTN_SELECT, EvCodes.BTN_START, EvCodes.BTN_MODE, EvCodes.BTN_THUMBL, EvCodes.BTN_THUMBR };
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_Z, EvCodes.ABS_RX, EvCodes.ABS_RY, EvCodes.ABS_RZ, EvCodes.ABS_HAT0X, EvCodes.ABS_HAT0Y };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs));
        foreach (var a in new[] { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_RX, EvCodes.ABS_RY }) d.SetAbsInfo(a, new AbsRange(-32768, 32767), 0);
        d.SetAbsInfo(EvCodes.ABS_Z, new AbsRange(0, 1023), 0);
        d.SetAbsInfo(EvCodes.ABS_RZ, new AbsRange(0, 1023), 0);
        d.SetAbsInfo(EvCodes.ABS_HAT0X, new AbsRange(-1, 1), 0);
        d.SetAbsInfo(EvCodes.ABS_HAT0Y, new AbsRange(-1, 1), 0);
        return d;
    }

    [Fact]
    public void NoData_UntilFirstCommit()
    {
        var d = Gamepad();
        Assert.False(d.HasData);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_SOUTH, 1);
        Assert.False(d.HasData);                                    // SYN_REPORT が来るまでは確定しない
        Assert.True(d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0));
        Assert.True(d.HasData);
        Assert.Equal(ControllerEngine.MaskOf("A"), d.Current.Buttons);
    }

    [Fact]
    public void Gamepad_PositionalButtonNames()
    {
        var d = Gamepad();
        d.CommitInitial();
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_EAST, 1);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_WEST, 1);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_NORTH, 1);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_START, 1);
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        ushort want = (ushort)(ControllerEngine.MaskOf("B") | ControllerEngine.MaskOf("X") | ControllerEngine.MaskOf("Y") | ControllerEngine.MaskOf("Start"));
        Assert.Equal(want, d.Current.Buttons);
    }

    [Fact]
    public void Gamepad_OverrideSwapsNames()
    {
        var d = Gamepad();
        d.GamepadOverrides[EvCodes.BTN_NORTH] = "X";
        d.GamepadOverrides[EvCodes.BTN_WEST] = "Y";
        d.CommitInitial();
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_NORTH, 1);
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal(ControllerEngine.MaskOf("X"), d.Current.Buttons);
    }

    [Fact]
    public void Gamepad_AxesAreNormalized_AndYInverted()
    {
        var d = Gamepad();
        d.CommitInitial();
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_X, 32767);
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_Y, 32767);     // Linux では下が正
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_RZ, 1023);
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal(32767, d.Current.LX);
        Assert.Equal(-32767, d.Current.LY);              // 上が正の XInput では負になる
        Assert.Equal(255, d.Current.RT);
        Assert.Equal(0, d.Current.LT);
    }

    [Fact]
    public void Hat_BecomesDpad()
    {
        var d = Gamepad();
        d.CommitInitial();
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_HAT0X, -1);
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_HAT0Y, 1);
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal((ushort)(ControllerEngine.MaskOf("DLeft") | ControllerEngine.MaskOf("DDown")), d.Current.Buttons);
    }

    [Fact]
    public void SynDropped_DiscardsUntilNextReport_AndAsksResync()
    {
        var d = Gamepad();
        d.CommitInitial();
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_DROPPED, 0);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_SOUTH, 1);              // 捨てられる
        Assert.False(d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0));
        Assert.True(d.NeedsResync);
        Assert.Equal((ushort)0, d.Current.Buttons);
    }

    [Fact]
    public void Joystick_UsesRawIndexWithPs4Order()
    {
        // 格闘ゲーム用の機体の多くは、番号だけのボタン（BTN_JOYSTICK から）で報告される
        var keys = Enumerable.Range(EvCodes.BTN_JOYSTICK, 14).ToList();
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_HAT0X, EvCodes.ABS_HAT0Y };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs));
        d.SetAbsInfo(EvCodes.ABS_X, new AbsRange(0, 255), 128);
        d.SetAbsInfo(EvCodes.ABS_Y, new AbsRange(0, 255), 128);
        d.CommitInitial();
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_JOYSTICK + 1, 1);   // 生の番号 1 は、既定で A
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_JOYSTICK + 7, 1);   // 生の番号 7 は、既定で RT
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal(ControllerEngine.MaskOf("A"), d.Current.Buttons);
        Assert.Equal(255, d.Current.RT);
        Assert.InRange(d.Current.LX, (short)-200, (short)200);  // 中央
    }

    [Fact]
    public void Joystick_DpadFromAxes()
    {
        // BUFFALO BGC-FC801 のように、十字キーを ABS_X と ABS_Y（0..255、中央 128）で報告するパッド
        var keys = Enumerable.Range(EvCodes.BTN_JOYSTICK, 8).ToList();
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs))
        {
            AxisMap = new PadAxisMap(LX: -1, LY: -1, RX: -1, RY: -1, DpadX: 0, DpadY: 1),
        };
        d.SetAbsInfo(EvCodes.ABS_X, new AbsRange(0, 255), 128);
        d.SetAbsInfo(EvCodes.ABS_Y, new AbsRange(0, 255), 128);
        d.CommitInitial();
        Assert.Equal((ushort)0, d.Current.Buttons);
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_X, 0);     // 左
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_Y, 0);     // 上
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal((ushort)(ControllerEngine.MaskOf("DLeft") | ControllerEngine.MaskOf("DUp")), d.Current.Buttons);
        Assert.Equal((short)0, d.Current.LX);          // 左のスティックは動かさない
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_X, 255);   // 右
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_Y, 255);   // 下
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal((ushort)(ControllerEngine.MaskOf("DRight") | ControllerEngine.MaskOf("DDown")), d.Current.Buttons);
    }

    [Fact]
    public void Joystick_StickIsCenteredUntilFirstReport_AndMapChangeRecomputes()
    {
        // 最初に押されるまで報告しないパッドでは、カーネルが軸を 0（範囲の端）のまま持っている
        var keys = Enumerable.Range(EvCodes.BTN_JOYSTICK, 8).ToList();
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs));
        d.SetAbsInfo(EvCodes.ABS_X, new AbsRange(0, 255), 0);
        d.SetAbsInfo(EvCodes.ABS_Y, new AbsRange(0, 255), 0);
        d.CommitInitial();
        Assert.Equal((short)0, d.Current.LX);
        Assert.Equal((short)0, d.Current.LY);
        // 割り当てを変えると、次の事象を待たずに状態が変わる
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_X, 128);
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_Y, 0);
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.NotEqual((short)0, d.Current.LY);
        d.AxisMap = new PadAxisMap(LX: -1, LY: -1, RX: -1, RY: -1, DpadX: 0, DpadY: 1);
        Assert.Equal((short)0, d.Current.LY);
        Assert.Equal(ControllerEngine.MaskOf("DUp"), d.Current.Buttons);
    }

    [Fact]
    public void ForceJoystick_ReadsGamepadCodesInOrder_WithPs4Layout()
    {
        // GP2040-CE（PS4 の形）を hid-generic で読むと、ボタンは BTN_SOUTH から14個、軸は X、Y、Z、RX、RY、RZ になる
        var keys = Enumerable.Range(EvCodes.BTN_SOUTH, 14).ToList();
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_Z, EvCodes.ABS_RX, EvCodes.ABS_RY, EvCodes.ABS_RZ };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs))
        {
            AxisMap = new PadAxisMap(LX: 0, LY: 1, RX: 2, RY: 5, LT: 3, RT: 4),
            ForceJoystick = true,
        };
        foreach (var a in new[] { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_Z, EvCodes.ABS_RZ }) d.SetAbsInfo(a, new AbsRange(0, 255), 128);
        d.SetAbsInfo(EvCodes.ABS_RX, new AbsRange(0, 255), 0);
        d.SetAbsInfo(EvCodes.ABS_RY, new AbsRange(0, 255), 0);
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_SOUTH + 1, 1);   // 2番目のボタン（PS4 の ×、下）は A
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal(ControllerEngine.MaskOf("A"), d.Current.Buttons);
        Assert.InRange(d.Current.RX, (short)-200, (short)200);   // 右スティックは中央
        Assert.InRange(d.Current.RY, (short)-200, (short)200);
        Assert.Equal(0, d.Current.LT);                            // トリガーは離した状態
        Assert.Equal(0, d.Current.RT);
    }

    [Fact]
    public void Sdl_RazerPantheraPs4_MapsPositionally()
    {
        // SDL_GameControllerDB の Linux の行。GP2040-CE の PS4 の形がこの番号で名乗る
        const string line = "03000000321500000104000011010000,Razer Panthera PS4,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,platform:Linux,";
        var db = SdlGameControllerDb.Parse(new[] { line });
        Assert.Equal(1, db.Count);
        var m = db.Find(0x03, 0x1532, 0x0401, 0x0111);
        Assert.NotNull(m);
        Assert.Equal("03000000321500000104000011010000", SdlMapping.MakeGuid(0x03, 0x1532, 0x0401, 0x0111));

        var keys = Enumerable.Range(EvCodes.BTN_SOUTH, 14).ToList();
        var abs = new List<int> { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_Z, EvCodes.ABS_RX, EvCodes.ABS_RY, EvCodes.ABS_RZ, EvCodes.ABS_HAT0X, EvCodes.ABS_HAT0Y };
        var d = new EvdevDecoder(new EvdevCaps(keys, abs)) { Sdl = m };
        foreach (var a in new[] { EvCodes.ABS_X, EvCodes.ABS_Y, EvCodes.ABS_Z, EvCodes.ABS_RZ }) d.SetAbsInfo(a, new AbsRange(0, 255), 128);
        d.SetAbsInfo(EvCodes.ABS_RX, new AbsRange(0, 255), 0);
        d.SetAbsInfo(EvCodes.ABS_RY, new AbsRange(0, 255), 0);
        d.SetAbsInfo(EvCodes.ABS_HAT0X, new AbsRange(-1, 1), 0);
        d.SetAbsInfo(EvCodes.ABS_HAT0Y, new AbsRange(-1, 1), 0);
        d.CommitInitial();
        d.Feed(EvCodes.EV_KEY, EvCodes.BTN_SOUTH + 1, 1);     // b1 は SDL の a（下）なので LLMCon の A
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_HAT0Y, -1);         // ハットの上
        d.Feed(EvCodes.EV_ABS, EvCodes.ABS_RY, 255);           // a4 は右のトリガー
        d.Feed(EvCodes.EV_SYN, EvCodes.SYN_REPORT, 0);
        Assert.Equal((ushort)(ControllerEngine.MaskOf("A") | ControllerEngine.MaskOf("DUp")), d.Current.Buttons);
        Assert.Equal(255, d.Current.RT);
        Assert.Equal(0, d.Current.LT);
        Assert.InRange(d.Current.RX, (short)-200, (short)200);
        Assert.InRange(d.Current.RY, (short)-200, (short)200);
    }

    [Fact]
    public void PadSetup_Arcade_RecordsButtonsAxesAndSkips()
    {
        var setup = PadSetup.Create("arcade")!;
        // 偽の読み取り：手順ごとに「何を押すか」を決め、最初の2回は離した状態、そのあと押した状態、そのあと離した状態を返す
        int step = -1, calls = 0;
        PadRawReading Rest() => new("x", "test", 8, 1, 2, new[] { 0.5, 0.5 }, new bool[8], "hat0x=0 hat0y=0 pressed=[]", 1);
        PadRawReading Read()
        {
            if (setup.Position != step) { step = setup.Position; calls = 0; }
            calls++;
            if (calls <= 2 || calls > 6) return Rest();
            var b = new bool[8]; var ax = new[] { 0.5, 0.5 };
            switch (setup.Current?.Target)
            {
                case "DUp": ax[1] = 0.0; break;          // 十字キーが軸で報告される機体
                case "DLeft": ax[0] = 0.0; break;
                case "X": b[3] = true; break;
                case "A": b[1] = true; break;
                case "Start": b[7] = true; break;
                default: return Rest();                // ほかは押さない（飛ばす）
            }
            return new("x", "test", 8, 1, 2, ax, b, "hat0x=0 hat0y=0 pressed=[]", 2);
        }
        while (!setup.Done) setup.WaitAndCapture(Read, setup.Current!.Target is "DUp" or "DLeft" or "X" or "A" or "Start" ? 2000 : 60);
        var prof = setup.BuildProfile(new PadHardware(0x1234, 0x5678, "test"), out _);
        Assert.True(prof.AsJoystick);
        Assert.Equal(1, prof.Axes!.DpadY);
        Assert.Equal(0, prof.Axes.DpadX);
        Assert.Contains("3=X", prof.Buttons);
        Assert.Contains("1=A", prof.Buttons);
        Assert.Contains("7=Start", prof.Buttons);
        Assert.Equal("skip", setup.Results.First(r => r.Target == "Y").Kind);
    }

    [Fact]
    public void RawButtonIndex_CoversJoystickAndHappyRanges()
    {
        Assert.Equal(0, EvdevDecoder.RawButtonIndex(EvCodes.BTN_JOYSTICK));
        Assert.Equal(15, EvdevDecoder.RawButtonIndex(EvCodes.BTN_JOYSTICK + 15));
        Assert.Equal(16, EvdevDecoder.RawButtonIndex(EvCodes.BTN_TRIGGER_HAPPY));
        Assert.Equal(-1, EvdevDecoder.RawButtonIndex(EvCodes.BTN_SOUTH));
    }
}

public class SerialFramingTests
{
    [Theory]
    [InlineData(new byte[] { }, new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x00 }, new byte[] { 0x01, 0x01 })]
    [InlineData(new byte[] { 0x11, 0x22, 0x00, 0x33 }, new byte[] { 0x03, 0x11, 0x22, 0x02, 0x33 })]
    [InlineData(new byte[] { 0x11, 0x00, 0x00, 0x00 }, new byte[] { 0x02, 0x11, 0x01, 0x01, 0x01 })]
    public void Cobs_MatchesKnownVectors(byte[] src, byte[] want)
    {
        // 符号の例は、COBS の説明（Cheshire と Baker の論文、Wikipedia の例）による
        var dst = new byte[src.Length + 2];
        int n = SerialFraming.CobsEncode(src, dst);
        Assert.Equal(want, dst[..n]);
        var back = new byte[src.Length + 2];
        int m = SerialFraming.CobsDecode(dst[..n], back);
        Assert.Equal(src, back[..m]);
    }

    [Fact]
    public void Crc8_MatchesSmbusCheckValue()
    {
        // CRC-8/SMBUS の検査の値：ASCII の "123456789" に対して 0xF4
        Assert.Equal(0xF4, SerialFraming.Crc8(System.Text.Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void WrapThenUnwrap_RoundTrips_AndHasNoZeroInside()
    {
        var s = new PadState { Buttons = 0, LT = 0, RT = 0, LX = 0, LY = 0, RX = 0, RY = 0 };   // 0 が多い状態
        var frame = new byte[PadFrame.Size];
        PadFrame.Encode(frame, s, 0, 0, 0, false);
        var wire = new byte[SerialFraming.MaxEncoded];
        int n = SerialFraming.Wrap(frame, wire);
        Assert.Equal(0, wire[n - 1]);
        Assert.DoesNotContain((byte)0, wire[..(n - 1)]);
        var back = new byte[PadFrame.Size];
        Assert.True(SerialFraming.TryUnwrap(wire[..(n - 1)], back));
        Assert.Equal(frame, back);
    }

    [Fact]
    public void Unwrap_RejectsCorruptedByte()
    {
        var frame = new byte[PadFrame.Size];
        PadFrame.Encode(frame, new PadState { Buttons = 0x1000 }, 5, 6, 7, false);
        var wire = new byte[SerialFraming.MaxEncoded];
        int n = SerialFraming.Wrap(frame, wire);
        wire[10] ^= 0x40;
        Assert.False(SerialFraming.TryUnwrap(wire[..(n - 1)], new byte[PadFrame.Size]));
    }
}
