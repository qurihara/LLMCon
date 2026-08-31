using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CntlLevelConnection;

/// <summary>
/// 機器の階層を辿って、そのパッドを隠すべきノードを決める。
///
/// nucbox-g3s の実測（experiments/hidhide-2026-08）で分かったことが2つある。
/// 第一に、HidHide に渡すのはデバイスインスタンスパスであって、シンボリックリンクではない。
/// 間違った形式を渡しても、エラーは出ないまま黙って効かない。
/// 第二に、XInput の機体はゲームパッドの HID のノードだけでは消えない。XInput と
/// Windows.Gaming.Input は HID のインターフェースを経由しないので、その上の親も隠す必要がある。
///
/// ただし親を無条件に辿ってはならない。ROG Ally の内蔵コントローラは複合機器で、
/// ひとつの親の下にゲームパッド以外の機能も並んでいる（2026/8/7 に実物で確認した。
/// MI_00 から MI_05 の6つがあり、ゲームパッドは MI_05 だけである）。その親を隠すと、
/// ゲームパッド以外の機能まで巻き込む。そこで「子がひとつしかない親までは辿り、
/// 複数の機能を持つ親で止める」ことにした。
/// </summary>
internal static class DeviceNodes
{
    private const string HidEnum = @"SYSTEM\CurrentControlSet\Enum\HID";

    /// <summary>
    /// その製造者と製品の識別子のゲームパッドについて、隠すべきノードを canonical な形で返す。
    /// 見つからなければ空。
    /// </summary>
    public static IReadOnlyList<string> ForGamePad(ushort vid, ushort pid)
    {
        var result = new List<string>();
        string want = $"VID_{vid:X4}&PID_{pid:X4}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(HidEnum);
            if (root == null) return result;
            foreach (var key in root.GetSubKeyNames())
            {
                if (key.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using var k = root.OpenSubKey(key);
                if (k == null) continue;
                foreach (var inst in k.GetSubKeyNames())
                {
                    var path = $@"HID\{key}\{inst}";
                    // レジストリには、いま挿さっていない機器の記録も残る。存在するものだけを見る。
                    if (Native.CM_Locate_DevNodeW(out uint dn, path, 0) != 0) continue;
                    // ゲームパッド以外（キーボードや製造者独自の機能）を巻き込まないようにする。
                    if (!IsGameController(k, inst)) continue;
                    // 差し込みで作られた機器（ViGEm の仮想コントローラ）は隠さない。
                    // XInput のモードを持つ機体の多くは Microsoft の Xbox 360 コントローラと
                    // 同じ識別子（VID_045E PID_028E）を名乗るので、識別子だけで選ぶと、
                    // 自分が出している仮想コントローラまで一緒に隠してしまう。そうなると
                    // ゲームからは何も見えなくなる（2026/8/8 に VSNOVA の XInput モードで気づいた）。
                    if (IsSoftwareDevice(dn)) continue;

                    var canonical = DeviceId(dn);
                    if (canonical == null) continue;
                    Add(result, canonical);

                    // 上へ辿る。子がひとつしかない親までが、このゲームパッドだけのものである。
                    uint node = dn;
                    for (int depth = 0; depth < 4; depth++)
                    {
                        if (Native.CM_Get_Parent(out uint parent, node, 0) != 0) break;
                        var id = DeviceId(parent);
                        if (id == null) break;
                        if (id.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) break;   // 別の機器に出た
                        if (ChildCount(parent) != 1) break;                                    // 他の機能も持つ親
                        Add(result, id);
                        node = parent;
                    }
                }
            }
        }
        catch { /* 読めない環境では、何も隠さない（黙って壊すより、隠さないほうがよい） */ }
        return result;
    }

    /// <summary>
    /// そのノードがゲームパッドか。表示名の元になっている input.inf の名前で見分ける。
    /// hid_device_system_game が「HID 準拠ゲームコントローラー」である。表示名そのものは
    /// 環境の言語で変わるが、この名前は変わらない。
    /// </summary>
    private static bool IsGameController(RegistryKey parent, string instance)
    {
        try
        {
            using var k = parent.OpenSubKey(instance);
            var desc = k?.GetValue("DeviceDesc") as string;
            return desc != null && desc.IndexOf("hid_device_system_game", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// その機器が、物ではなく差し込みで作られたものか。
    ///
    /// ViGEm の仮想コントローラは ROOT から生えている（ROOT\SYSTEM の下）。実物の USB の
    /// コントローラは、辿っていくと USB のハブに行き着く。上へ辿って ROOT に出会えば、
    /// それは物ではない。
    /// </summary>
    private static bool IsSoftwareDevice(uint dn)
    {
        uint node = dn;
        for (int depth = 0; depth < 8; depth++)
        {
            if (Native.CM_Get_Parent(out uint parent, node, 0) != 0) return false;
            var id = DeviceId(parent);
            if (id == null) return false;
            if (id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return true;
            if (id.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("HTREE", StringComparison.OrdinalIgnoreCase)) return false;
            node = parent;
        }
        return false;
    }

    private static void Add(List<string> list, string path)
    {
        if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) list.Add(path);
    }

    private static string? DeviceId(uint dn)
    {
        var sb = new StringBuilder(512);
        return Native.CM_Get_Device_IDW(dn, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    private static int ChildCount(uint dn)
    {
        if (Native.CM_Get_Child(out uint child, dn, 0) != 0) return 0;
        int n = 1;
        while (Native.CM_Get_Sibling(out child, child, 0) == 0) n++;
        return n;
    }

    private static class Native
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint parent, uint dn, uint flags);
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Child(out uint child, uint dn, uint flags);
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Sibling(out uint sibling, uint dn, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_IDW(uint dn, StringBuilder buffer, int len, uint flags);
    }
}

/// <summary>
/// HidHide の CLI を呼ぶ。導入は管理者権限が要るが、設定の変更は要らない
/// （nucbox-g3s で実測した。--cloak-on/off も --dev-hide も --app-reg も昇格なしで通る）。
/// </summary>
internal sealed class HidHideCli
{
    public string? Path { get; }
    public string? Missing { get; }

    public HidHideCli()
    {
        Path = Locate();
        Missing = Path == null
            ? "HidHide が見つかりません。winget install --id Nefarius.HidHide で導入してください（導入には管理者権限が要ります）。"
            : null;
    }

    public bool Available => Path != null;

    private static string? Locate()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var rel in new[] { @"Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
                                        @"Nefarius Software Solutions\HidHide\HidHideCLI.exe",
                                        @"Nefarius Software Solutions e.U\HidHide\x64\HidHideCLI.exe" })
            {
                var p = System.IO.Path.Combine(root, rel);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>
    /// CLI を1回呼ぶ。戻り値は (成功したか, 出力)。
    ///
    /// timeoutMs は、応答が返らないときに諦めるまでの時間である。Windows を終了する場面では
    /// 短くする。終了の処理に時間をかけると「このアプリがシャットダウンを妨げています」と
    /// 出るためである（Issue #23）。
    /// </summary>
    public (bool Ok, string Output) Run(params string[] args) => RunWithTimeout(10000, args);

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    // 起こした処理が失敗したときに、Windows が画面を出さないようにする。
    // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX。
    // ふだんは失敗しないが、機械を終了する場面では起こすこと自体ができず、
    // 「アプリケーションを正しく起動できませんでした」という画面が出た（Issue #23）。
    // 起こさない作りにしたうえで、それでも出さない備えを置く。
    private const uint SemQuiet = 0x0001 | 0x0002 | 0x8000;

    public (bool Ok, string Output) RunWithTimeout(int timeoutMs, params string[] args)
    {
        if (Path == null) return (false, Missing ?? "");
        uint prev = SetErrorMode(SemQuiet);
        try
        {
            var psi = new ProcessStartInfo(Path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (false, "起動できませんでした");
            string outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (false, "応答がありません"); }
            return (p.ExitCode == 0, outText.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { SetErrorMode(prev); }
    }
}

/// <summary>
/// 選んでいる物理パッドを、LLMCon 以外の処理から隠す（Issue #12）。
///
/// 使う順序に条件がある。HidHide は「新しく開く」のを止める仕組みなので、既に起動している
/// ゲームには効かない。LLMCon を先に起動し、パッドを選んでからゲームを始めること。
///
/// 異常終了への備えとして、隠したノードを覚えておき、次の起動で必ず戻す。全体の停止
/// （--cloak-off）も起動時に呼ぶので、前回が異常終了でも、次の起動で必ず正常化する。
/// </summary>
public sealed class HidHidePadHider : IPadHider
{
    private readonly IPadSource _source;
    private readonly HidHideCli _cli = new();
    private readonly string _stateFile;
    private readonly object _lock = new();

    private bool _requested;
    private List<string> _hidden = new();
    private string _target = "";
    private string _note = "";
    private string _last = "";

    public HidHidePadHider(IPadSource source, string instanceName)
    {
        _source = source;
        // 覚えごとは、製品の名前のフォルダに置く。利用者が AppData を覗いたときに、
        // 身に覚えのない名前が出てこないようにするためである（Issue #20）。
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Sanitize(instanceName));
        _stateFile = System.IO.Path.Combine(dir, "hidden-devices.txt");
        try { Directory.CreateDirectory(dir); } catch { /* 書けなくても本体は動かす */ }
        CleanUpLeftovers();
    }

    private static string Sanitize(string s)
        => new string(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    public bool Available => _cli.Available;
    public string? Unavailable => _cli.Missing;
    public bool Requested { get { lock (_lock) return _requested; } }

    /// <summary>最後に隠す・戻すを行ったときの結果。タスクトレイから読めるようにしてある。</summary>
    public string LastMessage { get { lock (_lock) return _last; } }

    /// <summary>
    /// 前回が異常終了だった場合の後始末。全体を止めたうえで、前回自分が隠したものを戻す。
    /// これがあるので、隠れたままコントローラが使えなくなることは無い。
    /// </summary>
    private void CleanUpLeftovers()
    {
        if (!Available) return;
        try
        {
            _cli.Run("--cloak-off");
            if (!File.Exists(_stateFile)) return;
            foreach (var line in File.ReadAllLines(_stateFile))
            {
                var path = line.Trim();
                if (path.Length > 0) _cli.Run("--dev-unhide", path);
            }
            File.Delete(_stateFile);
        }
        catch { /* 後始末に失敗しても起動は続ける */ }
    }

    public string Describe()
    {
        if (!Available) return "hide=unavailable";
        lock (_lock)
        {
            if (!_requested) return "hide=off";
            if (_hidden.Count == 0) return "hide=on(対象なし)";
            return $"hide=on({_target}, {_hidden.Count}ノード)";
        }
    }

    public string SetHiding(bool on)
    {
        // 導入されていなくても、隠す相手の特定まではできる。何を隠すことになるのかを
        // 先に見せておくと、導入するかどうかを判断でき、あとで効かないときの切り分けにも効く。
        if (!Available) return _cli.Missing! + "\n" + WouldHide();
        lock (_lock)
        {
            _requested = on;
            if (!on)
            {
                UnhideLocked();
                return _last = "物理パッドの隠蔽を解除しました。すでに起動しているゲームには、開き直すまで反映されません。";
            }
            return _last = ApplyLocked();
        }
    }

    public void OnSelectionChanged(string? padId)
    {
        if (!Available) return;
        lock (_lock)
        {
            if (!_requested) return;
            _last = ApplyLocked();
        }
    }

    public void Release()
    {
        if (!Available) return;
        lock (_lock) { _requested = false; UnhideLocked(); }
    }

    /// <summary>
    /// Windows を終了する場面での後始末（Issue #23）。
    ///
    /// <b>ここでは何も起こさない。</b>これが結論である。
    ///
    /// 2026/8/8 に実機で分かったことを書き残す。シャットダウンの最中に HidHideCLI を
    /// 起こそうとすると、起動そのものに失敗する。Windows の記録に、こう残っていた。
    ///
    ///   HidHideCLI.exe - アプリケーション エラー :
    ///   アプリケーションを正しく起動できませんでした (0xc0000142)。
    ///
    /// 0xC0000142 は、処理の初期化に失敗したことを表す。セッションが畳まれている最中は、
    /// 新しい処理を起こせない。しかも Windows は、この失敗を画面に出す。利用者から見ると
    /// 「パソコンを切ろうとすると、毎回エラーが出る」ことになる。
    ///
    /// 速さの問題ではなかった。時間を区切っても、待たないようにしても、起こした時点で失敗する。
    /// <b>したがって、終了時の解除は諦める。</b>隠蔽は次の起動の掃除で解ける。それが最後の砦である。
    ///
    /// 覚えの書き込みだけは行う。外の処理を起こさないので安全であり、次の起動で確実に戻せる。
    /// </summary>
    public void ReleaseQuickly()
    {
        if (!Available) return;
        lock (_lock)
        {
            _requested = false;
            SaveState();   // 隠したままの一覧を残す。次の起動でこれを見て戻す
        }
    }

    /// <summary>いま選んでいるパッドについて、隠すことになるノードを説明する。</summary>
    private string WouldHide()
    {
        var hw = _source.SelectedHardware();
        if (hw == null) return "（パッドを選んでいないので、隠す相手はまだ決まりません）";
        var nodes = DeviceNodes.ForGamePad(hw.Vid, hw.Pid);
        if (nodes.Count == 0)
            return $"（{hw.Name} VID_{hw.Vid:X4} PID_{hw.Pid:X4} に当たる機器を、一覧から見つけられませんでした）";
        return $"導入されていれば、{hw.Name} の次のノードを隠します。\n  " + string.Join("\n  ", nodes);
    }

    // ── ここから下は _lock を取った状態で呼ぶ ──

    /// <summary>いま選んでいるパッドを隠す。前の対象は先に戻す。</summary>
    private string ApplyLocked()
    {
        UnhideLocked();

        var hw = _source.SelectedHardware();
        if (hw == null)
            return "隠蔽を有効にしました。まだパッドを選んでいないので、隠しているものはありません。" +
                   "select_pad で選ぶと、その機体を隠します。";

        var nodes = DeviceNodes.ForGamePad(hw.Vid, hw.Pid);
        if (nodes.Count == 0)
            return $"隠す対象を機器の一覧から見つけられませんでした（{hw.Name} VID_{hw.Vid:X4} PID_{hw.Pid:X4}）。";

        // 自分を例外に加える。これをしないと、隠した瞬間に LLMCon 自身も読めなくなる。
        var exe = Environment.ProcessPath;
        if (exe != null) _cli.Run("--app-reg", exe);

        var failed = new List<string>();
        foreach (var n in nodes)
        {
            var (ok, output) = _cli.Run("--dev-hide", n);
            if (!ok) failed.Add($"{n} ({output})");
        }
        var (cloakOk, cloakOut) = _cli.Run("--cloak-on");

        _hidden = nodes.ToList();
        _target = hw.Name;
        _note = failed.Count > 0 ? $" 失敗: {string.Join(" / ", failed)}" : "";
        SaveState();

        var head = cloakOk
            ? $"{hw.Name} をゲームから隠しました（{nodes.Count} ノード）。"
            : $"{hw.Name} の登録はしましたが、隠蔽の開始に失敗しました（{cloakOut}）。";
        return head + _note +
               "\n  " + string.Join("\n  ", nodes) +
               "\n  注意: すでに起動しているゲームやブラウザには効きません。隠してから開き直してください。";
    }

    private void UnhideLocked()
    {
        if (_hidden.Count == 0) { _cli.Run("--cloak-off"); return; }
        _cli.Run("--cloak-off");
        foreach (var n in _hidden) _cli.Run("--dev-unhide", n);
        _hidden = new List<string>();
        _target = "";
        SaveState();
    }

    private void SaveState()
    {
        try
        {
            if (_hidden.Count == 0) { if (File.Exists(_stateFile)) File.Delete(_stateFile); return; }
            File.WriteAllLines(_stateFile, _hidden);
        }
        catch { /* 覚えられなくても、この起動のあいだは _hidden で戻せる */ }
    }
}
