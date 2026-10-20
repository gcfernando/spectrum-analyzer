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

// Standalone x86/STA evidence runner. Direct DrawToBitmap renders are not physical screenshots.
// Uses the final application's own capture and normal Load path; never writes user preferences.
internal static class RadialPinEvidenceRunner
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static string releaseDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        if (IntPtr.Size != 4) throw new InvalidOperationException("Compile and run this helper as x86.");
        releaseDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : @"Spectrum\bin\Release");
        string output = Path.GetFullPath(args.Length > 1 ? args[1] : @"artifacts\audit\radial-style");
        Directory.CreateDirectory(output);
        Directory.SetCurrentDirectory(releaseDirectory);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveReleaseAssembly;
        Application.SetCompatibleTextRenderingDefault(false);
        Form form = null;
        object analyzer = null;
        var pairs = new List<string>();
        var failures = new List<string>();
        var close = false;
        var orderlyStop = false;
        long capturedFrames = 0;
        bool hasSamples = false;
        int rate = 0, channels = 0;
        string state = "NotStarted";
        var started = DateTime.UtcNow;
        try
        {
            var application = Assembly.LoadFrom(Path.Combine(releaseDirectory, "Spectrum.exe"));
            form = (Form)Activator.CreateInstance(application.GetType("Spectrum.FormAudioSpectrum", true));
            Set(form, "_applyingRandomSelection", true);
            form.ShowInTaskbar = false;
            form.Show();
            analyzer = Get(form, "_analyzer");
            Set(form, "_appearance", "Palette");
            ((ComboBox)Get(form, "_modeSelector")).SelectedItem = "Radial Spectrum";
            ((ComboBox)Get(form, "_themeSelector")).SelectedItem = "Aurora";
            ((ComboBox)Get(form, "_styleSelector")).SelectedItem = "Glow";
            Set(form, "_showGuides", true);
            Call(form, "ApplyCurrentAppearance");
            Call(form, "ApplyVisualStyle");
            Call(form, "UpdateUtilityButtons");
            Set(form, "_randomRotationEnabled", true);
            Set(form, "_rotationIntervalMinutes", 30);
            Call(form, "ConfigureRotationTimer");
            form.ClientSize = new Size(1200, 480);
            Pump(3000);
            capturedFrames = (long)Property(analyzer, "CapturedFrames");
            var audio = Get(form, "_audioApplyBuffer");
            hasSamples = (bool)Property(audio, "HasSamples");
            var status = Property(analyzer, "CurrentStatus");
            rate = (int)Property(status, "SampleRate");
            channels = (int)Property(status, "Channels");
            state = Property(status, "State").ToString();
            if (capturedFrames == 0 || !hasSamples)
                failures.Add("No real captured PCM was available for the frozen display; the render does not establish active-audio acceptance.");
            Call(form, "SetDisplayFrozen", true);
            foreach (var size in new[] { new Size(1200, 480), new Size(1400, 600) })
            {
                form.ClientSize = size;
                Pump(100);
                if (form.TopMost) Call(form, "TogglePin");
                string prefix = "radial-aurora-glow-guides-random30m-" + size.Width + "x" + size.Height;
                using (var unpinned = Render(form))
                {
                    unpinned.Save(Path.Combine(output, prefix + "-unpinned-direct-render.png"), ImageFormat.Png);
                    var before = Geometry(form);
                    var values = (byte[])((byte[])Get(form, "_applyBuffer")).Clone();
                    var pin = (Control)Get(form, "_pinButton");
                    var pinOrigin = form.PointToClient(pin.Parent.PointToScreen(pin.Location));
                    var feedbackBounds = new Rectangle(pinOrigin, pin.Size);
                    Call(form, "TogglePin");
                    bool nativePin = (GetWindowLong(form.Handle, -20) & 8) != 0;
                    var after = Geometry(form);
                    bool sameGeometry = before.SequenceEqual(after);
                    bool sameFrame = values.SequenceEqual((byte[])Get(form, "_applyBuffer"));
                    if (!form.TopMost || !nativePin) failures.Add(prefix + ": native always-on-top state was not enabled.");
                    if (!sameGeometry)
                    {
                        for (int index = 0; index < Math.Max(before.Length, after.Length); index++)
                        {
                            string prior = index < before.Length ? before[index] : "<missing>";
                            string current = index < after.Length ? after[index] : "<missing>";
                            if (prior != current) failures.Add(prefix + ": geometry entry " + index + ": " + prior + " -> " + current);
                        }
                    }
                    if (!sameFrame) failures.Add(prefix + ": frozen displayed spectrum changed during Pin.");
                    using (var pinned = Render(form))
                    {
                        pinned.Save(Path.Combine(output, prefix + "-pinned-direct-render.png"), ImageFormat.Png);
                        long pixelsOutsideFeedback = 0;
                        for (int y = 0; y < pinned.Height; y++)
                        for (int x = 0; x < pinned.Width; x++)
                            if (!feedbackBounds.Contains(x, y) && pinned.GetPixel(x, y).ToArgb() != unpinned.GetPixel(x, y).ToArgb())
                                pixelsOutsideFeedback++;
                        pairs.Add("{\"width\":" + size.Width + ",\"height\":" + size.Height +
                            ",\"topMost\":" + Bool(form.TopMost) + ",\"nativeTopMost\":" + Bool(nativePin) +
                            ",\"sameGeometry\":" + Bool(sameGeometry) + ",\"sameFrozenSpectrum\":" + Bool(sameFrame) +
                            ",\"pixelDifferencesOutsidePinButton\":" + pixelsOutsideFeedback +
                            ",\"unpinnedImage\":" + Quote(prefix + "-unpinned-direct-render.png") +
                            ",\"pinnedImage\":" + Quote(prefix + "-pinned-direct-render.png") + "}");
                    }
                    Call(form, "TogglePin");
                    if (form.TopMost || (GetWindowLong(form.Handle, -20) & 8) != 0 || !before.SequenceEqual(Geometry(form)))
                        failures.Add(prefix + ": unpin failed native state/geometry restoration.");
                }
            }
            if ((string)((ComboBox)Get(form, "_modeSelector")).SelectedItem != "Radial Spectrum" ||
                (string)((ComboBox)Get(form, "_themeSelector")).SelectedItem != "Aurora" ||
                (string)((ComboBox)Get(form, "_styleSelector")).SelectedItem != "Glow")
                failures.Add("Requested mode/palette/effect was not selected.");
        }
        catch (Exception exception)
        {
            failures.Add(exception.GetBaseException().GetType().Name + ": " + exception.GetBaseException().Message);
        }
        finally
        {
            try
            {
                if (form != null)
                {
                    if (!form.IsDisposed) { form.TopMost = false; form.Close(); }
                    form.Dispose();
                    close = form.IsDisposed;
                }
                orderlyStop = analyzer != null && Property(Property(analyzer, "CurrentStatus"), "State").ToString() == "Stopped";
            }
            catch (Exception exception) { failures.Add("Cleanup: " + exception.GetBaseException().Message); }
        }
        if (!close || !orderlyStop) failures.Add("Form disposal/capture stop was not verified.");
        string json = "{\"helper\":\"RadialPinEvidenceRunner\",\"utcStarted\":" + Quote(started.ToString("o", CultureInfo.InvariantCulture)) +
            ",\"source\":\"real WASAPI loopback\",\"renderMethod\":\"direct DrawToBitmap; not a physical screenshot\"" +
            ",\"mode\":\"Radial Spectrum\",\"effect\":\"Glow\",\"palette\":\"Aurora\",\"guides\":true,\"randomMinutes\":30" +
            ",\"displayFrozenForPairs\":true,\"capturedFramesBeforeFreeze\":" + capturedFrames + ",\"hasCapturedSamples\":" + Bool(hasSamples) +
            ",\"captureStateBeforeFreeze\":" + Quote(state) + ",\"sampleRate\":" + rate + ",\"channels\":" + channels +
            ",\"formDisposed\":" + Bool(close) + ",\"orderlyCaptureStop\":" + Bool(orderlyStop) +
            ",\"pairs\":[" + string.Join(",", pairs) + "],\"failures\":[" + string.Join(",", failures.Select(Quote)) + "]}";
        File.WriteAllText(Path.Combine(output, "radial-pin-style-evidence.json"), json, new UTF8Encoding(false));
        Console.WriteLine(json);
        return failures.Count == 0 ? 0 : 1;
    }

    private static Assembly ResolveReleaseAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        string path = Path.Combine(releaseDirectory, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static Bitmap Render(Form form)
    {
        var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        return bitmap;
    }

    private static string[] Geometry(Form form)
    {
        var result = new List<string> { "window:" + form.Bounds, "client:" + form.ClientSize, "minimum:" + form.MinimumSize,
            "titleInset:" + Property(Get(form, "ambiance_ThemeSpectrum"), "TitleRightInset") };
        foreach (var field in new[] { "_selectionPanel", "_advancedVisualizer", "_captureStatusLabel", "ambiance_ControlBox" })
        {
            var control = (Control)Get(form, field);
            result.Add(field + ":" + control.Bounds + ":" + control.Visible);
            if (control is FlowLayoutPanel)
                foreach (Control child in control.Controls) result.Add(child.Name + ":" + child.Bounds + ":" + child.Visible);
        }
        return result.ToArray();
    }

    private static object Get(object target, string name) { return target.GetType().GetField(name, Instance).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Instance).SetValue(target, value); }
    private static object Property(object target, string name) { return target.GetType().GetProperty(name, Instance).GetValue(target, null); }
    private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, Instance).Invoke(target, args); }
    private static string Bool(bool value) { return value ? "true" : "false"; }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char character in value ?? string.Empty)
            if (character == '\\' || character == '"') result.Append('\\').Append(character);
            else if (character < 32) result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else result.Append(character);
        return result.Append('"').ToString();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
}
