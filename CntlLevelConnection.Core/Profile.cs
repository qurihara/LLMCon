using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CntlLevelConnection;

/// <summary>
/// 起動時の設定。コマンドラインの引数と、プロファイル（JSON のファイル）から作る。
/// Windows のアプリと、画面を持たないホストの両方が、同じ読み方をする。
/// </summary>
public sealed class LlmConOptions
{
    public string Name { get; set; } = "LLMCon";
    public int Port { get; set; } = 8777;

    /// <summary>待ち受けるアドレス。既定はループバック（この機械の中からのみ）。</summary>
    public string Bind { get; set; } = "127.0.0.1";

    /// <summary>出力先。"vigem"、"websocket"、または "vigem+websocket"。</summary>
    public string Sink { get; set; } = "vigem";

    /// <summary>デザインを固定するか（固定中はデザイン変更系のツールを拒否する）。</summary>
    public bool LockDesign { get; set; }

    /// <summary>起動時に適用するプリセットの名前（プロファイルの preset）。</summary>
    public string? Preset { get; set; }

    /// <summary>起動時に適用するデザインの HTML（designHtml を読んだ中身）。</summary>
    public string? DesignHtml { get; set; }

    /// <summary>タスクトレイのアイコンのファイル（Windows のみ意味を持つ）。</summary>
    public string? IconPath { get; set; }

    /// <summary>マイクの既定値（プロファイルの mic）。扱えない環境では無視される。</summary>
    public MicOptions? Mic { get; set; }

    /// <summary>物理パッドの軸の割り当て（プロファイルの padAxes）。扱えない環境では無視される。</summary>
    public PadAxisMap? PadAxes { get; set; }

    /// <summary>
    /// 機体ごとの割り当て（プロファイルの pads）。パッドを選んだときに、素性が合うものを当てる。
    /// 実測で分かったとおり、既定の割り当てがそのまま使える機体はほとんど無いので、これが要る。
    /// </summary>
    public List<PadProfile>? Pads { get; set; }

    /// <summary>
    /// ネイティブのウィンドウの中身（プロファイルの ui）。Windows のアプリだけが使う。
    ///
    /// status     状態の表示だけ。LLMCon の既定。設定と動的なデザインはブラウザにある
    /// fighting   状態の表示に、マイクのしきい値のつまみとレベルのメーターを添える
    /// controller 従来のソフトウェアコントローラ。物理パッドが無いときの開発用
    /// </summary>
    public string Ui { get; set; } = "status";

    /// <summary>起動と同時にタスクトレイへ隠れるか（プロファイルの startHidden）。常駐アプリ向け。</summary>
    public bool StartHidden { get; set; }

    /// <summary>
    /// 起動時から、選んだ物理パッドをゲームから隠すか（プロファイルの hidePads）。
    /// 格闘ゲーム用の製品では、これが無いと生の入力が改変を打ち消すので既定で有効にする。
    /// 汎用の LLMCon では、他のアプリから物理パッドが消えると開発の妨げになるので既定は無効である。
    /// </summary>
    public bool HidePads { get; set; }

    /// <summary>
    /// 起動した時点から効かせる改変ルール（プロファイルの rules）。set_mapping に渡すものと同じ形である。
    ///
    /// startSec と endSec を書くと、起動からの経過秒で効く窓を決められる。これにより、
    /// 「始めの30秒だけ強い攻撃のボタンを封じる」といった縛りを、遊ぶ人の側の申告に頼らずに作れる。
    /// 窓を計り直したいときは、MCP の set_mapping を同じ内容で呼ぶ（そこから数え直す）。
    /// </summary>
    public MappingRule[]? Rules { get; set; }

    /// <summary>プロファイルの読み込みで起きた問題（あれば画面や標準出力に出す）。</summary>
    public string? Warning { get; set; }

    public sealed class MicOptions
    {
        public bool Enabled { get; set; }
        public string? Button { get; set; }
        public double? Threshold { get; set; }
        public double? Low { get; set; }
        public string? Mode { get; set; }
    }

    /// <summary>
    /// コマンドラインとプロファイルから設定を作る。
    /// プロファイルの場所は、--profile が無ければ実行ファイルの隣の profile.json を使う。
    /// コマンドラインの明示指定は、プロファイルより優先する。
    /// </summary>
    public static LlmConOptions Parse(string[] args)
    {
        var o = new LlmConOptions();
        int? cliPort = null;
        bool? cliHide = null;
        bool cliHidden = false;
        string? cliName = null, cliBind = null, cliSink = null, cliUi = null, profilePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            // 物理パッドをゲームから隠すか。プロファイルを書き換えずに試せるようにしておく。
            if (args[i] == "--hide-pads") cliHide = true;
            if (args[i] == "--no-hide-pads") cliHide = false;
            if (args[i] == "--ui" && i + 1 < args.Length) cliUi = args[i + 1].Trim().ToLowerInvariant();
            if (args[i] == "--start-hidden") cliHidden = true;
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) cliPort = p;
            if (args[i] == "--name" && i + 1 < args.Length) cliName = args[i + 1];
            if (args[i] == "--bind" && i + 1 < args.Length) cliBind = args[i + 1];
            if (args[i] == "--sink" && i + 1 < args.Length) cliSink = args[i + 1];
            if (args[i] == "--profile" && i + 1 < args.Length) profilePath = args[i + 1];
        }

        // プロファイルの場所。--profile が無ければ、実行ファイルの隣の profile.json を自動で読む
        // （固定運用の製品は、これを同梱するだけでよい。絶対パスの引数に依存しない）。
        string baseDir = AppContext.BaseDirectory;
        if (profilePath == null)
        {
            var side = Path.Combine(baseDir, "profile.json");
            if (File.Exists(side)) profilePath = side;
        }
        else if (!Path.IsPathRooted(profilePath))
        {
            profilePath = Path.Combine(baseDir, profilePath);
        }

        if (profilePath != null && File.Exists(profilePath))
        {
            try
            {
                string profDir = Path.GetDirectoryName(Path.GetFullPath(profilePath)) ?? baseDir;
                using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var pn)) o.Name = pn.GetString() ?? o.Name;
                if (root.TryGetProperty("port", out var pp) && pp.TryGetInt32(out var ppv)) o.Port = ppv;
                if (root.TryGetProperty("bind", out var pb))
                {
                    var b = pb.GetString();
                    if (!string.IsNullOrWhiteSpace(b)) o.Bind = b!.Trim();
                }
                if (root.TryGetProperty("sink", out var ps))
                {
                    var v = ps.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) o.Sink = v!.Trim();
                }
                if (root.TryGetProperty("preset", out var pr)) o.Preset = pr.GetString();
                if (root.TryGetProperty("designHtml", out var dh))
                {
                    var f = dh.GetString();
                    if (!string.IsNullOrEmpty(f))
                    {
                        if (!Path.IsPathRooted(f)) f = Path.Combine(profDir, f);   // プロファイルの場所を基準に解決する
                        if (File.Exists(f)) o.DesignHtml = File.ReadAllText(f);
                    }
                }
                if (root.TryGetProperty("lockDesign", out var ld) && ld.ValueKind == JsonValueKind.True) o.LockDesign = true;
                if (root.TryGetProperty("hidePads", out var hp) && hp.ValueKind == JsonValueKind.True) o.HidePads = true;
                if (root.TryGetProperty("startHidden", out var sh) && sh.ValueKind == JsonValueKind.True) o.StartHidden = true;
                if (root.TryGetProperty("ui", out var pu))
                {
                    var v = (pu.GetString() ?? "").Trim().ToLowerInvariant();
                    if (v is "status" or "fighting" or "controller") o.Ui = v;
                    else if (v.Length > 0) o.Warning = $"プロファイルの ui に \"{v}\" と書かれています。status か fighting か controller のどれかにしてください。status として扱います。";
                }
                if (root.TryGetProperty("icon", out var ic))
                {
                    var f = ic.GetString();
                    if (!string.IsNullOrEmpty(f))
                    {
                        if (!Path.IsPathRooted(f)) f = Path.Combine(profDir, f);
                        if (File.Exists(f)) o.IconPath = f;
                    }
                }
                if (root.TryGetProperty("padAxes", out var pa))
                {
                    var d = PadAxisMap.Default;
                    int GetInt(string n, int fallback) => pa.TryGetProperty(n, out var v) && v.TryGetInt32(out var iv) ? iv : fallback;
                    bool GetBool(string n, bool fallback) => pa.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
                    o.PadAxes = new PadAxisMap(
                        GetInt("lx", d.LX), GetInt("ly", d.LY), GetInt("rx", d.RX), GetInt("ry", d.RY),
                        GetInt("lt", d.LT), GetInt("rt", d.RT), GetBool("invertY", d.InvertY),
                        GetInt("sharedTrigger", d.SharedTrigger));
                }
                if (root.TryGetProperty("pads", out var pads) && pads.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PadProfile>();
                    foreach (var e in pads.EnumerateArray())
                    {
                        var pf = new PadProfile();
                        if (e.TryGetProperty("label", out var el)) pf.Label = el.GetString();
                        if (e.TryGetProperty("vid", out var ev) && ev.TryGetInt32(out var evv)) pf.Vid = evv;
                        if (e.TryGetProperty("pid", out var ep) && ep.TryGetInt32(out var epv)) pf.Pid = epv;
                        if (e.TryGetProperty("nameContains", out var en)) pf.NameContains = en.GetString();
                        if (e.TryGetProperty("axes", out var ea))
                        {
                            var d = PadAxisMap.Default;
                            int A(string n, int f) => ea.TryGetProperty(n, out var v) && v.TryGetInt32(out var iv) ? iv : f;
                            bool B(string n, bool f) => ea.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True : f;
                            pf.Axes = new PadAxisMap(
                                A("lx", d.LX), A("ly", d.LY), A("rx", d.RX), A("ry", d.RY),
                                A("lt", d.LT), A("rt", d.RT), B("invertY", d.InvertY), A("sharedTrigger", d.SharedTrigger));
                        }
                        if (e.TryGetProperty("buttons", out var eb)) pf.Buttons = eb.GetString();
                        list.Add(pf);
                    }
                    if (list.Count > 0) o.Pads = list;
                }
                if (root.TryGetProperty("rules", out var pr2) && pr2.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        var rules = JsonSerializer.Deserialize<MappingRule[]>(pr2.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (rules is { Length: > 0 }) o.Rules = rules;
                    }
                    catch (Exception ex)
                    {
                        // ルールだけが壊れていても、他の設定は生かして起動する。
                        o.Warning = $"プロファイルの rules を読み取れませんでした: {ex.Message}";
                    }
                }
                if (root.TryGetProperty("mic", out var pm))
                {
                    var m = new MicOptions();
                    if (pm.TryGetProperty("enabled", out var me)) m.Enabled = me.ValueKind == JsonValueKind.True;
                    if (pm.TryGetProperty("button", out var mb)) m.Button = mb.GetString();
                    if (pm.TryGetProperty("threshold", out var mt)) m.Threshold = mt.GetDouble();
                    if (pm.TryGetProperty("low", out var ml)) m.Low = ml.GetDouble();
                    if (pm.TryGetProperty("mode", out var mm)) m.Mode = mm.GetString();
                    o.Mic = m;
                }
            }
            catch (Exception ex)
            {
                o.Warning = $"プロファイルの読み込みに失敗しました: {ex.Message}";
            }
        }

        // コマンドラインの明示指定を優先する
        if (cliPort is int cp) o.Port = cp;
        if (cliName != null) o.Name = cliName;
        if (cliBind != null) o.Bind = cliBind.Trim();
        if (cliSink != null) o.Sink = cliSink.Trim();
        if (cliHide is bool ch) o.HidePads = ch;
        if (cliUi is "status" or "fighting" or "controller") o.Ui = cliUi;
        if (cliHidden) o.StartHidden = true;
        return o;
    }
}
