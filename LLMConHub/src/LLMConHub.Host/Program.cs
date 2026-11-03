using System;
using System.Collections.Generic;
using System.Threading;
using CntlLevelConnection;
using LLMConHub;

// LLMConHub のホスト。Raspberry Pi の上で LLMCon のコアを動かし、確定した状態を
// Bluetooth の出力を受け持つ橋渡しのプログラム（bridge/llmconhub_bridge.py）へ UDP で渡す。
//
// 受ける引数は、画面を持たないホスト（CntlLevelConnection.Host）と同じもの（--port、--bind、--name、--sink、--profile）に、
// 次の2つを足したものである。
//   --sink の値に udp を書ける。"udp"、"udp=127.0.0.1:9100"、"websocket+udp" のように書く。
//   --sink の値に serial を書ける。"serial=/dev/ttyACM0" のように書く。出力用の機器（Pico、ESP32）へ USB のシリアルで送る。
//   --sink の値に procon-usb を書ける。"procon-usb" か "procon-usb=/dev/hidg0" のように書く。
//                     Raspberry Pi の USB の機器の口から、Switch の有線の Pro コントローラとして出力する（pi/procon_gadget.sh で機器を作っておく）。
//   --by-label        procon-usb で、ボタンを位置ではなく名前で合わせる（LLMCon の A を Switch の A にする）。
//   --pad evdev       最初に見つかったゲームパッドを evdev から読む。
//   --pad evdev=event3  /dev/input/event3 を読む。
//   --status          5秒ごとに、ループの回数、送った電文の数、入力の事象の数を表示する。
// --sink を省いたときは udp だけを使う（Windows の既定の vigem は、この機械には無い）。

var opts = LlmConOptions.Parse(args);
if (opts.Warning != null) Console.Error.WriteLine(opts.Warning);

string? padSpec = null;
bool status = false;
bool sinkGiven = false;
bool byLabel = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--pad" && i + 1 < args.Length) padSpec = args[i + 1];
    if (args[i] == "--status") status = true;
    if (args[i] == "--sink") sinkGiven = true;
    if (args[i] == "--by-label") byLabel = true;
}
string sinkSpec = sinkGiven ? opts.Sink : "udp";

// 物理のパッド。evdev は Linux にしか無い。
EvdevPadSource? pad = null;
if (padSpec != null)
{
    var p = padSpec.Trim();
    if (p.StartsWith("evdev", StringComparison.OrdinalIgnoreCase))
    {
        if (!OperatingSystem.IsLinux())
            Console.Error.WriteLine("注意: evdev は Linux にしか無いので、物理のパッドは読みません。");
        else
        {
            pad = new EvdevPadSource();
            if (Math.Abs(pad.ClockSkewMicros) > 1000)
                Console.Error.WriteLine($"注意: Stopwatch と CLOCK_MONOTONIC が {pad.ClockSkewMicros} マイクロ秒ずれています。遅延の測定の値は使えません。");
        }
    }
    else Console.Error.WriteLine($"注意: --pad {padSpec} は分かりません。evdev か evdev=event3 のように書いてください。");
}

// 出力先を決める。
var sinks = new List<IPadSink>();
var web = new WebController();
UdpFrameSink? udp = null;
ProconUsbSink? procon = null;
foreach (var raw in sinkSpec.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries))
{
    var t = raw.Trim();
    var name = t.Split('=', 2)[0].ToLowerInvariant();
    var arg = t.Contains('=') ? t.Split('=', 2)[1] : null;
    switch (name)
    {
        case "udp":
            udp = UdpFrameSink.FromSpec(arg, pad);
            sinks.Add(udp);
            break;
        case "serial":
            if (string.IsNullOrWhiteSpace(arg)) { Console.Error.WriteLine("注意: serial には機器の場所を書いてください（serial=/dev/ttyACM0）。"); break; }
            sinks.Add(SerialFrameSink.FromSpec(arg, pad));
            break;
        case "procon-usb" or "procon":
            if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("注意: procon-usb は Linux の USB gadget を使うので、この機械では使えません。"); break; }
            procon = ProconUsbSink.FromSpec(arg, byLabel, pad);
            sinks.Add(procon);
            break;
        case "websocket" or "ws":
            sinks.Add(new WebSocketSink(web));
            break;
        case "vigem":
            Console.Error.WriteLine("注意: ViGEm は Windows の仮想コントローラなので、この機械では使えません。");
            break;
        case "none":
            break;
        default:
            Console.Error.WriteLine($"注意: 出力先 {t} は分かりません。");
            break;
    }
}
if (sinks.Count == 0) { udp = UdpFrameSink.FromSpec(null, pad); sinks.Add(udp); }

var engine = new ControllerEngine();
var events = new EventLog();
var macros = new MacroEngine(engine);
var connections = new ConnectionManager(events);
engine.HumanEdges = connections.OnHumanEdges;
engine.Events = events;
connections.SelfLabel = $"{opts.Name}@{opts.Port}";

if (opts.DesignHtml != null) web.SetUi(opts.DesignHtml);
else if (opts.Preset != null)
{
    var html = ControllerPresets.Get(opts.Preset);
    if (html != null) web.SetUi(html);
    else Console.Error.WriteLine($"注意: プリセット '{opts.Preset}' が見つかりません。既定のデザインで動かします。");
}

engine.Start(sinks, padSource: pad);
if (engine.LastError != null) Console.Error.WriteLine($"注意: 出力先の用意に失敗したものがあります：{engine.LastError}");

if (pad != null)
{
    var id = padSpec!.Contains('=') ? padSpec.Split('=', 2)[1].Trim() : "auto";
    engine.UsePadProfiles(opts.Pads);   // プロファイルの機体ごとの割り当て（--profile）
    engine.SelectPad(id);
    if (pad.SelectedId != null) Console.WriteLine($"  physical pad   : /dev/input/{pad.SelectedId}");
    else Console.Error.WriteLine($"注意: 物理のパッドを開けませんでした：{pad.LastError}");
}

if (opts.Rules is { Length: > 0 }) engine.SetMapping(opts.Rules);

IMicTrigger mic = NullMicTrigger.Instance;

var app = LlmConHost.Build(new LlmConServices
{
    Engine = engine,
    Web = web,
    Macros = macros,
    Connections = connections,
    Events = events,
    Mic = mic,
    Info = new LlmConInfo(opts.Name, opts.Port, opts.LockDesign, opts.Bind),
    Bind = opts.Bind,
    ConsoleLogging = false,
});

Console.WriteLine($"LLMCon Hub host: name={opts.Name} bind={opts.Bind} port={opts.Port} sink={engine.SinkNames}");
Console.WriteLine($"  MCP            : http://{opts.Bind}:{opts.Port}/");
Console.WriteLine($"  web controller : http://{opts.Bind}:{opts.Port}/vcon.html");
Console.WriteLine("  終了は Ctrl+C。");

var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();

if (status)
{
    var t = new Thread(() =>
    {
        while (!done.Wait(5000))
            Console.WriteLine($"[status] loop={engine.LoopHz:F0}Hz udp.sent={udp?.Sent ?? 0} udp.errors={udp?.SendErrors ?? 0} pad.events={pad?.EventCount ?? 0} pad.error={pad?.LastError ?? "-"}"
                + (procon == null ? "" : $" procon.streaming={procon.Streaming} procon.in={procon.InputReportsSent} procon.replies={procon.RepliesSent} procon.recv={procon.ReportsReceived} procon.timeouts={procon.WriteTimeouts} procon.unknown={procon.UnknownReports} procon.error={procon.Error ?? "-"}"));
    }) { IsBackground = true };
    t.Start();
}

_ = app.RunAsync();
done.Wait();

Console.WriteLine("終了します。");
try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await app.StopAsync(cts.Token); } catch { /* ignore */ }
engine.Dispose();
pad?.Dispose();
