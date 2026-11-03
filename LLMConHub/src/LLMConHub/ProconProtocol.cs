using System;
using System.Collections.Generic;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// Nintendo Switch の Pro コントローラを USB の有線でつないだときの手順を、入出力から切り離して組み立てる部品。
///
/// Raspberry Pi の USB の機器の機能（Linux の USB gadget の HID の機能、/dev/hidg0）で Pro コントローラを名乗るときに、
/// Switch から届く報告（出力の報告）に答える報告と、ボタンとスティックの状態を載せた報告（入力の報告 0x30）を作る。
/// 入出力（/dev/hidg0 の読み書き）は ProconUsbSink が受け持つ。この部品は、バイトの列からバイトの列を作るだけなので、
/// mac の上の試験で、本物の Pro コントローラのやり取りの記録を流して確かめられる。
///
/// 手順の根拠は次の2つである。
///   dekuNukem の解析資料（USB-HID-Notes.md、bluetooth_hid_notes.md、bluetooth_hid_subcommands_notes.md、spi_flash_notes.md）
///     https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering
///   本物の Pro コントローラと Switch のあいだの USB のやり取りの記録（nscon の作者の gist の bypass_procon_log.txt）
///     https://gist.github.com/mzyy94/60ae253a45e2759451789a117c59acf9
/// コードは写さず、記録されたバイトの意味を読んで書き起こした。SPI のフラッシュの内容のうち、
/// スティックの較正の値だけは、LLMCon の軸の値を正確に写せるように、この部品で決めた値にしてある。
///
/// 報告の形（すべて64バイト）：
///   Switch → 機器：0x80 xx（USB の手順の命令）、0x01（振動のデータとサブコマンド）、0x10（振動のデータだけ）
///   機器 → Switch：0x81 xx（USB の手順の命令への返事）、0x21（サブコマンドへの返事）、0x30（定期の入力の報告）
/// </summary>
public sealed class ProconProtocol
{
    public const int ReportLength = 64;

    /// <summary>0x80 0x04 を受け取ってから、0x30 の定期の入力の報告を送り続ける。</summary>
    public bool Streaming { get; private set; }

    /// <summary>Switch が手順を始めたか（0x80 0x01 を受け取ったか）。</summary>
    public bool HandshakeSeen { get; private set; }

    /// <summary>受け取ったが意味を知らない報告の数（記録用）。</summary>
    public long UnknownReports { get; private set; }

    /// <summary>true のとき、ボタンを名前で合わせる（LLMCon の A を Switch の A にする）。既定は位置で合わせる。</summary>
    public bool ByLabel { get; init; }

    /// <summary>LT と RT を、この値より大きいときに ZL と ZR の押下とみなす（橋渡しのプログラムと同じ 127）。</summary>
    public byte TriggerThreshold { get; init; } = 127;

    /// <summary>機器の MAC アドレス（Switch が機器を見分けるのに使う）。0x80 0x01 の返事に載せる順で持つ。</summary>
    public byte[] Mac { get; init; } = { 0x4C, 0x4C, 0x4D, 0x43, 0x42, 0x54 }; // "LLMCBT" の文字の番号。本物の機器とは重ならない

    private byte _timer;

    // ---- 入力の報告に載せる値 ----------------------------------------------------------------

    /// <summary>電池と接続の状態（上位4ビットが電池の残量で 8 が満充電、+1 が充電中。下位4ビットの 1 は USB で給電中）。本物の記録の値。</summary>
    public const byte BatteryAndConnection = 0x91;

    // 右のボタンの1バイト目
    public const byte R_Y = 0x01, R_X = 0x02, R_B = 0x04, R_A = 0x08, R_SR = 0x10, R_SL = 0x20, R_R = 0x40, R_ZR = 0x80;
    // 共通のボタンの2バイト目
    public const byte S_Minus = 0x01, S_Plus = 0x02, S_RStick = 0x04, S_LStick = 0x08, S_Home = 0x10, S_Capture = 0x20, S_ChargingGrip = 0x80;
    // 左のボタンの3バイト目
    public const byte L_Down = 0x01, L_Up = 0x02, L_Right = 0x04, L_Left = 0x08, L_SR = 0x10, L_SL = 0x20, L_L = 0x40, L_ZL = 0x80;

    /// <summary>スティックの中心と、中心から端までの幅（12ビットの値）。SPI のフラッシュの較正の値と揃えてある。</summary>
    public const int StickCenter = 2048, StickRange = 1800;

    /// <summary>LLMCon の状態から、ボタンの3バイトを作る。</summary>
    public static void EncodeButtons(in PadState s, Span<byte> dst, bool byLabel, byte triggerThreshold)
    {
        ushort b = s.Buttons;
        bool Has(ushort bit) => (b & bit) != 0;
        byte right = 0, shared = S_ChargingGrip, left = 0;

        // 顔のボタン。XInput の並び（A が下、B が右、X が左、Y が上）を、Switch の並び（B が下、A が右、Y が左、X が上）へ。
        bool xa = Has(0x1000), xb = Has(0x2000), xx = Has(0x4000), xy = Has(0x8000);
        if (byLabel)
        {
            if (xa) right |= R_A; if (xb) right |= R_B; if (xx) right |= R_X; if (xy) right |= R_Y;
        }
        else
        {
            if (xa) right |= R_B; if (xb) right |= R_A; if (xx) right |= R_Y; if (xy) right |= R_X;
        }
        if (Has(0x0200)) right |= R_R;
        if (s.RT > triggerThreshold) right |= R_ZR;
        if (Has(0x0100)) left |= L_L;
        if (s.LT > triggerThreshold) left |= L_ZL;
        if (Has(0x0001)) left |= L_Up;
        if (Has(0x0002)) left |= L_Down;
        if (Has(0x0004)) left |= L_Left;
        if (Has(0x0008)) left |= L_Right;
        if (Has(0x0010)) shared |= S_Plus;
        if (Has(0x0020)) shared |= S_Minus;
        if (Has(0x0400)) shared |= S_Home;
        if (Has(0x0040)) shared |= S_LStick;
        if (Has(0x0080)) shared |= S_RStick;

        dst[0] = right; dst[1] = shared; dst[2] = left;
    }

    /// <summary>LLMCon の軸の値（-32768 から 32767、上と右が正）を、Switch の12ビットの値にする。</summary>
    public static int StickValue(short v)
    {
        int x = StickCenter + (int)Math.Round(v * (double)StickRange / 32767.0);
        return Math.Clamp(x, 0, 4095);
    }

    /// <summary>12ビットの値の組（x, y）を3バイトに詰める。</summary>
    public static void Pack12(int x, int y, Span<byte> dst)
    {
        dst[0] = (byte)(x & 0xFF);
        dst[1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        dst[2] = (byte)((y >> 4) & 0xFF);
    }

    /// <summary>3バイトから12ビットの値の組を取り出す（試験と記録用）。</summary>
    public static (int x, int y) Unpack12(ReadOnlySpan<byte> src)
        => (src[0] | ((src[1] & 0x0F) << 8), (src[1] >> 4) | (src[2] << 4));

    /// <summary>入力の報告の共通の部分（2バイト目から12バイト目）を書く。</summary>
    private void WriteStandard(Span<byte> r, in PadState s)
    {
        r[1] = _timer;
        _timer = unchecked((byte)(_timer + 1));
        r[2] = BatteryAndConnection;
        EncodeButtons(s, r.Slice(3, 3), ByLabel, TriggerThreshold);
        Pack12(StickValue(s.LX), StickValue(s.LY), r.Slice(6, 3));
        Pack12(StickValue(s.RX), StickValue(s.RY), r.Slice(9, 3));
        r[12] = 0x00; // 振動の機構の状態
    }

    /// <summary>0x30 の定期の入力の報告を作る。6軸のセンサの値は 0 にする（LLMCon は扱わない）。</summary>
    public void BuildInputReport(in PadState s, Span<byte> r)
    {
        r.Slice(0, ReportLength).Clear();
        r[0] = 0x30;
        WriteStandard(r, s);
    }

    // ---- Switch からの報告に答える ----------------------------------------------------------

    /// <summary>
    /// Switch から届いた1つの報告に答える。返事の報告を replies に足す（返事の要らない報告では何も足さない）。
    /// s は、0x21 の返事に載せるいまの状態である。
    /// </summary>
    public void HandleOutput(ReadOnlySpan<byte> report, in PadState s, List<byte[]> replies)
    {
        if (report.Length < 2) { if (report.Length > 0 && report[0] != 0) UnknownReports++; return; }
        switch (report[0])
        {
            case 0x80:
                HandleUsbCommand(report[1], replies);
                return;
            case 0x01:
                if (report.Length < 11) { UnknownReports++; return; }
                HandleSubcommand(report, s, replies);
                return;
            case 0x10:
                return; // 振動のデータだけ。LLMCon は振動を扱わないので、読み捨てる
            case 0x00:
                return; // 接続の直後に届く 00 00。返事は要らない
            default:
                UnknownReports++;
                return;
        }
    }

    private void HandleUsbCommand(byte cmd, List<byte[]> replies)
    {
        switch (cmd)
        {
            case 0x01: // 状態の問い合わせ。機器の種別（03 = Pro コントローラ）と MAC アドレスを返す
            {
                HandshakeSeen = true;
                var r = NewReport(0x81);
                r[1] = 0x01; r[2] = 0x00; r[3] = 0x03;
                Mac.CopyTo(r, 4);
                replies.Add(r);
                return;
            }
            case 0x02: // 握手
            case 0x03: // 通信の速さを 3Mbps に変える（USB では形だけ）
            {
                var r = NewReport(0x81);
                r[1] = cmd;
                replies.Add(r);
                return;
            }
            case 0x04: // 以後は HID だけで通信し、時間切れで切らない。0x30 の定期の報告を始める
                Streaming = true;
                return;
            case 0x05: // HID だけの通信をやめる
                Streaming = false;
                return;
            default:
                UnknownReports++;
                return;
        }
    }

    private void HandleSubcommand(ReadOnlySpan<byte> report, in PadState s, List<byte[]> replies)
    {
        byte sub = report[10];
        ReadOnlySpan<byte> args = report.Slice(11);
        var r = NewReport(0x21);
        WriteStandard(r, s);
        r[14] = sub;
        var data = r.AsSpan(15);
        switch (sub)
        {
            case 0x01: // Bluetooth の手動のペアリング
                r[13] = 0x81; data[0] = 0x03;
                break;
            case 0x02: // 機器の情報：ファームウェアの版 3.48、種別 03（Pro コントローラ）、02、MAC（逆の順）、03、色を SPI から読む 01
                r[13] = 0x82;
                data[0] = 0x03; data[1] = 0x48; data[2] = 0x03; data[3] = 0x02;
                for (int i = 0; i < 6; i++) data[4 + i] = Mac[5 - i];
                data[10] = 0x03; data[11] = 0x01;
                break;
            case 0x04: // トリガーのボタンが押されていた時間。すべて 0 を返す
                r[13] = 0x83;
                break;
            case 0x10: // SPI のフラッシュを読む。引数は番地（4バイト、小さい桁が先）と長さ（1バイト）
            {
                if (args.Length < 5) { UnknownReports++; return; }
                uint addr = (uint)(args[0] | (args[1] << 8) | (args[2] << 16) | (args[3] << 24));
                int len = Math.Min((int)args[4], 0x1D);
                r[13] = 0x90;
                args.Slice(0, 5).CopyTo(data);
                SpiFlash.Read(addr, data.Slice(5, len));
                break;
            }
            case 0x21: // NFC と IR の MCU の設定。本物の記録と同じ返事を返す
                r[13] = 0xA0;
                McuConfigReply.CopyTo(data);
                r[48] = Crc8Mcu(r.AsSpan(15, 33));
                break;
            default: // 0x03 入力の報告の形式、0x08 低電力、0x30 プレイヤーの LED、0x38 HOME の LED、0x40 6軸のセンサ、0x48 振動、など
                r[13] = 0x80;
                break;
        }
        replies.Add(r);
    }

    /// <summary>本物の記録で、サブコマンド 0x21 への返事の 15 バイト目から並んでいた値。</summary>
    private static readonly byte[] McuConfigReply = { 0x01, 0x00, 0xFF, 0x00, 0x03, 0x00, 0x05, 0x01 };

    /// <summary>MCU の返事の CRC-8（多項式 0x07、初期値 0）。本物の記録の 0x5C と一致することを試験で確かめている。</summary>
    public static byte Crc8Mcu(ReadOnlySpan<byte> data) => SerialFraming.Crc8(data);

    private static byte[] NewReport(byte id)
    {
        var r = new byte[ReportLength];
        r[0] = id;
        return r;
    }
}

/// <summary>
/// Pro コントローラの SPI のフラッシュの内容のうち、Switch が読みに来る部分。ここに無い番地は 0xFF を返す（何も書かれていない状態）。
/// </summary>
public static class SpiFlash
{
    private static readonly SortedDictionary<uint, byte[]> Regions = Build();

    private static SortedDictionary<uint, byte[]> Build()
    {
        var d = new SortedDictionary<uint, byte[]>();
        // 0x6000 製造番号（16バイト）は 0xFF のまま（製造番号なし）。

        // 0x603D 工場のスティックの較正（左9バイト、右9バイト）。LLMCon の軸を正確に写せるように、中心 2048、幅 1800 にした。
        // 左は「中心から上の幅、中心、中心から下の幅」、右は「中心、中心から下の幅、中心から上の幅」の順である。
        var cal = new byte[18];
        ProconProtocol.Pack12(ProconProtocol.StickRange, ProconProtocol.StickRange, cal.AsSpan(0, 3));
        ProconProtocol.Pack12(ProconProtocol.StickCenter, ProconProtocol.StickCenter, cal.AsSpan(3, 3));
        ProconProtocol.Pack12(ProconProtocol.StickRange, ProconProtocol.StickRange, cal.AsSpan(6, 3));
        ProconProtocol.Pack12(ProconProtocol.StickCenter, ProconProtocol.StickCenter, cal.AsSpan(9, 3));
        ProconProtocol.Pack12(ProconProtocol.StickRange, ProconProtocol.StickRange, cal.AsSpan(12, 3));
        ProconProtocol.Pack12(ProconProtocol.StickRange, ProconProtocol.StickRange, cal.AsSpan(15, 3));
        d[0x603D] = cal;
        d[0x604F] = new byte[] { 0xFF };
        // 0x6050 本体の色、ボタンの色、左右の握りの色（本物の記録の値。本体は濃い灰色、ボタンは白）
        d[0x6050] = Hex("323232ffffffffffffffffffff");
        // 0x6080 6軸のセンサの水平の補正と、スティックの不感帯などの値（本物の記録の値）
        d[0x6080] = Hex("50fd0000c60f0f30619630f3d41454411554c7799c333663");
        // 0x6098 右のスティックの不感帯などの値（本物の記録の値）
        d[0x6098] = Hex("0f30619630f3d41454411554c7799c333663");
        // 0x8010 利用者のスティックの較正は無し（0xFF のまま）。0x8026 の B2 A1 は、利用者の6軸の較正があるという印（本物の記録の値）
        d[0x8026] = Hex("b2a1");
        d[0x8028] = Hex("beff3e00f001004000400040fefffeff0800e73be73be73b");
        return d;
    }

    public static void Read(uint addr, Span<byte> dst)
    {
        dst.Fill(0xFF);
        foreach (var (start, bytes) in Regions)
        {
            long lo = Math.Max(start, addr), hi = Math.Min(start + (long)bytes.Length, addr + (long)dst.Length);
            for (long a = lo; a < hi; a++) dst[(int)(a - addr)] = bytes[a - start];
        }
    }

    private static byte[] Hex(string h) => Convert.FromHexString(h);
}
