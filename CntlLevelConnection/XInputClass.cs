using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace CntlLevelConnection;

/// <summary>
/// その機体が「XInput の機体」かどうかを見分ける。
///
/// Windows は、XInput として扱う機器のインスタンスのパスに <c>IG_</c> を入れる。
/// これが標準的な手がかりである。2026/8/6 に実物で確認した。
///
///   HID\VID_0C12&amp;PID_0EF8&amp;IG_01   Hitbox。XInput の機体
///   HID\VID_0F0D&amp;PID_0092\9&amp;...    導電性コントローラ。素の HID
///
/// Windows.Gaming.Input は機器のパスを見せてくれないので、製造者と製品の識別子で
/// 突き合わせる。レジストリの HID の列挙を読み、IG_ を含む鍵から識別子を集める。
/// SetupAPI を呼ぶより短く、依存も増えない。
/// </summary>
internal static class XInputClass
{
    private const string HidEnumPath = @"SYSTEM\CurrentControlSet\Enum\HID";

    private static readonly object Lock = new();
    private static HashSet<(ushort vid, ushort pid)>? _cache;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// XInput の機体として登録されている製造者と製品の識別子の集合。
    /// 機器の抜き差しで変わるので、しばらく経ったら取り直す。
    /// </summary>
    private static HashSet<(ushort, ushort)> XInputIds()
    {
        lock (Lock)
        {
            if (_cache != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 5) return _cache;
            var set = new HashSet<(ushort, ushort)>();
            try
            {
                using var hid = Registry.LocalMachine.OpenSubKey(HidEnumPath);
                if (hid != null)
                    foreach (var name in hid.GetSubKeyNames())
                    {
                        // 鍵の名前は HID\VID_0C12&PID_0EF8&IG_01 のような形である
                        if (name.IndexOf("IG_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (TryParseIds(name, out var vid, out var pid)) set.Add((vid, pid));
                    }
            }
            catch { /* 読めない環境では、素の HID として扱う（これまでどおりの動き） */ }
            _cache = set;
            _cachedAt = DateTime.UtcNow;
            return set;
        }
    }

    private static bool TryParseIds(string key, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        return TryParseHexAfter(key, "VID_", out vid) && TryParseHexAfter(key, "PID_", out pid);
    }

    private static bool TryParseHexAfter(string s, string marker, out ushort value)
    {
        value = 0;
        int i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + marker.Length + 4 > s.Length) return false;
        return ushort.TryParse(s.AsSpan(i + marker.Length, 4),
            System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    /// <summary>その製造者と製品の識別子の機体が、XInput の機体か。</summary>
    public static bool IsXInputDevice(ushort vid, ushort pid) => XInputIds().Contains((vid, pid));

    /// <summary>覚えているものを捨てる。機器の抜き差しの直後に呼ぶと、すぐ反映される。</summary>
    public static void Forget() { lock (Lock) _cache = null; }
}
