using System;
using System.Buffers.Binary;
using System.Diagnostics;
using CntlLevelConnection;

namespace LLMConHub;

/// <summary>
/// LLMCon が確定させたコントローラの状態を、別のプログラムへ渡すための固定長の電文（36バイト）。
///
/// 受け手は、Bluetooth のコントローラとして振る舞う橋渡しのプログラム（bridge/llmconhub_bridge.py）や、
/// 将来の Pico W や ESP32 の出力用の機器である。どの受け手でも同じ形で読めるように、
/// 文字列ではなく、値の並びを固定したバイト列にした。数値はすべてリトルエンディアンである。
///
/// 並びは次のとおりである。
///   0  2バイト  識別の印。ASCII の 'L' と 'B'。
///   2  1バイト  電文の版。いまは 1。
///   3  1バイト  旗。ビット0 が立っていれば、状態の変化ではなく「生きている」ことを知らせる定期の電文である。
///   4  4バイト  通し番号。送るたびに1つ増える。受け手が取りこぼしを数えるために使う。
///   8  8バイト  送った時刻（単調増加の時計で、マイクロ秒）。
///  16  8バイト  この状態の元になった物理のパッドの入力の時刻（同じ時計で、マイクロ秒）。分からなければ 0。
///  24  2バイト  ボタンのビット（XInput と同じ並び。PadState.Buttons と同じ）。
///  26  1バイト  左トリガー（0 から 255）。
///  27  1バイト  右トリガー（0 から 255）。
///  28  2バイト  左スティックの横（-32768 から 32767）。
///  30  2バイト  左スティックの縦（上が正）。
///  32  2バイト  右スティックの横。
///  34  2バイト  右スティックの縦（上が正）。
///
/// 2つの時刻は、遅延を測るために入れてある。Linux では、.NET の Stopwatch も、evdev の入力の時刻
/// （EVIOCSCLOCKID で CLOCK_MONOTONIC に切り替えたもの）も、Python の time.monotonic_ns も、同じ
/// CLOCK_MONOTONIC を読むので、受け手は「受け取った時刻 − 入力の時刻」で、LLMCon の中で掛かった
/// 時間と送りの時間の合計を計算できる。
/// </summary>
public static class PadFrame
{
    public const int Size = 36;
    public const byte Version = 1;
    public const byte FlagHeartbeat = 0x01;

    /// <summary>単調増加の時計の現在の値（マイクロ秒）。Stopwatch を元にする。</summary>
    public static ulong NowMicros()
        => (ulong)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 1_000_000.0);

    /// <summary>電文を、渡された領域（36バイト以上）に書く。メモリを新しく確保しない。</summary>
    public static void Encode(Span<byte> dst, in PadState s, uint seq, ulong sentMicros, ulong inputMicros, bool heartbeat)
    {
        if (dst.Length < Size) throw new ArgumentException($"電文には {Size} バイトが要ります。", nameof(dst));
        dst[0] = (byte)'L';
        dst[1] = (byte)'B';
        dst[2] = Version;
        dst[3] = heartbeat ? FlagHeartbeat : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), seq);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(8), sentMicros);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(16), inputMicros);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(24), s.Buttons);
        dst[26] = s.LT;
        dst[27] = s.RT;
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(28), s.LX);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(30), s.LY);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(32), s.RX);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(34), s.RY);
    }

    /// <summary>電文を読む。識別の印か版が合わなければ false を返す。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> src, out PadState s, out uint seq, out ulong sentMicros, out ulong inputMicros, out bool heartbeat)
    {
        s = default; seq = 0; sentMicros = 0; inputMicros = 0; heartbeat = false;
        if (src.Length < Size || src[0] != (byte)'L' || src[1] != (byte)'B' || src[2] != Version) return false;
        heartbeat = (src[3] & FlagHeartbeat) != 0;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4));
        sentMicros = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(8));
        inputMicros = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(16));
        s.Buttons = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(24));
        s.LT = src[26];
        s.RT = src[27];
        s.LX = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(28));
        s.LY = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(30));
        s.RX = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(32));
        s.RY = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(34));
        return true;
    }
}
