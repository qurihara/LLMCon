using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CntlLevelConnection;

public partial class MainWindow : Window
{
    private readonly ControllerEngine _engine;
    private readonly App _app;

    // キー → ボタンタグ (HANDOFF 6-B)
    private readonly Dictionary<Key, string> _keyMap = new()
    {
        [Key.J]  = "A",  [Key.K] = "B",  [Key.U]  = "X",  [Key.I]  = "Y",
        [Key.Q]  = "LB", [Key.O] = "RB", [Key.D1] = "LT", [Key.D2] = "RT",
        [Key.W]  = "DUp", [Key.S] = "DDown", [Key.A] = "DLeft", [Key.D] = "DRight",
        [Key.Enter] = "Start", [Key.Back] = "Back", [Key.Escape] = "Guide",
        [Key.Z]  = "LS", [Key.X] = "RS",
    };

    public MainWindow(ControllerEngine engine, App app)
    {
        InitializeComponent();
        _engine = engine;
        _app = app;

        string mcp = app.McpRunning ? $" / MCP http://{app.McpBind}:{app.McpPort}" : " / MCP 起動失敗";
        bool plain = app.UiMode == "fighting";   // 利用者に見せる画面では、部品の名前を出さない（Issue #20）
        if (engine.Connected)
        {
            StatusText.Text = $"  ● 接続済み (出力: {engine.SinkNames}){mcp}";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            StatusText.Text = $"  ● 出力の準備に失敗: {engine.LastError}";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            // 出力の用意に失敗したときだけ知らせる（WebSocket だけの構成では出さない）
            if (app.Sinks.Contains("vigem", StringComparison.OrdinalIgnoreCase))
                MessageBox.Show(
                    plain
                        ? $"コントローラを使うための部品が見つかりません。\n\n{app.InstanceName} を入れ直すと直ることがあります。\n入れ直したあとは、パソコンを再起動してください。"
                        : $"ViGEm Bus Driver に接続できませんでした。\n\nViGEmBus がインストール・起動されているか確認してください。\n\n詳細: {engine.LastError}",
                    app.InstanceName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 題には製品の名前だけを出す。開発用の画面のときだけ、そうと分かるようにする。
        Title = app.UiMode == "controller" ? $"{app.InstanceName}（開発用）" : app.InstanceName;

        PopulatePresets();
        RescanPads();
        Loaded += (_, _) => RescanPads();

        ApplyUiMode(app.UiMode);

        // ループ統計を定期表示
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            double hz = _engine.LoopHz;
            double addedMs = (hz > 0 ? 1000.0 / hz / 2.0 : 0) + 0.5;
            LoopText.Text = $"ループ {hz:F0} Hz / 追加遅延 ~{addedMs:F1} ms";
            if (StatusArea.Visibility == Visibility.Visible) UpdateStatusPanel(hz, addedMs);
            if (MicArea.Visibility == Visibility.Visible) UpdateMicPanel();
        };
        timer.Start();
    }

    // ── ウィンドウの中身を製品ごとに変える（Issue #13）─────────
    //
    // LLMCon は「無味無臭の常駐アプリ」で、動的なデザインはブラウザに置く。したがって
    // ネイティブのウィンドウは状態の表示だけにする。KuriCon はブラウザなしで完結させたいので、
    // マイクのしきい値を詰めるためのつまみとメーターを足す。従来のソフトウェアコントローラは
    // 消さずに残す。物理パッドが無いときに手早く入力を送れるので、開発では今も使う。
    private void ApplyUiMode(string mode)
    {
        bool controller = mode == "controller";
        bool fighting = mode == "fighting";

        ControllerArea.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        StickArea.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        FooterArea.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        StatusArea.Visibility = controller ? Visibility.Collapsed : Visibility.Visible;
        MicArea.Visibility = fighting ? Visibility.Visible : Visibility.Collapsed;

        if (controller) return;

        // 見出しは製品の名前にする。接続の状態は下の状態パネルに出るので、ここでは繰り返さない。
        HeaderText.Text = _app.InstanceName;
        StatusText.Visibility = Visibility.Collapsed;

        // 状態を見るだけの窓は小さくてよい。物理パッドの選択はタスクトレイにある。
        SizeToContent = SizeToContent.Height;
        Width = 560;
        Height = fighting ? 340 : 220;
        if (fighting && _app.Mic != null)
        {
            MicThresholdSlider.Value = _app.Mic.Threshold;
            UpdateMicPanel();
        }
        UpdateStatusPanel(_engine.LoopHz, 0);
    }

    private void UpdateStatusPanel(double hz, double addedMs)
    {
        var padId = _engine.SelectedPadId;
        var padName = padId == null
            ? "なし"
            : _engine.ListPads().FirstOrDefault(p => p.Id == padId)?.Name ?? "選んでいるが見つからない";

        // 格闘ゲームの利用者に見せる画面では、部品の名前も内部の言葉も出さない（Issue #20）。
        // 研究用の LLMCon では、出力先やアドレスが分からないと切り分けができないので、そのまま出す。
        if (_app.UiMode == "fighting")
        {
            int rules = _engine.RuleCount + _engine.UiRuleCount + _engine.ConnRuleCount;
            bool hiding = _engine.PadHider is { } hh && hh.Requested && _engine.SelectedPadId != null;
            StatusLine1.Text = _engine.Connected ? "● 動いています" : "● うまく始められませんでした";
            StatusLine2.Text = $"ボタンが届くまでの遅れ　1000分の{(hz > 0 ? 1000.0 / hz / 2.0 + 0.5 : 0):F1}秒";
            StatusLine3.Text = $"使うコントローラ　{padName}"
                             + (hiding ? "（他のアプリからは見えません）" : "");
            StatusLine4.Text = rules == 0 ? "裏返しているボタン　なし" : $"裏返しているボタン　{rules} 個";
            return;
        }

        var hide = _engine.PadHider is { } h ? $"   隠蔽 {h.Describe()}" : "";
        StatusLine1.Text = _engine.Connected
            ? $"● 動作中　出力 {_engine.SinkNames}"
            : $"● 出力の準備に失敗　{_engine.LastError}";
        StatusLine2.Text = $"ループ {hz:F0} Hz（追加遅延 およそ {(hz > 0 ? 1000.0 / hz / 2.0 + 0.5 : 0):F1} ミリ秒）"
                         + $"　改変ルール {_engine.RuleCount + _engine.UiRuleCount + _engine.ConnRuleCount} 個";
        StatusLine3.Text = $"物理パッド {padName}{hide}";
        StatusLine4.Text = _app.McpRunning
            ? $"MCP http://{_app.McpBind}:{_app.McpPort}/　ウェブ版コントローラ http://127.0.0.1:{_app.McpPort}/vcon.html"
            : "MCP の起動に失敗しました";
    }

    private void UpdateMicPanel()
    {
        var mic = _app.Mic;
        if (mic == null) return;
        double level = Math.Clamp(mic.Level, 0, 1);
        MicLevelBar.Width = level * 300.0;
        MicLevelText.Text = $"{level:F3}";
        Canvas.SetLeft(MicThresholdMark, Math.Clamp(mic.Threshold, 0, 1) * 300.0);
        MicThresholdText.Text = $"{mic.Threshold:F3}";
        MicStateText.Text = mic.Enabled
            ? $"有効　声で押すボタン {mic.Button}"
            : "いまは切ってあります（画面の右下のアイコンから入れられます）";
    }

    private void MicThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 較正で決めた値を、あとから手で詰めるためのつまみである。
        // 解除のしきい値は、これまでどおり半分にしておく。
        if (_app?.Mic == null) return;
        if (Math.Abs(_app.Mic.Threshold - e.NewValue) < 0.0005) return;
        _app.Mic.Configure(_app.Mic.Enabled, null, e.NewValue, e.NewValue * 0.5, null);
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e) => _app.CalibrateMic();

    // ── 物理パッド選択 ────────────────────────────────────
    private void RescanPads()
    {
        if (PadCombo is null) return;
        string? want = _engine.SelectedPadId;
        PadCombo.Items.Clear();
        PadCombo.Items.Add(new ComboBoxItem { Content = "なし（ソフトウェアのみ）", Tag = (string?)null });
        foreach (var info in _engine.ListPads())
            PadCombo.Items.Add(new ComboBoxItem { Content = info.Name, Tag = info.Id });

        PadCombo.SelectedIndex = 0;
        if (want != null)
            for (int k = 0; k < PadCombo.Items.Count; k++)
                if (PadCombo.Items[k] is ComboBoxItem it && (string?)it.Tag == want) { PadCombo.SelectedIndex = k; break; }
    }

    private void PadCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        string? id = (PadCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        _engine.SelectPad(id);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => RescanPads();

    // ── プリセット選択 ────────────────────────────────────
    private void PopulatePresets()
    {
        if (PresetCombo is null) return;
        PresetCombo.Items.Clear();
        PresetCombo.Items.Add(new ComboBoxItem { Content = "スルー（改変なし・最小遅延）", Tag = Preset.Passthrough });
        PresetCombo.Items.Add(new ComboBoxItem { Content = "A を無効化",                 Tag = Preset.DisableA });
        PresetCombo.Items.Add(new ComboBoxItem { Content = "A ↔ B 入れ替え",             Tag = Preset.SwapAB });
        PresetCombo.Items.Add(new ComboBoxItem { Content = "B を連打（ターボ 15Hz）",     Tag = Preset.TurboB });
        PresetCombo.SelectedIndex = 0;
        _presetsReady = true;
    }

    // 一覧を作っている最中は、選択が変わってもエンジンへ送らない。
    // ここで送ると、プロファイルの rules で入れた起動時のルールを、
    // 「スルー（改変なし）」で上書きして消してしまう（Issue #22）。
    private bool _presetsReady;

    private void PresetCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_presetsReady) return;
        if (PresetCombo?.SelectedItem is ComboBoxItem it && it.Tag is Preset p) _engine.SetMapping(MappingPresets.Build(p));
    }

    // ── ソフトウェア入力（マウス）──────────────────────────
    private void Button_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag) { _engine.SetSoftwareButton(tag, true); UpdatePressedLabel(); }
    }

    private void Button_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag) { _engine.SetSoftwareButton(tag, false); UpdatePressedLabel(); }
    }

    // ── ソフトウェア入力（キーボード / トンネリング）────────
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        if (_keyMap.TryGetValue(e.Key, out var tag)) { _engine.SetSoftwareButton(tag, true); UpdatePressedLabel(); e.Handled = true; }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_keyMap.TryGetValue(e.Key, out var tag)) { _engine.SetSoftwareButton(tag, false); UpdatePressedLabel(); e.Handled = true; }
    }

    // ── ソフトウェア入力（スティック・トラックパッド）───────
    private const double PadSize = 120.0;
    private const double KnobSize = 34.0;

    private void Pad_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border pad) { pad.CaptureMouse(); UpdateStick(pad, e.GetPosition(pad)); }
    }

    private void Pad_Move(object sender, MouseEventArgs e)
    {
        if (sender is Border pad && pad.IsMouseCaptured) UpdateStick(pad, e.GetPosition(pad));
    }

    private void Pad_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border pad) { pad.ReleaseMouseCapture(); ResetStick(pad); }
    }

    private void UpdateStick(Border pad, Point p)
    {
        double half = PadSize / 2.0;
        double nx = Math.Clamp((p.X - half) / half, -1.0, 1.0);
        double ny = Math.Clamp((p.Y - half) / half, -1.0, 1.0);
        short ax = (short)(nx * 32767);
        short ay = (short)(-ny * 32767);

        bool left = (string?)pad.Tag == "L";
        _engine.SetSoftwareStick(left, ax, ay);
        MoveKnob(left ? KnobL : KnobR, p);
    }

    private void ResetStick(Border pad)
    {
        bool left = (string?)pad.Tag == "L";
        _engine.SetSoftwareStick(left, 0, 0);
        double center = (PadSize - KnobSize) / 2.0;
        var knob = left ? KnobL : KnobR;
        Canvas.SetLeft(knob, center);
        Canvas.SetTop(knob, center);
    }

    private void MoveKnob(Ellipse knob, Point p)
    {
        double x = Math.Clamp(p.X - KnobSize / 2.0, 0, PadSize - KnobSize);
        double y = Math.Clamp(p.Y - KnobSize / 2.0, 0, PadSize - KnobSize);
        Canvas.SetLeft(knob, x);
        Canvas.SetTop(knob, y);
    }

    private void UpdatePressedLabel() => PressedText.Text = _engine.PressedLabel();

    // ウィンドウの×は、終了ではなくタスクトレイへ隠す（常駐アプリ）。本当の終了はトレイのメニューから。
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_app.Exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
