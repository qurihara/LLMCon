using System.Collections.Concurrent;
using System.Diagnostics;
using CntlLevelConnection;
using LLMConHub;
using Xunit;

namespace LLMConHub.Tests;

/// <summary>
/// Pro コントローラの USB の手順の試験。Switch が送る報告は、本物の Pro コントローラと Switch のあいだの USB のやり取りの記録
/// （https://gist.github.com/mzyy94/60ae253a45e2759451789a117c59acf9 の bypass_procon_log.txt）から取った。
/// </summary>
public class ProconProtocolTests
{
    private static byte[] H(string hex)
    {
        var b = Convert.FromHexString(hex);
        var r = new byte[64];
        b.CopyTo(r, 0);
        return r;
    }

    private static byte[]? One(ProconProtocol p, string hex, PadState s = default)
    {
        var list = new List<byte[]>();
        p.HandleOutput(H(hex), s, list);
        Assert.True(list.Count <= 1);
        return list.Count == 0 ? null : list[0];
    }

    [Fact]
    public void Pack12_RoundTrips()
    {
        var buf = new byte[3];
        foreach (var (x, y) in new[] { (0, 0), (4095, 4095), (2048, 2048), (1, 4094), (0x5BA, 0x621) })
        {
            ProconProtocol.Pack12(x, y, buf);
            Assert.Equal((x, y), ProconProtocol.Unpack12(buf));
        }
        // 本物の記録の左のスティックの値 f3 e7 7a を、x=0x7F3、y=0x7AE と読めること
        Assert.Equal((0x7F3, 0x7AE), ProconProtocol.Unpack12(new byte[] { 0xF3, 0xE7, 0x7A }));
    }

    [Fact]
    public void StickValue_MapsCenterAndEnds()
    {
        Assert.Equal(2048, ProconProtocol.StickValue(0));
        Assert.Equal(2048 + 1800, ProconProtocol.StickValue(32767));
        Assert.Equal(2048 - 1800, ProconProtocol.StickValue(-32767));
        Assert.Equal(2048 - 1800, ProconProtocol.StickValue(-32768)); // -32768 も端に収まる
        Assert.Equal(2048 + 900, ProconProtocol.StickValue(16384));
    }

    [Fact]
    public void Buttons_ArePositionalByDefault()
    {
        var b = new byte[3];
        // LLMCon の A（下）は Switch の B（下）、B（右）は A（右）、X（左）は Y（左）、Y（上）は X（上）
        ProconProtocol.EncodeButtons(new PadState { Buttons = 0x1000 }, b, false, 127);
        Assert.Equal(ProconProtocol.R_B, b[0]);
        ProconProtocol.EncodeButtons(new PadState { Buttons = 0x2000 }, b, false, 127);
        Assert.Equal(ProconProtocol.R_A, b[0]);
        ProconProtocol.EncodeButtons(new PadState { Buttons = 0x4000 }, b, false, 127);
        Assert.Equal(ProconProtocol.R_Y, b[0]);
        ProconProtocol.EncodeButtons(new PadState { Buttons = 0x8000 }, b, false, 127);
        Assert.Equal(ProconProtocol.R_X, b[0]);
        // 名前で合わせるとき
        ProconProtocol.EncodeButtons(new PadState { Buttons = 0x1000 }, b, true, 127);
        Assert.Equal(ProconProtocol.R_A, b[0]);
    }

    [Fact]
    public void Buttons_ShoulderTriggerDpadMeta()
    {
        var b = new byte[3];
        var s = new PadState { Buttons = (ushort)(0x0100 | 0x0200 | 0x0001 | 0x0008 | 0x0010 | 0x0020 | 0x0400 | 0x0040 | 0x0080), LT = 200, RT = 128 };
        ProconProtocol.EncodeButtons(s, b, false, 127);
        Assert.Equal(ProconProtocol.R_R | ProconProtocol.R_ZR, b[0]);
        Assert.Equal(ProconProtocol.S_ChargingGrip | ProconProtocol.S_Plus | ProconProtocol.S_Minus | ProconProtocol.S_Home
                     | ProconProtocol.S_LStick | ProconProtocol.S_RStick, b[1]);
        Assert.Equal(ProconProtocol.L_L | ProconProtocol.L_ZL | ProconProtocol.L_Up | ProconProtocol.L_Right, b[2]);
        // しきい値ちょうどは押していない
        ProconProtocol.EncodeButtons(new PadState { LT = 127, RT = 127 }, b, false, 127);
        Assert.Equal(0, b[0]);
        Assert.Equal(0, b[2]);
        Assert.Equal(ProconProtocol.S_ChargingGrip, b[1]);
    }

    [Fact]
    public void Handshake_MatchesRecordedSequence()
    {
        var p = new ProconProtocol();
        Assert.Null(One(p, "0000"));
        Assert.Null(One(p, "8005"));
        Assert.False(p.Streaming);

        var r = One(p, "8001")!;
        Assert.Equal(new byte[] { 0x81, 0x01, 0x00, 0x03 }, r[..4]);
        Assert.True(p.HandshakeSeen);

        r = One(p, "8002")!;
        Assert.Equal(new byte[] { 0x81, 0x02 }, r[..2]);
        Assert.All(r[2..], x => Assert.Equal(0, x));

        r = One(p, "01000000000000000000033000")!;   // サブコマンド 03 30（入力の報告を 0x30 の形式にする）
        Assert.Equal(0x21, r[0]);
        Assert.Equal(ProconProtocol.BatteryAndConnection, r[2]);
        Assert.Equal(0x80, r[13]);
        Assert.Equal(0x03, r[14]);

        Assert.Null(One(p, "8004"));
        Assert.True(p.Streaming);
        Assert.Equal(0, p.UnknownReports);
    }

    [Fact]
    public void Subcommands_ReplyLikeRealController()
    {
        var p = new ProconProtocol();

        var r = One(p, "01080000000000000000480000")!;   // 0x48 振動を有効にする
        Assert.Equal((byte)0x80, r[13]); Assert.Equal((byte)0x48, r[14]);

        r = One(p, "010a0000000000000000020000")!;       // 0x02 機器の情報
        Assert.Equal((byte)0x82, r[13]); Assert.Equal((byte)0x02, r[14]);
        Assert.Equal(Convert.FromHexString("03480302"), r[15..19]);
        Assert.Equal(p.Mac.Reverse().ToArray(), r[19..25]);
        Assert.Equal(new byte[] { 0x03, 0x01 }, r[25..27]);

        r = One(p, "010e0000000000000000010418")!;       // 0x01 手動のペアリング
        Assert.Equal((byte)0x81, r[13]); Assert.Equal((byte)0x01, r[14]); Assert.Equal((byte)0x03, r[15]);

        r = One(p, "01000000000000000000040000")!;       // 0x04 トリガーの時間
        Assert.Equal((byte)0x83, r[13]); Assert.Equal((byte)0x04, r[14]);

        r = One(p, "01060000000000000000400100")!;       // 0x40 6軸のセンサを有効にする
        Assert.Equal((byte)0x80, r[13]); Assert.Equal((byte)0x40, r[14]);

        r = One(p, "01000000000000000000300100")!;       // 0x30 プレイヤーの LED
        Assert.Equal((byte)0x80, r[13]); Assert.Equal((byte)0x30, r[14]);

        // 0x21 MCU の設定。本物の記録の返事と、15 バイト目から 48 バイト目まで（CRC を含む）が一致すること
        r = One(p, "010ec218037200014040212100")!;
        var recorded = Convert.FromHexString("218691008000422cc2e0b77e00a0210100ff0003000501000000000000000000000000000000000000000000000000005c000000000000000000000000000000");
        Assert.Equal(recorded[13..49], r[13..49]);

        Assert.Null(One(p, "10070001404000014040"));   // 振動のデータだけの報告には返事をしない
        Assert.Equal(0, p.UnknownReports);
    }

    [Theory]
    // 番地（小さい桁が先）と長さ、本物の記録で返ってきたデータ
    [InlineData("0060000010", "ffffffffffffffffffffffffffffffff")]
    [InlineData("506000000d", "323232ffffffffffffffffffff")]
    [InlineData("8060000018", "50fd0000c60f0f30619630f3d41454411554c7799c333663")]
    [InlineData("9860000012", "0f30619630f3d41454411554c7799c333663")]
    [InlineData("1080000018", "ffffffffffffffffffffffffffffffffffffffffffffb2a1")]
    [InlineData("2880000018", "beff3e00f001004000400040fefffeff0800e73be73be73b")]
    public void SpiRead_ReturnsRecordedContents(string args, string expected)
    {
        var p = new ProconProtocol();
        var r = One(p, "01010000000000000000" + "10" + args)!;
        Assert.Equal((byte)0x90, r[13]);
        Assert.Equal((byte)0x10, r[14]);
        Assert.Equal(Convert.FromHexString(args), r[15..20]);
        var exp = Convert.FromHexString(expected);
        Assert.Equal(exp, r[20..(20 + exp.Length)]);
    }

    [Fact]
    public void SpiStickCalibration_DecodesToCenterAndRange()
    {
        var p = new ProconProtocol();
        var r = One(p, "01040000000000000000103d60000019")!;
        var d = r[20..45];
        // 左：上の幅、中心、下の幅
        Assert.Equal((1800, 1800), ProconProtocol.Unpack12(d[0..3]));
        Assert.Equal((2048, 2048), ProconProtocol.Unpack12(d[3..6]));
        Assert.Equal((1800, 1800), ProconProtocol.Unpack12(d[6..9]));
        // 右：中心、下の幅、上の幅
        Assert.Equal((2048, 2048), ProconProtocol.Unpack12(d[9..12]));
        Assert.Equal((1800, 1800), ProconProtocol.Unpack12(d[12..15]));
        Assert.Equal((1800, 1800), ProconProtocol.Unpack12(d[15..18]));
        // 続く 0xFF と、本体とボタンの色
        Assert.Equal(Convert.FromHexString("ff323232ffffff"), d[18..25]);
    }

    [Fact]
    public void InputReport_CarriesState()
    {
        var p = new ProconProtocol();
        var r = new byte[64];
        p.BuildInputReport(new PadState { Buttons = 0x1000, LX = 32767, LY = -32767, RX = 0, RY = 16384 }, r);
        Assert.Equal(0x30, r[0]);
        Assert.Equal(ProconProtocol.BatteryAndConnection, r[2]);
        Assert.Equal(ProconProtocol.R_B, r[3]);
        Assert.Equal((2048 + 1800, 2048 - 1800), ProconProtocol.Unpack12(r.AsSpan(6, 3)));
        Assert.Equal((2048, 2048 + 900), ProconProtocol.Unpack12(r.AsSpan(9, 3)));
        byte t0 = r[1];
        p.BuildInputReport(default, r);
        Assert.Equal((byte)(t0 + 1), r[1]); // 時刻の欄が進む
    }
}

/// <summary>試験用の偽の USB の口。Switch の役を試験が受け持つ。</summary>
internal sealed class FakeHidEndpoint : IHidEndpoint
{
    public readonly BlockingCollection<byte[]> ToDevice = new();
    public readonly BlockingCollection<(long ticks, byte[] report)> FromDevice = new();

    public int Read(Span<byte> buf, int timeoutMs)
    {
        if (!ToDevice.TryTake(out var r, timeoutMs)) return 0;
        r.CopyTo(buf);
        return r.Length;
    }

    public bool Write(ReadOnlySpan<byte> report, int timeoutMs)
    {
        FromDevice.Add((Stopwatch.GetTimestamp(), report.ToArray()));
        return true;
    }

    public void Dispose() { }

    public byte[] Expect(Func<byte[], bool> pred, int timeoutMs = 1000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (FromDevice.TryTake(out var x, 50) && pred(x.report)) return x.report;
        }
        throw new Xunit.Sdk.XunitException("期待した報告が届きませんでした。");
    }
}

public class ProconUsbSinkTests
{
    private static byte[] H(string hex) { var r = new byte[64]; Convert.FromHexString(hex).CopyTo(r, 0); return r; }

    [Fact]
    public void Sink_AnswersHandshake_ThenStreamsState()
    {
        var ep = new FakeHidEndpoint();
        using var sink = new ProconUsbSink(() => ep, "fake");
        Assert.True(sink.Start());

        // 手順の前は、0x30 を送らない
        Thread.Sleep(50);
        Assert.False(ep.FromDevice.TryTake(out _, 0));

        ep.ToDevice.Add(H("8001"));
        ep.Expect(r => r[0] == 0x81 && r[1] == 0x01);
        ep.ToDevice.Add(H("8002"));
        ep.Expect(r => r[0] == 0x81 && r[1] == 0x02);
        ep.ToDevice.Add(H("8004"));
        ep.Expect(r => r[0] == 0x30);
        Assert.True(sink.Connected);

        // A を押すと、位置で合わせた Switch の B が立った 0x30 が届く
        long t0 = Stopwatch.GetTimestamp();
        sink.Write(new PadState { Buttons = 0x1000 });
        var got = ep.Expect(r => r[0] == 0x30 && (r[3] & ProconProtocol.R_B) != 0);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Assert.True(ms < 50, $"変化の報告まで {ms:F2} ミリ秒かかった");

        // サブコマンドへの返事は、いまの状態を載せる
        ep.ToDevice.Add(H("01000000000000000000300100"));
        var reply = ep.Expect(r => r[0] == 0x21 && r[14] == 0x30);
        Assert.Equal(ProconProtocol.R_B, reply[3]);
    }

    [Fact]
    public void Sink_SendsPeriodicReportsWhenIdle()
    {
        var ep = new FakeHidEndpoint();
        using var sink = new ProconUsbSink(() => ep, "fake");
        sink.Start();
        ep.ToDevice.Add(H("8004"));
        ep.Expect(r => r[0] == 0x30);
        while (ep.FromDevice.TryTake(out _)) { }
        Thread.Sleep(200);
        int n = 0;
        while (ep.FromDevice.TryTake(out var x)) if (x.report[0] == 0x30) n++;
        // 8ミリ秒ごとなら 200ミリ秒で約25回。機械の負荷で間引かれても、少なくとも 10 回は届くこと
        Assert.InRange(n, 10, 40);
    }
}
