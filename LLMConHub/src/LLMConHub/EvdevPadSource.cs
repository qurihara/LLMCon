using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// Linux の evdev（/dev/input/event*）から物理のゲームパッドを読む入力源。Raspberry Pi の上で動かすためのもの。
///
/// 読み取りは専用のスレッドで行う。そのスレッドは、poll で事象を待ち、届いた事象を EvdevDecoder に渡す。
/// エンジンのループが呼ぶ TryRead は、確定した状態の写しを返すだけなので、ループを待たせない。
///
/// 事象の時刻は、EVIOCSCLOCKID で CLOCK_MONOTONIC に切り替える。.NET の Stopwatch も Linux では
/// CLOCK_MONOTONIC を読むので、入力の時刻と出力の時刻を同じ時計で比べられる。
/// この前提が崩れていないかを、起動時に clock_gettime と Stopwatch を比べて確かめ、ClockSkewMicros に入れる。
///
/// 機器の一覧は sysfs（/sys/class/input/event*/device）から作る。ゲームパッドかジョイスティックの
/// ボタン（BTN_SOUTH か BTN_JOYSTICK）を持つものだけを出す。
///
/// この実装は、2026-10-30 の時点で、実機（Raspberry Pi）ではまだ動かしていない。mac で組み立てと、
/// 変換の部分（EvdevDecoder）の試験だけを済ませてある。実機で、ボタンと軸ごとに実際の値を確かめること。
/// </summary>
public sealed class EvdevPadSource : IPadSource, IInputTimestamp, IDisposable
{
    private readonly string _sysRoot;
    private readonly string _devRoot;
    private readonly object _lock = new();

    private Thread? _reader;
    private volatile bool _running;
    private int _fd = -1;
    private string? _selectedId;
    private string _selectedName = "";
    private EvdevDecoder? _decoder;
    private PadState _current;
    private bool _hasData;
    private ulong _lastInputMicros;
    private ulong _lastEventMicros;
    private PadAxisMap _axisMap = PadAxisMap.Default;
    private PadButtonMap _buttonMap = PadButtonMap.Default;
    private IReadOnlyList<PadProfile>? _profiles;
    private (ushort, ushort)? _lastHw;

    // 「最初に見つかったパッド」を選んでいるとき（auto）は、パッドが無い、または外れたら、
    // 見張りの糸が2秒ごとに探し直して開く。起動のあとで挿したパッドや、挿し直したパッドを拾うためである
    // （2026-11-03、起動のあとで挿した BUFFALO のパッドを、ホストが拾わなかった）。
    private volatile bool _auto;
    // auto:N のときの順番（1から数える）。1台の Raspberry Pi で LLMCon を2つ動かすとき（MODE=dual）、
    // 1つ目のホストは1つ目のパッドを、2つ目のホストは2つ目のパッドを読むために使う。
    private volatile int _autoIndex = 1;
    private volatile bool _lost;
    private Thread? _watcher;

    /// <summary>Stopwatch の時計と CLOCK_MONOTONIC の差（マイクロ秒）。大きければ、遅延の測定の値を信用してはならない。</summary>
    public long ClockSkewMicros { get; private set; }

    /// <summary>最後に起きた失敗の説明。</summary>
    public string? LastError { get; private set; }

    /// <summary>読んだ事象の数（診断のため）。</summary>
    public long EventCount { get; private set; }

    public EvdevPadSource(string sysRoot = "/sys/class/input", string devRoot = "/dev/input")
    {
        _sysRoot = sysRoot;
        _devRoot = devRoot;
        ClockSkewMicros = MeasureClockSkew();
    }

    // ── 一覧 ──

    /// <summary>sysfs に現れている入力の機器のうち、ゲームパッドかジョイスティックのものを返す。識別子は "event3" のような名前である。</summary>
    public IReadOnlyList<PadInfo> List()
    {
        var list = new List<PadInfo>();
        if (!Directory.Exists(_sysRoot)) return list;
        // event2 が event10 より先に来るように、番号の数で並べる
        foreach (var dir in Directory.GetDirectories(_sysRoot, "event*")
                     .OrderBy(d => int.TryParse(Path.GetFileName(d).AsSpan(5), out var k) ? k : int.MaxValue))
        {
            var id = Path.GetFileName(dir);
            var caps = ReadCaps(dir);
            if (caps == null || !caps.IsPad) continue;
            list.Add(new PadInfo(id, ReadText(Path.Combine(dir, "device", "name")) ?? id));
        }
        return list;
    }

    /// <summary>sysfs の1つの機器のフォルダから、ボタンと軸の一覧を読む。</summary>
    internal static EvdevCaps? ReadCaps(string eventDir)
    {
        var key = ReadText(Path.Combine(eventDir, "device", "capabilities", "key"));
        var abs = ReadText(Path.Combine(eventDir, "device", "capabilities", "abs"));
        if (key == null) return null;
        int bits = Environment.Is64BitOperatingSystem ? 64 : 32;
        return new EvdevCaps(EvdevCaps.ParseBitmap(key, bits), abs == null ? new List<int>() : EvdevCaps.ParseBitmap(abs, bits));
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    public PadHardware? SelectedHardware()
    {
        var id = _selectedId;
        if (id == null) return null;
        var hw = ReadHardware(id);
        return new PadHardware(hw.Vendor, hw.Product, _selectedName);
    }

    private (ushort Vendor, ushort Product, string Name) ReadHardware(string id)
    {
        var dir = Path.Combine(_sysRoot, id, "device", "id");
        ushort Hex(string n) => ushort.TryParse(ReadText(Path.Combine(dir, n)), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : (ushort)0;
        return (Hex("vendor"), Hex("product"), ReadText(Path.Combine(_sysRoot, id, "device", "name")) ?? id);
    }

    // ── 選択と読み取り ──

    public string? SelectedId => _selectedId;

    public void Select(string? id)
    {
        Close();
        _auto = id != null && (id == "auto" || id.StartsWith("auto:", StringComparison.Ordinal));
        _autoIndex = 1;
        if (_auto && id!.Length > 5 && int.TryParse(id.AsSpan(5), out var n) && n >= 1) _autoIndex = n;
        if (_auto) StartWatcher();
        if (string.IsNullOrEmpty(id)) return;
        if (_auto) id = NthPad(_autoIndex);
        if (id == null) { LastError = "ゲームパッドが見つかりません。"; return; }
        Open(id);
    }

    /// <summary>番号の若い順に並べたパッドのうち、n 番目（1から数える）の識別子。無ければ null。</summary>
    private string? NthPad(int n) => List().Skip(n - 1).FirstOrDefault()?.Id;

    /// <summary>プロファイルの機体ごとの割り当てを受け取る。パッドを開くたびに、VID と PID が合うものを当てる。</summary>
    public void UsePadProfiles(IReadOnlyList<PadProfile>? profiles) => _profiles = profiles;

    private void StartWatcher()
    {
        if (_watcher != null) return;
        _watcher = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                if (!_auto) continue;
                bool open; lock (_lock) open = _fd >= 0;
                if (open && !_lost) continue;
                var id = NthPad(_autoIndex);
                if (id == null) continue;
                Close();
                if (!_auto) continue;
                Open(id);
                if (_fd >= 0) LastError = null;
            }
        }) { IsBackground = true, Name = "EvdevWatcher" };
        _watcher.Start();
    }

    public bool TryRead(out PadState state)
    {
        lock (_lock)
        {
            state = _hasData ? _current : default;
            return _hasData && _fd >= 0;
        }
    }

    public ulong LastInputMicros { get { lock (_lock) return _lastInputMicros; } }

    private void Open(string id)
    {
        var caps = ReadCaps(Path.Combine(_sysRoot, id));
        if (caps == null || !caps.IsPad) { LastError = $"{id} はゲームパッドではありません。"; return; }

        var path = Path.Combine(_devRoot, id);
        int fd = Native.open(path, Native.O_RDONLY | Native.O_NONBLOCK);
        if (fd < 0)
        {
            LastError = $"{path} を開けません（errno {Marshal.GetLastWin32Error()}）。input の集団に入っているか、権限を確かめてください。";
            return;
        }

        int clock = Native.CLOCK_MONOTONIC;
        if (Native.ioctl(fd, Native.EVIOCSCLOCKID, ref clock) < 0)
            LastError = "事象の時刻を CLOCK_MONOTONIC に切り替えられませんでした。遅延の測定の値は使えません。";

        // プロファイルに、この機体（VID と PID）の割り当てがあれば当てる
        var hw = ReadHardware(id);
        var prof = PadProfile.FindFor(_profiles, hw.Vendor, hw.Product, hw.Name);
        // プロファイルに無い別の機体に替わったときは、前の機体の割り当てを持ち越さずに既定へ戻す
        var key = (hw.Vendor, hw.Product);
        if (prof == null && _lastHw != null && _lastHw != key) { _axisMap = PadAxisMap.Default; _buttonMap = PadButtonMap.Default; }
        _lastHw = key;
        if (prof?.Axes != null) _axisMap = prof.Axes;
        if (!string.IsNullOrEmpty(prof?.Buttons)) _buttonMap = PadButtonMap.FromSpec(prof!.Buttons, out _);

        var dec = new EvdevDecoder(caps) { AxisMap = _axisMap, ButtonMap = _buttonMap };
        ReadInitialState(fd, dec);

        lock (_lock)
        {
            _fd = fd;
            _selectedId = id;
            _selectedName = ReadText(Path.Combine(_sysRoot, id, "device", "name")) ?? id;
            _decoder = dec;
            _current = dec.Current;
            _hasData = dec.HasData;
        }
        _lost = false;
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "EvdevReader" };
        _reader.Start();
    }

    /// <summary>開いた直後の、ボタンと軸のいまの値を読む。これを読まないと、最初の事象が来るまで状態が分からない。</summary>
    private static unsafe void ReadInitialState(int fd, EvdevDecoder dec)
    {
        foreach (var code in dec.Caps.Abs)
        {
            if (code >= EvCodes.ABS_CNT) continue;
            var info = new Native.InputAbsInfo();
            if (Native.ioctl(fd, Native.EVIOCGABS(code), ref info) >= 0)
                dec.SetAbsInfo(code, new AbsRange(info.Minimum, info.Maximum), info.Value);
        }
        var keys = new byte[EvCodes.KEY_CNT / 8];
        fixed (byte* p = keys)
        {
            if (Native.ioctl(fd, Native.EVIOCGKEY((uint)keys.Length), p) >= 0)
                foreach (var code in dec.Caps.Keys)
                    if (code < EvCodes.KEY_CNT) dec.SetKey(code, (keys[code / 8] & (1 << (code % 8))) != 0);
        }
        dec.CommitInitial();
    }

    private unsafe void ReadLoop()
    {
        int evSize = Native.InputEventSize;
        var buf = new byte[evSize * 64];
        var pfd = new Native.PollFd { fd = _fd, events = Native.POLLIN };
        while (_running)
        {
            pfd.fd = _fd;
            pfd.revents = 0;
            int pr = Native.poll(ref pfd, 1, 100);
            if (!_running) break;
            if (pr <= 0) continue;
            if ((pfd.revents & (Native.POLLERR | Native.POLLHUP)) != 0)
            {
                LastError = "ゲームパッドが外れました。";
                lock (_lock) _hasData = false;
                _lost = true;
                break;
            }
            long n;
            fixed (byte* p = buf) n = Native.read(_fd, p, (nuint)buf.Length);
            if (n <= 0) continue;

            var dec = _decoder!;
            for (int off = 0; off + evSize <= n; off += evSize)
            {
                ulong micros; ushort type, code; int value;
                if (evSize == 24)
                {
                    long sec = BitConverter.ToInt64(buf, off);
                    long usec = BitConverter.ToInt64(buf, off + 8);
                    micros = (ulong)(sec * 1_000_000 + usec);
                    type = BitConverter.ToUInt16(buf, off + 16);
                    code = BitConverter.ToUInt16(buf, off + 18);
                    value = BitConverter.ToInt32(buf, off + 20);
                }
                else
                {
                    long sec = BitConverter.ToInt32(buf, off);
                    long usec = BitConverter.ToInt32(buf, off + 4);
                    micros = (ulong)(sec * 1_000_000 + usec);
                    type = BitConverter.ToUInt16(buf, off + 8);
                    code = BitConverter.ToUInt16(buf, off + 10);
                    value = BitConverter.ToInt32(buf, off + 12);
                }
                EventCount++;
                _lastEventMicros = micros;
                if (dec.Feed(type, code, value))
                {
                    var s = dec.Current;
                    lock (_lock)
                    {
                        if (!Same(s, _current)) _lastInputMicros = micros;
                        _current = s;
                        _hasData = true;
                    }
                }
                if (dec.NeedsResync && type == EvCodes.EV_SYN && code == EvCodes.SYN_REPORT)
                {
                    ReadInitialState(_fd, dec);
                    lock (_lock) { _current = dec.Current; _lastInputMicros = micros; }
                }
            }
        }
    }

    private static bool Same(in PadState a, in PadState b)
        => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;

    private void Close()
    {
        _running = false;
        _reader?.Join(500);
        _reader = null;
        lock (_lock)
        {
            if (_fd >= 0) Native.close(_fd);
            _fd = -1;
            _selectedId = null;
            _decoder = null;
            _hasData = false;
            _current = default;
        }
    }

    public void Dispose() => Close();

    // ── 割り当て ──

    public void ExcludeOwnPad(string? id) { /* この機械では、自分の出力を自分の入力として読むことは無い */ }

    public PadAxisMap AxisMap => _axisMap;
    public void SetAxisMap(PadAxisMap map)
    {
        _axisMap = map;
        lock (_lock) if (_decoder != null) _decoder.AxisMap = map;
    }

    public PadButtonMap ButtonMap => _buttonMap;
    public void SetButtonMap(PadButtonMap map)
    {
        _buttonMap = map;
        lock (_lock) if (_decoder != null) _decoder.ButtonMap = map;
    }

    public PadRawReading? ReadRaw(string? id = null)
    {
        lock (_lock)
        {
            if (_decoder == null || (id != null && id != _selectedId)) return null;
            return _decoder.Raw(_selectedId!, _selectedName, _lastEventMicros);
        }
    }

    // ── 時計の確認 ──

    private static long MeasureClockSkew()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try
        {
            var ts = new Native.Timespec();
            ulong before = PadFrame.NowMicros();
            Native.clock_gettime(Native.CLOCK_MONOTONIC, ref ts);
            ulong after = PadFrame.NowMicros();
            ulong mono = (ulong)(ts.tv_sec * 1_000_000 + ts.tv_nsec / 1000);
            return (long)mono - (long)((before + after) / 2);
        }
        catch { return long.MaxValue; }
    }

    // ── OS の呼び出し ──

    private static class Native
    {
        public const int O_RDONLY = 0, O_NONBLOCK = 0x800;     // Linux の値（arm64 と x86_64 で同じ）
        public const int CLOCK_MONOTONIC = 1;
        public const short POLLIN = 0x1, POLLERR = 0x8, POLLHUP = 0x10;

        /// <summary>struct input_event の大きさ。64ビットの機械では 24、32ビットでは 16。</summary>
        public static int InputEventSize => IntPtr.Size == 8 ? 24 : 16;

        // _IOC(dir, type, nr, size) = dir<<30 | size<<16 | type<<8 | nr。読み取りは dir=2、書き込みは dir=1。
        private static nuint Ioc(uint dir, uint nr, uint size) => (nuint)((dir << 30) | (size << 16) | ((uint)'E' << 8) | nr);
        public static readonly nuint EVIOCSCLOCKID = Ioc(1, 0xa0, 4);
        public static nuint EVIOCGABS(int abs) => Ioc(2, 0x40 + (uint)abs, 24);
        public static nuint EVIOCGKEY(uint len) => Ioc(2, 0x18, len);

        [StructLayout(LayoutKind.Sequential)]
        public struct InputAbsInfo { public int Value, Minimum, Maximum, Fuzz, Flat, Resolution; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd { public int fd; public short events; public short revents; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Timespec { public long tv_sec; public long tv_nsec; }

        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern unsafe long read(int fd, byte* buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern int poll(ref PollFd fds, nuint nfds, int timeout);
        [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, nuint request, ref int arg);
        [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, nuint request, ref InputAbsInfo arg);
        [DllImport("libc", SetLastError = true)] public static extern unsafe int ioctl(int fd, nuint request, byte* arg);
        [DllImport("libc", SetLastError = true)] public static extern int clock_gettime(int clk, ref Timespec ts);
    }
}
