using CntlLevelConnection;
using Xunit;

namespace CntlLevelConnection.Core.Tests;

public class PadButtonMapTests
{
    [Fact]
    public void Default_FollowsPs4HidLayout()
    {
        var d = PadButtonMap.Default;
        Assert.Equal("X", d.TargetOf(0));
        Assert.Equal("A", d.TargetOf(1));
        Assert.Equal("LT", d.TargetOf(6));
        Assert.Equal("Start", d.TargetOf(9));
        Assert.Null(d.TargetOf(99));
        Assert.Equal("既定のまま", d.Describe());
    }

    [Fact]
    public void FromSpec_OverridesOnlyListedButtons_CaseInsensitive()
    {
        var m = PadButtonMap.FromSpec("8=lt, 9=RT", out var error);

        Assert.Equal("", error);
        Assert.Equal("LT", m.TargetOf(8));
        Assert.Equal("RT", m.TargetOf(9));
        Assert.Equal("A", m.TargetOf(1));
        Assert.Equal("b8=LT b9=RT", m.Describe());
    }

    [Fact]
    public void FromSpec_DashRemovesAssignment()
    {
        var m = PadButtonMap.FromSpec("12=-", out var error);

        Assert.Equal("", error);
        Assert.Null(m.TargetOf(12));
        Assert.Equal("b12=-", m.Describe());
    }

    [Theory]
    [InlineData("8LT")]
    [InlineData("x=A")]
    [InlineData("-1=A")]
    [InlineData("8=Jump")]
    public void FromSpec_ReportsErrorForMalformedInput(string spec)
    {
        PadButtonMap.FromSpec(spec, out var error);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void FromSpec_EmptySpecGivesDefault()
    {
        var m = PadButtonMap.FromSpec("  ", out var error);
        Assert.Equal("", error);
        Assert.Equal(PadButtonMap.Default.DescribeAll(), m.DescribeAll());
    }
}

public class PadAxisMapTests
{
    [Fact]
    public void Default_UsesFirstFourAxes()
    {
        var d = PadAxisMap.Default;
        Assert.Equal(3, d.MaxAxisIndex);
        Assert.Equal("LX=0 LY=1 RX=2 RY=3 LT=- RT=- invertY=True", d.Describe());
    }

    [Fact]
    public void MaxAxisIndex_IncludesSharedTrigger()
    {
        var m = PadAxisMap.Default with { SharedTrigger = 5 };
        Assert.Equal(5, m.MaxAxisIndex);
        Assert.Contains("sharedTrigger=5", m.Describe());
    }

    [Fact]
    public void RawReading_WithZeroTimestamp_IsInvalid()
    {
        // Issue #7: 報告が一度も届いていない既定値を、本物の読み取りと取り違えない
        var empty = new PadRawReading("id", "pad", 0, 0, 0, Array.Empty<double>(), Array.Empty<bool>(), "");
        var real = empty with { Timestamp = 123 };

        Assert.False(empty.IsValid);
        Assert.True(real.IsValid);
        Assert.Contains("時刻印が 0", empty.Describe());
    }
}

public class ButtonNameTests
{
    [Fact]
    public void MaskOfAndNamesFromMask_RoundTrip()
    {
        var mask = (ushort)(ControllerEngine.MaskOf("A") | ControllerEngine.MaskOf("dup") | ControllerEngine.MaskOf("Start"));

        Assert.Equal(new[] { "A", "Start", "DUp" }, ControllerEngine.NamesFromMask(mask));
    }

    [Fact]
    public void UnknownButton_HasNoBit()
    {
        Assert.Equal((ushort)0, ControllerEngine.MaskOf("Jump"));
        Assert.Equal((ushort)0, ControllerEngine.MaskOf(null));
        Assert.False(ControllerEngine.IsKnownButton("Jump"));
        Assert.True(ControllerEngine.IsKnownButton("guide"));
    }

    [Fact]
    public void Presets_BuildExpectedRules()
    {
        Assert.Equal(new[] { new MappingRule("disable", Button: "A") }, MappingPresets.Build(Preset.DisableA));

        var swap = MappingPresets.Build(Preset.SwapAB);
        Assert.Equal(2, swap.Length);
        Assert.All(swap, r => Assert.Equal("remap", r.Op));
    }
}
