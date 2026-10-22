using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Spectrum.Tests;

// Opt-in integration helper, outside test discovery. Run in a dedicated x86 STA
// process when the coordinator owns the GUI slot; it opens the actual main form.
public static class MainWorkspacePerformanceVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static string Run(string outputDirectory)
    {
        if (IntPtr.Size != 4 || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Run in a dedicated x86 STA Windows process.");
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        var start = Stopwatch.StartNew();
        var phases = new List<string>(); var renders = new List<string>(); var failures = new List<string>();
        long rawFrames = 0;
        var threadErrors = 0;
        ThreadExceptionEventHandler threadError = (s, e) => { threadErrors++; failures.Add(e.Exception.GetBaseException().Message); };
        Application.ThreadException += threadError;
        var applicationPath = typeof(FormAudioSpectrum).Assembly.Location;
        var applicationHash = Hash(applicationPath);
        var originalProcessPriority = Process.GetCurrentProcess().PriorityClass.ToString();
        long beginningGdi = 0, endingGdi = 0;
        string finalCaptureState = "Not initialized";
        try
        {
            using var form = new FormAudioSpectrum();
            form.Show(); Pump(1300);
            // Suppress preference persistence while using the real mode-change handlers.
            Set(form, "_applyingRandomSelection", true);
            Set(form, "_randomRotationEnabled", false);
            Get<System.Windows.Forms.Timer>(form, "_rotationTimer")?.Stop();
            var modeSelector = Get<ComboBox>(form, "_modeSelector");
            Get<ComboBox>(form, "_themeSelector").SelectedItem = "Studio";
            Get<ComboBox>(form, "_styleSelector").SelectedItem = "None";
            modeSelector.SelectedItem = "Spectrum";
            form.SetDisplayFrozen(false); form.NavigateWorkspace("Visualize");
            var analyzer = Get<Analyzer>(form, "_analyzer");
            Action<AnalysisFrame> observe = frame => Interlocked.Increment(ref rawFrames);
            analyzer.AnalysisFrameAvailable += observe;
            try
            {
                Pump(300);
                beginningGdi = Resources().Gdi;
                phases.Add(MeasurePhase("Visualize live", analyzer, () => Interlocked.Read(ref rawFrames), 4500));
                form.NavigateWorkspace("Analyze"); Pump(350);
                phases.Add(MeasurePhase("Analyze live", analyzer, () => Interlocked.Read(ref rawFrames), 4500));
                form.NavigateWorkspace("Visualize"); Pump(250);
                var expectedModes = FormAudioSpectrum.VisualModeNames;
                foreach (var mode in expectedModes)
                {
                    if (start.Elapsed.TotalSeconds > 26)
                    { failures.Add("The bounded run reached its deadline before all modes completed."); break; }
                    modeSelector.SelectedItem = mode;
                    Pump(300);
                    var before = Resources();
                    var samples = new List<double>();
                    // Refresh synchronously invalidates and paints the real shown window.
                    // It measures this host's paint cost, not compositor FPS or end-to-end latency.
                    for (var sample = 0; sample < 3; sample++)
                    {
                        var paint = Stopwatch.StartNew(); form.Refresh(); paint.Stop();
                        samples.Add(paint.Elapsed.TotalMilliseconds); Pump(15);
                    }
                    var after = Resources();
                    renders.Add("{\"mode\":" + Quote(mode) + ",\"windowWidth\":" + form.Width + ",\"windowHeight\":" + form.Height +
                        ",\"paintSamples\":3,\"averagePaintMilliseconds\":" + Number(samples.Average()) +
                        ",\"minimumPaintMilliseconds\":" + Number(samples.Min()) + ",\"maximumPaintMilliseconds\":" + Number(samples.Max()) +
                        ",\"privateBytes\":" + after.Private + ",\"workingSetBytes\":" + after.Working +
                        ",\"managedBytes\":" + after.Managed + ",\"gdiHandles\":" + after.Gdi + ",\"userHandles\":" + after.User +
                        ",\"gdiDelta\":" + (after.Gdi - before.Gdi) + ",\"userDelta\":" + (after.User - before.User) + "}");
                }
                endingGdi = Resources().Gdi;
                finalCaptureState = analyzer.CurrentStatus.State.ToString();
            }
            finally { analyzer.AnalysisFrameAvailable -= observe; }
            form.Close(); Pump(100);
        }
        catch (Exception exception) { failures.Add(exception.GetBaseException().Message); }
        finally { Application.ThreadException -= threadError; }
        var hashAfter = Hash(applicationPath);
        if (hashAfter != applicationHash) failures.Add("Application hash changed during verification.");
        var json = "{\"helper\":\"MainWorkspacePerformanceVerification\",\"dateUtc\":" + Quote(DateTime.UtcNow.ToString("o")) +
            ",\"releaseAssemblySha256\":" + Quote(applicationHash) + ",\"processBits\":32,\"durationSeconds\":" + Number(start.Elapsed.TotalSeconds) +
            ",\"priorityClass\":" + Quote(originalProcessPriority) + ",\"logicalProcessors\":" + Environment.ProcessorCount +
            ",\"shownApplication\":true,\"syntheticAudioInjected\":false,\"preferencesPersistenceSuppressed\":true," +
            "\"visualStyle\":\"None\",\"palette\":\"Studio\",\"observedNativeRawWindows\":" + Interlocked.Read(ref rawFrames) +
            ",\"finalCaptureState\":" + Quote(finalCaptureState) + ",\"modesCompleted\":" + renders.Count +
            ",\"expectedModes\":" + FormAudioSpectrum.VisualModeNames.Length + ",\"gdiHandlesBeforePhases\":" + beginningGdi +
            ",\"gdiHandlesAfterModes\":" + endingGdi + ",\"threadExceptions\":" + threadErrors +
            ",\"phases\":[" + string.Join(",", phases) + "],\"renderCosts\":[" + string.Join(",", renders) +
            "],\"failures\":[" + string.Join(",", failures.Select(Quote)) + "],\"limits\":[" +
            Quote("One bounded run on this host; no FPS, latency, sustained-memory, or universal performance guarantee.") + "," +
            Quote("CPU phases include native capture, presentation, background workspace processing, and normal host scheduling.") + "," +
            Quote("Paint samples use synchronous Refresh on the actual shown form. They exclude compositor display and mode-switch warmup.") + "," +
            Quote("Memory/GDI differences include legitimate caches retained while switching views; they do not by themselves establish a leak.") + "," +
            Quote("Only Studio palette and None effect are sampled. Capture can be unavailable when the default output is inactive; native window counts record whether delivery occurred.") + "]}";
        File.WriteAllText(Path.Combine(directory, "main-workspace-performance.json"), json, new UTF8Encoding(false));
        return json;
    }

    private static string MeasurePhase(string name, Analyzer analyzer, Func<long> rawWindows, int milliseconds)
    {
        var before = Resources(); var countBefore = rawWindows();
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        using var process = Process.GetCurrentProcess();
        var priorCpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        Pump(milliseconds); var seconds = clock.Elapsed.TotalSeconds;
        var cpu = (process.TotalProcessorTime - priorCpu).TotalSeconds / seconds * 100;
        var after = Resources(); var status = analyzer.CurrentStatus;
        return "{\"name\":" + Quote(name) + ",\"durationSeconds\":" + Number(seconds) +
            ",\"cpuPercentOfOneCore\":" + Number(cpu) + ",\"cpuPercentOfMachine\":" + Number(cpu / Environment.ProcessorCount) +
            ",\"rawWindowsObserved\":" + (rawWindows() - countBefore) + ",\"captureState\":" + Quote(status.State.ToString()) +
            ",\"sampleRate\":" + status.SampleRate + ",\"channels\":" + status.Channels +
            ",\"privateBytesBefore\":" + before.Private + ",\"privateBytesAfter\":" + after.Private +
            ",\"managedBytesBefore\":" + before.Managed + ",\"managedBytesAfter\":" + after.Managed +
            ",\"gdiHandlesBefore\":" + before.Gdi + ",\"gdiHandlesAfter\":" + after.Gdi +
            ",\"userHandlesBefore\":" + before.User + ",\"userHandlesAfter\":" + after.User +
            ",\"gen0Collections\":" + (GC.CollectionCount(0) - gen0) + ",\"gen1Collections\":" + (GC.CollectionCount(1) - gen1) +
            ",\"gen2Collections\":" + (GC.CollectionCount(2) - gen2) + "}";
    }
    private sealed class ResourceSnapshot
    { internal long Private, Working, Managed, Gdi, User; }
    private static ResourceSnapshot Resources()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new ResourceSnapshot { Private = process.PrivateMemorySize64, Working = process.WorkingSet64,
            Managed = GC.GetTotalMemory(false), Gdi = GetGuiResources(process.Handle, 0), User = GetGuiResources(process.Handle, 1) };
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static string Hash(string path)
    { using var algorithm = SHA256.Create(); using var file = File.OpenRead(path); return BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", ""); }
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Quote(string text) => "\"" + (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
