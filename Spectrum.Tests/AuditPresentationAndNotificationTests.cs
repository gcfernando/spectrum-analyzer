// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public class AuditPresentationAndNotificationTests
{
    public static IEnumerable<object[]> RangeLayoutCases()
    {
        foreach (var sampleRate in new[] { 22050, 32000 })
        foreach (var width in new[] { 300, 520, 840, 900 })
        foreach (var fontScale in new[] { 1f, 1.5f, 2f })
            yield return new object[] { sampleRate, width, fontScale };
    }

    [Theory]
    [MemberData(nameof(RangeLayoutCases))]
    public void RenderedFrequencyRangeRowContainsCompleteExactTextOrIsHiddenAndReclaimed(int sampleRate, int width, float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.AdvancedSpectralBalance,
                Size = new Size(width, 240), Font = new Font("Segoe UI", 9 * fontScale)
            };
            var data = new AudioVisualizationData();
            data.Update(new float[960], 480, 2, sampleRate);
            data.SetSevenBandPower(new double[7]);
            control.SetAudioData(data);
            using var actual = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(actual);
            var font = CombinedRandomizationTests.GetField<Font>(control, "_analysisBodyFont");
            var titleFont = CombinedRandomizationTests.GetField<Font>(control, "_analysisTitleFont");
            var slotWidth = control.SevenBandPlotBounds.Width / 7;
            var compact = width / 7f < 96;
            var allFit = Enumerable.Range(0, 7).All(band =>
                graphics.MeasureString(control.GetSevenBandRangeLabel(band, compact), font).Width <= slotWidth);
            Assert.Equal(allFit, control.SevenBandRangesVisible);
            if (width >= 840 && fontScale == 1) Assert.True(control.SevenBandRangesVisible);
            if (width == 300 && sampleRate == 22050) Assert.False(control.SevenBandRangesVisible);

            var lineHeight = (float)Math.Ceiling(font.GetHeight(graphics));
            var valueHeight = (float)Math.Ceiling(titleFont.GetHeight(graphics));
            var showName = width / 7f >= 52;
            var expectedBottom = control.Height - valueHeight - (showName ? lineHeight : 0)
                - (control.SevenBandRangesVisible ? lineHeight : 0) - 4 - 3;
            Assert.InRange(Math.Abs(control.SevenBandPlotBounds.Bottom - expectedBottom), 0, .001f);

            // Compare actual rendered text ink with the frequency/axis text brush disabled.
            // Name and value text use the separate primary brush. This detects partial range
            // text left in the optional row when there is insufficient room for exact edges.
            var brush = CombinedRandomizationTests.GetField<SolidBrush>(control, "_textBrush");
            var savedColor = brush.Color;
            Bitmap withoutRange;
            try
            {
                brush.Color = Color.Transparent;
                withoutRange = VisualizationReadabilityTests.Render(control);
            }
            finally { brush.Color = savedColor; }
            using (withoutRange)
            {
                for (var band = 0; band < 7; band++)
                {
                    var left = (int)Math.Ceiling(control.SevenBandPlotBounds.Left + slotWidth * band);
                    var right = (int)Math.Floor(control.SevenBandPlotBounds.Left + slotWidth * (band + 1));
                    var ink = 0;
                    for (var y = (int)Math.Ceiling(control.Height - lineHeight - 3); y < control.Height - 3; y++)
                    for (var x = left; x < right; x++)
                        if (actual.GetPixel(x, y).ToArgb() != withoutRange.GetPixel(x, y).ToArgb()) ink++;
                    if (control.SevenBandRangesVisible) Assert.True(ink > 0, "Every displayed range must contain complete text ink.");
                    else Assert.Equal(0, ink);
                }
            }
        });
    }

    [Theory]
    [InlineData(8000, 4, "2–4 kHz", "2–4k")]
    [InlineData(16000, 5, "6–8 kHz", "6–8k")]
    [InlineData(16000, 6, "n/a", "n/a")]
    [InlineData(22050, 6, "10–11.025 kHz", "10–11.025k")]
    [InlineData(32000, 6, "10–16 kHz", "10–16k")]
    [InlineData(44100, 6, "10–20 kHz", "10–20k")]
    [InlineData(48000, 6, "10–20 kHz", "10–20k")]
    public void SevenBandFrequencyLabelsDescribeTheActualNyquistLimitedBand(int sampleRate, int band, string full, string compact)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.AdvancedSpectralBalance };
            var data = new AudioVisualizationData();
            data.Update(new float[960], 480, 2, sampleRate);
            control.SetAudioData(data);
            Assert.Equal(full, control.GetSevenBandRangeLabel(band, false));
            Assert.Equal(compact, control.GetSevenBandRangeLabel(band, true));
            // Reusing a renderer after device-format changes must invalidate cached range text.
            data.Update(new float[960], 480, 2, 96000);
            control.SetAudioData(data);
            Assert.Equal(SevenBandAnalyzer.Definitions[band].RangeLabel, control.GetSevenBandRangeLabel(band, false));
        });
    }

    [Theory]
    [InlineData(CaptureState.Capturing, "other", false)]
    [InlineData(CaptureState.Silent, "other", false)]
    [InlineData(CaptureState.Capturing, null, false)]
    [InlineData(CaptureState.Capturing, "", false)]
    [InlineData(CaptureState.Capturing, "SELECTED", true)]
    [InlineData(CaptureState.Unavailable, "other", true)]
    [InlineData(CaptureState.Starting, "other", true)]
    [InlineData(CaptureState.Recovering, "other", true)]
    public void EndpointEventsRecoverOnlyTheSelectedOrUnavailableCapture(CaptureState state, string changedEndpoint, bool shouldRecover)
    {
        var status = new CaptureStatus(state, "test", "selected", "test endpoint", null, 48000, 2);
        var calls = 0;
        var client = NewClient(() => calls++, () => status);
        Notify(client, "OnDeviceAdded", changedEndpoint);
        Notify(client, "OnDeviceRemoved", changedEndpoint);
        var stateMethod = client.GetType().GetMethod("OnDeviceStateChanged");
        var deviceState = Enum.Parse(stateMethod.GetParameters()[1].ParameterType, "Active");
        stateMethod.Invoke(client, new[] { (object)changedEndpoint, deviceState });
        Assert.Equal(shouldRecover ? 3 : 0, calls);
    }

    [Theory]
    [InlineData(null, 48000, 2)]
    [InlineData("selected", 0, 2)]
    [InlineData("selected", 48000, 0)]
    public void AChangeCanRetryCaptureWhenNoUsableEndpointFormatIsKnown(string deviceId, int sampleRate, int channels)
    {
        var status = new CaptureStatus(CaptureState.Capturing, "test", deviceId, "test", null, sampleRate, channels);
        var calls = 0;
        var client = NewClient(() => calls++, () => status);
        Notify(client, "OnDeviceAdded", "other");
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Render", "Multimedia", true)]
    [InlineData("Render", "Console", false)]
    [InlineData("Render", "Communications", false)]
    [InlineData("Capture", "Multimedia", false)]
    public void DefaultNotificationsFollowOnlyTheRenderMultimediaRole(string flow, string role, bool shouldRecover)
    {
        var calls = 0;
        var client = NewClient(() => calls++, () => new CaptureStatus(CaptureState.Capturing, "test", "selected", "test", null, 48000, 2));
        var method = client.GetType().GetMethod("OnDefaultDeviceChanged");
        var parameters = method.GetParameters();
        method.Invoke(client, new[] { Enum.Parse(parameters[0].ParameterType, flow), Enum.Parse(parameters[1].ParameterType, role), "new endpoint" });
        Assert.Equal(shouldRecover ? 1 : 0, calls);
    }

    [Fact]
    public void NotificationFilteringReadsTheLatestImmutableStatusAtCallbackTime()
    {
        var status = new CaptureStatus(CaptureState.Capturing, "test", "selected", "test", null, 48000, 2);
        var calls = 0;
        var client = NewClient(() => calls++, () => status);
        Notify(client, "OnDeviceAdded", "other");
        Assert.Equal(0, calls);
        status = new CaptureStatus(CaptureState.Unavailable, "test", "selected", "test", null, 0, 0);
        Notify(client, "OnDeviceAdded", "other");
        Assert.Equal(1, calls);
        status = new CaptureStatus(CaptureState.Capturing, "test", "other", "test", null, 48000, 2);
        Notify(client, "OnDeviceRemoved", "SELECTED");
        Assert.Equal(1, calls);
        Notify(client, "OnDeviceRemoved", "OTHER");
        Assert.Equal(2, calls);
    }

    private static object NewClient(Action action, Func<CaptureStatus> status) =>
        Activator.CreateInstance(typeof(Analyzer).GetNestedType("DeviceNotificationClient", BindingFlags.NonPublic), new object[] { action, status });

    private static void Notify(object client, string method, string id) =>
        client.GetType().GetMethod(method).Invoke(client, new object[] { id });
}
