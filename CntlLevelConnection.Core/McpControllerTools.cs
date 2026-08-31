using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace CntlLevelConnection;

/// <summary>このLLMConインスタンスの識別情報。UiLocked はプロファイルによるデザイン固定。Bind は待ち受けアドレス。</summary>
public sealed record LlmConInfo(string Name, int Port, bool UiLocked = false, string Bind = "127.0.0.1");

/// <summary>
/// LLMCon の MCP ツール群（HTTP公開）。LLMが この仮想コントローラを操作・改変する。
/// ボタン名: A B X Y LB RB LS RS Start Back Guide DUp DDown DLeft DRight
/// 入力は ControllerEngine の「LLM注入」経路へ入り、ループで 物理+ソフト入力とマージされる。
/// set_mapping は 物理+ソフト(人間)入力に適用する改変ルールを切り替える。
/// </summary>
[McpServerToolType]
public sealed class McpControllerTools(ControllerEngine engine, MacroEngine macros, WebController web, LlmConInfo info, ConnectionManager connections, EventLog events, IMicTrigger mic)
{
    private static string Bad(string[] buttons)
    {
        var bad = buttons.Where(b => !ControllerEngine.IsKnownButton(b)).ToArray();
        return bad.Length > 0 ? $"unknown buttons: {string.Join(",", bad)}" : "";
    }

    [McpServerTool(Name = "get_info")]
    [Description("Get this LLMCon instance info: name, MCP port, whether the virtual controller is connected, " +
                 "the mic-trigger state, and whether the UI design is locked by a profile.")]
    public string GetInfo()
    {
        var hide = engine.PadHider is { } h ? $" {h.Describe()}" : "";
        return $"name={info.Name} bind={info.Bind} port={info.Port} sink={engine.SinkNames} connected={engine.Connected} uiLocked={info.UiLocked}{hide} ({mic.Describe()})";
    }

    // ── 物理パッド（軸の割り当てを調べて設定する）────────────────
    [McpServerTool(Name = "list_pads")]
    [Description("List the physical game pads this LLMCon can read (the virtual pad it creates is excluded), " +
                 "and show which one is currently selected. On platforms without physical pad support this is empty.")]
    public string ListPads()
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        var pads = engine.ListPads();
        if (pads.Count == 0) return "(no physical pads)";
        var sel = engine.SelectedPadId;
        // id は記号を多く含むので、引用符で囲んで境界をはっきりさせる（そのまま select_pad に渡せる）。
        // 番号でも選べるようにしてあるので、書き写しの手間も要らない。
        return string.Join("\n", pads.Select((p, i) =>
            $"{(p.Id == sel ? "* " : "  ")}[{i}] {p.Name}\n      id=\"{p.Id}\""));
    }

    [McpServerTool(Name = "select_pad")]
    [Description("Choose which physical pad to read. Give either the index shown by list_pads (\"0\", \"1\", ...) or " +
                 "the full id. Pass an empty string to read none. Each pad remembers its own axis mapping.")]
    public string SelectPad([Description("pad index from list_pads, or the full id, or empty for none")] string id)
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        id = (id ?? "").Trim();
        if (id.Length == 0)
        {
            engine.SelectPad(null);
            return "selected pad: (none)";
        }
        var pads = engine.ListPads();
        // 番号での指定を許す（id は記号を多く含み、書き写しで壊れやすいため）
        string? target = null;
        if (int.TryParse(id, out var idx) && idx >= 0 && idx < pads.Count) target = pads[idx].Id;
        else if (pads.Any(p => p.Id == id)) target = id;
        if (target == null)
            return $"そのパッドが見つかりません: \"{id}\"\n候補:\n" +
                   string.Join("\n", pads.Select((p, i) => $"  [{i}] {p.Name}"));
        engine.SelectPad(target);
        var sel = engine.SelectedPadId;
        if (sel == null) return $"選択できませんでした（パッドが外れた可能性があります）: \"{id}\"";
        var name = pads.FirstOrDefault(p => p.Id == sel)?.Name ?? "(不明)";
        return $"selected pad: {name} / axes {engine.PadAxes.Describe()}";
    }

    [McpServerTool(Name = "get_pad_raw")]
    [Description("Read the RAW axis and button values of a physical pad, before any mapping is applied. " +
                 "Use this to work out which raw axis is which control: move a stick or press a trigger and watch " +
                 "the numbers. Axes all read 0..1 — a stick rests near 0.5 and swings both ways, a trigger rests at " +
                 "0 and only rises. Then feed the numbers into set_pad_axes. Without an id it reads the selected " +
                 "pad, or the first one if none is selected.")]
    public string GetPadRaw([Description("pad index from list_pads or the full id (optional; defaults to the selected pad)")] string? id = null)
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        string? target = null;
        var key = (id ?? "").Trim();
        if (key.Length > 0)
        {
            var pads = engine.ListPads();
            if (int.TryParse(key, out var idx) && idx >= 0 && idx < pads.Count) target = pads[idx].Id;
            else target = key;
        }
        var r = engine.ReadPadRaw(target);
        if (r is null) return "no pad to read (connect a pad, or check list_pads)";
        return r.Describe()
             + $"\n  current mapping: {engine.PadAxes.Describe()}"
             + $"\n  buttons: {engine.PadButtons.Describe()}";
    }

    [McpServerTool(Name = "set_pad_axes")]
    [Description("Set which raw axis drives each analog control of the selected physical pad, so that the right " +
                 "stick and the triggers work on pads whose layout differs from the common one. Defaults are " +
                 "lx=0 ly=1 rx=2 ry=3 with the triggers unmapped. Use -1 for \"not present\". invertY flips the " +
                 "vertical axes (most pads report down as the larger value, so this is normally true). " +
                 "If both triggers share one axis (resting mid-range, one direction each), give its number as " +
                 "sharedTrigger and leave lt/rt at -1. Discover the numbers with get_pad_raw. The mapping is " +
                 "remembered per pad.")]
    public string SetPadAxes(
        [Description("raw axis for the left stick X")] int lx = 0,
        [Description("raw axis for the left stick Y")] int ly = 1,
        [Description("raw axis for the right stick X")] int rx = 2,
        [Description("raw axis for the right stick Y")] int ry = 3,
        [Description("raw axis for the left trigger, -1 if none")] int lt = -1,
        [Description("raw axis for the right trigger, -1 if none")] int rt = -1,
        [Description("flip the vertical axes (usually true)")] bool invertY = true,
        [Description("raw axis shared by both triggers, -1 if not shared")] int sharedTrigger = -1)
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        var map = new PadAxisMap(lx, ly, rx, ry, lt, rt, invertY, sharedTrigger);
        engine.SetPadAxes(map);
        var raw = engine.ReadPadRaw();
        string note = raw != null && map.MaxAxisIndex >= raw.AxisCount
            ? $"  注意: この割り当ては軸 {map.MaxAxisIndex} を指していますが、パッドの軸は {raw.AxisCount} 本（0..{raw.AxisCount - 1}）です。範囲の外は 0 になります。"
            : "";
        return $"pad axes set: {map.Describe()}" + (note == "" ? "" : "\n" + note);
    }

    [McpServerTool(Name = "set_pad_buttons")]
    [Description("Set which raw button of the selected physical pad drives each control. The default follows the " +
                 "PS4-style HID layout that fight sticks use (b0=X b1=A b2=B b3=Y, b4=LB b5=RB, b6=LT b7=RT, " +
                 "b8=Back b9=Start, b10=LS b11=RS, b12=Guide), which is what plain HID pads report; XInput pads " +
                 "are read through XInput and never need this. Give only the entries you want to change, " +
                 "as \"8=LT,9=RT\". Use \"8=-\" to unassign. Targets: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide," +
                 "DUp,DDown,DLeft,DRight,LT,RT. Assigning a button to LT or RT gives 0 or 255 only, never an " +
                 "analog value. Discover the raw numbers with get_pad_raw. Remembered per pad. " +
                 "Pass an empty string to go back to the default layout.")]
    public string SetPadButtons(
        [Description("overrides such as \"8=LT,9=RT\"; empty string restores the default layout")] string spec = "")
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        var map = PadButtonMap.FromSpec(spec, out var error);
        if (error != "") return $"割り当てを読み取れませんでした。{error}";
        engine.SetPadButtons(map);
        var raw = engine.ReadPadRaw();
        string note = "";
        if (raw != null)
        {
            var over = Enumerable.Range(0, 32).Where(i => map.TargetOf(i) != null && i >= raw.ButtonCount).ToList();
            if (over.Count > 0)
                note = $"\n  注意: この割り当てはボタン {string.Join(",", over)} を指していますが、"
                     + $"パッドのボタンは {raw.ButtonCount} 個（0..{raw.ButtonCount - 1}）です。範囲の外は無視されます。";
        }
        return $"pad buttons set: {map.Describe()}\n  全体: {map.DescribeAll()}" + note;
    }

    [McpServerTool(Name = "get_pad_profile")]
    [Description("Show the selected pad's axis and button mapping as a JSON snippet you can paste into the " +
                 "profile, so the mapping survives a restart. Mappings set with set_pad_axes and set_pad_buttons " +
                 "are only remembered while this process runs; the profile's \"pads\" list is matched by the pad's " +
                 "vendor and product id every time a pad is selected.")]
    public string GetPadProfile()
    {
        if (!engine.HasPadSource) return "this platform has no physical pad support";
        var snippet = engine.DescribeSelectedPadAsProfile();
        return snippet ?? "パッドを選んでいません。先に select_pad で選んでください。";
    }

    [McpServerTool(Name = "set_pad_hidden")]
    [Description("Hide the selected physical pad from every other process, so that only this LLMCon reads it. " +
                 "Without this, a game sees both the physical pad and the virtual controller, and the raw input " +
                 "cancels out the modification rules (an inverted button is pressed and not pressed at once). " +
                 "Requires HidHide (winget install --id Nefarius.HidHide; installing it needs administrator rights, " +
                 "changing the settings does not). The hiding follows select_pad: choose another pad and the target " +
                 "moves with it. IMPORTANT: hiding only affects handles opened afterwards, so a game or browser that " +
                 "is already running keeps seeing the pad until it is restarted. Hiding is released when this LLMCon " +
                 "exits, and also cleaned up on the next start if it exited abnormally.")]
    public string SetPadHidden([Description("true to hide the selected pad from other processes, false to release")] bool enabled)
    {
        if (engine.PadHider is not { } h) return "this platform cannot hide pads";
        return h.SetHiding(enabled);
    }

    [McpServerTool(Name = "get_state")]
    [Description("Get the current virtual controller output state (after merging physical+software+LLM input and applying rules).")]
    public string GetState() => engine.GetState();

    [McpServerTool(Name = "tap")]
    [Description("Press buttons simultaneously for N frames (at the current fps, default 60) then release. " +
                 "buttons: array e.g. [\"A\"] or [\"A\",\"B\"]. Buttons: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight.")]
    public async Task<string> Tap([Description("button names")] string[] buttons, [Description("frames")] int frames = 3)
    {
        var bad = Bad(buttons); if (bad != "") return bad;
        foreach (var b in buttons) engine.SetLlmButton(b, true);
        try { await Task.Delay((int)Math.Max(1, frames * 1000.0 / engine.Fps)); }
        finally { foreach (var b in buttons) engine.SetLlmButton(b, false); }
        return $"tapped [{string.Join("+", buttons)}] for {frames} frames";
    }

    [McpServerTool(Name = "hold")]
    [Description("Press and hold buttons (additive) until released.")]
    public string Hold([Description("button names")] string[] buttons)
    {
        var bad = Bad(buttons); if (bad != "") return bad;
        foreach (var b in buttons) engine.SetLlmButton(b, true);
        return $"holding [{string.Join("+", buttons)}]";
    }

    [McpServerTool(Name = "release")]
    [Description("Release specific held buttons.")]
    public string Release([Description("button names")] string[] buttons)
    {
        var bad = Bad(buttons); if (bad != "") return bad;
        foreach (var b in buttons) engine.SetLlmButton(b, false);
        return $"released [{string.Join("+", buttons)}]";
    }

    [McpServerTool(Name = "release_all")]
    [Description("Release ALL LLM-injected inputs (neutral). Does not affect the human's physical/software input.")]
    public string ReleaseAll() { engine.LlmNeutral(); return "llm inputs neutral"; }

    [McpServerTool(Name = "set_stick")]
    [Description("Set a stick (LLM-injected) with normalized values. side: left or right. x,y in -1.0..1.0 (y up positive). 0,0 centered.")]
    public string SetStick(
        [Description("left or right")] string side,
        [Description("-1.0..1.0 (right positive)")] double x,
        [Description("-1.0..1.0 (up positive)")] double y)
    {
        bool left = IsLeft(side);
        short sx = (short)Math.Clamp(x * 32767.0, short.MinValue, short.MaxValue);
        short sy = (short)Math.Clamp(y * 32767.0, short.MinValue, short.MaxValue);
        engine.SetLlmStick(left, sx, sy);
        return $"{(left ? "left" : "right")} stick = ({x:F2},{y:F2})";
    }

    [McpServerTool(Name = "set_trigger")]
    [Description("Set a trigger (LLM-injected) with a normalized value. side: left or right. value 0.0..1.0.")]
    public string SetTrigger(
        [Description("left or right")] string side,
        [Description("0.0..1.0")] double value)
    {
        bool left = IsLeft(side);
        engine.SetLlmTrigger(left, (byte)Math.Clamp(value * 255.0, 0, 255));
        return $"{(left ? "left" : "right")} trigger = {value:F2}";
    }

    [McpServerTool(Name = "set_mapping")]
    [Description("Set the modification ruleset applied to the HUMAN input (physical+software). This is how the LLM " +
                 "externally modifies the player's controls. rules: array of {op,...}. " +
                 "Digital-button ops: \"disable\"{button} / \"remap\"{from,to} / \"turbo\"{button,hz} / " +
                 "\"invert\"{button} (button is ON while NOT pressed and OFF while pressed). " +
                 "Time op: \"delay\"{delayMs} lags the WHOLE human input (buttons, sticks, triggers) by delayMs ms " +
                 "(reaction-delay handicap; LLM-injected input is not delayed). " +
                 "Analog ops on sticks/triggers: \"gain\"{axis,amount=factor} / \"deadzone\"{axis,amount=0..1} / " +
                 "\"invert\"{axis} (on a stick axis this negates it; on a trigger it is a BUTTON inversion — the " +
                 "trigger is read as pressed or not with a threshold, default 30/255 as in XInput, overridable with " +
                 "amount, and the output is 0 when pressed and 255 when not, so pads whose buttons only reach a " +
                 "middle value still invert correctly) / " +
                 "\"clamp\"{axis,amount=0..1 max} / \"curve\"{axis,amount=exponent, >1 finer near center} / " +
                 "\"rate\"{axis,amount=max units per second} (limits how fast the axis can change) / " +
                 "\"swap\"{axis=stick} (exchange the stick's X and Y) / \"rotate\"{axis=stick,amount=degrees}. " +
                 "axis: LX,LY,RX,RY,LT,RT or shorthands LS,RS,sticks,triggers,all. swap/rotate act per stick (use LS,RS,sticks,all). " +
                 "Analog order per axis is deadzone, curve, gain, clamp, invert, then per-stick swap and rotate, then rate. " +
                 "Optional per-rule startSec/endSec (seconds after this call) for time-varying mods. Empty array = passthrough. " +
                 "Buttons: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight. " +
                 "Examples: [{\"op\":\"disable\",\"button\":\"A\"},{\"op\":\"turbo\",\"button\":\"B\",\"hz\":15}] ; " +
                 "swap A/B -> [{\"op\":\"remap\",\"from\":\"A\",\"to\":\"B\"},{\"op\":\"remap\",\"from\":\"B\",\"to\":\"A\"}] ; " +
                 "delay input by 150ms -> [{\"op\":\"delay\",\"delayMs\":150}] ; " +
                 "halve left-stick sensitivity with a deadzone -> [{\"op\":\"deadzone\",\"axis\":\"LS\",\"amount\":0.2},{\"op\":\"gain\",\"axis\":\"LS\",\"amount\":0.5}] ; " +
                 "swap left stick X/Y -> [{\"op\":\"swap\",\"axis\":\"LS\"}] ; rotate left stick 90 deg -> [{\"op\":\"rotate\",\"axis\":\"LS\",\"amount\":90}] ; " +
                 "sluggish left stick (max 3 per second) -> [{\"op\":\"rate\",\"axis\":\"LS\",\"amount\":3}] ; " +
                 "disable Start only 30-60s -> [{\"op\":\"disable\",\"button\":\"Start\",\"startSec\":30,\"endSec\":60}].")]
    public string SetMapping([Description("array of mapping rules; empty = passthrough")] MappingRule[] rules)
    {
        engine.SetMapping(rules ?? Array.Empty<MappingRule>());
        return $"mapping set: {(rules?.Length ?? 0)} rule(s)";
    }

    [McpServerTool(Name = "set_fps")]
    [Description("Set the frame rate used to interpret frame counts (for tap and macros). Default 60, range 1..1000.")]
    public string SetFps([Description("frames per second, 1..1000")] int fps)
    {
        engine.SetFps(fps);
        return $"fps = {fps}";
    }

    [McpServerTool(Name = "define_macro")]
    [Description("Define a named frame-based macro. Each step holds a controller state for 'frames' frames " +
                 "(at the current fps, default 60). A step is {frames, buttons[], lx, ly, rx, ry, lt, rt}; " +
                 "buttons are held during the step, and omitted sticks/triggers are neutral. " +
                 "Example (walk forward 2 frames, then press on frame 3): " +
                 "[{\"frames\":2,\"buttons\":[\"DRight\"]},{\"frames\":1,\"buttons\":[\"DRight\",\"A\"]}].")]
    public string DefineMacro(
        [Description("macro name")] string name,
        [Description("ordered list of frame steps")] MacroStep[] steps)
    {
        macros.Define(name, steps);
        var total = steps.Sum(s => Math.Max(0, s.Frames));
        return $"defined macro '{name}': {steps.Length} steps, {total} frames";
    }

    [McpServerTool(Name = "run_macro")]
    [Description("Run a previously defined macro frame-accurately, then return to neutral. " +
                 "Cancels any currently running macro. Returns when the macro finishes.")]
    public async Task<string> RunMacro([Description("macro name")] string name)
        => await macros.RunAsync(name);

    [McpServerTool(Name = "stop_macro")]
    [Description("Stop the currently running macro and return the LLM-injected input to neutral.")]
    public string StopMacro()
    {
        macros.Stop();
        return "stopped";
    }

    [McpServerTool(Name = "list_macros")]
    [Description("List the names of defined macros, and the current fps.")]
    public string ListMacros()
    {
        var l = macros.List();
        var names = l.Count > 0 ? string.Join(", ", l) : "(none)";
        return $"fps={engine.Fps:F0}; macros: {names}";
    }

    [McpServerTool(Name = "set_controller_ui")]
    [Description("Replace the on-screen web controller's design with the given HTML (it may include <style> and <script>). " +
                 "Connected browser pages reload automatically to show it. The page is served at /vcon.html. " +
                 "Wire inputs by adding attributes to elements: data-btn=\"A\" (also B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight) " +
                 "makes an element act as that button on press/release; data-stick=\"left\" or \"right\" makes an element act as that analog stick when dragged. " +
                 "Input wiring lives in the page harness, so buttons keep working even if your script has an error. " +
                 "Use reset_controller_ui to restore the default design if something breaks.")]
    public async Task<string> SetControllerUi([Description("HTML (with optional CSS and JavaScript) for the controller design")] string html)
    {
        if (info.UiLocked) return "the controller design is locked by the profile";
        web.SetUi(html ?? "");
        await web.BroadcastReloadAsync();
        return $"controller UI updated ({(html?.Length ?? 0)} chars); connected pages reloaded";
    }

    [McpServerTool(Name = "reset_controller_ui")]
    [Description("Restore the default on-screen web controller design (emergency recovery). Connected pages reload.")]
    public async Task<string> ResetControllerUi()
    {
        if (info.UiLocked) return "the controller design is locked by the profile";
        web.Reset();
        await web.BroadcastReloadAsync();
        return "controller UI reset to default";
    }

    [McpServerTool(Name = "get_controller_ui")]
    [Description("Get the current on-screen web controller design (HTML).")]
    public string GetControllerUi() => web.GetDesign();

    [McpServerTool(Name = "list_controller_presets")]
    [Description("List the built-in web controller design presets (name: concept). Use set_controller_preset to apply one, " +
                 "or get_controller_ui to fetch its HTML as a starting point for your own design.")]
    public string ListControllerPresets() => ControllerPresets.List();

    [McpServerTool(Name = "set_controller_preset")]
    [Description("Apply a built-in web controller design preset by name (see list_controller_presets). " +
                 "Connected browser pages reload automatically. Presets include famicom, retro-analog, inclusive-xl, " +
                 "hard-tiny, moving, shrinking, shuffle, fitts, one-button, piano, neon-art, rhythm, hidden, one-handed.")]
    public async Task<string> SetControllerPreset([Description("preset name")] string name)
    {
        if (info.UiLocked) return "the controller design is locked by the profile";
        var html = ControllerPresets.Get(name);
        if (html is null) return $"unknown preset '{name}'. available: {ControllerPresets.Names()}";
        web.SetUi(html);
        await web.BroadcastReloadAsync();
        return $"controller preset set to '{name}'; connected pages reloaded";
    }

    [McpServerTool(Name = "set_mic_trigger")]
    [Description("Configure the microphone-threshold button: when the mic level exceeds the threshold, a button is " +
                 "operated. The signal path is native (WASAPI, ~8 ms sound-to-XInput, measured) and the mic-driven " +
                 "button enters as HUMAN input, so all modification rules (invert, delay, remap, ...) apply to it. " +
                 "mode: \"hold\" (down while above threshold, with hysteresis via low), \"toggle\" (flips on each " +
                 "crossing), \"tap\" (short press on each crossing). threshold/low are normalized peak levels 0..1. " +
                 "Pass enabled=false to turn it off.")]
    public string SetMicTrigger(
        [Description("enable or disable")] bool enabled,
        [Description("target button (A,B,X,Y,LB,RB,LT,RT,Start,Back,DUp,DDown,DLeft,DRight)")] string? button = null,
        [Description("trigger level 0..1 (normalized peak)")] double? threshold = null,
        [Description("release level 0..1 for hold-mode hysteresis (default threshold/2)")] double? low = null,
        [Description("hold | toggle | tap")] string? mode = null)
        => mic.Configure(enabled, button, threshold, low, mode);

    // ── コントローラをまたいだ接続 ───────────────────────────
    [McpServerTool(Name = "add_connection")]
    [Description("Connect THIS controller to another LLMCon: when an event is detected on this controller's human " +
                 "input, send an action to the target LLMCon. Detection and delivery run in the fast loop (the LLM is " +
                 "not in the reactive path). Returns a connection id.\n" +
                 "event: {type:\"press\"|\"release\"|\"sequence\", button:\"A\" (for press/release), buttons:[\"DDown\",\"DRight\",\"A\"] (for sequence), windowMs:500 (max gap between sequence inputs)}. " +
                 "Buttons: A,B,X,Y,LB,RB,LS,RS,Start,Back,Guide,DUp,DDown,DLeft,DRight (digital buttons and d-pad; analog sticks/triggers are not events).\n" +
                 "target: {host:\"127.0.0.1\", port:8778}.\n" +
                 "action (what to do to the target): " +
                 "{kind:\"mapping\", rules:[...set_mapping rules...], durationSec:1} to modify the target's human input for a while (composes with the target's existing mapping, does not clobber it); " +
                 "{kind:\"inject_tap\", buttons:[\"A\"], frames:3} to tap buttons on the target; " +
                 "{kind:\"inject_macro\", macro:\"name\"} to run a macro already defined on the target.\n" +
                 "cooldownMs (optional): minimum interval between fires.\n" +
                 "Example (1P's A press makes 2P unable to act for 1s): " +
                 "event={type:\"press\",button:\"A\"}, target={host:\"127.0.0.1\",port:8778}, " +
                 "action={kind:\"mapping\",durationSec:1,rules:[{op:\"disable\",button:\"A\"},{op:\"disable\",button:\"B\"},{op:\"disable\",button:\"X\"},{op:\"disable\",button:\"Y\"}]}.")]
    public string AddConnection(
        [Description("event detected on THIS controller's human input")] ConnEvent @event,
        [Description("the target LLMCon to act on")] ConnTarget target,
        [Description("what to do to the target when the event fires")] ConnAction action,
        [Description("optional minimum interval between fires, in ms")] double? cooldownMs = null)
    {
        try { return $"connection added: {connections.Add(@event, target, action, cooldownMs)}"; }
        catch (Exception ex) { return $"error: {ex.Message}"; }
    }

    [McpServerTool(Name = "remove_connection")]
    [Description("Remove a connection by its id (as returned by add_connection / shown by list_connections).")]
    public string RemoveConnection([Description("connection id, e.g. c1")] string id)
        => connections.Remove(id) ? $"removed {id}" : $"no connection with id {id}";

    [McpServerTool(Name = "list_connections")]
    [Description("List the cross-controller connections configured on THIS LLMCon, including each one's fire count.")]
    public string ListConnections()
    {
        var l = connections.List();
        if (l.Count == 0) return "(no connections)";
        return string.Join("\n", l.Select(c => c.Describe()));
    }

    [McpServerTool(Name = "get_events")]
    [Description("Get recent events recorded on THIS LLMCon (observation only; the LLM polls this, it is not in the " +
                 "reactive path). Kinds: \"send\" (this controller sent an action to a target), " +
                 "\"recv\" (this controller received an action from a peer, including which peer), \"skip\" (an event " +
                 "matched but was suppressed by cooldown), \"block\" (the human pressed a button that a disable rule " +
                 "was suppressing -- use this to check afterwards how often a self-imposed restriction actually bit). " +
                 "Each line is prefixed with a sequence number; pass afterSeq " +
                 "with the last number you saw to fetch only newer events when polling. Each LLMCon has its own log, so a " +
                 "manager LLM polls every instance it oversees. A running tally of suppressed presses per button is " +
                 "appended at the end; the tally survives even after old events fall out of the log.")]
    public string GetEvents(
        [Description("max events to return (default 20)")] int count = 20,
        [Description("only return events with a sequence number greater than this (default 0 = all)")] long afterSeq = 0)
    {
        var es = events.Recent(count, afterSeq);
        var blocked = engine.BlockedCounts();
        var tally = blocked.Count == 0 ? null
            : "suppressed presses so far: " + string.Join(", ", blocked.Select(b => $"{b.button} {b.count}x"));
        if (es.Count == 0)
            return $"(no events; lastSeq={events.LastSeq})" + (tally is null ? "" : "\n" + tally);
        var body = string.Join("\n", es.Select(e => $"#{e.Seq} {e.Time} {e.Kind}: {e.Detail}"));
        return tally is null ? body : body + "\n" + tally;
    }

    private static bool IsLeft(string side) => side.Trim().ToLowerInvariant() is "left" or "l" or "ls" or "lt";
}
