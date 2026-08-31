using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Windows.Gaming.Input;

// Probes Windows.Gaming.Input against every attached controller, with three
// independent channels so the failure modes can be told apart:
//
//   WGI     - Windows.Gaming.Input (the thing under test)
//   XInput  - works without a window and without focus; the control channel
//   events  - device arrive/remove, and foreground-window changes
//
// The 2026/8/6 run on nucbox-g3s lost WGI values at t=49s with no explanation,
// because device arrive/remove was not being logged. It is now.
//
//   wgiprobe                    no window
//   wgiprobe --window           real Form, kept in front, messages pumped
//   wgiprobe --map              log every input event (for mapping tables)
//   wgiprobe --seconds=N        how long to sample
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint i, ref XINPUT_STATE s);

    // winmm is the control channel for devices that are NOT XInput (Switch/PS4
    // mode on GP2040-CE, plain HID pads). It works with no window and no focus,
    // so if winmm sees a press and WGI does not, WGI is the one failing.
    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX
    {
        public uint dwSize, dwFlags;
        public uint dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }
    [DllImport("winmm.dll")] static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);

    private static JOYINFOEX NewJoyInfo()
    {
        var j = new JOYINFOEX();
        j.dwSize = (uint)Marshal.SizeOf(typeof(JOYINFOEX));
        j.dwFlags = 0x000000ff; // JOY_RETURNALL
        return j;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    private static readonly object Gate = new object();
    private static System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static IntPtr FormHandle = IntPtr.Zero;
    private static int DeviceEvents;

    private static void Log(string s)
    {
        lock (Gate) Console.WriteLine(string.Format("[{0,7:F2}s] {1}", Clock.Elapsed.TotalSeconds, s));
    }

    private static string XiButtons(ushort b)
    {
        string[] n = { "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Start", "Back",
                       "LeftThumb", "RightThumb", "LB", "RB", "?10", "?11", "A", "B", "X", "Y" };
        var on = Enumerable.Range(0, 16).Where(i => (b & (1 << i)) != 0).Select(i => n[i]);
        return on.Any() ? string.Join("+", on) : "none";
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        var t = sb.ToString();
        return string.IsNullOrEmpty(t) ? "(untitled)" : t;
    }

    private sealed class Dev
    {
        public RawGameController Raw;
        public Gamepad Pad;          // non-null => XInput-shaped device
        public string Name;
        public int Index;
        public bool[] Buttons;
        public GameControllerSwitchPosition[] Switches;
        public double[] Axes;
        public bool[] PrevB;
        public double[] BaseA;
        public ulong TsFirst, TsLast;
        public int Samples, NonZeroTs, Changed, Events;
        public string Prev;
    }

    private static int Main(string[] args)
    {
        bool withWindow = args.Contains("--window");
        bool mapMode = args.Contains("--map");
        // --armed: do not start the clock until the first real input is seen on a
        // control channel. Removes the need to coordinate "press now" with a human.
        bool armed = args.Contains("--armed");
        int seconds = 8;
        var sa = args.FirstOrDefault(a => a.StartsWith("--seconds="));
        if (sa != null) seconds = int.Parse(sa.Substring("--seconds=".Length));

        Console.WriteLine("mode     : " + (withWindow ? "WITH window (Form, not TopMost, messages pumped)" : "NO window (bare console)"));
        Console.WriteLine("duration : " + seconds + "s" + (mapMode ? "   [MAP]" : ""));
        Console.WriteLine();

        Form form = null;
        if (withWindow)
        {
            // Deliberately NOT TopMost. TopMost controls z-order, not focus, so a
            // click on another window moves focus while wgiprobe stays painted on
            // top -- the operator cannot see that anything happened, and the run
            // becomes confusing to drive. Let it go behind like a normal window.
            form = new Form { Text = "wgiprobe -- click away to drop focus", Width = 460, Height = 200 };
            form.Show();
            form.Activate();
            form.BringToFront();
            FormHandle = form.Handle;
            Application.DoEvents();
        }

        // Device arrival/removal is the thing that was missing last time.
        RawGameController.RawGameControllerAdded += (s, c) => { Interlocked.Increment(ref DeviceEvents); Log("DEVICE  RawGameController ADDED   vid=0x" + c.HardwareVendorId.ToString("X4") + " pid=0x" + c.HardwareProductId.ToString("X4")); };
        RawGameController.RawGameControllerRemoved += (s, c) => { Interlocked.Increment(ref DeviceEvents); Log("DEVICE  RawGameController REMOVED vid=0x" + c.HardwareVendorId.ToString("X4") + " pid=0x" + c.HardwareProductId.ToString("X4")); };
        Gamepad.GamepadAdded += (s, c) => { Interlocked.Increment(ref DeviceEvents); Log("DEVICE  Gamepad ADDED"); };
        Gamepad.GamepadRemoved += (s, c) => { Interlocked.Increment(ref DeviceEvents); Log("DEVICE  Gamepad REMOVED"); };

        for (int i = 0; i < 20; i++) { if (withWindow) Application.DoEvents(); Thread.Sleep(50); }

        var raws = RawGameController.RawGameControllers.ToList();
        var pads = Gamepad.Gamepads.ToList();

        // A raw controller that also appears as a Gamepad is XInput-shaped.
        // One that does not is a plain HID device -- the other half of the rule.
        var padFor = new Dictionary<RawGameController, Gamepad>();
        foreach (var p in pads)
        {
            var r = RawGameController.FromGameController(p);
            if (r != null) padFor[r] = p;
        }

        Console.WriteLine("=== inventory ===");
        Console.WriteLine("RawGameControllers : " + raws.Count + "    Gamepads : " + pads.Count);
        var devs = new List<Dev>();
        for (int i = 0; i < raws.Count; i++)
        {
            var r = raws[i];
            string name;
            try { name = r.DisplayName; } catch (Exception) { name = "(unavailable)"; }
            bool isXInput = padFor.ContainsKey(r);
            Console.WriteLine(string.Format("  [{0}] vid=0x{1:X4} pid=0x{2:X4}  buttons={3} axes={4} switches={5}  kind={6}",
                i, r.HardwareVendorId, r.HardwareProductId, r.ButtonCount, r.AxisCount, r.SwitchCount,
                isXInput ? "XInput (has Gamepad)" : "plain HID (no Gamepad)"));
            Console.WriteLine("      name=\"" + name + "\"");
            devs.Add(new Dev
            {
                Raw = r,
                Pad = isXInput ? padFor[r] : null,
                Name = name,
                Index = i,
                Buttons = new bool[r.ButtonCount],
                Switches = new GameControllerSwitchPosition[r.SwitchCount],
                Axes = new double[r.AxisCount],
            });
        }
        Console.WriteLine();

        Console.WriteLine("=== XInput slots at start ===");
        for (uint i = 0; i < 4; i++)
        {
            var s = new XINPUT_STATE();
            Console.WriteLine("  slot " + i + ": " + (XInputGetState(i, ref s) == 0 ? "CONNECTED packet=" + s.dwPacketNumber : "empty"));
        }
        Console.WriteLine();

        if (devs.Count == 0)
        {
            Console.WriteLine("RESULT: nothing enumerated.");
            if (form != null) form.Close();
            return 2;
        }

        Console.WriteLine("=== log ===");
        Clock = System.Diagnostics.Stopwatch.StartNew();
        // The initial enumeration fires Added for every attached pad. Counting
        // those would make every run end with a "the device set changed" warning
        // and quietly undermine its own result.
        Interlocked.Exchange(ref DeviceEvents, 0);

        var xiPrev = new XINPUT_STATE[4];
        var xiConn = new bool[4];
        for (uint i = 0; i < 4; i++) xiConn[i] = XInputGetState(i, ref xiPrev[i]) == 0;

        IntPtr prevFg = GetForegroundWindow();
        Log("FOCUS   foreground = \"" + TitleOf(prevFg) + "\"" + (prevFg == FormHandle ? "  <- our window" : ""));

        // The honest measure of "is WGI delivering" is not the timestamp -- that
        // keeps advancing after focus is lost, while every value silently reads
        // zero. Compare, per foreground segment, how many inputs the control
        // channels saw against how many WGI reported.
        var segments = new List<string>();
        int segControl = 0, segWgi = 0, segWgiLate = 0;
        // Edge counting misses a held button entirely: the bitmask never changes,
        // so it reads as "nothing was pressed". Level counting -- is anything
        // pressed *right now* -- works whether the operator holds or mashes, and
        // holding across a focus change is the cleanest signal there is.
        int segControlHeld = 0, segWgiHeld = 0;
        double segStart = 0;
        string segTitle = TitleOf(prevFg);
        bool segOurs = prevFg == FormHandle;
        int segDead = 0, segAlive = 0;

        // Losing focus zeroes every axis at once, and that burst would otherwise
        // read as "WGI is active" in the segment that just began. Only count WGI
        // activity that happens well clear of the segment boundary.
        Action bumpWgi = () =>
        {
            segWgi++;
            if (Clock.Elapsed.TotalSeconds - segStart > 0.25) segWgiLate++;
        };

        Action<double> closeSegment = end =>
        {
            // Judge on held samples when there are any, since that covers both
            // styles; fall back to edge counts only if nothing was held.
            bool sawInput = segControlHeld > 0 || segControl > 0;
            bool wgiSawIt = segWgiHeld > 0 || segWgiLate > 0;
            string verdict;
            if (!sawInput) verdict = "INCONCLUSIVE (nothing pressed or held)";
            else if (!wgiSawIt) { verdict = "WGI DEAD (control saw input, WGI saw none)"; segDead++; }
            else { verdict = "WGI alive"; segAlive++; }
            segments.Add(string.Format("  {0,6:F2}-{1,6:F2}s  {2}  control: held={3,4} edges={4,3}   wgi: held={5,4} edges={6,3}   {7}   \"{8}\"",
                segStart, end, segOurs ? "[ours]    " : "[not ours]",
                segControlHeld, segControl, segWgiHeld, segWgiLate, verdict, segTitle));
        };

        int xiEvents = 0, fgChanges = 0, xiConnChanges = 0, mmEvents = 0;

        var mmPrev = new JOYINFOEX[2];
        var mmOk = new bool[2];
        for (uint i = 0; i < 2; i++) { mmPrev[i] = NewJoyInfo(); mmOk[i] = joyGetPosEx(i, ref mmPrev[i]) == 0; }
        Console.WriteLine("  winmm joystick 0 present: " + mmOk[0] + "   joystick 1 present: " + mmOk[1]);

        var runClock = armed ? null : Clock;
        const int maxWait = 900;
        if (armed) Console.WriteLine("  ARMED: waiting for the first input (up to " + maxWait + "s), then recording " + seconds + "s.");

        while (true)
        {
            if (runClock == null)
            {
                if (Clock.Elapsed.TotalSeconds >= maxWait) { Console.WriteLine(); Console.WriteLine("(armed: timed out waiting for input)"); break; }
            }
            else if (runClock.Elapsed.TotalSeconds >= seconds) break;

            if (withWindow) Application.DoEvents();

            var fg = GetForegroundWindow();
            if (fg != prevFg)
            {
                fgChanges++;
                Log("FOCUS   foreground -> \"" + TitleOf(fg) + "\"" + (fg == FormHandle ? "  <- our window" : "  <- NOT our window"));
                closeSegment(Clock.Elapsed.TotalSeconds);
                segStart = Clock.Elapsed.TotalSeconds;
                segControl = 0; segWgi = 0; segWgiLate = 0;
                segControlHeld = 0; segWgiHeld = 0;
                segTitle = TitleOf(fg); segOurs = fg == FormHandle;
                prevFg = fg;
            }

            bool controlHeldNow = false, wgiHeldNow = false;

            for (uint i = 0; i < 4; i++)
            {
                var xi = new XINPUT_STATE();
                bool conn = XInputGetState(i, ref xi) == 0;
                if (conn && (xi.Gamepad.wButtons != 0 || xi.Gamepad.bLeftTrigger > 40 || xi.Gamepad.bRightTrigger > 40))
                    controlHeldNow = true;
                if (conn != xiConn[i])
                {
                    xiConnChanges++;
                    Log("XINPUT  slot " + i + " " + (conn ? "CONNECTED" : "DISCONNECTED") + "   <<< device re-enumerated");
                    xiConn[i] = conn;
                    xiPrev[i] = xi;
                    continue;
                }
                if (!conn) continue;
                if (xi.Gamepad.wButtons != xiPrev[i].Gamepad.wButtons)
                {
                    Log("XINPUT  slot " + i + " buttons = " + XiButtons(xi.Gamepad.wButtons) + string.Format("  (0x{0:X4})", xi.Gamepad.wButtons));
                    xiEvents++; segControl++;
                }
                if (Math.Abs(xi.Gamepad.bLeftTrigger - xiPrev[i].Gamepad.bLeftTrigger) > 40 ||
                    Math.Abs(xi.Gamepad.bRightTrigger - xiPrev[i].Gamepad.bRightTrigger) > 40 ||
                    Math.Abs(xi.Gamepad.sThumbLX - xiPrev[i].Gamepad.sThumbLX) > 9000 ||
                    Math.Abs(xi.Gamepad.sThumbLY - xiPrev[i].Gamepad.sThumbLY) > 9000 ||
                    Math.Abs(xi.Gamepad.sThumbRX - xiPrev[i].Gamepad.sThumbRX) > 9000 ||
                    Math.Abs(xi.Gamepad.sThumbRY - xiPrev[i].Gamepad.sThumbRY) > 9000)
                {
                    Log(string.Format("XINPUT  slot {0} axes  LT={1} RT={2} LX={3} LY={4} RX={5} RY={6}",
                        i, xi.Gamepad.bLeftTrigger, xi.Gamepad.bRightTrigger,
                        xi.Gamepad.sThumbLX, xi.Gamepad.sThumbLY, xi.Gamepad.sThumbRX, xi.Gamepad.sThumbRY));
                    xiEvents++; segControl++;
                }
                xiPrev[i] = xi;
            }

            // --- control channel for non-XInput devices ---
            for (uint i = 0; i < 2; i++)
            {
                if (!mmOk[i]) continue;
                var mm = NewJoyInfo();
                if (joyGetPosEx(i, ref mm) != 0) continue;
                if (mm.dwButtons != 0 || mm.dwPOV != 65535) controlHeldNow = true;
                bool moved = mm.dwButtons != mmPrev[i].dwButtons
                          || mm.dwPOV != mmPrev[i].dwPOV
                          || Math.Abs((long)mm.dwXpos - mmPrev[i].dwXpos) > 6000
                          || Math.Abs((long)mm.dwYpos - mmPrev[i].dwYpos) > 6000
                          || Math.Abs((long)mm.dwZpos - mmPrev[i].dwZpos) > 6000
                          || Math.Abs((long)mm.dwRpos - mmPrev[i].dwRpos) > 6000;
                if (moved)
                {
                    Log(string.Format("WINMM   joy{0} buttons=0x{1:X} POV={2} X={3} Y={4} Z={5} R={6}",
                        i, mm.dwButtons, mm.dwPOV, mm.dwXpos, mm.dwYpos, mm.dwZpos, mm.dwRpos));
                    mmEvents++; segControl++;
                    mmPrev[i] = mm;
                }
            }

            // Arm on a held button too, not just an edge -- the operator may
            // already be holding one when the run starts.
            if (runClock == null && ((xiEvents + mmEvents) > 0 || controlHeldNow))
            {
                runClock = System.Diagnostics.Stopwatch.StartNew();
                Log("ARMED   first input detected -- recording for " + seconds + "s from here");
            }

            foreach (var d in devs)
            {
                ulong ts;
                try { ts = d.Raw.GetCurrentReading(d.Buttons, d.Switches, d.Axes); }
                catch (Exception ex) { Log("WGI     dev[" + d.Index + "] read threw: " + ex.GetType().Name); continue; }

                d.Samples++;
                if (d.TsFirst == 0) d.TsFirst = ts;
                d.TsLast = ts;
                if (ts != 0) d.NonZeroTs++;

                if (d.Buttons.Any(b => b) || d.Switches.Any(s => s != GameControllerSwitchPosition.Center))
                    wgiHeldNow = true;

                if (d.PrevB == null)
                {
                    d.PrevB = (bool[])d.Buttons.Clone();
                    d.BaseA = (double[])d.Axes.Clone();
                    Log("WGI     dev[" + d.Index + "] baseline axes: " + string.Join(", ", d.BaseA.Select((v, i) => "a" + i + "=" + v.ToString("F3"))));
                    continue;
                }

                if (mapMode)
                {
                    for (int i = 0; i < d.Buttons.Length; i++)
                    {
                        if (d.Buttons[i] && !d.PrevB[i])
                        {
                            string named = "-";
                            if (d.Pad != null) { var g = d.Pad.GetCurrentReading(); named = ((uint)g.Buttons).ToString() + " (" + g.Buttons + ")"; }
                            Log("WGI     dev[" + d.Index + "] b" + i + " DOWN   Gamepad.Buttons=" + named);
                            d.Events++; bumpWgi();
                        }
                        d.PrevB[i] = d.Buttons[i];
                    }
                    for (int i = 0; i < d.Axes.Length; i++)
                    {
                        if (Math.Abs(d.Axes[i] - d.BaseA[i]) > 0.25)
                        {
                            Log(string.Format("WGI     dev[{0}] a{1} = {2:F3}  (was {3:F3})", d.Index, i, d.Axes[i], d.BaseA[i]));
                            d.BaseA[i] = d.Axes[i];
                            d.Events++; bumpWgi();
                        }
                    }
                    for (int i = 0; i < d.Switches.Length; i++)
                        if (d.Switches[i] != GameControllerSwitchPosition.Center)
                            Log("WGI     dev[" + d.Index + "] s" + i + " -> " + d.Switches[i]);
                }
                else
                {
                    string cur = string.Join(",", d.Axes.Select(a => a.ToString("F3")))
                               + "|" + string.Join("", d.Buttons.Select(b => b ? "1" : "0"));
                    if (d.Prev != null && cur != d.Prev) { d.Changed++; bumpWgi(); }
                    d.Prev = cur;
                }
            }

            if (controlHeldNow) segControlHeld++;
            if (wgiHeldNow) segWgiHeld++;

            Thread.Sleep(mapMode ? 15 : 30);
        }

        Console.WriteLine();
        Console.WriteLine("=== summary ===");
        Console.WriteLine("  device arrive/remove events : " + DeviceEvents);
        Console.WriteLine("  XInput connect/disconnect   : " + xiConnChanges);
        Console.WriteLine("  XInput input events         : " + xiEvents);
        Console.WriteLine("  winmm input events (control): " + mmEvents);
        Console.WriteLine("  foreground changes          : " + fgChanges);
        Console.WriteLine();

        closeSegment(Clock.Elapsed.TotalSeconds);
        Console.WriteLine("=== per foreground segment ===");
        Console.WriteLine("  (control = inputs seen by XInput/winmm, which never need focus)");
        foreach (var s in segments) Console.WriteLine(s);
        Console.WriteLine();

        int controlTotal = xiEvents + mmEvents;
        int judged = segDead + segAlive;
        if (controlTotal == 0)
            Console.WriteLine("VERDICT: nothing was pressed on any channel. INCONCLUSIVE.");
        else if (judged < segments.Count)
            Console.WriteLine("VERDICT: only " + judged + " of " + segments.Count + " segments had input to judge by. "
                + "PARTIAL -- press throughout every segment, especially after focus changes.");
        else if (segDead > 0 && segAlive > 0)
            Console.WriteLine("VERDICT: WGI was alive in one foreground segment and dead in another. Focus is what changed.");
        else if (segDead > 0)
            Console.WriteLine("VERDICT: input DID happen (" + controlTotal + " control events) but WGI delivered nothing.");
        else
            Console.WriteLine("VERDICT: input happened and WGI delivered readings in every segment.");
        Console.WriteLine();
        Console.WriteLine("  Do NOT judge by the timestamp. It keeps advancing after focus is lost");
        Console.WriteLine("  while every value silently reads zero -- a healthy-looking clock over");
        Console.WriteLine("  dead data. Judge by whether readings change when the control channels");
        Console.WriteLine("  say input happened.");
        Console.WriteLine();

        foreach (var d in devs)
        {
            bool arriving = d.NonZeroTs > 0;
            Console.WriteLine(string.Format("  dev[{0}] {1}  kind={2}", d.Index, d.Name, d.Pad != null ? "XInput" : "plain HID"));
            Console.WriteLine(string.Format("      samples={0}  non-zero timestamps={1}/{0}  ts {2} -> {3}",
                d.Samples, d.NonZeroTs, d.TsFirst, d.TsLast));
            Console.WriteLine(string.Format("      last axes: {0}", d.Prev ?? string.Join(",", d.Axes.Select(a => a.ToString("F3")))));
            Console.WriteLine("      VALUES ARRIVING: " + (arriving ? "YES" : "NO"));
        }
        Console.WriteLine();

        if (DeviceEvents > 0 || xiConnChanges > 0)
            Console.WriteLine("NOTE: the device set changed during the run -- a dead WGI reading may just be a stale object, not the windowing rule.");

        if (form != null) form.Close();
        return 0;
    }
}
