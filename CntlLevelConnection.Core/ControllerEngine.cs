using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace CntlLevelConnection;

public enum Preset { Passthrough, DisableA, SwapAB, TurboB }

/// <summary>
/// LLMCon の中核（UIから分離）。1000Hz単一ライタループ・WGI物理読取・入力マージ・ルール適用を担う。
/// UI(MainWindow)からも MCPサーバからも同一インスタンスを共有。
/// 入力源: 物理(WGI) / ソフト(GUI human・ウェブ版コントローラ・マイク) / LLM注入(MCP)。
/// パイプライン: merge(物理,ソフト) → ルール適用 → LLM注入をmerge → 出力シンクへ(1周回=1回)。
/// 出力先は IPadSink として差し替えられる（ViGEm、WebSocket、将来の HID ガジェット）。
/// </summary>
public sealed class ControllerEngine : IDisposable
{
    private readonly List<IPadSink> _sinks = new();
    private readonly object _lock = new();

    // human(GUI)入力
    private readonly HashSet<string> _soft = new(StringComparer.OrdinalIgnoreCase);
    private short _softLX, _softLY, _softRX, _softRY;
    // LLM注入入力（MCP用）
    private readonly HashSet<string> _llm = new(StringComparer.OrdinalIgnoreCase);
    private short _llmLX, _llmLY, _llmRX, _llmRY;
    private byte _llmLT, _llmRT;
    // 直近出力（get_state用）
    private PadState _lastOut;

    /// <summary>物理パッドの読み取り。渡されないときは物理パッドを扱わない（mac など）。</summary>
    private IPadSource? _padSource;

    /// <summary>
    /// 直近のループで物理パッドを読めたかどうか。パッドを選んでいるのに読めない状態を、
    /// 黙って隠さず get_state に出すために持つ（Issue #7）。
    /// </summary>
    private volatile bool _padReadOk;
    public string? OwnPadId { get; private set; }
    private volatile MappingRule[] _rules = Array.Empty<MappingRule>();
    private double _mappingStartSec;
    public int RuleCount => _rules.Length;

    // コントローラ間接続による改変ルール（標準のマッピングとは別の層・絶対時刻で自動的に期限切れ）。
    private readonly object _connLock = new();
    private readonly List<(MappingRule rule, double expiry)> _connRules = new();
    private volatile int _connCount;
    public int ConnRuleCount => _connCount;

    // UI（ウェブページのチェックボックスなど）由来の改変ルール層。標準のマッピングとも接続由来とも独立に合成する。
    private volatile MappingRule[] _uiRules = Array.Empty<MappingRule>();
    public int UiRuleCount => _uiRules.Length;
    public void SetUiMapping(MappingRule[] rules) => _uiRules = Normalize(rules);

    /// <summary>
    /// 受け取ったルールを、適用の前に一度ならす。
    ///
    /// LT と RT はボタンの旗ではなく 0 から 255 の値を持つトリガーなので、反転はアナログの段
    /// （Axis 指定）でしか効かない。しかし利用者は「ボタンとしての RT」を反転したいと思って
    /// Button に書く。ウェブ版の設定パネルも、MCP の set_mapping も、コントローラ間接続の作用も
    /// そうである。入口ごとに直すと漏れるので、ここで読み替える。
    ///
    /// ルールを受け取る3か所（SetMapping、SetUiMapping、AddConnectionRules）で一度だけ通す。
    /// ループの中ではやらない。ルールの数は少ないが、毎周回やる必要は無い。
    /// </summary>
    private static MappingRule[] Normalize(MappingRule[]? rules)
    {
        if (rules is null || rules.Length == 0) return Array.Empty<MappingRule>();
        var result = new MappingRule[rules.Length];
        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            result[i] = (string.Equals(r.Op, "invert", StringComparison.OrdinalIgnoreCase) && IsTriggerName(r.Button))
                ? r with { Button = null, Axis = r.Button }
                : r;
        }
        return result;
    }

    /// <summary>
    /// いまの画面由来の改変ルール。タスクトレイとウェブ版の設定パネルが同じ層を使うので、
    /// 片方から変えたときにもう片方が今の状態を読めるように出す。
    /// </summary>
    public MappingRule[] UiRules => _uiRules;

    /// <summary>
    /// LT と RT は、ボタンの旗ではなく 0 から 255 の値を持つトリガーである。
    /// そのため反転も、ボタンの段（Button 指定）ではなくアナログの段（Axis 指定）で掛ける必要がある。
    /// 格闘ゲーム用のコントローラはデジタルなので、結果は他のボタンと同じく2値になる。
    /// </summary>
    private static bool IsTriggerName(string? name)
        => string.Equals(name, "LT", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "RT", StringComparison.OrdinalIgnoreCase);

    private static bool SameInvertTarget(MappingRule r, string name)
    {
        if (!string.Equals(r.Op, "invert", StringComparison.OrdinalIgnoreCase)) return false;
        return IsTriggerName(name)
            ? string.Equals(r.Axis, name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(r.Button, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 画面由来の層にある「その入力の反転」を入り切りする。他のルールはそのまま残す。
    /// タスクトレイとウェブ版のどちらから触っても、同じ層の同じ形になるようにするための入口である。
    /// LT と RT の読み替えは SetUiMapping が行うので、ここでは Button に素直に書けばよい。
    /// </summary>
    public void SetUiInvert(string name, bool on)
    {
        var rest = _uiRules.Where(r => !SameInvertTarget(r, name)).ToList();
        if (on) rest.Add(new MappingRule("invert", Button: name));
        SetUiMapping(rest.ToArray());
    }

    /// <summary>その入力が、画面由来の層で反転されているか。</summary>
    public bool IsUiInverted(string name) => _uiRules.Any(r => SameInvertTarget(r, name));

    /// <summary>画面由来の層から、反転のルールだけを全部外す。</summary>
    public void ClearUiInverts()
        => _uiRules = _uiRules.Where(r => !string.Equals(r.Op, "invert", StringComparison.OrdinalIgnoreCase)).ToArray();

    // マイク由来の入力。人間入力として合流する（すべての改変ルールが効く）。
    private readonly HashSet<string> _micBtn = new(StringComparer.OrdinalIgnoreCase);
    public void SetMicButton(string tag, bool down)
    { lock (_lock) { if (down) _micBtn.Add(tag); else _micBtn.Remove(tag); } }

    // 人間入力（物理とソフトを合成したもの）の押下・解放エッジを外へ伝える。
    // 引数は (押下エッジのマスク, 解放エッジのマスク, 経過秒)。エッジが立った時にだけ呼ぶ。
    public Action<ushort, ushort, double>? HumanEdges;

    private ushort _prevHumanBtn;

    // delay（反応遅延）のための人間入力の履歴。時刻つきで貯めておき、指定時間だけ前の入力を取り出す。
    // ループスレッドだけが読み書きするので、ロックは要らない。
    private const int HistCap = 4096;   // 1000Hz で約4秒、500Hz で約8秒ぶん
    private readonly double[] _histT = new double[HistCap];
    private readonly PadState[] _histS = new PadState[HistCap];
    private int _histHead;
    private int _histCount;

    // 変化速度の上限（rate）のための、直前の出力値と時刻。軸の並びは LX,LY,RX,RY,LT,RT。
    private readonly double[] _ratePrev = new double[6];
    private double _rateLastNow = -1;

    // マクロと tap が使う共有のフレームレート。既定は60で、受け付ける範囲は1から1000。
    public double Fps { get; private set; } = 60.0;
    public void SetFps(double fps)
    {
        if (fps < 1 || fps > 1000) throw new ArgumentException("fps must be in 1..1000");
        Fps = fps;
    }

    public string? LastError { get; private set; }

    /// <summary>出力先が1つ以上、使える状態にあるか。</summary>
    public bool Connected { get { lock (_sinks) return _sinks.Any(s => s.Connected); } }

    /// <summary>使っている出力先の名前（画面や get_info の表示用）。</summary>
    public string SinkNames { get { lock (_sinks) return _sinks.Count == 0 ? "none" : string.Join("+", _sinks.Select(s => s.Name)); } }

    public double LoopHz { get; private set; }

    private Thread? _loop;
    private volatile bool _running;
    private readonly Stopwatch _clock = new();

    private static readonly (string tag, ushort bit)[] ButtonBits =
    {
        ("A",0x1000),("B",0x2000),("X",0x4000),("Y",0x8000),
        ("LB",0x0100),("RB",0x0200),("LS",0x0040),("RS",0x0080),
        ("Start",0x0010),("Back",0x0020),("Guide",0x0400),
        ("DUp",0x0001),("DDown",0x0002),("DLeft",0x0004),("DRight",0x0008),
    };
    public static bool IsKnownButton(string tag) => ButtonBits.Any(t => string.Equals(t.tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>ボタン名からビットを得る（未知は0）。コントローラ間接続の事象照合で使う。</summary>
    public static ushort MaskOf(string? tag) => Bit(tag);

    /// <summary>ビットマスクに含まれるボタン名を ButtonBits の順で返す。</summary>
    public static List<string> NamesFromMask(ushort mask)
    {
        var l = new List<string>();
        foreach (var (tag, bit) in ButtonBits) if ((mask & bit) != 0) l.Add(tag);
        return l;
    }

    /// <summary>
    /// 封じられた押下を記録する先。観測専用であり、反応的な経路には関与しない。
    /// 縛りプレイのように、あるボタンを一定のあいだ使えなくする改変を課したとき、
    /// 「実際に何回押そうとして封じられたか」を後から数えられるようにするためにある。
    /// コントローラをまたいだ接続の事象と同じ記録に並べて残す。
    /// </summary>
    public EventLog? Events { get; set; }

    private ushort _blockedPrev;
    private readonly int[] _blockCount = new int[16];

    /// <summary>
    /// disable によって落とされたボタンを見て、押しはじめの瞬間だけを記録する。
    /// 高速実行層は毎秒1000回まわるので、押されているあいだ記録し続けてはならない。
    /// </summary>
    private void NoteBlocked(ushort blockedNow)
    {
        if (blockedNow == _blockedPrev) return;
        ushort rising = (ushort)(blockedNow & ~_blockedPrev);
        _blockedPrev = blockedNow;
        if (rising == 0) return;
        var names = NamesFromMask(rising);
        for (int i = 0; i < ButtonBits.Length; i++)
            if ((rising & ButtonBits[i].bit) != 0) _blockCount[i]++;
        Events?.Add("block", string.Join(",", names) + " を押したが，改変ルールによって封じられた");
    }

    /// <summary>ボタンごとの、封じられた押下の回数。記録が古くなって消えても失われない。</summary>
    public IReadOnlyList<(string button, int count)> BlockedCounts()
    {
        var l = new List<(string, int)>();
        for (int i = 0; i < ButtonBits.Length; i++)
            if (_blockCount[i] > 0) l.Add((ButtonBits[i].tag, _blockCount[i]));
        return l;
    }

    public void ResetBlockedCounts() => Array.Clear(_blockCount);

    /// <summary>
    /// 出力先を用意してループを開始する。ひとつでも使える出力先があれば true。
    /// すべての出力先が失敗しても、ループは回す（GUI と MCP は動かし、状態を画面に出すため）。
    /// </summary>
    public bool Start(IEnumerable<IPadSink> sinks, IPadSource? padSource = null)
    {
        _padSource = padSource;
        var errors = new List<string>();
        lock (_sinks)
        {
            foreach (var sink in sinks)
            {
                bool ok;
                try { ok = sink.Start(); }
                catch (Exception ex) { ok = false; errors.Add($"{sink.Name}: {ex.Message}"); }
                if (ok)
                {
                    _sinks.Add(sink);
                    // 出力が作った仮想コントローラは、自分の物理パッドの一覧から除く必要がある
                    // 識別子とスロットは別々に分かることがある。片方が分からなくても、
                    // もう片方は伝える。以前は識別子が分からないとスロットも伝えていなかったため、
                    // 自分の出力が一覧に残っていた。
                    if (sink is IOwnPadIdentity own)
                    {
                        if (own.OwnPadId != null)
                        {
                            OwnPadId = own.OwnPadId;
                            _padSource?.ExcludeOwnPad(own.OwnPadId);
                        }
                        if (own.OwnXInputSlot >= 0) _padSource?.ExcludeOwnSlot(own.OwnXInputSlot);
                    }
                }
                else if (sink.Error != null) errors.Add($"{sink.Name}: {sink.Error}");
            }
        }
        if (errors.Count > 0) LastError = string.Join(" / ", errors);

        _running = true; _clock.Start();
        _loop = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "OutputLoop" };
        _loop.Start();
        return Connected;
    }

    // ── 物理パッド（渡されていなければ、物理パッドは扱わない）──
    public IReadOnlyList<PadInfo> ListPads() => _padSource?.List() ?? Array.Empty<PadInfo>();

    public void SelectPad(string? id)
    {
        _padSource?.Select(id);
        // 隠す対象は「いま読んでいるパッド」である。選び直したら、隠す先も移す。
        try { _hider?.OnSelectionChanged(_padSource?.SelectedId); } catch { /* 隠蔽の失敗で選択を壊さない */ }
    }

    public string? SelectedPadId => _padSource?.SelectedId;

    private IPadHider? _hider;

    /// <summary>物理パッドをゲームから隠す仕組みを差し込む（Windows のみ。Issue #12）。</summary>
    public void UsePadHider(IPadHider hider) => _hider = hider;

    /// <summary>隠す仕組み。扱えない環境では null。</summary>
    public IPadHider? PadHider => _hider;

    /// <summary>物理パッドを扱える環境か（mac などでは扱えない）。</summary>
    public bool HasPadSource => _padSource != null;

    /// <summary>いまの軸の割り当て。物理パッドを扱えない環境では既定を返す。</summary>
    public PadAxisMap PadAxes => _padSource?.AxisMap ?? PadAxisMap.Default;

    /// <summary>軸の割り当てを差し替える。</summary>
    public void SetPadAxes(PadAxisMap map) => _padSource?.SetAxisMap(map);

    /// <summary>いまのボタンの割り当て。物理パッドを扱えない環境では既定を返す。</summary>
    public PadButtonMap PadButtons => _padSource?.ButtonMap ?? PadButtonMap.Default;

    /// <summary>ボタンの割り当てを差し替える。</summary>
    public void SetPadButtons(PadButtonMap map) => _padSource?.SetButtonMap(map);

    /// <summary>機体ごとの割り当てをプロファイルから受け取る。</summary>
    public void UsePadProfiles(IReadOnlyList<PadProfile>? profiles) => _padSource?.UsePadProfiles(profiles);

    /// <summary>いま選んでいるパッドの割り当てを、プロファイルへ貼り付けられる形にする。</summary>
    public string? DescribeSelectedPadAsProfile() => _padSource?.DescribeSelectedAsProfile();

    /// <summary>生の軸とボタンの値を読む（割り当てを決めるために使う）。</summary>
    public PadRawReading? ReadPadRaw(string? id = null) => _padSource?.ReadRaw(id);

    // ── 改変ルール（人間入力に適用・データ駆動）──
    public void SetMapping(MappingRule[] rules)
    {
        lock (_lock) { _rules = Normalize(rules); _mappingStartSec = _clock.Elapsed.TotalSeconds; }
    }

    /// <summary>
    /// コントローラ間接続の作用として、人間入力への改変ルールを durationSec 秒だけ重ねる。
    /// 標準のマッピング（SetMapping）は上書きせず、別の層として合成し、期限が来たら自動的に消える。
    /// </summary>
    public void AddConnectionRules(MappingRule[] rules, double durationSec)
    {
        if (rules is null || rules.Length == 0) return;
        double exp = _clock.Elapsed.TotalSeconds + Math.Max(0.001, durationSec);
        var normalized = Normalize(rules);
        lock (_connLock)
        {
            foreach (var r in normalized) _connRules.Add((r, exp));
            _connCount = _connRules.Count;
        }
    }

    public void ClearConnectionRules()
    {
        lock (_connLock) { _connRules.Clear(); _connCount = 0; }
    }

    // ── human(GUI)入力 ──
    public void SetSoftwareButton(string tag, bool down)
    { lock (_lock) { if (down) _soft.Add(tag); else _soft.Remove(tag); } }
    public void SetSoftwareStick(bool left, short x, short y)
    { lock (_lock) { if (left) { _softLX = x; _softLY = y; } else { _softRX = x; _softRY = y; } } }
    public string PressedLabel()
    { lock (_lock) return _soft.Count > 0 ? string.Join(" + ", _soft) : "なし"; }

    // ── LLM注入入力（MCP・増分2で使用）──
    public void SetLlmButton(string tag, bool down)
    { lock (_lock) { if (down) _llm.Add(tag); else _llm.Remove(tag); } }
    public void SetLlmStick(bool left, short x, short y)
    { lock (_lock) { if (left) { _llmLX = x; _llmLY = y; } else { _llmRX = x; _llmRY = y; } } }
    public void SetLlmTrigger(bool left, byte v)
    { lock (_lock) { if (left) _llmLT = v; else _llmRT = v; } }
    public void LlmNeutral()
    { lock (_lock) { _llm.Clear(); _llmLX = _llmLY = _llmRX = _llmRY = 0; _llmLT = _llmRT = 0; } }

    /// <summary>LLM注入状態を丸ごと差し替える（マクロの1ステップ適用に使う）。</summary>
    public void SetLlmSnapshot(IEnumerable<string> buttons, short lx, short ly, short rx, short ry, byte lt, byte rt)
    {
        lock (_lock)
        {
            _llm.Clear();
            foreach (var b in buttons) _llm.Add(b);
            _llmLX = lx; _llmLY = ly; _llmRX = rx; _llmRY = ry; _llmLT = lt; _llmRT = rt;
        }
    }

    public string GetState()
    {
        PadState s; double hz; bool pad; int rc;
        lock (_lock) { s = _lastOut; }
        hz = LoopHz; pad = _padSource?.SelectedId != null; rc = _rules.Length;
        var names = new List<string>();
        foreach (var (tag, bit) in ButtonBits) if ((s.Buttons & bit) != 0) names.Add(tag);
        var b = names.Count > 0 ? string.Join(",", names) : "-";
        // 「選んでいるが読めていない」を「選んでいない」と同じ表示にしない。
        // 読めない状態を黙って隠すと、原因の切り分けに時間を失う（Issue #7）。
        var padText = !pad ? "none" : (_padReadOk ? "yes" : "selected-but-no-data");
        // 物理パッドを隠しているかどうかも出す。利用者が「コントローラが認識されない」と
        // 混乱したときに、原因へ辿り着けるようにするためである（Issue #12）。
        var hideText = _hider == null ? "" : $", {_hider.Describe()}";
        return $"buttons=[{b}] LT={s.LT} RT={s.RT} LX={s.LX} LY={s.LY} RX={s.RX} RY={s.RY} (rules={rc}, ui={_uiRules.Length}, conn={_connCount}, pad={padText}{hideText}, loop={hz:F0}Hz)";
    }

    // ── ループ（唯一の出力のライタ・~1000Hz）──
    private void Loop()
    {
        using var hiRes = HiResTimer.Request();
        try
        {
            long iter = 0; var rate = Stopwatch.StartNew();
            while (_running)
            {
                PadState soft, llm;
                lock (_lock) { soft = BuildSoftLocked(); llm = BuildLlmLocked(); }

                // 読めなかったときは phys を既定値のままにする。中身の無い読み取りを混ぜると、
                // 仮想コントローラのスティックが端に張り付く（Issue #7）。
                PadState phys = default;
                bool physOk = _padSource?.TryRead(out phys) ?? false;
                if (!physOk) phys = default;
                _padReadOk = physOk;

                PadState human = PadState.Merge(soft, phys);
                double nowSec = _clock.Elapsed.TotalSeconds;

                // delay 用に、今の人間入力を時刻つきで履歴へ積む。
                PushHistory(nowSec, human);

                // 人間入力の押下・解放エッジを検出して伝える（コントローラ間接続の事象検出）。
                ushort curBtn = human.Buttons;
                ushort pressed = (ushort)(curBtn & ~_prevHumanBtn);
                ushort released = (ushort)(_prevHumanBtn & ~curBtn);
                _prevHumanBtn = curBtn;
                if ((pressed | released) != 0)
                {
                    var edges = HumanEdges;
                    if (edges != null) edges(pressed, released, nowSec);
                }

                PadState ruled = ApplyMapping(human, nowSec);
                PadState final = PadState.Merge(ruled, llm);   // LLM注入は意図的なのでルール後にmerge
                WriteOut(final);
                lock (_lock) _lastOut = final;

                iter++;
                if (rate.ElapsedMilliseconds >= 500) { LoopHz = iter * 1000.0 / rate.ElapsedMilliseconds; iter = 0; rate.Restart(); }
                Thread.Sleep(1);
            }
        }
        finally { /* 分解能は using で戻る */ }
    }

    private PadState BuildSoftLocked()
    {
        var s = new PadState();
        foreach (var (tag, bit) in ButtonBits) if (_soft.Contains(tag) || _micBtn.Contains(tag)) s.Buttons |= bit;
        if (_soft.Contains("LT") || _micBtn.Contains("LT")) s.LT = 255;
        if (_soft.Contains("RT") || _micBtn.Contains("RT")) s.RT = 255;
        s.LX = _softLX; s.LY = _softLY; s.RX = _softRX; s.RY = _softRY;
        return s;
    }

    private PadState BuildLlmLocked()
    {
        var s = new PadState();
        foreach (var (tag, bit) in ButtonBits) if (_llm.Contains(tag)) s.Buttons |= bit;
        s.LT = _llmLT; s.RT = _llmRT;
        s.LX = _llmLX; s.LY = _llmLY; s.RX = _llmRX; s.RY = _llmRY;
        return s;
    }

    // ── delay（反応遅延）用の履歴。ループスレッド専用なのでロック不要。──
    private void PushHistory(double t, in PadState s)
    {
        _histT[_histHead] = t;
        _histS[_histHead] = s;
        _histHead = (_histHead + 1) % HistCap;
        if (_histCount < HistCap) _histCount++;
    }

    /// <summary>時刻 target 以前で最も新しい人間入力を返す。target が履歴より古いときは、持っている中で最も古いものを返す。</summary>
    private bool TryGetDelayed(double target, out PadState s)
    {
        s = default;
        if (_histCount == 0) return false;
        int idx = (_histHead - 1 + HistCap) % HistCap;
        int oldest = idx;
        for (int n = 0; n < _histCount; n++)
        {
            if (_histT[idx] <= target) { s = _histS[idx]; return true; }
            oldest = idx;
            idx = (idx - 1 + HistCap) % HistCap;
        }
        s = _histS[oldest];   // 履歴の範囲より古い遅延なら、持っている最も古い入力で代替する
        return true;
    }

    /// <summary>
    /// データ駆動ルールを人間入力に適用する。順序は remap、disable、turbo。時間窓に対応する。
    /// 標準のマッピング（_rules、set_mapping からの経過秒で有効）に加えて、
    /// コントローラ間接続の作用（_connRules、絶対時刻で自動的に期限切れ）も同じ語彙で重ねる。
    /// </summary>
    private PadState ApplyMapping(PadState s, double now)
    {
        var standing = _rules;
        double since = now - _mappingStartSec;

        // 接続ルールは短い窓のあいだだけ存在するので、無いときは確保せず素通りさせる。
        MappingRule[] conn = Array.Empty<MappingRule>();
        if (_connCount > 0)
        {
            lock (_connLock)
            {
                for (int i = _connRules.Count - 1; i >= 0; i--)
                    if (now >= _connRules[i].expiry) _connRules.RemoveAt(i);
                _connCount = _connRules.Count;
                if (_connRules.Count > 0)
                {
                    conn = new MappingRule[_connRules.Count];
                    for (int i = 0; i < conn.Length; i++) conn[i] = _connRules[i].rule;
                }
            }
        }

        var ui = _uiRules;
        if (standing.Length == 0 && conn.Length == 0 && ui.Length == 0) return s;

        // delay（反応遅延）: 有効な delay ルールのうち最大の遅延を採り、人間入力の全体を過去の状態に差し替える。
        // ボタンだけでなくスティックやトリガーもまとめて遅れる。LLM注入はこの後で合成されるので遅れない。
        double delaySec = 0;
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "delay")) delaySec = Math.Max(delaySec, (r.DelayMs ?? 0) / 1000.0);
        foreach (var r in conn) if (IsOp(r, "delay")) delaySec = Math.Max(delaySec, (r.DelayMs ?? 0) / 1000.0);
        foreach (var r in ui) if (IsOp(r, "delay")) delaySec = Math.Max(delaySec, (r.DelayMs ?? 0) / 1000.0);
        if (delaySec > 0 && TryGetDelayed(now - delaySec, out var delayed)) s = delayed;

        ushort orig = s.Buttons;

        // remap（同時適用: まず全 from を落とし、orig基準で to を立てる→swapも正しい）
        ushort removeMask = 0;
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "remap")) removeMask |= Bit(r.From);
        foreach (var r in conn) if (IsOp(r, "remap")) removeMask |= Bit(r.From);
        foreach (var r in ui) if (IsOp(r, "remap")) removeMask |= Bit(r.From);
        ushort b = (ushort)(orig & ~removeMask);
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "remap") && (orig & Bit(r.From)) != 0) b |= Bit(r.To);
        foreach (var r in conn) if (IsOp(r, "remap") && (orig & Bit(r.From)) != 0) b |= Bit(r.To);
        foreach (var r in ui) if (IsOp(r, "remap") && (orig & Bit(r.From)) != 0) b |= Bit(r.To);

        // disable
        ushort beforeDisable = b;
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "disable")) b = (ushort)(b & ~Bit(r.Button));
        foreach (var r in conn) if (IsOp(r, "disable")) b = (ushort)(b & ~Bit(r.Button));
        foreach (var r in ui) if (IsOp(r, "disable")) b = (ushort)(b & ~Bit(r.Button));
        NoteBlocked((ushort)(beforeDisable & ~b));

        // turbo（押下中を hz でゲート）
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "turbo")) ApplyTurbo(ref b, r, now);
        foreach (var r in conn) if (IsOp(r, "turbo")) ApplyTurbo(ref b, r, now);
        foreach (var r in ui) if (IsOp(r, "turbo")) ApplyTurbo(ref b, r, now);

        // invert（ボタンの反転。押されていないとき On、押されているとき Off。Button 指定のときだけ）
        foreach (var r in standing) if (Active(r, since) && IsOp(r, "invert") && r.Button != null) b ^= Bit(r.Button);
        foreach (var r in conn) if (IsOp(r, "invert") && r.Button != null) b ^= Bit(r.Button);
        foreach (var r in ui) if (IsOp(r, "invert") && r.Button != null) b ^= Bit(r.Button);

        s.Buttons = b;

        // アナログ変換（スティックとトリガーへの改変）。ボタンの改変とは別に、軸の値そのものを変形する。
        if (HasAnalog(standing, since) || HasAnalog(conn, -1.0) || HasAnalog(ui, -1.0))
            ProcessAnalog(ref s, standing, since, conn, ui, now);
        return s;
    }

    private static void ApplyTurbo(ref ushort b, MappingRule r, double now)
    {
        ushort tb = Bit(r.Button);
        if ((b & tb) != 0 && (now * (r.Hz ?? 15.0)) % 1.0 >= 0.5) b = (ushort)(b & ~tb);
    }

    // ── アナログ変換 ─────────────────────────────────────
    // 軸ごとの変換: deadzone（不感帯）、curve（応答曲線）、gain（感度）、clamp（制限）、invert（反転）、rate（変化速度の上限）。
    // スティック単位（2軸）の変換: swap（XとYの入れ替え）、rotate（回転）。
    private static readonly string[] AnalogOps = { "gain", "deadzone", "invert", "clamp", "curve", "swap", "rotate", "rate" };
    private static bool IsAnalogOp(string? op)
    {
        if (op is null) return false;
        foreach (var a in AnalogOps) if (string.Equals(op, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // since が負のときは（接続ルールのように）常に有効とみなす。
    private static bool HasAnalog(MappingRule[] rules, double since)
    {
        foreach (var r in rules) if (IsAnalogOp(r.Op) && (since < 0 || Active(r, since))) return true;
        return false;
    }

    // 対象の軸に一致するか。LS/RS/sticks/triggers/all の別名にも対応する。
    private static bool AxisMatches(string? target, string axis)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var t = target.Trim();
        if (t.Equals(axis, StringComparison.OrdinalIgnoreCase)) return true;
        return t.ToLowerInvariant() switch
        {
            "ls" => axis is "LX" or "LY",
            "rs" => axis is "RX" or "RY",
            "sticks" => axis is "LX" or "LY" or "RX" or "RY",
            "triggers" => axis is "LT" or "RT",
            "all" => true,
            _ => false,
        };
    }

    private static double DeadzoneBip(double v, double t)
    {
        t = Math.Clamp(t, 0, 0.999);
        double a = Math.Abs(v);
        if (a <= t) return 0;
        return Math.Sign(v) * (a - t) / (1 - t);   // しきい値の外を再スケールして連続にする
    }

    private static double DeadzoneUni(double v, double t)
    {
        t = Math.Clamp(t, 0, 0.999);
        if (v <= t) return 0;
        return (v - t) / (1 - t);
    }

    private static double CurveBip(double v, double e)
    {
        if (e <= 0) return v;
        double a = Math.Min(1.0, Math.Abs(v));
        return Math.Sign(v) * Math.Pow(a, e);   // e>1 で中心付近が細かく、端が粗くなる
    }
    private static double CurveUni(double v, double e)
        => e <= 0 ? v : Math.Pow(Math.Clamp(v, 0, 1), e);

    private static (double x, double y) Rotate(double x, double y, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return (x * c - y * s, x * s + y * c);
    }

    private static short ToShort(double v) => (short)Math.Round(Math.Clamp(v, -1.0, 1.0) * 32767.0);
    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1.0) * 255.0);

    // 有効なルールを1本ずつ列挙する（standing は時間窓を評価し、conn と ui は常に有効）。
    private static IEnumerable<MappingRule> ActiveRules(MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui)
    {
        foreach (var r in standing) if (Active(r, since)) yield return r;
        foreach (var r in conn) yield return r;
        foreach (var r in ui) yield return r;
    }

    // スティックの1軸（-1..1）への軸ごとの変換。順序は deadzone、curve、gain、clamp、invert。
    private double AxisStick(double v, string axis, MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui)
    {
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "deadzone") && AxisMatches(r.Axis, axis)) v = DeadzoneBip(v, r.Amount ?? 0);
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "curve") && AxisMatches(r.Axis, axis)) v = CurveBip(v, r.Amount ?? 1.0);
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "gain") && AxisMatches(r.Axis, axis)) v *= r.Amount ?? 1.0;
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "clamp") && AxisMatches(r.Axis, axis)) { double m = Math.Abs(r.Amount ?? 1.0); v = Math.Clamp(v, -m, m); }
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "invert") && AxisMatches(r.Axis, axis)) v = -v;
        return v;
    }

    // トリガーの1軸（0..1）への軸ごとの変換。
    private double AxisTrig(double v, string axis, MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui)
    {
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "deadzone") && AxisMatches(r.Axis, axis)) v = DeadzoneUni(v, r.Amount ?? 0);
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "curve") && AxisMatches(r.Axis, axis)) v = CurveUni(v, r.Amount ?? 1.0);
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "gain") && AxisMatches(r.Axis, axis)) v *= r.Amount ?? 1.0;
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "clamp") && AxisMatches(r.Axis, axis)) { double m = Math.Clamp(r.Amount ?? 1.0, 0, 1); v = Math.Clamp(v, 0, m); }
        // 反転は、値を引き算するのではなく、しきい値で2値にしてから裏返す（Issue #16）。
        // 引き算にしていたときは、中間の値を返すボタンで反転が働かなかった。栗原さんの
        // 導電性フィラメントのボタンは抵抗が高く、押しても 165 から 177 までしか落ちない。
        // 255 から引くと押した状態の出力が 78 から 100 になり、XInput の押下の判定の
        // しきい値（30）を超えたままなので、押しても離しても「押されている」ことになる。
        foreach (var r in ActiveRules(standing, since, conn, ui))
            if (IsOp(r, "invert") && AxisMatches(r.Axis, axis))
                v = v >= Math.Clamp(r.Amount ?? TriggerPressThreshold, 0, 1) ? 0.0 : 1.0;
        return v;
    }

    /// <summary>
    /// トリガーを押していると見なす境目。XInput の XINPUT_GAMEPAD_TRIGGER_THRESHOLD（30）に合わせてある。
    /// 反転のルールに amount を書くと、その機体に合わせて動かせる。
    /// </summary>
    public const double TriggerPressThreshold = 30.0 / 255.0;

    // スティック単位（2軸）の変換。axisX でそのスティックを指す。swap（XとYの入れ替え）と rotate（回転）。
    private (double x, double y) CrossStick(double x, double y, string axisX, MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui)
    {
        bool swap = false;
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "swap") && AxisMatches(r.Axis, axisX)) swap = true;
        if (swap) { (x, y) = (y, x); }
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "rotate") && AxisMatches(r.Axis, axisX)) (x, y) = Rotate(x, y, r.Amount ?? 0);
        return (x, y);
    }

    // 変化速度の上限（rate）。1秒あたりの最大変化量（正規化）を超えて動かないようにする。状態つき。
    private double RateLimit(double target, int idx, string axis, double dt, MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui)
    {
        double maxRate = double.PositiveInfinity;
        foreach (var r in ActiveRules(standing, since, conn, ui)) if (IsOp(r, "rate") && AxisMatches(r.Axis, axis)) maxRate = Math.Min(maxRate, Math.Max(0, r.Amount ?? 0));
        double outv;
        if (double.IsInfinity(maxRate) || dt <= 0) outv = target;
        else { double d = maxRate * dt; outv = Math.Clamp(target, _ratePrev[idx] - d, _ratePrev[idx] + d); }
        _ratePrev[idx] = outv;
        return outv;
    }

    // アナログ変換の全体。各スティックを2軸としてまとめて処理し、トリガーは別に処理する。
    private void ProcessAnalog(ref PadState s, MappingRule[] standing, double since, MappingRule[] conn, MappingRule[] ui, double now)
    {
        double dt = _rateLastNow < 0 ? 0 : Math.Max(0, now - _rateLastNow);
        _rateLastNow = now;

        double lx = AxisStick(s.LX / 32767.0, "LX", standing, since, conn, ui);
        double ly = AxisStick(s.LY / 32767.0, "LY", standing, since, conn, ui);
        (lx, ly) = CrossStick(lx, ly, "LX", standing, since, conn, ui);
        s.LX = ToShort(RateLimit(lx, 0, "LX", dt, standing, since, conn, ui));
        s.LY = ToShort(RateLimit(ly, 1, "LY", dt, standing, since, conn, ui));

        double rx = AxisStick(s.RX / 32767.0, "RX", standing, since, conn, ui);
        double ry = AxisStick(s.RY / 32767.0, "RY", standing, since, conn, ui);
        (rx, ry) = CrossStick(rx, ry, "RX", standing, since, conn, ui);
        s.RX = ToShort(RateLimit(rx, 2, "RX", dt, standing, since, conn, ui));
        s.RY = ToShort(RateLimit(ry, 3, "RY", dt, standing, since, conn, ui));

        double lt = AxisTrig(s.LT / 255.0, "LT", standing, since, conn, ui);
        s.LT = ToByte(RateLimit(lt, 4, "LT", dt, standing, since, conn, ui));
        double rt = AxisTrig(s.RT / 255.0, "RT", standing, since, conn, ui);
        s.RT = ToByte(RateLimit(rt, 5, "RT", dt, standing, since, conn, ui));
    }

    private static bool IsOp(MappingRule r, string op) => string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase);
    private static bool Active(MappingRule r, double since)
        => (r.StartSec is null || since >= r.StartSec) && (r.EndSec is null || since < r.EndSec);
    private static ushort Bit(string? name)
    {
        if (name is null) return 0;
        foreach (var (tag, bit) in ButtonBits) if (string.Equals(tag, name, StringComparison.OrdinalIgnoreCase)) return bit;
        return 0;
    }

    /// <summary>確定した状態を、すべての出力先へ書く。ひとつが失敗しても他は続ける。</summary>
    private void WriteOut(in PadState s)
    {
        lock (_sinks)
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Write(s); }
                catch { /* ある出力先の失敗で、ループ全体を止めない */ }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _loop?.Join(500);
        lock (_sinks)
        {
            foreach (var s in _sinks) { try { s.Dispose(); } catch { /* ignore */ } }
            _sinks.Clear();
        }
    }
}
