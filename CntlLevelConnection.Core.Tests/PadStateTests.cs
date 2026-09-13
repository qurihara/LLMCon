using CntlLevelConnection;
using Xunit;

namespace CntlLevelConnection.Core.Tests;

public class PadStateTests
{
    [Fact]
    public void Merge_OrsButtons_AndTakesLargerTrigger()
    {
        var a = new PadState { Buttons = 0x1000, LT = 10, RT = 200 };
        var b = new PadState { Buttons = 0x2000, LT = 50, RT = 100 };

        var m = PadState.Merge(a, b);

        Assert.Equal((ushort)0x3000, m.Buttons);
        Assert.Equal((byte)50, m.LT);
        Assert.Equal((byte)200, m.RT);
    }

    [Fact]
    public void Merge_TakesAxisWithLargerMagnitude_RegardlessOfSign()
    {
        var a = new PadState { LX = 1000, LY = -30000, RX = 0, RY = 5 };
        var b = new PadState { LX = -2000, LY = 20000, RX = 0, RY = -5 };

        var m = PadState.Merge(a, b);

        Assert.Equal((short)-2000, m.LX);
        Assert.Equal((short)-30000, m.LY);
        Assert.Equal((short)0, m.RX);
        Assert.Equal((short)5, m.RY); // 同じ大きさなら先の入力を採る
    }

    [Fact]
    public void Merge_HandlesShortMinValueWithoutOverflow()
    {
        var a = new PadState { LX = short.MinValue };
        var b = new PadState { LX = short.MaxValue };

        Assert.Equal(short.MinValue, PadState.Merge(a, b).LX);
    }
}
