using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CntlLevelConnection;

/// <summary>
/// マイク遅延の実測（実験用。--miclab で有効化。通常の利用には関与しない）。
/// スピーカーからビープ音を鳴らし（時刻 t0 を記録）、同じ音を
///  (a) ネイティブ（WASAPI 共有モード）で取り込んでしきい値検出しボタン A を押す経路と、
///  (b) ブラウザ（getUserMedia + AudioWorklet）で検出し WebSocket で届いてボタン B を押す経路とで、
/// 検出時刻を同一の QPC 時計（Stopwatch.GetTimestamp）で記録する。
/// ビープの再生遅延と音の伝播は両経路に共通に乗るので、両者の差分が取り込み経路の差を表す。
/// ネイティブ側は取り込みバッファ内のサンプル位置から「音が AD 変換に到達した時刻」も推定し（tSound）、
/// 絶対値（音が鳴ってから、の遅延）の見積もりに使う。
/// 記録は JSON Lines 形式で1行1事象としてファイルに書く。
/// </summary>
public sealed class MicLab : IDisposable
{
    private readonly ControllerEngine _engine;
    private readonly object _lock = new();
    private readonly StreamWriter _logW;

    private MMDevice? _renderDev, _capDev;
    private WasapiOut? _out;
    private BufferedWaveProvider? _bwp;
    private WasapiCapture? _cap;
    private byte[] _beep = Array.Empty<byte>();
    private float _volumeOrig = -1f;
    private float _micVolOrig = -1f;
    private bool _micMuteOrig;

    private string _mode = "off";       // off | calib | armed
    private bool _armedNat = true;
    private long _lastFireNat;          // 0 で初期化する（QPC は正の値なので、long.MinValue だと差の計算が桁あふれする）
    private long _quietSinceNat = -1;

    private double _thrNat = 999, _lowNat = 999, _thrBr = 999, _lowBr = 999;
    private double _ambNat, _beepNat, _ambBr, _beepBr;

    private readonly List<(long t, double p)> _natPeaks = new();
    private readonly List<(long t, double p)> _brPeaks = new();
    private readonly List<long> _beeps = new();
    private long _brLvlCount;
    private int _trial;

    private static long Ticks(double sec) => (long)(sec * Stopwatch.Frequency);

    private readonly string? _renderPick, _capPick;

    public MicLab(ControllerEngine engine, string logPath, string? renderPick = null, string? capPick = null)
    {
        _engine = engine;
        _renderPick = renderPick;
        _capPick = capPick;
        var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _logW = new StreamWriter(logPath, append: false) { AutoFlush = true };
    }

    // 名前の部分一致でデバイスを選ぶ。指定が無い、または見つからないときは既定のデバイス。
    private static MMDevice PickDevice(MMDeviceEnumerator en, DataFlow flow, string? pick)
    {
        if (!string.IsNullOrWhiteSpace(pick))
        {
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                if (d.FriendlyName.Contains(pick, StringComparison.OrdinalIgnoreCase)) return d;
        }
        return en.GetDefaultAudioEndpoint(flow, Role.Console);
    }

    public void Start()
    {
        var en = new MMDeviceEnumerator();
        _renderDev = PickDevice(en, DataFlow.Render, _renderPick);
        _capDev = PickDevice(en, DataFlow.Capture, _capPick);

        // 音量。静かすぎると較正できないので、低ければ上げる。元の値は覚えておき、終了時に戻す。
        _volumeOrig = _renderDev.AudioEndpointVolume.MasterVolumeLevelScalar;
        if (_renderDev.AudioEndpointVolume.Mute) _renderDev.AudioEndpointVolume.Mute = false;
        if (_volumeOrig < 0.5f) _renderDev.AudioEndpointVolume.MasterVolumeLevelScalar = 0.7f;

        // マイク側も同様に確認する（ミュートや音量ゼロだとデジタル無音になる）。元の値は終了時に戻す。
        _micMuteOrig = _capDev.AudioEndpointVolume.Mute;
        _micVolOrig = _capDev.AudioEndpointVolume.MasterVolumeLevelScalar;
        if (_micMuteOrig) _capDev.AudioEndpointVolume.Mute = false;
        if (_micVolOrig < 0.5f) _capDev.AudioEndpointVolume.MasterVolumeLevelScalar = 0.8f;

        // 出力は常時再生にして、ビープはバッファへ書き足す（再生開始のばらつきを避け、遅延を一定に近づける）。
        var mix = _renderDev.AudioClient.MixFormat;
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        _bwp = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
        _out = new WasapiOut(_renderDev, AudioClientShareMode.Shared, true, 20);
        _out.Init(_bwp);
        _out.Play();
        _beep = MakeBeep(fmt, 2000.0, 0.08, 0.75);

        // 入力（共有モード・イベント駆動・10ミリ秒バッファ）
        _cap = new WasapiCapture(_capDev, true, 10);
        _cap.DataAvailable += OnData;
        _cap.StartRecording();

        Log(new
        {
            ev = "config",
            t = Stopwatch.GetTimestamp(),
            freq = Stopwatch.Frequency,
            os = Environment.OSVersion.VersionString,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            renderDev = _renderDev.FriendlyName,
            captureDev = _capDev.FriendlyName,
            renderFormat = $"{fmt.SampleRate}Hz x{fmt.Channels} float",
            captureFormat = $"{_cap.WaveFormat.SampleRate}Hz x{_cap.WaveFormat.Channels} {_cap.WaveFormat.BitsPerSample}bit {_cap.WaveFormat.Encoding}",
            captureBufMs = 10,
            renderLatencyMs = 20,
            beepHz = 2000,
            beepMs = 80,
            volumeOrig = _volumeOrig,
            volumeNow = _renderDev.AudioEndpointVolume.MasterVolumeLevelScalar,
            micMuteOrig = _micMuteOrig,
            micVolOrig = _micVolOrig,
            micVolNow = _capDev.AudioEndpointVolume.MasterVolumeLevelScalar,
        });
    }

    private static byte[] MakeBeep(WaveFormat fmt, double freqHz, double durSec, double amp)
    {
        int rate = fmt.SampleRate, ch = fmt.Channels;
        int frames = (int)(rate * durSec);
        int fadeIn = (int)(rate * 0.002), fadeOut = (int)(rate * 0.005);
        var data = new float[frames * ch];
        for (int i = 0; i < frames; i++)
        {
            double env = 1.0;
            if (i < fadeIn) env = i / (double)fadeIn;
            else if (i > frames - fadeOut) env = Math.Max(0, (frames - i) / (double)fadeOut);
            float v = (float)(amp * env * Math.Sin(2 * Math.PI * freqHz * i / rate));
            for (int c = 0; c < ch; c++) data[i * ch + c] = v;
        }
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>ビープを1回鳴らす。戻り値は再生をバッファへ書き込んだ時刻（QPC ticks）。</summary>
    public long PlayBeep()
    {
        lock (_lock)
        {
            int n = ++_trial;
            long t0 = Stopwatch.GetTimestamp();
            _bwp!.AddSamples(_beep, 0, _beep.Length);
            _beeps.Add(t0);
            Log(new { ev = "beep", t = t0, n });
            return t0;
        }
    }

    public void SetMode(string mode)
    {
        lock (_lock)
        {
            mode = (mode ?? "off").ToLowerInvariant();
            if (mode == "calib")
            {
                _natPeaks.Clear(); _brPeaks.Clear(); _beeps.Clear();
                _thrNat = _thrBr = 999;  // 較正中は発火しない
            }
            if (mode == "armed") ComputeThresholdsLocked();
            _mode = mode;
            _armedNat = true; _quietSinceNat = -1;
            Log(new { ev = "mode", t = Stopwatch.GetTimestamp(), mode });
        }
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        int m = s.Count / 2;
        return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
    }

    private void ComputeThresholdsLocked()
    {
        long pre = Ticks(0.05), post = Ticks(0.5);
        bool InBeep(long t) => _beeps.Any(b => t >= b - pre && t <= b + post);

        var ambN = _natPeaks.Where(x => !InBeep(x.t)).Select(x => x.p).ToList();
        _ambNat = Median(ambN);
        var bkN = new List<double>();
        foreach (var b in _beeps)
        {
            var mx = _natPeaks.Where(x => x.t >= b && x.t <= b + Ticks(0.45)).Select(x => x.p).DefaultIfEmpty(0).Max();
            if (mx > 0) bkN.Add(mx);
        }
        _beepNat = Median(bkN);
        _thrNat = Math.Max(0.01, Math.Sqrt(Math.Max(_ambNat, 1e-4) * Math.Max(_beepNat, 1e-4)));
        _lowNat = Math.Min(_thrNat * 0.5, Math.Max(_ambNat * 2.5, 0.005));

        var ambB = _brPeaks.Where(x => !InBeep(x.t)).Select(x => x.p).ToList();
        _ambBr = Median(ambB);
        var bkB = new List<double>();
        foreach (var b in _beeps)
        {
            var mx = _brPeaks.Where(x => x.t >= b && x.t <= b + Ticks(0.55)).Select(x => x.p).DefaultIfEmpty(0).Max();
            if (mx > 0) bkB.Add(mx);
        }
        _beepBr = Median(bkB);
        if (_beepBr > 0)
        {
            _thrBr = Math.Max(0.01, Math.Sqrt(Math.Max(_ambBr, 1e-4) * Math.Max(_beepBr, 1e-4)));
            _lowBr = Math.Min(_thrBr * 0.5, Math.Max(_ambBr * 2.5, 0.005));
        }

        Log(new
        {
            ev = "thr", t = Stopwatch.GetTimestamp(),
            thrNat = _thrNat, lowNat = _lowNat, ambNat = _ambNat, beepNat = _beepNat,
            snrNat = _ambNat > 0 ? _beepNat / _ambNat : 0,
            thrBr = _thrBr, lowBr = _lowBr, ambBr = _ambBr, beepBr = _beepBr,
            snrBr = _ambBr > 0 ? _beepBr / _ambBr : 0,
            natPeakSamples = _natPeaks.Count, brPeakSamples = _brPeaks.Count, beeps = _beeps.Count,
        });
    }

    // ── ネイティブ取り込み（WASAPI コールバック） ──
    private void OnData(object? s, WaveInEventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        var wf = _cap!.WaveFormat;
        int ch = wf.Channels;
        int bps = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bps * ch);
        if (frames <= 0) return;

        double thr;
        lock (_lock) thr = _thrNat;

        double peak = 0; int idxFrame = -1;
        for (int f = 0; f < frames; f++)
        {
            double m = 0;
            for (int c = 0; c < ch; c++)
            {
                int off = (f * ch + c) * bps;
                double v = bps == 4
                    ? Math.Abs(BitConverter.ToSingle(e.Buffer, off))
                    : Math.Abs(BitConverter.ToInt16(e.Buffer, off) / 32768.0);
                if (v > m) m = v;
            }
            if (m > peak) peak = m;
            if (idxFrame < 0 && m > thr) idxFrame = f;
        }

        lock (_lock)
        {
            if (_mode == "calib")
            {
                if (_natPeaks.Count < 8000) _natPeaks.Add((now, peak));
                return;
            }
            if (_mode != "armed") return;

            if (_armedNat && idxFrame >= 0 && now - _lastFireNat > Ticks(0.6))
            {
                long tSound = now - (long)((frames - idxFrame) * (double)Stopwatch.Frequency / wf.SampleRate);
                _armedNat = false; _lastFireNat = now; _quietSinceNat = -1;
                Log(new { ev = "nat", t = now, tSound, peak });
                _engine.SetSoftwareButton("A", true);
                Task.Delay(120).ContinueWith(_ => _engine.SetSoftwareButton("A", false));
            }
            else if (!_armedNat)
            {
                // 再武装。静けさが0.25秒続いたら、または発火から1秒たったら（環境音が上がっても詰まらないように）。
                if (now - _lastFireNat > Ticks(1.0)) _armedNat = true;
                else if (peak < _lowNat)
                {
                    if (_quietSinceNat < 0) _quietSinceNat = now;
                    else if (now - _quietSinceNat > Ticks(0.25) && now - _lastFireNat > Ticks(0.6)) _armedNat = true;
                }
                else _quietSinceNat = -1;
            }
        }
    }

    // ── ブラウザ経路からの通知（WebSocket 経由・App が呼ぶ） ──
    /// <summary>ブラウザの検出がWebSocketで届いた時刻を記録する（ボタンBの押下メッセージの受信時）。</summary>
    public void OnBrowserDetect(long qpcTicks)
    {
        Log(new { ev = "br", t = qpcTicks });
    }

    /// <summary>ブラウザからの音量レベル報告（較正に使う）。</summary>
    public void OnBrowserLevel(double peak, long qpcTicks)
    {
        lock (_lock)
        {
            _brLvlCount++;
            if (_mode == "calib" && _brPeaks.Count < 8000) _brPeaks.Add((qpcTicks, peak));
        }
    }

    public void SetVolume(double v)
    {
        if (_renderDev is null) return;
        _renderDev.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(v, 0.0, 1.0);
        Log(new { ev = "volume", t = Stopwatch.GetTimestamp(), v });
    }

    public string StatusJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(new
            {
                mode = _mode, trial = _trial, brLvlCount = _brLvlCount,
                thrNat = _thrNat, lowNat = _lowNat, thrBr = _thrBr, lowBr = _lowBr,
                ambNat = _ambNat, beepNat = _beepNat, ambBr = _ambBr, beepBr = _beepBr,
                snrNat = _ambNat > 0 ? _beepNat / _ambNat : 0,
                snrBr = _ambBr > 0 ? _beepBr / _ambBr : 0,
                loopHz = _engine.LoopHz,
                volume = _renderDev?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1,
            });
        }
    }

    public string ThresholdJson()
    {
        lock (_lock) return JsonSerializer.Serialize(new { thrBr = _thrBr, lowBr = _lowBr });
    }

    private void Log(object o)
    {
        lock (_logW) _logW.WriteLine(JsonSerializer.Serialize(o));
    }

    public void Dispose()
    {
        try { _cap?.StopRecording(); _cap?.Dispose(); } catch { /* ignore */ }
        try { _out?.Stop(); _out?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_renderDev != null && _volumeOrig >= 0)
                _renderDev.AudioEndpointVolume.MasterVolumeLevelScalar = _volumeOrig;
        }
        catch { /* ignore */ }
        try
        {
            if (_capDev != null && _micVolOrig >= 0)
            {
                _capDev.AudioEndpointVolume.MasterVolumeLevelScalar = _micVolOrig;
                _capDev.AudioEndpointVolume.Mute = _micMuteOrig;
            }
        }
        catch { /* ignore */ }
        try { _logW.Dispose(); } catch { /* ignore */ }
    }

    // ── 計測ページ（ブラウザ経路）。getUserMedia は加工（自動音量調整など）を無効にする ──
    public const string PageHtml =
"""
<!doctype html>
<html lang="ja">
<head><meta charset="utf-8"><title>MicLab</title>
<style>
body{font-family:sans-serif;background:#101418;color:#e8ecf2;padding:18px;}
#bar{width:420px;height:18px;background:#20262e;border:1px solid #3a4450;}
#fill{height:100%;width:0;background:#57c78a;}
.row{margin:9px 0;font-size:14px;}
b{color:#9fc1ff;}
</style></head>
<body>
<h3>MicLab（マイク遅延実測・ブラウザ経路）</h3>
<div class="row">WebSocket: <b id="ws">-</b> / マイク: <b id="mic">-</b> / しきい値: <b id="thrv">-</b> / 発火: <b id="fires">0</b></div>
<div id="bar"><div id="fill"></div></div>
<div class="row" style="color:#8a94a2">このページはビープ音を検出すると、WebSocket でボタン B の押下を送ります。</div>
<script>
(function(){
  var wsEl=document.getElementById('ws'),micEl=document.getElementById('mic'),thrEl=document.getElementById('thrv'),fill=document.getElementById('fill'),firesEl=document.getElementById('fires');
  var ws=null,wsReady=false,fires=0,node=null;
  function connect(){
    ws=new WebSocket('ws://'+location.host+'/vcon/ws');
    ws.onopen=function(){wsReady=true;wsEl.textContent='OK';};
    ws.onclose=function(){wsReady=false;wsEl.textContent='closed';setTimeout(connect,1000);};
    ws.onmessage=function(){ /* reload 通知は無視する */ };
  }
  connect();
  function send(o){ if(wsReady){ try{ ws.send(JSON.stringify(o)); }catch(e){} } }
  var workletCode=''+
  'class PeakDetector extends AudioWorkletProcessor{'+
  'constructor(){super();this.thr=1e9;this.low=1e9;this.armed=true;this.quiet=0;this.lastFire=-1e9;this.maxP=0;this.cnt=0;'+
  'var self=this;this.port.onmessage=function(e){var d=e.data;if(d&&d.thr!==undefined){self.thr=d.thr;self.low=d.low;}};}'+
  'process(inputs){var chs=inputs[0];if(!chs||!chs[0])return true;var a=chs[0];var p=0;'+
  'for(var i=0;i<a.length;i++){var v=a[i]<0?-a[i]:a[i];if(v>p)p=v;}'+
  'if(p>this.maxP)this.maxP=p;this.cnt++;'+
  'if(this.cnt>=Math.max(1,Math.round(sampleRate/128/10))){this.port.postMessage({type:"lvl",p:this.maxP});this.maxP=0;this.cnt=0;}'+
  'if(this.armed){if(p>this.thr&&(currentTime-this.lastFire)>0.6){this.armed=false;this.lastFire=currentTime;this.quiet=0;this.port.postMessage({type:"fire"});}}'+
  'else{if(currentTime-this.lastFire>1.0){this.armed=true;}'+
  'else if(p<this.low){this.quiet+=a.length/sampleRate;if(this.quiet>0.25&&(currentTime-this.lastFire)>0.6){this.armed=true;}}else{this.quiet=0;}}'+
  'return true;}}'+
  'registerProcessor("peak-detector",PeakDetector);';
  navigator.mediaDevices.getUserMedia({audio:{echoCancellation:false,noiseSuppression:false,autoGainControl:false}}).then(function(stream){
    var ctx=new AudioContext({latencyHint:'interactive'});
    var url=URL.createObjectURL(new Blob([workletCode],{type:'text/javascript'}));
    ctx.audioWorklet.addModule(url).then(function(){
      var src=ctx.createMediaStreamSource(stream);
      node=new AudioWorkletNode(ctx,'peak-detector');
      node.port.onmessage=function(e){var m=e.data;
        if(m.type==='lvl'){ fill.style.width=Math.min(100,m.p*300)+'%'; send({t:'miclvl',v:m.p}); }
        if(m.type==='fire'){ send({t:'btn',b:'B',d:true}); setTimeout(function(){send({t:'btn',b:'B',d:false});},120); fires++; firesEl.textContent=fires; }
      };
      src.connect(node); node.connect(ctx.destination);
      micEl.textContent='OK ('+ctx.sampleRate+'Hz)';
      if(ctx.state!=='running'){ ctx.resume(); }
      document.addEventListener('pointerdown',function(){ ctx.resume(); });
    });
  }).catch(function(err){ micEl.textContent='ERROR: '+err; });
  setInterval(function(){
    fetch('/miclab/threshold').then(function(r){return r.json();}).then(function(j){
      thrEl.textContent=(typeof j.thrBr==='number')?j.thrBr.toFixed(4):'-';
      if(node){ node.port.postMessage({thr:j.thrBr,low:j.lowBr}); }
    }).catch(function(){});
  },500);
})();
</script>
</body>
</html>
""";
}
