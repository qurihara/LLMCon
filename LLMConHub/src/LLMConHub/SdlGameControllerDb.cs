using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// SDL のコントローラの対応表（SDL_GameControllerDB の gamecontrollerdb.txt）の1行を、LLMCon の名前への割り当てにしたもの。
///
/// なぜ要るか：物理のパッドの生のボタンと軸の並びは機種ごとに違う。手で対応表を作ると、手元の機種しか扱えない。
/// SDL の対応表は有志が保守していて、Linux だけで数百の機種を、位置で決めた名前（a は下、b は右、x は左、y は上）に合わせている。
/// LLMCon の名前（Xbox の位置）と SDL の名前は同じ位置を指すので、そのまま当てはめられる。
///
/// 書式の例：03000000321500000104000011010000,Razer Panthera PS4,a:b1,b:b2,...,lefttrigger:a3,leftx:a0,...,dpup:h0.1,platform:Linux,
///   bN    N 番目のボタン（SDL の Linux の数え方。BTN_JOYSTICK 以上の番号を昇順に、そのあと BTN_JOYSTICK より小さい番号を昇順に）
///   aN    N 番目の軸（ハットを除く ABS の番号の昇順）。+aN と -aN は軸の半分、aN~ は向きの反転
///   hN.M  N 番目のハット。M は 1 が上、2 が右、4 が下、8 が左
/// </summary>
public sealed class SdlMapping
{
    /// <summary>割り当ての元。種類（b、a、h）と番号、軸なら半分の向きと反転、ハットなら向きのビット。</summary>
    public readonly record struct Source(char Kind, int Index, int Half, bool Invert, int HatMask)
    {
        public static Source? Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int half = 0;
            if (s[0] == '+') { half = 1; s = s[1..]; }
            else if (s[0] == '-') { half = -1; s = s[1..]; }
            bool inv = s.EndsWith('~');
            if (inv) s = s[..^1];
            if (s.Length < 2) return null;
            char kind = s[0];
            if (kind == 'h')
            {
                var parts = s[1..].Split('.');
                if (parts.Length == 2 && int.TryParse(parts[0], out var hi) && int.TryParse(parts[1], out var hm))
                    return new Source('h', hi, 0, false, hm);
                return null;
            }
            if ((kind == 'b' || kind == 'a') && int.TryParse(s[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                return new Source(kind, idx, half, inv, 0);
            return null;
        }
    }

    public string Guid { get; }
    public string Name { get; }

    /// <summary>LLMCon の名前（A、LB、DUp、LT、LX など）から、割り当ての元へ。</summary>
    public IReadOnlyDictionary<string, Source> Targets { get; }

    private SdlMapping(string guid, string name, Dictionary<string, Source> targets)
    {
        Guid = guid; Name = name; Targets = targets;
    }

    // SDL の名前から LLMCon の名前へ
    private static readonly Dictionary<string, string> TargetNames = new()
    {
        ["a"] = "A", ["b"] = "B", ["x"] = "X", ["y"] = "Y",
        ["back"] = "Back", ["start"] = "Start", ["guide"] = "Guide",
        ["leftshoulder"] = "LB", ["rightshoulder"] = "RB",
        ["leftstick"] = "LS", ["rightstick"] = "RS",
        ["dpup"] = "DUp", ["dpdown"] = "DDown", ["dpleft"] = "DLeft", ["dpright"] = "DRight",
        ["lefttrigger"] = "LT", ["righttrigger"] = "RT",
        ["leftx"] = "LX", ["lefty"] = "LY", ["rightx"] = "RX", ["righty"] = "RY",
    };

    /// <summary>対応表の1行を読む。Linux の行でなければ、または読めなければ null。</summary>
    public static SdlMapping? ParseLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '#') return null;
        var f = line.Split(',');
        if (f.Length < 3 || f[0].Length != 32) return null;
        if (!f.Any(x => x.Trim() == "platform:Linux")) return null;
        var t = new Dictionary<string, Source>();
        foreach (var kv in f.Skip(2))
        {
            int c = kv.IndexOf(':');
            if (c <= 0) continue;
            var key = kv[..c].Trim().TrimStart('+', '-');
            if (!TargetNames.TryGetValue(key, out var target)) continue;
            var src = Source.Parse(kv[(c + 1)..].Trim());
            if (src != null) t[target] = src.Value;
        }
        return new SdlMapping(f[0].ToLowerInvariant(), f[1], t);
    }

    /// <summary>Linux の SDL の GUID（バス、製造元、製品、版を、それぞれ2バイトの小さい順と0で並べた16バイト）。</summary>
    public static string MakeGuid(ushort bus, ushort vendor, ushort product, ushort version)
    {
        static string Le(ushort v) => $"{v & 0xff:x2}{v >> 8:x2}0000";
        return Le(bus) + Le(vendor) + Le(product) + Le(version);
    }
}

/// <summary>SDL の対応表の全体。Linux の行だけを読み、GUID と、製造元と製品の組で引けるようにする。</summary>
public sealed class SdlGameControllerDb
{
    private readonly Dictionary<string, SdlMapping> _byGuid = new();
    private readonly Dictionary<(ushort, ushort), SdlMapping> _byVidPid = new();

    public int Count => _byGuid.Count;

    public static SdlGameControllerDb Parse(IEnumerable<string> lines)
    {
        var db = new SdlGameControllerDb();
        foreach (var line in lines)
        {
            var m = SdlMapping.ParseLine(line);
            if (m == null) continue;
            db._byGuid[m.Guid] = m;
            // GUID の 8〜11 文字目が製造元、16〜19 文字目が製品（どちらも2バイトの小さい順）
            if (ushort.TryParse(m.Guid.Substring(10, 2) + m.Guid.Substring(8, 2), NumberStyles.HexNumber, null, out var vid) &&
                ushort.TryParse(m.Guid.Substring(18, 2) + m.Guid.Substring(16, 2), NumberStyles.HexNumber, null, out var pid) &&
                (vid != 0 || pid != 0))
                db._byVidPid.TryAdd((vid, pid), m);
        }
        return db;
    }

    /// <summary>ファイルから読む。無ければ空の対応表を返す。</summary>
    public static SdlGameControllerDb Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadLines(path)) : new SdlGameControllerDb(); }
        catch { return new SdlGameControllerDb(); }
    }

    /// <summary>機体の素性から割り当てを引く。GUID が完全に合うものを先に、無ければ製造元と製品が合うものを使う。</summary>
    public SdlMapping? Find(ushort bus, ushort vendor, ushort product, ushort version)
    {
        if (_byGuid.TryGetValue(SdlMapping.MakeGuid(bus, vendor, product, version), out var m)) return m;
        return _byVidPid.TryGetValue((vendor, product), out var m2) ? m2 : null;
    }
}
