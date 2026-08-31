using System;
using System.Collections.Generic;
using System.Linq;

namespace CntlLevelConnection;

/// <summary>事象の記録1件。Seq は通し番号で、大規模言語モデルが afterSeq で増分だけ取りに行くのに使う。</summary>
public sealed record EventLogEntry(long Seq, string Time, string Kind, string Detail);

/// <summary>
/// コントローラをまたいだ接続で起きた事象を、直近の一定件数だけ覚えておく記録。
/// 大規模言語モデルが取りに行く（ポーリングする）ための観測専用のサイドチャネルであり、
/// 反応的な経路には関与しない。種類は send（作用を送った）、recv（作用を受けた）、
/// skip（クールダウンで抑制した）など。
/// </summary>
public sealed class EventLog
{
    private readonly object _lock = new();
    private readonly LinkedList<EventLogEntry> _entries = new();
    private long _seq;
    private const int Max = 200;

    public void Add(string kind, string detail)
    {
        var t = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_lock)
        {
            _entries.AddLast(new EventLogEntry(++_seq, t, kind, detail));
            while (_entries.Count > Max) _entries.RemoveFirst();
        }
    }

    /// <summary>直近の事象を返す。afterSeq を渡すと、その番号より後だけを返す（増分の取得）。</summary>
    public IReadOnlyList<EventLogEntry> Recent(int count, long afterSeq)
    {
        lock (_lock)
        {
            IEnumerable<EventLogEntry> q = _entries;
            if (afterSeq > 0) q = q.Where(e => e.Seq > afterSeq);
            var all = q.ToList();
            if (count > 0 && all.Count > count) all = all.Skip(all.Count - count).ToList();
            return all;
        }
    }

    public long LastSeq { get { lock (_lock) return _seq; } }
}
