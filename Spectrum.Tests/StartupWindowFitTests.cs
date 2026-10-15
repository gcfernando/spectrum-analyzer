// Developer :: > Gehan Fernando
using System.Drawing;
using System.Linq;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Makes sure the window fits on screen when the app first starts up.
public class StartupWindowFitTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 360, 300, 360, 300, 1200, 480)]
    [InlineData(0, 0, 1024, 728, -88, 124, 0, 124, 1024, 480)]
    [InlineData(0, 40, 1280, 640, 1100, 610, 80, 200, 1200, 480)]
    [InlineData(-1600, -200, 1366, 728, 3000, 2200, -1434, 48, 1200, 480)]
    [InlineData(0, 0, 1920, 400, 100, 100, 100, 0, 1200, 400)]
    [InlineData(40, 0, 984, 768, 0, 140, 40, 140, 984, 480)]
    public void SharedSmartBoundsFitsTheWindowToUsableDesktopWithoutChangingMinimum(
        int left, int top, int width, int height, int initialLeft, int initialTop,
        int expectedLeft, int expectedTop, int expectedWidth, int expectedHeight)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            form.Bounds = new Rectangle(initialLeft, initialTop, 1200, 480);
            var minimum = form.MinimumSize;
            var area = new Rectangle(left, top, width, height);
            form.Controls.OfType<Ambiance_ThemeContainer>().Single().CorrectBounds(area);
            Assert.Equal(new Rectangle(expectedLeft, expectedTop, expectedWidth, expectedHeight),
                form.Bounds);
            Assert.True(area.Contains(form.Bounds));
            Assert.Equal(minimum, form.MinimumSize);
        });
    }

    [Fact]
    public void DesktopSmallerThanTechnicalMinimumKeepsTopLeftAccessible()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            form.Bounds = new Rectangle(-100, -100, 1200, 480);
            var area = new Rectangle(0, 0, 800, 200);
            form.Controls.OfType<Ambiance_ThemeContainer>().Single().CorrectBounds(area);
            Assert.Equal(Point.Empty, form.Location);
            Assert.Equal(new Size(900, 250), form.Size);
            Assert.Equal(new Size(900, 250), form.MinimumSize);
            Assert.False(area.Contains(form.Bounds));
        });
    }

    [Fact]
    public void MovingToALargerDesktopDoesNotEnlargeTheUsersWindow()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            form.Bounds = new Rectangle(20, 100, 960, 360);
            var theme = form.Controls.OfType<Ambiance_ThemeContainer>().Single();
            theme.CorrectBounds(new Rectangle(0, 0, 1024, 728));
            var chosenBounds = form.Bounds;
            theme.CorrectBounds(new Rectangle(-1920, -200, 5760, 2160));
            Assert.Equal(chosenBounds, form.Bounds);
        });
    }
}
