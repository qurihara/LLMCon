using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CntlLevelConnection;

/// <summary>
/// LLMCon を構成する部品の一式。ホストを組み立てるときに、これをまとめて渡す。
/// Windows のアプリと、画面を持たないホストの両方が、同じものを使う。
/// </summary>
public sealed class LlmConServices
{
    public required ControllerEngine Engine { get; init; }
    public required WebController Web { get; init; }
    public required MacroEngine Macros { get; init; }
    public required ConnectionManager Connections { get; init; }
    public required EventLog Events { get; init; }
    public required IMicTrigger Mic { get; init; }
    public required LlmConInfo Info { get; init; }

    /// <summary>待ち受けるアドレス。既定はループバック。</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>ログをコンソールへ出すか。WPF のアプリでは出さない。</summary>
    public bool ConsoleLogging { get; init; }

    /// <summary>追加の経路を足したいときに使う（Windows の実測用の口など）。</summary>
    public Action<WebApplication>? ConfigureExtra { get; init; }

    /// <summary>
    /// ウェブ版コントローラから届いたメッセージを覗く口（任意）。マイク遅延の実測が使う。
    /// 引数は (種別 "btn" か "miclvl", ボタン名, 押したか, 値, 受信時刻の刻み)。
    /// </summary>
    public Action<string, string?, bool, double, long>? WebMessageObserver { get; init; }
}

/// <summary>
/// MCP サーバとウェブの配信を組み立てる、環境に依存しない部品。
/// Kestrel の設定、MCP のツールの登録、ウェブ版コントローラの配信、WebSocket の受け口、
/// コントローラ間接続の受け口を、ここにまとめてある。
/// </summary>
public static class LlmConHost
{
    /// <summary>ホストを組み立てる。呼び出し側が RunAsync する。</summary>
    public static WebApplication Build(LlmConServices s)
    {
        var builder = WebApplication.CreateBuilder();
        if (!s.ConsoleLogging) builder.Logging.ClearProviders();

        builder.Services.AddSingleton(s.Engine);
        builder.Services.AddSingleton(s.Macros);
        builder.Services.AddSingleton(s.Web);
        builder.Services.AddSingleton(s.Connections);
        builder.Services.AddSingleton(s.Events);
        builder.Services.AddSingleton(s.Mic);
        builder.Services.AddSingleton(s.Info);
        builder.Services.AddMcpServer()
               .WithHttpTransport()
               // ツールの型は Core にあるので、この組み立てを行うアセンブリ（Core）を明示する。
               // 引数なしの WithToolsFromAssembly は呼び出し元のアセンブリを見るため、
               // Windows のアプリから呼ぶとツールが1つも見つからなくなる。
               .WithToolsFromAssembly(typeof(McpControllerTools).Assembly);

        var app = builder.Build();

        // 待ち受けるアドレス。ループバック以外を指定したときは、この機械の中からも触れるように
        // ループバックも併せて開く（ウェブ版コントローラや自分自身の検証のため）。
        app.Urls.Add($"http://{s.Bind}:{s.Info.Port}");
        if (s.Bind != "127.0.0.1" && s.Bind != "localhost" && s.Bind != "0.0.0.0" && s.Bind != "*")
            app.Urls.Add($"http://127.0.0.1:{s.Info.Port}");

        app.UseWebSockets();
        app.MapMcp();

        // ウェブ版の仮想コントローラ
        app.MapGet("/vcon.html", () => Results.Content(s.Web.Harness, "text/html; charset=utf-8"));
        app.MapGet("/vcon/design", () => Results.Content(s.Web.GetDesign(), "text/html; charset=utf-8"));
        app.MapPost("/vcon/reset", async () => { s.Web.Reset(); await s.Web.BroadcastReloadAsync(); return Results.Ok(); });
        app.Map("/vcon/ws", ctx => HandleWebSocketAsync(ctx, s));

        // コントローラ間接続の薄い受け口。他の LLMCon が事象を検出したとき、ここへ作用を直接送ってくる。
        app.MapPost("/connect/apply", async (HttpContext ctx) =>
        {
            string body;
            using (var r = new StreamReader(ctx.Request.Body)) body = await r.ReadToEndAsync();
            ApplyConnectionAction(body, s);
            return Results.Ok();
        });

        s.ConfigureExtra?.Invoke(app);
        return app;
    }

    // ── ウェブ版コントローラからの入力を受け取り、エンジンへ流し込む ──
    private static async Task HandleWebSocketAsync(HttpContext ctx, LlmConServices s)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var sock = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = s.Web.Register(sock);
        var buf = new byte[8192];
        try
        {
            while (sock.State == WebSocketState.Open)
            {
                var res = await sock.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count > 0) HandleWebMessage(Encoding.UTF8.GetString(buf, 0, res.Count), s);
            }
        }
        catch { /* 切断は無視 */ }
        finally { s.Web.Unregister(id); }
    }

    private static void HandleWebMessage(string json, LlmConServices s)
    {
        long qpc = System.Diagnostics.Stopwatch.GetTimestamp();   // 実測用（受信時刻）
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            if (t == "btn")
            {
                var b = root.GetProperty("b").GetString();
                var d = root.GetProperty("d").GetBoolean();
                s.WebMessageObserver?.Invoke("btn", b, d, 0, qpc);
                if (!string.IsNullOrEmpty(b)) s.Engine.SetSoftwareButton(b, d);
            }
            else if (t == "miclvl")
            {
                s.WebMessageObserver?.Invoke("miclvl", null, false, root.GetProperty("v").GetDouble(), qpc);
            }
            else if (t == "uirules")
            {
                // ページ（チェックボックスなどのUI）由来の改変ルール層を丸ごと差し替える。
                var rules = JsonSerializer.Deserialize<MappingRule[]>(root.GetProperty("rules").GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                s.Engine.SetUiMapping(rules ?? Array.Empty<MappingRule>());
            }
            else if (t == "miccfg")
            {
                // ページからのマイク設定。{on, b, thr, low, mode}（省略した項目は変えない）
                bool on = root.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
                string? b2 = root.TryGetProperty("b", out var bb) ? bb.GetString() : null;
                double? thr = root.TryGetProperty("thr", out var tt) ? tt.GetDouble() : null;
                double? low = root.TryGetProperty("low", out var ll) ? ll.GetDouble() : null;
                string? mode = root.TryGetProperty("mode", out var mm) ? mm.GetString() : null;
                s.Mic.Configure(on, b2, thr, low, mode);
            }
            else if (t == "stick")
            {
                var side = root.GetProperty("s").GetString();
                double x = root.GetProperty("x").GetDouble();
                double y = root.GetProperty("y").GetDouble();
                bool left = side is "left" or "l" or "ls";
                short sx = (short)Math.Clamp(x * 32767.0, short.MinValue, short.MaxValue);
                short sy = (short)Math.Clamp(y * 32767.0, short.MinValue, short.MaxValue);
                s.Engine.SetSoftwareStick(left, sx, sy);
            }
        }
        catch { /* 壊れたメッセージは無視 */ }
    }

    // ── コントローラ間接続: 受け取った作用を自分のエンジンへ適用する ──
    private static readonly JsonSerializerOptions ConnJson = new() { PropertyNameCaseInsensitive = true };

    private static void ApplyConnectionAction(string body, LlmConServices s)
    {
        try
        {
            var a = JsonSerializer.Deserialize<ConnApply>(body, ConnJson);
            if (a is null) return;
            string from = string.IsNullOrEmpty(a.From) ? "?" : a.From!;
            switch ((a.Kind ?? "").ToLowerInvariant())
            {
                case "mapping":
                    // 相手の標準のマッピングは壊さず、別の層として時間つきで重ねる。
                    s.Engine.AddConnectionRules(a.Rules ?? Array.Empty<MappingRule>(), a.DurationSec ?? 1.0);
                    s.Events.Add("recv", $"from {from} [mapping] {a.Rules?.Length ?? 0} rule(s) for {a.DurationSec ?? 1.0}s");
                    break;
                case "inject_tap":
                    _ = InjectTapAsync(s.Engine, a.Buttons ?? Array.Empty<string>(), a.Frames ?? 3);
                    s.Events.Add("recv", $"from {from} [inject_tap] [{string.Join("+", a.Buttons ?? Array.Empty<string>())}] {a.Frames ?? 3}f");
                    break;
                case "inject_macro":
                    if (!string.IsNullOrWhiteSpace(a.Macro)) _ = s.Macros.RunAsync(a.Macro!);
                    s.Events.Add("recv", $"from {from} [inject_macro] '{a.Macro}'");
                    break;
            }
        }
        catch { /* 壊れた作用は無視する */ }
    }

    // 注入のタップ。LLM注入経路へボタンを立て、フレームぶん待ってから下ろす。
    private static async Task InjectTapAsync(ControllerEngine engine, string[] buttons, int frames)
    {
        var valid = new List<string>();
        foreach (var b in buttons) if (ControllerEngine.IsKnownButton(b)) valid.Add(b);
        if (valid.Count == 0) return;
        foreach (var b in valid) engine.SetLlmButton(b, true);
        try { await Task.Delay((int)Math.Max(1, frames * 1000.0 / engine.Fps)); }
        finally { foreach (var b in valid) engine.SetLlmButton(b, false); }
    }
}
