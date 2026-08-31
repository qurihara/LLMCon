using System;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CntlLevelConnection;

/// <summary>
/// マイクのしきい値ボタン。マイクの音量がしきい値を超えたときに、指定したボタンを操作する。
/// 実測（experiments/mic-latency-2026-07）に基づき、信号の経路はネイティブ（WASAPI 共有モード・
/// イベント駆動・10ミリ秒バッファ）で実装する。ブラウザ経路は1フレーム（16.7ミリ秒）に収まらないため
/// 設定UIにだけ使う。マイク由来の入力は「人間入力」としてエンジンに合流し、反転や遅延などの
/// すべての改変ルールが効く。
/// 動作モード: hold（しきい値を超えている間だけ押す。ヒステリシスつき）/
/// toggle（超えた瞬間に押し・離しを切り替える）/ tap（超えた瞬間に短く押す）。
/// </summary>
public sealed class MicInput : IMicTrigger, IDisposable
{
    private readonly ControllerEngine _engine;
    private readonly object _lock = new();
    private WasapiCapture? _cap;

    private bool _enabled;
    private string _button = "RT";
    private double _thr = 0.20;
    private double _low = 0.10;          // hold の解除しきい値（ヒステリシス）
    private string _mode = "hold";       // hold | toggle | tap

    private bool _down;                  // 現在ボタンを押しているか
    private bool _armed = true;          // toggle と tap の再武装（レベルが low を下回ると再武装）
    private volatile float _level;       // 直近ブロックのピーク（UIのメーター用）

    public MicInput(ControllerEngine engine) => _engine = engine;

    public bool Enabled { get { lock (_lock) return _enabled; } }
    public double Level => _level;

    /// <summary>いま声を割り当てているボタン。タスクトレイの表示に使う。</summary>
    public string Button { get { lock (_lock) return _button; } }

    /// <summary>いまのしきい値。タスクトレイの表示と較正に使う。</summary>
    public double Threshold { get { lock (_lock) return _thr; } }

    /// <summary>いまの動作モード。</summary>
    public string Mode { get { lock (_lock) return _mode; } }

    /// <summary>
    /// 取り込みだけを始める。ボタンを押す動作はしない。較正のときに、しきい値を決める前の
    /// レベルを測るために使う。既に有効なら何もしない。
    /// </summary>
    public string StartListening()
    {
        lock (_lock)
        {
            if (_cap is not null) return "";
            try { StartCaptureLocked(); return ""; }
            catch (Exception ex) { return $"mic capture failed: {ex.Message}"; }
        }
    }

    /// <summary>いま取り込んでいる装置の名前。どれを掴んでいるかが見えないと、切り分けができない。</summary>
    private string _deviceName = "(未取得)";
    public string DeviceName { get { lock (_lock) return _deviceName; } }

    public string Describe()
    {
        lock (_lock)
            return _enabled
                ? $"mic on: button={_button} thr={_thr:F2} low={_low:F2} mode={_mode} level={_level:F3} device=\"{_deviceName}\""
                : $"mic off (device=\"{_deviceName}\")";
    }

    /// <summary>設定を差し替える。enabled を真にした最初の呼び出しで取り込みを開始する。</summary>
    public string Configure(bool enabled, string? button, double? threshold, double? low, string? mode)
    {
        lock (_lock)
        {
            if (button != null)
            {
                if (!ControllerEngine.IsKnownButton(button) && button != "LT" && button != "RT")
                    return $"unknown button '{button}'";
                if (_down) { _engine.SetMicButton(_button, false); _down = false; }
                _button = button;
            }
            if (threshold is double t) _thr = Math.Clamp(t, 0.005, 1.0);
            if (low is double l) _low = Math.Clamp(l, 0.001, 1.0);
            if (_low >= _thr) _low = _thr * 0.5;
            if (mode != null)
            {
                var m = mode.ToLowerInvariant();
                if (m is not ("hold" or "toggle" or "tap")) return $"unknown mode '{mode}' (use hold, toggle, or tap)";
                _mode = m;
            }
            if (enabled && _cap is null)
            {
                try { StartCaptureLocked(); }
                catch (Exception ex) { return $"mic capture failed: {ex.Message}"; }
            }
            if (!enabled && _down) { _engine.SetMicButton(_button, false); _down = false; }
            _enabled = enabled;
            _armed = true;
            return Describe();
        }
    }

    private void StartCaptureLocked()
    {
        var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        // どの装置を掴んでいるかを控える。声を拾えないときの切り分けに要る。
        try { _deviceName = dev.FriendlyName; } catch { _deviceName = "(名前を取れません)"; }
        _cap = new WasapiCapture(dev, true, 10);   // 共有モード・イベント駆動・10ミリ秒バッファ
        _cap.DataAvailable += OnData;
        _cap.StartRecording();
    }

    private void OnData(object? s, WaveInEventArgs e)
    {
        var cap = _cap;
        if (cap is null) return;
        var wf = cap.WaveFormat;
        int ch = wf.Channels, bps = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bps * ch);
        if (frames <= 0) return;

        double peak = 0;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
            {
                int off = (f * ch + c) * bps;
                double v = bps == 4
                    ? Math.Abs(BitConverter.ToSingle(e.Buffer, off))
                    : Math.Abs(BitConverter.ToInt16(e.Buffer, off) / 32768.0);
                if (v > peak) peak = v;
            }
        _level = (float)peak;

        lock (_lock)
        {
            if (!_enabled) return;
            switch (_mode)
            {
                case "hold":
                    if (!_down && peak >= _thr) { _down = true; _engine.SetMicButton(_button, true); }
                    else if (_down && peak <= _low) { _down = false; _engine.SetMicButton(_button, false); }
                    break;
                case "toggle":
                    if (_armed && peak >= _thr)
                    {
                        _armed = false;
                        _down = !_down;
                        _engine.SetMicButton(_button, _down);
                    }
                    else if (!_armed && peak <= _low) _armed = true;
                    break;
                case "tap":
                    if (_armed && peak >= _thr)
                    {
                        _armed = false;
                        var btn = _button;
                        _engine.SetMicButton(btn, true);
                        Task.Delay(120).ContinueWith(_ => _engine.SetMicButton(btn, false));
                    }
                    else if (!_armed && peak <= _low) _armed = true;
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_down) { try { _engine.SetMicButton(_button, false); } catch { /* ignore */ } _down = false; }
            _enabled = false;
        }
        try { _cap?.StopRecording(); _cap?.Dispose(); } catch { /* ignore */ }
        _cap = null;
    }
}
