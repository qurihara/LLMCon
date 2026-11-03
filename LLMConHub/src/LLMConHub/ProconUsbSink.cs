using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>USB の HID の機器の口（Linux の /dev/hidg0 か、試験の偽物）を読み書きする境界。</summary>
public interface IHidEndpoint : IDisposable
{
    /// <summary>Switch から届いた報告を1つ読む。timeoutMs のあいだに届かなければ 0 を返す。</summary>
    int Read(Span<byte> buf, int timeoutMs);

    /// <summary>報告を1つ送る。timeoutMs のあいだに送れなければ false を返す。</summary>
    bool Write(ReadOnlySpan<byte> report, int timeoutMs);
}

/// <summary>
/// 確定したコントローラの状態を、Switch の有線の Pro コントローラとして USB で出力する出力先（IPadSink の5つ目の実装）。
///
/// Raspberry Pi の USB-C の口（Pi 4）か USB の OTG の口（Zero 2 W）を、Linux の USB gadget で Pro コントローラの機器にし、
/// その口の /dev/hidg0 を読み書きする。機器の作り方は pi/procon_gadget.sh にある。
///
/// 2つのスレッドで動く。
///   読み取りのスレッドは、Switch から届く報告を読み、ProconProtocol で返事を作って送り待ちの列に入れる。
///   書き込みのスレッドは、返事を先に送り、Switch が 0x80 0x04 を送ってきたあとは、0x30 の入力の報告を
///   状態が変わった時点と、変化が無くても 8ミリ秒ごとに送る。
/// Write（エンジンのループから毎周呼ばれる）は、最新の状態を置き場に入れて合図するだけにしてある。
///
/// 2026-10-31 の時点で、mac の上の試験（本物の記録を流す試験）でしか確かめていない。実機の Switch では未確認である。
/// </summary>
public sealed class ProconUsbSink : IPadSink
{
    private readonly Func<IHidEndpoint> _open;
    private readonly string _label;
    private IHidEndpoint? _ep;
    private readonly ProconProtocol _proto;
    private readonly object _stateLock = new();
    private PadState _state;
    private PadState _last;
    private bool _hasLast;
    private readonly ConcurrentQueue<byte[]> _replies = new();
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _running;
    private volatile bool _dirty;
    private Thread? _reader, _writer;

    /// <summary>定期の入力の報告の間隔（ミリ秒）。本物の Pro コントローラの USB の報告の間隔に合わせた。</summary>
    public int IntervalMs { get; init; } = 8;

    /// <summary>物理のパッドの入力の時刻を教えるもの。遅延の記録に使う。null なら入力の時刻は 0 と記録する。</summary>
    public IInputTimestamp? InputClock { get; init; }

    /// <summary>
    /// 遅延の記録の CSV の場所（環境変数 LLMCONHUB_USB_LOG）。null なら記録しない。
    /// 状態が変わった入力の報告を書き終えるたびに、input_us（入力の事象の時刻）、written_us（/dev/hidg0 へ書き終えた時刻）、
    /// buttons を1行書く。どちらも PadFrame.NowMicros と同じ時計（CLOCK_MONOTONIC）である。
    /// </summary>
    public string? LatencyLogPath { get; init; }

    public long InputReportsSent => Interlocked.Read(ref _inputSent);
    public long RepliesSent => Interlocked.Read(ref _repliesSent);
    public long WriteTimeouts => Interlocked.Read(ref _writeTimeouts);
    public long ReportsReceived => Interlocked.Read(ref _received);
    public bool Streaming => _proto.Streaming;
    public long UnknownReports => _proto.UnknownReports;
    private long _inputSent, _repliesSent, _writeTimeouts, _received;

    public ProconUsbSink(Func<IHidEndpoint> open, string label = "/dev/hidg0", bool byLabel = false)
    {
        _open = open;
        _label = label;
        _proto = new ProconProtocol { ByLabel = byLabel };
    }

    /// <summary>"/dev/hidg0" のような指定から作る。省けば /dev/hidg0 を使う。</summary>
    public static ProconUsbSink FromSpec(string? spec, bool byLabel = false, IInputTimestamp? inputClock = null)
    {
        var path = string.IsNullOrWhiteSpace(spec) ? "/dev/hidg0" : spec.Trim();
        var log = Environment.GetEnvironmentVariable("LLMCONHUB_USB_LOG");
        return new ProconUsbSink(() => new LinuxHidEndpoint(path), path, byLabel)
        {
            InputClock = inputClock,
            LatencyLogPath = string.IsNullOrWhiteSpace(log) ? null : log,
        };
    }

    public string Name => $"procon-usb({_label})";
    public bool Connected => _ep != null && _proto.Streaming;
    public string? Error { get; private set; }

    public bool Start()
    {
        try { _ep = _open(); }
        catch (Exception ex) { Error = ex.Message; return false; }
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ProconUsbReader" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ProconUsbWriter" };
        _reader.Start();
        _writer.Start();
        return true;
    }

    public void Write(in PadState s)
    {
        if (!_running) return;
        if (_hasLast && Same(s, _last)) return;
        _last = s; _hasLast = true;
        lock (_stateLock) _state = s;
        _dirty = true;
        _signal.Set();
    }

    private PadState Current() { lock (_stateLock) return _state; }

    private void ReadLoop()
    {
        var buf = new byte[ProconProtocol.ReportLength];
        var replies = new List<byte[]>(2);
        while (_running)
        {
            int n;
            try { n = _ep!.Read(buf, 100); }
            catch (Exception ex) { Error = ex.Message; Thread.Sleep(100); continue; }
            if (n <= 0) continue;
            Interlocked.Increment(ref _received);
            replies.Clear();
            lock (_proto) _proto.HandleOutput(buf.AsSpan(0, n), Current(), replies);
            foreach (var r in replies) _replies.Enqueue(r);
            _signal.Set();
        }
    }

    private void WriteLoop()
    {
        var input = new byte[ProconProtocol.ReportLength];
        long lastInputTicks = 0;
        PadState lastWritten = default;
        bool hasWritten = false;
        System.IO.StreamWriter? log = null;
        if (LatencyLogPath != null)
        {
            try { log = new System.IO.StreamWriter(LatencyLogPath, append: false) { AutoFlush = true }; log.WriteLine("input_us,written_us,buttons"); }
            catch (Exception ex) { Error = $"遅延の記録を開けません：{ex.Message}"; }
        }
        long intervalTicks = System.Diagnostics.Stopwatch.Frequency * IntervalMs / 1000;
        while (_running)
        {
            _signal.WaitOne(_proto.Streaming ? IntervalMs : 100);
            if (!_running) break;

            while (_replies.TryDequeue(out var r))
            {
                if (Send(r)) Interlocked.Increment(ref _repliesSent);
            }

            if (!_proto.Streaming) continue;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_dirty && now - lastInputTicks < intervalTicks) continue;
            _dirty = false;
            var cur = Current();
            lock (_proto) _proto.BuildInputReport(cur, input);
            if (Send(input))
            {
                Interlocked.Increment(ref _inputSent);
                if (log != null && (!hasWritten || !Same(cur, lastWritten)))
                    log.WriteLine($"{InputClock?.LastInputMicros ?? 0},{PadFrame.NowMicros()},{cur.Buttons}");
                lastWritten = cur; hasWritten = true;
            }
            lastInputTicks = now;
        }
    }

    private bool Send(byte[] report)
    {
        try
        {
            if (_ep!.Write(report, 100)) return true;
            Interlocked.Increment(ref _writeTimeouts);
            return false;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Interlocked.Increment(ref _writeTimeouts);
            Thread.Sleep(50);
            return false;
        }
    }

    private static bool Same(in PadState a, in PadState b)
        => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _reader?.Join(500);
        _writer?.Join(500);
        try { _ep?.Dispose(); } catch { /* ignore */ }
        _ep = null;
    }
}

/// <summary>Linux の /dev/hidg0 を、読み書きを待ちすぎないように poll で読み書きする。</summary>
public sealed class LinuxHidEndpoint : IHidEndpoint
{
    private const int O_RDWR = 2, O_NONBLOCK = 0x800; // Linux の値
    private const short POLLIN = 1, POLLOUT = 4;
    private const int EAGAIN = 11, EINTR = 4;
    private int _fd;

    public LinuxHidEndpoint(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("USB gadget の /dev/hidg0 は Linux にしかありません。");
        _fd = Native.open(path, O_RDWR | O_NONBLOCK);
        if (_fd < 0) throw new System.IO.IOException($"{path} を開けません（errno {Marshal.GetLastWin32Error()}）。pi/procon_gadget.sh で機器を作ってください。");
    }

    public unsafe int Read(Span<byte> buf, int timeoutMs)
    {
        var p = new Native.PollFd { fd = _fd, events = POLLIN };
        int pr = Native.poll(ref p, 1, timeoutMs);
        if (pr <= 0) return 0;
        fixed (byte* b = buf)
        {
            long n = Native.read(_fd, b, (nuint)buf.Length);
            if (n < 0)
            {
                int e = Marshal.GetLastWin32Error();
                if (e == EAGAIN || e == EINTR) return 0;
                throw new System.IO.IOException($"/dev/hidg0 の読み取りに失敗しました（errno {e}）。");
            }
            return (int)n;
        }
    }

    public unsafe bool Write(ReadOnlySpan<byte> report, int timeoutMs)
    {
        var p = new Native.PollFd { fd = _fd, events = POLLOUT };
        int pr = Native.poll(ref p, 1, timeoutMs);
        if (pr <= 0) return false;
        fixed (byte* b = report)
        {
            long n = Native.write(_fd, b, (nuint)report.Length);
            if (n < 0)
            {
                int e = Marshal.GetLastWin32Error();
                if (e == EAGAIN || e == EINTR) return false;
                throw new System.IO.IOException($"/dev/hidg0 への書き込みに失敗しました（errno {e}）。Switch とつながっていない可能性があります。");
            }
            return n == report.Length;
        }
    }

    public void Dispose()
    {
        if (_fd >= 0) { Native.close(_fd); _fd = -1; }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd { public int fd; public short events; public short revents; }
        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern unsafe long read(int fd, byte* buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern unsafe long write(int fd, byte* buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern int poll(ref PollFd fds, nuint nfds, int timeout);
    }
}
