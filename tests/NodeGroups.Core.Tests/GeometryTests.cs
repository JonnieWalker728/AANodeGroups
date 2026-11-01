using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void RectanglesContainBoundariesAndTranslateWithoutChangingDimensions()
    {
        var rect = new Rect2(-10, -20, 30, 40);
        Assert.True(rect.Contains(new Point2(-10, -20)));
        Assert.True(rect.Contains(new Point2(20, 20)));
        Assert.False(rect.Contains(new Point2(20.1, 0)));
        Assert.Equal(new Rect2(0, -40, 30, 40), rect.Translate(new Point2(10, -20)));
    }

    [Fact]
    public void OverflowedRectangleEndsAreInvalidAndCannotCaptureArbitraryNodes()
    {
        var rect = new Rect2(double.MaxValue / 2, 0, double.MaxValue, 50);
        Assert.False(rect.IsValid);
        Assert.False(rect.Contains(new Point2(double.MaxValue, 25)));
    }
}
