using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// シリアルの線で PadFrame を送るための区切りの付け方。
///
/// UDP と違い、シリアルの線はバイトの列が切れ目なく流れるので、電文の切れ目を受け手が見つけられる必要がある。
/// そこで、電文（36バイト）の後ろに CRC-8 を1バイト付け、全体を COBS（Consistent Overhead Byte Stuffing）で
/// 0 を含まない列に変え、最後に 0 を1バイト置く。受け手は 0 で区切り、COBS を戻し、CRC-8 を確かめる。
/// 線の途中で受け始めても、次の 0 から正しく読める。COBS も CRC-8 も、マイコンの C で数十行で書ける。
///
/// CRC-8 の多項式は 0x07（初期値 0、反転なし。CRC-8/SMBUS と同じ）である。
/// </summary>
public static class SerialFraming
{
    /// <summary>区切りを付けた電文の最大の長さ（COBS で1バイト、CRC で1バイト、区切りで1バイト増える）。</summary>
    public const int MaxEncoded = PadFrame.Size + 3;

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }

    /// <summary>COBS で符号化する。戻り値は書いた長さ（区切りの 0 は含まない）。</summary>
    public static int CobsEncode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int read = 0, write = 1, codeIndex = 0;
        byte code = 1;
        while (read < src.Length)
        {
            if (src[read] == 0)
            {
                dst[codeIndex] = code;
                code = 1;
                codeIndex = write++;
                read++;
            }
            else
            {
                dst[write++] = src[read++];
                code++;
                if (code == 0xFF)
                {
                    dst[codeIndex] = code;
                    code = 1;
                    codeIndex = write++;
                }
            }
        }
        dst[codeIndex] = code;
        return write;
    }

    /// <summary>COBS を戻す。形が壊れていれば -1 を返す。</summary>
    public static int CobsDecode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int read = 0, write = 0;
        while (read < src.Length)
        {
            byte code = src[read++];
            if (code == 0) return -1;
            for (int i = 1; i < code; i++)
            {
                if (read >= src.Length) return -1;
                dst[write++] = src[read++];
            }
            if (code < 0xFF && read < src.Length) dst[write++] = 0;
        }
        return write;
    }

    /// <summary>電文を、区切りを付けた形にする。戻り値は書いた長さ（最後の 0 を含む）。</summary>
    public static int Wrap(ReadOnlySpan<byte> frame, Span<byte> dst)
    {
        Span<byte> tmp = stackalloc byte[PadFrame.Size + 1];
        frame.Slice(0, PadFrame.Size).CopyTo(tmp);
        tmp[PadFrame.Size] = Crc8(frame.Slice(0, PadFrame.Size));
        int n = CobsEncode(tmp, dst);
        dst[n] = 0;
        return n + 1;
    }

    /// <summary>区切りの 0 を除いた1つの塊から、電文を取り出す。CRC が合わなければ false。</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> chunk, Span<byte> frame)
    {
        Span<byte> tmp = stackalloc byte[PadFrame.Size + 8];
        if (chunk.Length > tmp.Length + 2) return false;
        int n = CobsDecode(chunk, tmp);
        if (n != PadFrame.Size + 1) return false;
        if (Crc8(tmp.Slice(0, PadFrame.Size)) != tmp[PadFrame.Size]) return false;
        tmp.Slice(0, PadFrame.Size).CopyTo(frame);
        return true;
    }
}

/// <summary>
/// 確定したコントローラの状態を、USB のシリアル（Pico や ESP32 の USB CDC）で、出力用の機器へ送る出力先。
///
/// 出力用の機器（Bluetooth で Switch 2 につなぐ ESP32、USB でゲーム機につなぐ Pico など）は、
/// 受け取った状態を、そのままコントローラの報告として出す。LLMCon のコアは、Raspberry Pi か PC に残る。
///
/// シリアルへの書き込みは、相手の受け取りが遅れると待たされることがある。エンジンのループを待たせないために、
/// Write は最新の状態を1つの置き場に入れて合図するだけにし、専用のスレッドが書き込む。
/// 書き込みが追いつかないときは、途中の状態を飛ばして最新の状態を送る（コントローラの状態は、最新だけに意味があるため）。
/// 変化が無いあいだも、100ミリ秒に1回だけ同じ状態を送る。
///
/// 線の設定（生のモード、エコーなし）は、開く前に stty で行う。USB CDC では通信の速さの設定は意味を持たないが、
/// 本物のシリアルの線（UART）の場合のために、速さを指定できるようにしてある。
///
/// この実装は、2026-10-30 の時点で、mac の擬似端末（pty）でしか確かめていない。実機の機器では未確認である。
/// </summary>
public sealed class SerialFrameSink : IPadSink
{
    private readonly string _path;
    private readonly int _baud;
    private readonly IInputTimestamp? _inputClock;
    private FileStream? _stream;
    private Thread? _writer;
    private volatile bool _running;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _slotLock = new();
    private PadState _slot;
    private bool _slotDirty;
    private PadState _last;
    private bool _hasLast;

    /// <summary>送った電文の数。</summary>
    public long Sent { get; private set; }

    /// <summary>書き込みに失敗した回数。</summary>
    public long SendErrors { get; private set; }

    /// <summary>新しい状態を、送る前に次の状態で上書きした回数（書き込みが追いつかなかった回数）。</summary>
    public long Coalesced { get; private set; }

    public SerialFrameSink(string path, int baud = 115200, IInputTimestamp? inputClock = null)
    {
        _path = path;
        _baud = baud;
        _inputClock = inputClock;
    }

    /// <summary>"/dev/ttyACM0" または "/dev/ttyACM0@921600" のような指定から作る。</summary>
    public static SerialFrameSink FromSpec(string spec, IInputTimestamp? inputClock = null)
    {
        var t = spec.Trim();
        int at = t.LastIndexOf('@');
        if (at > 0 && int.TryParse(t[(at + 1)..], out var baud)) return new SerialFrameSink(t[..at], baud, inputClock);
        return new SerialFrameSink(t, 115200, inputClock);
    }

    public string Name => $"serial({_path})";
    public bool Connected => _stream != null;
    public string? Error { get; private set; }

    public bool Start()
    {
        try
        {
            ConfigureLine(_path, _baud);
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        _running = true;
        _writer = new Thread(WriteLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "SerialFrameWriter" };
        _writer.Start();
        return true;
    }

    /// <summary>stty で線を生のモードにする。Linux は -F、mac は -f で端末を指定する。</summary>
    private static void ConfigureLine(string path, int baud)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
        var flag = OperatingSystem.IsLinux() ? "-F" : "-f";
        var psi = new ProcessStartInfo("stty", $"{flag} {path} raw -echo {baud}") { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit(3000);
        if (p.ExitCode != 0) throw new IOException($"stty が失敗しました：{p.StandardError.ReadToEnd().Trim()}");
    }

    public void Write(in PadState s)
    {
        if (!_running) return;
        bool changed = !_hasLast || !Same(s, _last);
        if (!changed) return;          // 変化が無いときの定期の送信は、書き込みのスレッドが受け持つ
        _last = s; _hasLast = true;
        lock (_slotLock)
        {
            if (_slotDirty) Coalesced++;
            _slot = s;
            _slotDirty = true;
        }
        _signal.Set();
    }

    private void WriteLoop()
    {
        var frame = new byte[PadFrame.Size];
        var wire = new byte[SerialFraming.MaxEncoded];
        uint seq = 0;
        PadState current = default;
        bool have = false;
        while (_running)
        {
            _signal.WaitOne(100);      // 合図が無くても100ミリ秒で起き、定期の電文を送る
            if (!_running) break;
            bool changed = false;
            lock (_slotLock)
            {
                if (_slotDirty) { current = _slot; _slotDirty = false; changed = true; have = true; }
            }
            if (!have) continue;
            ulong now = PadFrame.NowMicros();
            PadFrame.Encode(frame, current, unchecked(++seq), now, _inputClock?.LastInputMicros ?? 0, heartbeat: !changed);
            int n = SerialFraming.Wrap(frame, wire);
            try
            {
                _stream!.Write(wire, 0, n);
                _stream.Flush();
                Sent++;
            }
            catch (Exception)
            {
                SendErrors++;
            }
        }
    }

    private static bool Same(in PadState a, in PadState b)
        => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _writer?.Join(500);
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _stream = null;
    }
}
