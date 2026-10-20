// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Spectrum.Tests;

// Explicit integration helper, outside automatic test discovery. Run alone in x86 PowerShell -STA.
// Resource samples describe this short run, not a long-duration stability certification.
public static class DesktopStabilityVerification
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static string Run(int seconds = 120, string outputDirectory = null)
    {
        if (IntPtr.Size != 4 || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Use x86 Windows PowerShell -STA for the real desktop/capture helper.");
        if (seconds < 30 || seconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Use 30–3600 seconds to exercise all modes.");
        outputDirectory = Path.GetFullPath(outputDirectory ?? Path.Combine(Environment.CurrentDirectory, "desktop-stability"));
        Directory.CreateDirectory(outputDirectory);
        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var samples = new List<string>();
        var actions = new List<string>();
        var failures = new List<string>();
        var images = new List<string>();
        var visited = new List<string>();
        var modes = FormAudioSpectrum.VisualModeNames;
        long windows = 0, validWindows = 0, unavailableWindows = 0, lastEndFrame = 0;
        var nonfinite = 0;
        OnChangeHandler observe = (_, frame) =>
        {
            Interlocked.Increment(ref windows);
            var audio = frame.Audio;
            if (audio == null || !audio.HasSamples) { Interlocked.Increment(ref unavailableWindows); return; }
            Interlocked.Increment(ref validWindows);
            Interlocked.Exchange(ref lastEndFrame, audio.EndFrame);
            if (!Finite(audio.LeftRms) || !Finite(audio.RightRms) || !Finite(audio.LeftPeak) || !Finite(audio.RightPeak))
                Interlocked.Exchange(ref nonfinite, 1);
        };
        FormAudioSpectrum form = null;
        Analyzer analyzer = null;
        var pinChecks = 0;
        var freezes = 0;
        var restores = 0;
        var resizes = 0;
        var closed = false;
        var orderlyStop = false;
        var disposalMilliseconds = 0d;
        using (var process = Process.GetCurrentProcess())
        {
            var priorCpu = process.TotalProcessorTime;
            var priorSample = clock.Elapsed.TotalSeconds;
            var nextSample = 1d;
            var nextSecond = 0;
            var modeIndex = -1;
            try
            {
                // The normal Load path starts real WASAPI capture. This guard suppresses selector saves.
                form = new FormAudioSpectrum { ShowInTaskbar = false };
                Set(form, "_applyingRandomSelection", true);
                Analyzer.OnChange += observe;
                form.Show();
                Set(form, "_randomRotationEnabled", false);
                Call(form, "ConfigureRotationTimer");
                analyzer = Field<Analyzer>(form, "_analyzer");
                var selector = Field<ComboBox>(form, "_modeSelector");
                // Measure the requested exercise after startup, so slow initialization cannot skip early modes.
                started = DateTime.UtcNow;
                clock.Restart();
                priorCpu = process.TotalProcessorTime;
                priorSample = 0;
                while (clock.Elapsed.TotalSeconds < seconds && !form.IsDisposed)
                {
                    Application.DoEvents();
                    var elapsed = clock.Elapsed.TotalSeconds;
                    var desiredMode = Math.Min(modes.Length - 1, (int)(elapsed * modes.Length / seconds));
                    if (desiredMode != modeIndex)
                    {
                        modeIndex = desiredMode;
                        selector.SelectedItem = modes[modeIndex];
                        visited.Add(modes[modeIndex]);
                        actions.Add(ActionJson(elapsed, "mode", modes[modeIndex]));
                        Console.WriteLine("Desktop audit {0:F1}s: mode={1}; valid PCM windows={2}",
                            elapsed, modes[modeIndex], Interlocked.Read(ref validWindows));
                        if (modes[modeIndex] == "Radial Spectrum" && images.Count == 0)
                        {
                            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                            var wasFrozen = Field<bool>(form, "_displayFrozen");
                            var wasPinned = form.TopMost;
                            form.SetDisplayFrozen(true);
                            if (wasPinned) CheckPin(form, failures, ref pinChecks);
                            SaveImage(form, outputDirectory, "radial-unpinned-direct-render.png", images);
                            CheckPin(form, failures, ref pinChecks);
                            SaveImage(form, outputDirectory, "radial-pinned-direct-render.png", images);
                            if (!wasPinned) CheckPin(form, failures, ref pinChecks);
                            form.SetDisplayFrozen(wasFrozen);
                        }
                    }
                    if (elapsed >= nextSecond)
                    {
                        var second = nextSecond++;
                        if (second % 15 == 3 || second % 15 == 4)
                        {
                            CheckPin(form, failures, ref pinChecks);
                            actions.Add(ActionJson(elapsed, "pin", form.TopMost.ToString()));
                        }
                        if (second % 12 == 5 || second % 12 == 6)
                        {
                            var freeze = second % 12 == 5;
                            form.SetDisplayFrozen(freeze);
                            if (freeze) freezes++;
                            actions.Add(ActionJson(elapsed, "displayFrozen", freeze.ToString()));
                        }
                        if (second % 20 == 10)
                        {
                            form.ClientSize = resizes++ % 2 == 0 ? new Size(1400, 600) : new Size(1200, 480);
                            actions.Add(ActionJson(elapsed, "resize", form.ClientSize.ToString()));
                        }
                        if (second % 24 == 18) form.WindowState = FormWindowState.Minimized;
                        if (second % 24 == 19)
                        {
                            form.WindowState = FormWindowState.Normal;
                            restores++;
                            actions.Add(ActionJson(elapsed, "minimizeRestore", restores.ToString(CultureInfo.InvariantCulture)));
                        }
                    }
                    if (elapsed >= nextSample)
                    {
                        nextSample = elapsed + 1;
                        process.Refresh();
                        var cpu = process.TotalProcessorTime;
                        var corePercent = (cpu - priorCpu).TotalSeconds / Math.Max(.001, elapsed - priorSample) * 100;
                        var status = analyzer.CurrentStatus;
                        samples.Add("{\"seconds\":" + Number(elapsed) + ",\"cpuPercentOfOneCore\":" + Number(corePercent) +
                            ",\"cpuPercentOfMachine\":" + Number(corePercent / Environment.ProcessorCount) +
                            ",\"privateBytes\":" + process.PrivateMemorySize64 + ",\"workingSetBytes\":" + process.WorkingSet64 +
                            ",\"gdiHandles\":" + GetGuiResources(process.Handle, 0) + ",\"userHandles\":" + GetGuiResources(process.Handle, 1) +
                            ",\"processHandles\":" + process.HandleCount + ",\"threads\":" + process.Threads.Count +
                            ",\"state\":" + Quote(status.State.ToString()) + ",\"sampleRate\":" + status.SampleRate +
                            ",\"channels\":" + status.Channels + ",\"capturedFrames\":" + analyzer.CapturedFrames +
                            ",\"validWindows\":" + Interlocked.Read(ref validWindows) + ",\"mode\":" + Quote(modes[modeIndex]) +
                            ",\"displayFrozen\":" + JsonBool(Field<bool>(form, "_displayFrozen")) +
                            ",\"minimized\":" + JsonBool(form.WindowState == FormWindowState.Minimized) + "}");
                        priorCpu = cpu;
                        priorSample = elapsed;
                    }
                    Thread.Sleep(5);
                }
                if (form.IsDisposed) failures.Add("The window closed before the requested duration.");
                if (Interlocked.Read(ref validWindows) == 0) failures.Add("No real captured PCM windows were observed; device integration remains unavailable.");
                if (Volatile.Read(ref nonfinite) != 0) failures.Add("Captured RMS/sample-peak contains NaN or infinity.");
                if (visited.Distinct().Count() != modes.Length) failures.Add("Not all registered visualization modes were exercised.");
            }
            catch (Exception exception) { failures.Add(exception.GetBaseException().GetType().Name + ": " + exception.GetBaseException().Message); }
            finally
            {
                var disposal = Stopwatch.StartNew();
                try
                {
                    if (form != null)
                    {
                        if (!form.IsDisposed) { form.TopMost = false; form.Close(); }
                        form.Dispose();
                        closed = form.IsDisposed;
                    }
                    orderlyStop = analyzer != null && analyzer.CurrentStatus.State == CaptureState.Stopped;
                }
                catch (Exception exception) { failures.Add("Cleanup: " + exception.GetBaseException().Message); }
                finally { Analyzer.OnChange -= observe; }
                disposalMilliseconds = disposal.Elapsed.TotalMilliseconds;
            }
        }
        if (!closed) failures.Add("The native form was not disposed.");
        if (!orderlyStop) failures.Add("The capture pipeline did not finish in Stopped state.");
        var json = "{\"helper\":\"DesktopStabilityVerification\",\"utcStarted\":" + Quote(started.ToString("o", CultureInfo.InvariantCulture)) +
            ",\"requestedSeconds\":" + seconds + ",\"durationSeconds\":" + Number(clock.Elapsed.TotalSeconds) +
            ",\"logicalProcessors\":" + Environment.ProcessorCount + ",\"architecture\":\"x86\",\"realCapture\":true,\"playbackGenerated\":false" +
            ",\"validPcmWindows\":" + Interlocked.Read(ref validWindows) + ",\"unavailableWindows\":" + Interlocked.Read(ref unavailableWindows) +
            ",\"publishedWindows\":" + Interlocked.Read(ref windows) + ",\"lastObservedEndFrame\":" + Interlocked.Read(ref lastEndFrame) +
            ",\"pinGeometryChecks\":" + pinChecks + ",\"freezeResumeCycles\":" + freezes + ",\"minimizeRestoreCycles\":" + restores +
            ",\"resizes\":" + resizes + ",\"formDisposed\":" + JsonBool(closed) + ",\"orderlyCaptureStop\":" + JsonBool(orderlyStop) +
            ",\"disposeMilliseconds\":" + Number(disposalMilliseconds) + ",\"modesVisited\":[" + string.Join(",", visited.Select(Quote)) +
            "],\"directRenderedImages\":[" + string.Join(",", images.Select(Quote)) + "],\"failures\":[" + string.Join(",", failures.Select(Quote)) +
            "],\"actions\":[" + string.Join(",", actions) + "],\"resourceSamples\":[" + string.Join(",", samples) + "]}";
        var report = Path.Combine(outputDirectory, "desktop-stability.json");
        File.WriteAllText(report, json, new UTF8Encoding(false));
        if (failures.Count != 0) throw new InvalidOperationException("Desktop validation failed. Inspect " + report);
        return json;
    }

    private static void CheckPin(FormAudioSpectrum form, List<string> failures, ref int count)
    {
        var before = Geometry(form);
        var prior = form.TopMost;
        Call(form, "TogglePin");
        count++;
        if (form.TopMost == prior || ((GetWindowLong(form.Handle, -20) & 8) != 0) != form.TopMost)
            failures.Add("Pin did not toggle native always-on-top state (check " + count + ").");
        var after = Geometry(form);
        if (!before.SequenceEqual(after))
        {
            var differences = new List<string>();
            for (var index = 0; index < Math.Max(before.Length, after.Length); index++)
            {
                var original = index < before.Length ? before[index] : "<missing>";
                var current = index < after.Length ? after[index] : "<missing>";
                if (original != current) differences.Add("entry " + index + ": before=" + original + "; after=" + current);
            }
            var detail = "Pin shifted window/toolbar/plot geometry (check " + count +
                "; mode=" + Field<string>(form, "_visualMode") + "; client=" + form.ClientSize +
                "; windowState=" + form.WindowState + "; topMost=" + form.TopMost + "): " + string.Join(" | ", differences);
            failures.Add(detail);
            Console.WriteLine(detail);
        }
    }

    private static string[] Geometry(FormAudioSpectrum form)
    {
        var geometry = new List<string> { form.Bounds.ToString(), form.ClientSize.ToString(), form.MinimumSize.ToString(),
            Field<Ambiance_ThemeContainer>(form, "ambiance_ThemeSpectrum").TitleRightInset.ToString(CultureInfo.InvariantCulture) };
        foreach (var field in new[] { "_selectionPanel", "_captureStatusLabel", "_advancedVisualizer", "ambiance_ControlBox" })
        {
            var control = Field<Control>(form, field);
            geometry.Add(field + ":" + control.Bounds + ":" + control.Visible);
            if (control is FlowLayoutPanel panel)
                foreach (Control child in panel.Controls) geometry.Add(child.Name + ":" + child.Bounds + ":" + child.Visible);
        }
        return geometry.ToArray();
    }

    private static void SaveImage(FormAudioSpectrum form, string directory, string name, List<string> images)
    {
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        images.Add(name);
    }

    private static string ActionJson(double seconds, string action, string value) =>
        "{\"seconds\":" + Number(seconds) + ",\"action\":" + Quote(action) + ",\"value\":" + Quote(value) + "}";
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    private static void Call(object target, string name) => target.GetType().GetMethod(name, PrivateInstance).Invoke(target, null);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static string Number(double value) => Finite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "null";
    private static string JsonBool(bool value) => value ? "true" : "false";
    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value ?? string.Empty)
            if (character == '\\' || character == '"') builder.Append('\\').Append(character);
            else if (character < 32) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(character);
        return builder.Append('"').ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
