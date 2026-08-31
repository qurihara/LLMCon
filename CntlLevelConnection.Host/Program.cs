using System;
using System.Collections.Generic;
using System.Threading;
using CntlLevelConnection;

// 画面を持たない LLMCon のホスト。mac や Linux で動かすためのもの。
// 受ける引数は Windows のアプリと同じ（--port、--bind、--name、--sink、--profile）。
//
// 物理パッドは扱わない。人間入力は、ウェブ版コントローラ（/vcon.html）とその WebSocket から入る。
// これで改変ルールも、コントローラをまたいだ接続の事象も、これまでどおり働く。

var opts = LlmConOptions.Parse(args);
if (opts.Warning != null) Console.Error.WriteLine(opts.Warning);

// 出力先を決める。この環境に ViGEm は無いので、指定されていたら websocket に落とし、その旨を伝える。
var sinkNames = new List<string>();
bool vigemAsked = false;
foreach (var raw in opts.Sink.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries))
{
    switch (raw.Trim().ToLowerInvariant())
    {
        case "vigem": vigemAsked = true; break;
        case "websocket" or "ws": sinkNames.Add("websocket"); break;
        case "none": break;
    }
}
if (vigemAsked)
    Console.Error.WriteLine("注意: ViGEm は Windows の仮想コントローラなので、この環境では使えません。websocket の出力先だけで動かします。");
if (sinkNames.Count == 0) sinkNames.Add("websocket");

var engine = new ControllerEngine();
var web = new WebController();
var events = new EventLog();
var macros = new MacroEngine(engine);
var connections = new ConnectionManager(events);
engine.HumanEdges = connections.OnHumanEdges;      // 人間入力のエッジを接続の事象検出へ流す
engine.Events = events;                            // 封じられた押下も同じ記録に残す
connections.SelfLabel = $"{opts.Name}@{opts.Port}";

// プロファイルの指定があれば、起動時のデザインを適用する
if (opts.DesignHtml != null) web.SetUi(opts.DesignHtml);
else if (opts.Preset != null)
{
    var html = ControllerPresets.Get(opts.Preset);
    if (html != null) web.SetUi(html);
    else Console.Error.WriteLine($"注意: プリセット '{opts.Preset}' が見つかりません。既定のデザインで動かします。");
}

// 出力先を用意する。物理パッドは渡さない（この環境では扱わない）。
var sinks = new List<IPadSink>();
foreach (var n in sinkNames) if (n == "websocket") sinks.Add(new WebSocketSink(web));
engine.Start(sinks, padSource: null);

// 起動した時点から効かせる改変ルール（プロファイルの rules）。時間の窓はここから数え始める。
if (opts.Rules is { Length: > 0 }) engine.SetMapping(opts.Rules);

// マイクは扱えないので、何もしない実装を渡す。MCP のツールは、使えない旨を返す。
IMicTrigger mic = NullMicTrigger.Instance;
if (opts.Mic is { Enabled: true })
    Console.Error.WriteLine("注意: プロファイルでマイクが有効になっていますが、この環境では使えません。");

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
    ConsoleLogging = false,   // 起動の案内は自分で出すので、枠組みのログは出さない
});

Console.WriteLine($"LLMCon host: name={opts.Name} bind={opts.Bind} port={opts.Port} sink={engine.SinkNames} lockDesign={opts.LockDesign}");
Console.WriteLine($"  MCP            : http://{opts.Bind}:{opts.Port}/");
Console.WriteLine($"  web controller : http://{opts.Bind}:{opts.Port}/vcon.html");
Console.WriteLine("  終了は Ctrl+C。");

// Ctrl+C で後片付けをしてから終わる
var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();

_ = app.RunAsync();
done.Wait();

Console.WriteLine("終了します。");
try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await app.StopAsync(cts.Token); } catch { /* ignore */ }
engine.Dispose();
