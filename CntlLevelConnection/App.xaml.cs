using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CntlLevelConnection;

public partial class App : Application
{
    /// <summary>UI と MCP サーバとウェブのコントローラで共有する唯一のエンジン。</summary>
    public ControllerEngine Engine { get; } = new();
    public WebController Web { get; } = new();
    public EventLog Events { get; } = new();
    public MacroEngine Macros { get; }
    public ConnectionManager Connections { get; }
    public int McpPort { get; private set; } = 8777;
    public string InstanceName { get; private set; } = "LLMCon";

    /// <summary>
    /// MCP サーバが待ち受けるアドレス。既定はループバック（127.0.0.1）で、この機械の中からしか届かない。
    /// --bind か、プロファイルの bind 項目を明示したときだけ広がる。LLMCon には認証の仕組みが無いので、
    /// 広げるときは Tailscale のような閉じたネットワークのアドレスだけに絞ること（0.0.0.0 は勧めない）。
    /// </summary>
    public string McpBind { get; private set; } = "127.0.0.1";

    /// <summary>
    /// 使う出力先。"vigem"（既定。Windows の仮想コントローラ）、"websocket"（つながっているページへ配る）、
    /// または両方を "vigem+websocket" のように「+」で並べる。--sink かプロファイルの sink で指定する。
    /// </summary>
    public string Sinks { get; private set; } = "vigem";

    public bool McpRunning { get; private set; }

    /// <summary>マイク遅延実測（実験用・--miclab で有効）。通常は null。</summary>
    public MicLab? Lab { get; private set; }

    /// <summary>マイクのしきい値ボタン（信号経路はネイティブ。実測により決定）。</summary>
    public MicInput Mic { get; private set; } = null!;

    /// <summary>物理パッドをゲームから隠す仕組み（HidHide）。導入されていなければ使えない状態で残る。</summary>
    public HidHidePadHider? Hider { get; private set; }

    /// <summary>プロファイルでデザインが固定されているか（固定中はデザイン変更系のツールを拒否する）。</summary>
    public bool UiLocked { get; private set; }

    /// <summary>
    /// ネイティブのウィンドウの中身。status（状態の表示だけ。既定）、fighting（マイクのつまみも出す）、
    /// controller（従来のソフトウェアコントローラ。開発用）。
    /// </summary>
    public string UiMode { get; private set; } = "status";

    /// <summary>タスクトレイのメニューから「終了」を選んだときだけ真になる（ウィンドウを閉じてもトレイに残すため）。</summary>
    public bool Exiting { get; private set; }

    private WebApplication? _mcp;
    private string? _trayIconPath;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _win;

    public App()
    {
        Macros = new MacroEngine(Engine);                 // エンジンの初期化子は既に走っている
        Connections = new ConnectionManager(Events);
        Engine.HumanEdges = Connections.OnHumanEdges;     // 人間入力のエッジを接続の事象検出へ流す
        Engine.Events = Events;                           // 封じられた押下も同じ記録に残す
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // コマンドラインとプロファイルの読み取りは、Core の共通部品が行う
        // （画面を持たないホストと同じ読み方にするため）。
        // --miclab（マイク遅延の実測。Windows の実験用）だけは、ここで読む。
        var a = e.Args;
        bool micLab = false; string? micLabLog = null, micLabRender = null, micLabCapture = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--miclab")
            {
                micLab = true;
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) micLabLog = a[i + 1];
            }
            if (a[i] == "--miclab-render" && i + 1 < a.Length) micLabRender = a[i + 1];
            if (a[i] == "--miclab-capture" && i + 1 < a.Length) micLabCapture = a[i + 1];
        }

        var opts = LlmConOptions.Parse(a);
        if (opts.Warning != null)
            MessageBox.Show(opts.Warning, opts.Name, MessageBoxButton.OK, MessageBoxImage.Warning);

        InstanceName = opts.Name;
        McpPort = opts.Port;
        McpBind = opts.Bind;
        Sinks = opts.Sink;
        UiLocked = opts.LockDesign;
        UiMode = opts.Ui;
        _trayIconPath = opts.IconPath;

        // 起動時のデザイン（プロファイルの designHtml が preset より優先）
        if (opts.DesignHtml != null) Web.SetUi(opts.DesignHtml);
        else if (opts.Preset != null)
        {
            var html = ControllerPresets.Get(opts.Preset);
            if (html != null) Web.SetUi(html);
        }

        Connections.SelfLabel = $"{InstanceName}@{McpPort}";   // 受け手側の記録に残す自分の識別
        Web.PageTitle = InstanceName;                          // ブラウザのタブに内部の名前を出さない

        // Windows に固有の実装を、Core の抽象へ差し込む。
        // 高分解能タイマーは winmm、物理パッドの読み取りは Windows.Gaming.Input を使う。
        HiResTimer.Use(new WinMmHiResTimer());

        // 出力先と物理パッドの読み取りを渡してループを開始する。
        // 失敗しても MCP と GUI は起動し、状態を GUI に表示する。
        var padSource = new WgiPadSource();
        padSource.UsePadProfiles(opts.Pads);                            // プロファイルの機体ごとの割り当て
        if (opts.PadAxes != null) padSource.SetAxisMap(opts.PadAxes);   // 全体に効く古い形の指定
        Engine.Start(BuildSinks(), padSource);

        // 起動した時点から効かせる改変ルール（プロファイルの rules）。
        // 時間の窓は、この呼び出しから数え始める（Issue #22 の「始めの30秒だけ封じる」縛り）。
        if (opts.Rules is { Length: > 0 }) Engine.SetMapping(opts.Rules);

        // 物理パッドをゲームから隠す仕組み（Issue #12）。組み立てた時点で、前回が異常終了だった
        // 場合の後始末（全体の停止と、前回隠したものを戻すこと）が済む。
        Hider = new HidHidePadHider(padSource, InstanceName);
        Engine.UsePadHider(Hider);
        if (opts.HidePads) Hider.SetHiding(true);

        // マイクのしきい値ボタン。プロファイルに既定値があれば適用する。
        Mic = new MicInput(Engine);
        if (opts.Mic is { } m)
        {
            try { Mic.Configure(m.Enabled, m.Button, m.Threshold, m.Low, m.Mode); }
            catch { /* プロファイルのマイク設定が壊れていても起動は続ける */ }
        }
        // 較正した結果があれば、プロファイルの既定値より優先する。しきい値は部屋とマイクで決まるので、
        // 一度決めたものが次の起動でも使えないと、毎回やり直すことになる。
        LoadMicCalibration();

        if (micLab)
        {
            try
            {
                Lab = new MicLab(Engine, micLabLog ?? Path.Combine(Path.GetTempPath(), "miclab-events.jsonl"), micLabRender, micLabCapture);
                Lab.Start();
            }
            catch (Exception ex)
            {
                Lab = null;
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "miclab-error.txt"), ex.ToString()); } catch { /* ignore */ }
            }
        }

        StartMcpHost();

        // マイクのレベルをページへ配る（メーター表示用。マイクが有効なときだけ・約10Hz）
        var micTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        micTimer.Tick += (_, _) =>
        {
            if (Mic.Enabled)
                _ = Web.BroadcastTextAsync($"{{\"t\":\"miclvl\",\"v\":{Mic.Level:F4}}}");
        };
        micTimer.Start();

        _win = new MainWindow(Engine, this);
        // 常駐アプリなので、起動と同時にトレイへ隠れる形も選べる（プロファイルの startHidden）。
        // 隠して起動しても、トレイのアイコンからいつでも出せる。
        if (!opts.StartHidden) _win.Show();

        SetupTray();
    }

    /// <summary>Sinks の指定（"vigem"、"websocket"、"vigem+websocket" など）から出力先を作る。</summary>
    private List<IPadSink> BuildSinks()
    {
        var list = new List<IPadSink>();
        foreach (var raw in Sinks.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "vigem": list.Add(new ViGEmSink()); break;
                case "websocket" or "ws": list.Add(new WebSocketSink(Web)); break;
                case "none": break;
            }
        }
        if (list.Count == 0) list.Add(new ViGEmSink());   // 指定が壊れていたら、これまでどおりの動きにする
        return list;
    }

    // ── タスクトレイ常駐 ──────────────────────────────────
    private void SetupTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                // 吹き出しに出る文字。利用者に見せる製品では、名前だけにする（Issue #20）。
                Text = UiMode == "fighting" ? InstanceName : $"{InstanceName}  http://{McpBind}:{McpPort}/",
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            // 開くたびに作り直す。反転やマイクの状態は、ウェブ版や MCP のツールからも
            // 変わるので、開いた時点の状態を必ず映すためである。
            menu.Opening += (_, _) => BuildTrayMenu(menu);
            BuildTrayMenu(menu);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ToggleWindow();
        }
        catch { /* トレイが作れなくても本体は動かす */ }
    }

    /// <summary>
    /// タスクトレイのメニューを組み立て直す。
    ///
    /// KuriCon はブラウザを開かずに使えることを要件にしているので、反転もマイクも
    /// ここで完結させる。同じ設定はウェブ版と MCP のツールからも変えられるため、
    /// 開くたびに作り直して、いまの状態をそのまま映す。
    /// </summary>
    private void BuildTrayMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        // 見出しに出すのは名前だけにする。ポートの番号は、格闘ゲームの利用者には意味が無い（Issue #20）。
        // 研究用に複数のインスタンスを並べる LLMCon では、どれがどれか分かる必要があるので出す。
        menu.Items.Add(UiMode == "fighting" ? InstanceName : $"{InstanceName}  (ポート {McpPort})").Enabled = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // ── 反転 ───────────────────────────────────────────
        // 対戦の途中で切り替えることがあるので、階層を深くしない。
        // いま何が反転しているかを親の見出しに出して、開かなくても分かるようにする。
        var inverted = TrayButtons.Where(b => Engine.IsUiInverted(b)).Select(ButtonLabel).ToList();
        var invHead = inverted.Count == 0
            ? "反転（なし）"
            : $"反転（{string.Join(" ", inverted)}）";
        var invMenu = new System.Windows.Forms.ToolStripMenuItem(invHead);
        foreach (var b in TrayButtons)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(ButtonLabel(b))
            {
                Checked = Engine.IsUiInverted(b),
                CheckOnClick = true,
            };
            var tag = b;
            item.Click += (_, _) => Engine.SetUiInvert(tag, item.Checked);
            invMenu.DropDownItems.Add(item);
        }
        invMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        invMenu.DropDownItems.Add("すべて反転", null, (_, _) =>
        { foreach (var b in TrayButtons) Engine.SetUiInvert(b, true); });
        invMenu.DropDownItems.Add("すべて解除", null, (_, _) => Engine.ClearUiInverts());
        menu.Items.Add(invMenu);

        // ── マイク ─────────────────────────────────────────
        var micOn = Mic?.Enabled ?? false;
        // 利用者に見せる言葉は、平均的な格闘ゲームのプレイヤーに通じるものにする（Issue #20）。
        // 「しきい値」は使わず、「反応する大きさ」と書く。
        var micHead = Mic == null ? "マイク（使えません）"
                    : micOn ? $"マイク（{ButtonLabel(Mic.Button)}、反応する大きさ {Mic.Threshold:F2}）"
                            : "マイク（切）";
        var micMenu = new System.Windows.Forms.ToolStripMenuItem(micHead) { Enabled = Mic != null };
        if (Mic != null)
        {
            var onOff = new System.Windows.Forms.ToolStripMenuItem("有効にする")
            { Checked = micOn, CheckOnClick = true };
            onOff.Click += (_, _) => Mic.Configure(onOff.Checked, null, null, null, null);
            micMenu.DropDownItems.Add(onOff);

            micMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            var assign = new System.Windows.Forms.ToolStripMenuItem("声を割り当てるボタン");
            foreach (var b in TrayButtons)
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(ButtonLabel(b))
                { Checked = string.Equals(Mic.Button, b, StringComparison.OrdinalIgnoreCase) };
                var tag = b;
                item.Click += (_, _) => Mic.Configure(Mic.Enabled, tag, null, null, null);
                assign.DropDownItems.Add(item);
            }
            micMenu.DropDownItems.Add(assign);

            micMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            micMenu.DropDownItems.Add("マイクを調整する", null, (_, _) => CalibrateMic());
        }
        menu.Items.Add(micMenu);

        // ── 物理パッドの選択 ────────────────────────────────
        // 状態を見るだけの窓（ui=status と ui=fighting）には選ぶものを置かないので、
        // ここが唯一の入口になる。ブラウザを開かずに使えることが要件である。
        BuildPadMenu(menu);

        // ── 物理パッドをゲームから隠す ──────────────────────
        // 隠したことを利用者が見失うと「コントローラが認識されない」という形で困る。
        // 何を隠しているかを見出しに出し、ここから必ず解除できるようにしておく。
        BuildHideMenu(menu);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("ウェブ版コントローラを開く", null, (_, _) => OpenInBrowser($"http://127.0.0.1:{McpPort}/vcon.html"));
        menu.Items.Add("ウィンドウを表示 / 隠す", null, (_, _) => ToggleWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());
    }

    /// <summary>
    /// 読み取る物理パッドを選ぶ項目を組み立てる。開くたびに一覧を取り直すので、
    /// あとから挿した機体もそのまま出る。
    /// </summary>
    private void BuildPadMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        var pads = Engine.ListPads();
        var sel = Engine.SelectedPadId;
        var current = sel == null ? "なし" : pads.FirstOrDefault(p => p.Id == sel)?.Name ?? "選択中（一覧に無い）";
        var padMenu = new System.Windows.Forms.ToolStripMenuItem($"使うコントローラ（{current}）");

        var none = new System.Windows.Forms.ToolStripMenuItem("なし（ソフトウェアのみ）") { Checked = sel == null };
        none.Click += (_, _) => Engine.SelectPad(null);
        padMenu.DropDownItems.Add(none);

        if (pads.Count > 0) padMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        foreach (var p in pads)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(p.Name) { Checked = p.Id == sel };
            var id = p.Id;
            item.Click += (_, _) => Engine.SelectPad(id);
            padMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(padMenu);
    }

    /// <summary>
    /// 物理パッドをゲームから隠す項目を組み立てる。
    ///
    /// 隠すと、その物理パッドは LLMCon 以外のすべての処理から見えなくなる。ゲームには
    /// 改変後の入力だけが届くようになるが、利用者から見ると「コントローラが消えた」ようにも
    /// 見えるので、いまの状態を見出しに出し、ここから解除できるようにしておく。
    /// </summary>
    private void BuildHideMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        if (Hider == null) return;

        // 見せる言葉は、何のための機能かが分かるものにする（Issue #20）。
        // 「隠蔽」「物理パッド」は使わず、「コントローラを他のアプリから隠す」と書く。
        if (!Hider.Available)
        {
            var missing = new System.Windows.Forms.ToolStripMenuItem("コントローラを他のアプリから隠す（使えません）") { Enabled = false };
            menu.Items.Add(missing);
            return;
        }

        var selected = Engine.SelectedPadId != null;
        var head = !Hider.Requested
            ? "コントローラを他のアプリから隠す（切）"
            : selected ? $"コントローラを他のアプリから隠す（入・{Engine.ListPads().FirstOrDefault(p => p.Id == Engine.SelectedPadId)?.Name ?? "対象なし"}）"
                       : "コントローラを他のアプリから隠す（入・コントローラ未選択）";
        var hideMenu = new System.Windows.Forms.ToolStripMenuItem(head);

        var toggle = new System.Windows.Forms.ToolStripMenuItem("隠す")
        { Checked = Hider.Requested, CheckOnClick = true };
        toggle.Click += (_, _) =>
        {
            var result = Hider.SetHiding(toggle.Checked);
            NotifyTray("コントローラを隠す", result.Split('\n')[0]);
        };
        hideMenu.DropDownItems.Add(toggle);

        hideMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        hideMenu.DropDownItems.Add("いまの状態を見る", null, (_, _) =>
            MessageBox.Show(Hider.LastMessage == "" ? Hider.Describe() : Hider.LastMessage,
                            "コントローラを隠す", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(hideMenu);
    }

    /// <summary>
    /// マイクを較正する。環境音と声をそれぞれ数秒測り、ピークの幾何平均をしきい値にする。
    ///
    /// しきい値の調整はメーターを見ながら行うものなので、メニューの項目には向かない。
    /// 利用者はメーターを読む必要がなく、静かにして数秒待ち、そのあと声を出すだけでよい。
    /// 手法は experiments/mic-latency-2026-07 の較正と同じ考え方である。
    /// </summary>
    public async void CalibrateMic()
    {
        if (Mic == null) return;
        var err = Mic.StartListening();
        if (err != "") { NotifyTray("マイクの調整", err); return; }

        async Task<double> PeakOver(int seconds)
        {
            double peak = 0;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                peak = Math.Max(peak, Mic.Level);
                await Task.Delay(50);
            }
            return peak;
        }

        // 案内を読んでから息を吸って声を出すまでの間があるので、測る時間は長めに取る。
        // 短いと、声を出す前に測り終えてしまい、環境音と変わらない値でしきい値が決まる。
        const int MeasureSeconds = 5;
        const int LeadSeconds = 2;

        // 合図はビープ音で出す。画面の通知は見ていないことがあるが、音なら分かる。
        // 実際、通知だけにしていたときは、声を出す前に測り終えてしまい、環境音とほぼ同じ値で
        // しきい値が決まってしまった。鳴らしたあと少し待つのは、この音自体をマイクが拾うためである。
        void Cue(int times)
        {
            for (int i = 0; i < times; i++)
            {
                try { Console.Beep(1200, 120); } catch { /* 鳴らせない環境でも続ける */ }
                System.Threading.Thread.Sleep(90);
            }
        }

        NotifyTray("マイクの調整（1/2）",
            $"「ピッ」と1回鳴ったら、{MeasureSeconds}秒間、静かにしてください。まわりの音の大きさを測ります。");
        await Task.Delay(LeadSeconds * 1000);
        Cue(1);
        await Task.Delay(800);                      // ビープ音がマイクに残る分を捨てる
        double quiet = await PeakOver(MeasureSeconds);

        NotifyTray("マイクの調整（2/2）",
            $"「ピッピッ」と2回鳴ったら、{MeasureSeconds}秒間、途切れないように声を出してください。");
        await Task.Delay(LeadSeconds * 1000);
        Cue(2);
        await Task.Delay(800);
        double voice = await PeakOver(MeasureSeconds);

        try { Console.Beep(400, 300); } catch { }   // 終わりの合図

        if (voice < 0.005)
        {
            NotifyTray("マイクの調整",
                "声が聞こえませんでした。マイクが挿さっているか確かめて、もう一度お試しください。");
            return;
        }

        // 声と環境音の差が小さいときは、しきい値を決めても使い物にならない。
        // 環境音で誤って反応するか、声で反応しないかのどちらかになる。やり直してもらう。
        if (voice < quiet * 3.0)
        {
            NotifyTray("マイクの調整",
                $"声と環境音の差が小さすぎます（環境音 {quiet:F3}、声 {voice:F3}）。" +
                "しきい値は変えていません。案内が出てから声を出し、途切れないように続けてください。");
            return;
        }

        // しきい値は、環境音と声のあいだの、声寄りに置く。
        //
        // 以前は幾何平均（ちょうど真ん中）にしていたが、それでは環境音のたかだか3倍ほどにしかならず、
        // 物音で反応してしまう。2026/8/7 にストリートファイター6で実際に使って分かった。
        // 較正のあいだの環境音は、そのときたまたま静かなだけのことがあるので、余裕を見る必要がある。
        //
        // 声の側へ寄せたうえで、環境音からは3倍以上離し、声の6割は超えないようにする。
        // 上限があるのは、寄せすぎて声で越えられなくなるのを防ぐためである。
        double thr = Math.Pow(quiet, 0.35) * Math.Pow(voice, 0.65);
        thr = Math.Max(thr, quiet * 3.0);
        thr = Math.Min(thr, voice * 0.6);

        // 解除のしきい値（ヒステリシス）も、環境音より確実に上に置く。
        double low = Math.Max(thr * 0.6, Math.Min(quiet * 1.5, thr * 0.9));

        Mic.Configure(true, null, thr, low, null);
        SaveMicCalibration(thr, low);
        NotifyTray("マイクの調整",
            $"しきい値を {thr:F3} にしました（環境音 {quiet:F3}、声 {voice:F3}、環境音の {thr / Math.Max(quiet, 1e-6):F1} 倍）。");
    }

    /// <summary>
    /// この製品が覚えごとを置く場所。フォルダの名前は製品の名前にする。利用者が AppData を
    /// 覗いたときに、身に覚えのない名前が出てこないようにするためである（Issue #20）。
    /// </summary>
    private string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        new string(InstanceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()));

    /// <summary>
    /// 較正で決めたしきい値を覚えておく場所。プロファイルには書き戻さない
    /// （プロファイルは配布物として手で整えるものなので、こちらが書き換えると混乱する）。
    /// 部屋とマイクによって決まる値なので、機械ごとに持つのが素直である。
    /// </summary>
    private string MicCalibrationPath => Path.Combine(DataDir, "mic.json");

    /// <summary>較正の結果を保存する。次の起動でも同じしきい値で始められるようにする。</summary>
    private void SaveMicCalibration(double threshold, double low)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MicCalibrationPath)!);
            File.WriteAllText(MicCalibrationPath,
                $"{{\"threshold\":{threshold:F4},\"low\":{low:F4}}}");
        }
        catch { /* 保存できなくても、この起動のあいだは効いている */ }
    }

    /// <summary>
    /// 前に較正した結果があれば読んで当てる。プロファイルの既定値より優先する。
    /// 較正しないまま使うと、部屋によっては声で反応しないか、物音で反応する。
    /// </summary>
    private void LoadMicCalibration()
    {
        try
        {
            if (!File.Exists(MicCalibrationPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(MicCalibrationPath));
            var root = doc.RootElement;
            double? thr = root.TryGetProperty("threshold", out var t) ? t.GetDouble() : null;
            double? low = root.TryGetProperty("low", out var l) ? l.GetDouble() : null;
            if (thr is double v && v > 0) Mic.Configure(Mic.Enabled, null, v, low, null);
        }
        catch { /* 壊れていたら、プロファイルの既定値のまま進める */ }
    }

    /// <summary>タスクトレイから短い案内を出す。較正の手順を伝えるのに使う。</summary>
    private void NotifyTray(string title, string text)
    {
        try { _tray?.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info); }
        catch { /* 通知が出せなくても、較正そのものは進める */ }
    }

    /// <summary>
    /// タスクトレイに出すボタンの一覧。反転の対象と、声を割り当てられる先である。
    ///
    /// スティックの押し込み（LS と RS。Xbox の表記では L3 と R3）も入れてある。以前は
    /// 「格闘ゲームでは使わない」として外していたが、これは誤りであった。ストリート
    /// ファイター6で割り当てる項目は、弱中強のパンチとキックで6つ、そこに投げ、パリィ、
    /// ドライブインパクト、パンチ同時押し、キック同時押しが加わる。ABXY と LB RB LT RT の
    /// 8個では収まらないので、同時押しを L3 と R3 に置く配置が実際に使われている
    /// （2026/8/7 に調べた。Issue #21）。
    ///
    /// Guide だけは外してある。押しても Steam や Xbox のオーバーレイに飲まれてしまい、
    /// ゲームまで届かないためである。
    ///
    /// なお、エンジンも仮想パッドへの出力も、ここに出していないものを含めて Xbox の
    /// デジタルボタン15個をすべて扱える。制限があるのはこの一覧だけである。
    /// </summary>
    private static readonly string[] TrayButtons =
    {
        "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS",
        "DUp", "DDown", "DLeft", "DRight", "Start", "Back",
    };

    /// <summary>
    /// ボタンを画面に出すときの名前。いまの Xbox のコントローラの呼び名に合わせる。
    ///
    /// Start と Back は Xbox 360 の時代の呼び名で、XInput の定数の名前でもある。実物の
    /// コントローラでは、Xbox One より後は「メニュー」と「ビュー」に変わっているため、
    /// 利用者が手元の機体を見て探すときに見つからない。そこで表示だけ読み替える。
    ///
    /// 内部のタグは XInput や ViGEm と同じ Start と Back のままにしてある。設定ファイルの
    /// mic.button や、MCP の呼び出しで使う名前を変えると、これまでの設定が動かなくなる。
    /// </summary>
    private static string ButtonLabel(string tag) => tag switch
    {
        "Start" => "Menu",
        "Back" => "View",
        _ => tag,
    };

    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            if (_trayIconPath != null) return new System.Drawing.Icon(_trayIconPath);
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var ic = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ic != null) return ic;
            }
        }
        catch { /* ignore */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void ToggleWindow()
    {
        if (_win == null) return;
        if (_win.IsVisible) { _win.Hide(); }
        else { _win.Show(); _win.WindowState = WindowState.Normal; _win.Activate(); }
    }

    private static void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>本当に終了する（トレイのメニューから呼ぶ）。ウィンドウの×はトレイに隠すだけ。</summary>
    public void ExitApp()
    {
        Exiting = true;
        if (_tray != null) { _tray.Visible = false; }
        _win?.Close();
        Shutdown();
    }

    /// <summary>
    /// MCP サーバとウェブの配信を起動する。組み立ては Core の共通部品が行う
    /// （画面を持たないホストと同じものを使うため）。ここでは Windows に固有の口だけを足す。
    /// </summary>
    private void StartMcpHost()
    {
        try
        {
            var services = new LlmConServices
            {
                Engine = Engine,
                Web = Web,
                Macros = Macros,
                Connections = Connections,
                Events = Events,
                Mic = Mic,
                Info = new LlmConInfo(InstanceName, McpPort, UiLocked, McpBind),
                Bind = McpBind,
                ConsoleLogging = false,          // WPF なのでコンソールへのログは出さない
                WebMessageObserver = OnWebMessage,
                ConfigureExtra = ConfigureWindowsRoutes,
            };
            _mcp = LlmConHost.Build(services);
            _ = _mcp.RunAsync();
            McpRunning = true;
        }
        catch
        {
            McpRunning = false;   // MCP ホストが失敗しても GUI は動かす
        }
    }

    /// <summary>
    /// ウェブ版コントローラからのメッセージを覗く。マイク遅延の実測（--miclab）のときだけ意味がある。
    /// </summary>
    private void OnWebMessage(string kind, string? button, bool down, double value, long qpc)
    {
        if (Lab is null) return;
        if (kind == "btn" && down && string.Equals(button, "B", StringComparison.OrdinalIgnoreCase))
            Lab.OnBrowserDetect(qpc);
        else if (kind == "miclvl")
            Lab.OnBrowserLevel(value, qpc);
    }

    /// <summary>Windows に固有の経路。マイク遅延の実測（--miclab のときだけ）。</summary>
    private void ConfigureWindowsRoutes(WebApplication app)
    {
        if (Lab is null) return;
        var lab = Lab;
        app.MapGet("/miclab.html", () => Results.Content(MicLab.PageHtml, "text/html; charset=utf-8"));
        app.MapPost("/miclab/beep", () => Results.Content($"{{\"t0\":{lab.PlayBeep()}}}", "application/json"));
        app.MapGet("/miclab/status", () => Results.Content(lab.StatusJson(), "application/json"));
        app.MapGet("/miclab/threshold", () => Results.Content(lab.ThresholdJson(), "application/json"));
        app.MapPost("/miclab/mode", async (HttpContext ctx) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            using var doc = JsonDocument.Parse(await r.ReadToEndAsync());
            lab.SetMode(doc.RootElement.GetProperty("mode").GetString() ?? "off");
            return Results.Ok();
        });
        app.MapPost("/miclab/volume", async (HttpContext ctx) =>
        {
            using var r = new StreamReader(ctx.Request.Body);
            using var doc = JsonDocument.Parse(await r.ReadToEndAsync());
            lab.SetVolume(doc.RootElement.GetProperty("v").GetDouble());
            return Results.Ok();
        });
    }

    /// <summary>
    /// Windows の終了（シャットダウン・再起動・ログオフ）が始まったときに呼ばれる。
    ///
    /// ここで手間をかけてはならない。終了の処理に時間をかけるアプリには、Windows が
    /// 「このアプリがシャットダウンを妨げています」という画面を出す（Issue #23）。
    ///
    /// 妨げていた本体は、窓を閉じる要求を必ず握りつぶしていたことである。ふだんは
    /// 「×で閉じてもトレイに残る」ために必要な動きだが、Windows が終わろうとしている
    /// ときにそれをやると、終了を拒むアプリに見える。ここで Exiting を立てて、素直に
    /// 閉じられるようにする。
    ///
    /// 隠蔽の解除は、時間を区切って行う。全体の停止だけは必ず呼ぶので、コントローラは
    /// 見えるようになる。外し残しは次の起動の掃除で片付く。
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Exiting = true;
        _sessionEnding = true;
        try { Hider?.ReleaseQuickly(); } catch { /* 終了を妨げない */ }
        base.OnSessionEnding(e);
    }

    private bool _sessionEnding;

    protected override void OnExit(ExitEventArgs e)
    {
        // 隠蔽は必ず戻す。戻し損ねると、利用者から見て「コントローラが二度と認識されない」に
        // なるので、他の後始末より先に行う（Issue #12）。
        // Windows の終了のときは、すでに時間を区切って済ませてあるので、ここでは何もしない。
        if (!_sessionEnding) { try { Hider?.Release(); } catch { /* ignore */ } }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { /* ignore */ }
        // Windows の終了のときは、サーバの停止を待たない。待つと終了を妨げる。
        try { if (_sessionEnding) _ = _mcp?.StopAsync(); else _mcp?.StopAsync().Wait(1000); } catch { /* ignore */ }
        Lab?.Dispose();
        Mic?.Dispose();
        Engine.Dispose();
        base.OnExit(e);
    }
}
