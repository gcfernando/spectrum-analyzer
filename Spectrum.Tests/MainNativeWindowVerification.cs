using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Spectrum.Tests;

// Explicit runtime proof. Launches Program/Main/app.config in a separate owned Spectrum process.
// No Fact/Theory attribute: requires an exclusively coordinated desktop and native audio slot.
public static class MainNativeWindowVerification
{
    public static string Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        if (Process.GetProcessesByName("Spectrum").Length != 0)
            throw new InvalidOperationException("Another Spectrum process is running. No existing process was touched.");
        var loadedAssembly = typeof(FormAudioSpectrum).Assembly.Location;
        var repository = new DirectoryInfo(Path.GetDirectoryName(loadedAssembly));
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Spectrum.sln"))) repository = repository.Parent;
        if (repository == null) throw new InvalidOperationException("The repository Release executable could not be located.");
        var executable = Path.Combine(repository.FullName, "Spectrum", "bin", "Release", "Spectrum.exe");
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The application assembly did not resolve to Spectrum.exe.");
        if (Hash(loadedAssembly) != Hash(executable)) throw new InvalidOperationException("The loaded application assembly differs from the actual Release executable.");
        var oldContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (oldContext == IntPtr.Zero) throw new InvalidOperationException("This Windows version did not accept a thread-local PerMonitorV2 capture context.");
        Process child = null; Probe probe = null; string failure = null; bool orderly = false; int? exit = null;
        var steps = new List<string>(); var dpi = 0U; var awareness = 0; var perMonitorV2 = false; var buttons = 0;
        Rectangle? initialBounds = null; Rectangle? exercisedBounds = null; var startupChromeVerified = false; var maximizeRestoreVerified = false;
        var started = Stopwatch.StartNew();
        try
        {
            child = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            if (child == null) throw new InvalidOperationException("Spectrum.exe did not start.");
            probe = new Probe(child, output);
            probe.Wait(() =>
            {
                var candidates = probe.Windows().Where(h => Title(h) == "Spectrum").ToArray();
                if (candidates.Length > 1) throw new InvalidOperationException("The owned process exposed multiple main windows.");
                if (candidates.Length == 1) probe.Main = candidates[0]; return probe.Main != IntPtr.Zero;
            }, "actual executable main window", 8000);
            ShowWindow(probe.Main, 5); SetForegroundWindow(probe.Main);
            probe.Wait(() => IsWindowVisible(probe.Main) && probe.Button(probe.Main, "Visualize", false) != IntPtr.Zero, "visible native main navigation");
            Check(Title(probe.Main) == "Spectrum", "The native main window must retain its Spectrum task-switcher/accessibility title.");
            var context = GetWindowDpiAwarenessContext(probe.Main); awareness = GetAwarenessFromDpiAwarenessContext(context);
            perMonitorV2 = AreDpiAwarenessContextsEqual(context, new IntPtr(-4)); dpi = GetDpiForWindow(probe.Main);
            if (!perMonitorV2) throw new InvalidOperationException("The actual Spectrum window is not PerMonitorV2 (observed awareness=" + awareness + ", dpi=" + dpi + ").");
            steps.Add("Actual executable main window, Spectrum native title and PerMonitorV2 DPI context observed");
            probe.Pause(150);
            var area = Screen.FromHandle(probe.Main).WorkingArea; var before = Bounds(probe.Main);
            initialBounds = before;
            probe.Capture(probe.Main, "00-actual-main-startup.png");
            probe.VerifyStartupChrome(dpi);
            startupChromeVerified = true;
            steps.Add("Original startup bounds and native top-right caption button geometry recorded before resizing");
            var width = Math.Min(area.Width - 12, (int)Math.Ceiling(1200 * dpi / 96d));
            var height = Math.Min(area.Height - 12, (int)Math.Ceiling(480 * dpi / 96d));
            SetWindowPos(probe.Main, IntPtr.Zero, Math.Max(area.Left + 6, Math.Min(before.Left, area.Right - width - 6)),
                Math.Max(area.Top + 6, Math.Min(before.Top, area.Bottom - height - 6)), width, height, 0x0014);
            probe.Pause(250); var stable = Bounds(probe.Main);
            exercisedBounds = stable;
            probe.Capture(probe.Main, "01-actual-main-visualize.png");
            Check(!IsZoomed(probe.Main), "The default main window must begin in its normal state.");
            probe.ClickMaximizeRestore(); buttons++;
            probe.Wait(() => IsZoomed(probe.Main), "native caption Maximize reached WS_MAXIMIZE");
            probe.Capture(probe.Main, "01a-actual-main-maximized.png");
            probe.ClickMaximizeRestore(); buttons++;
            probe.Wait(() => !IsZoomed(probe.Main) && Bounds(probe.Main) == stable, "native caption Restore recovered exact normal bounds");
            SameBounds(); maximizeRestoreVerified = true;
            steps.Add("Native middle caption button maximized the actual window and restored its exact original bounds");

            Click("Analyze"); probe.Wait(() => probe.Button(probe.Main, "Session ▾", false) != IntPtr.Zero &&
                probe.Button(probe.Main, "Settings ▾", false) != IntPtr.Zero && probe.HasAnalysisTabs(), "embedded Analyze commands and spectrum tabs");
            Check(!probe.Children(probe.Main).Any(h => IsWindowVisible(h) && Title(h) == "Current / averaged"), "Analyze must show its analysis view.");
            SameBounds(); probe.Capture(probe.Main, "02-actual-main-analyze.png"); steps.Add("Native Analyze button revealed embedded controls");
            Click("Compare"); probe.Wait(() => probe.Button(probe.Main, "Capture reference", false) != IntPtr.Zero && !probe.HasAnalysisTabs(), "dedicated Compare commands and view");
            SameBounds(); probe.Capture(probe.Main, "03-actual-main-compare.png"); steps.Add("Native Compare button revealed the current/reference comparison view");
            Click("Sessions"); probe.Wait(() => probe.Button(probe.Main, "Save analysis", false) != IntPtr.Zero && probe.Button(probe.Main, "Import…", false) != IntPtr.Zero, "session library controls");
            SameBounds(); probe.Capture(probe.Main, "04-actual-main-sessions.png"); steps.Add("Native Sessions button revealed the passive local library");

            Click("Analyze"); probe.Wait(() => probe.Button(probe.Main, "Pop out", false) != IntPtr.Zero, "analysis Pop out button");
            Click("Pop out"); var popout = IntPtr.Zero;
            probe.Wait(() =>
            {
                var owned = probe.Windows().Where(h => IsWindowVisible(h) && Title(h) == "Spectrum · Analysis Workbench").ToArray();
                if (owned.Length > 1) throw new InvalidOperationException("Multiple analysis popouts appeared.");
                if (owned.Length == 1) popout = owned[0]; return popout != IntPtr.Zero;
            }, "owned analysis popout");
            Check(OwnerChain(popout, probe.Main), "Analysis popout must be owned by the actual main window."); SameBounds();
            probe.Capture(popout, "05-actual-analysis-popout.png"); steps.Add("Native Pop out button created a main-owned modeless workbench");
            Click("Analyze"); probe.Wait(() => !probe.Windows().Any(h => IsWindowVisible(h) && Title(h) == "Spectrum · Analysis Workbench"), "analysis reembedded from popout");
            probe.Wait(() => probe.Button(probe.Main, "Settings ▾", false) != IntPtr.Zero && probe.HasAnalysisTabs(), "reembedded Analyze commands and spectrum tabs"); SameBounds();
            steps.Add("Native Analyze button reembedded the same workbench");

            Click("Visualize"); probe.Wait(() => probe.Button(probe.Main, "Freeze", false) != IntPtr.Zero, "Visualize Freeze button");
            Click("Freeze"); probe.Wait(() => probe.Button(probe.Main, "Resume", false) != IntPtr.Zero, "Freeze to Resume state"); SameBounds();
            probe.Capture(probe.Main, "06-actual-main-frozen.png");
            Click("Resume"); probe.Wait(() => probe.Button(probe.Main, "Freeze", false) != IntPtr.Zero, "Resume to Freeze state"); SameBounds();
            steps.Add("Native Freeze/Resume toggled both captions without changing main bounds");
            var pinned = (GetWindowLongPtr(probe.Main, -20).ToInt64() & 8) != 0;
            probe.WaitForPin(pinned, "pin-initial");
            Click(pinned ? "Pinned" : "Pin"); probe.WaitForPin(!pinned, "pin-toggled"); SameBounds();
            Click(pinned ? "Pin" : "Pinned"); probe.WaitForPin(pinned, "pin-restored"); SameBounds();
            steps.Add("Native Pin/Pinned toggled and restored WS_EX_TOPMOST without changing main bounds");

            Click("Browse views"); var browser = IntPtr.Zero;
            probe.Wait(() => { browser = probe.Windows().FirstOrDefault(h => IsWindowVisible(h) && Title(h) == "Find a visualization"); return browser != IntPtr.Zero; }, "Browse views modal");
            Check(OwnerChain(browser, probe.Main) && !IsWindowEnabled(probe.Main), "Browse views must be modal to the owned main window.");
            probe.Capture(browser, "07-actual-browse-views.png"); probe.Click(browser, "Cancel"); buttons++;
            probe.Wait(() => !IsWindow(browser) || !IsWindowVisible(browser), "Browse views closed"); Check(IsWindowEnabled(probe.Main), "Main window must recover after closing Browse views.");
            SameBounds(); steps.Add("Native Browse views opened its owned modal and Cancel returned to the main window");

            var sourceAnchor = Bounds(probe.Button(probe.Main, "Choose source"));
            probe.WriteWindows("source-menu-before.txt", sourceAnchor); Click("Choose source"); var menu = IntPtr.Zero;
            try
            {
                probe.Wait(() =>
                {
                    var matches = probe.Windows().Where(h => h != probe.Main && IsWindowVisible(h) &&
                        Class(h).StartsWith("WindowsForms", StringComparison.Ordinal) && Title(h) != "Spectrum · Analysis Workbench" && Title(h) != "Find a visualization" &&
                        (GetWindowLongPtr(h, -16).ToInt64() & 0x80000000L) != 0 && Bounds(h).Width >= 80 && Bounds(h).Height >= 20 &&
                        (OwnerChain(h, probe.Main) || IsNearAnchor(Bounds(h), sourceAnchor, dpi))).ToArray();
                    Check(matches.Length <= 1, "Multiple same-process dropdown candidates match the source button; see source-menu-after.txt.");
                    if (matches.Length == 1) menu = matches[0];
                    return menu != IntPtr.Zero;
                }, "owned visible native source menu popup, including a reused HWND");
            }
            finally { probe.WriteWindows("source-menu-after.txt", sourceAnchor); }
            probe.Capture(menu, "08-actual-source-menu.png");
            PostMessage(menu, 0x0100, new IntPtr(0x1B), IntPtr.Zero);
            probe.Wait(() => !IsWindow(menu) || !IsWindowVisible(menu), "source menu closed without selection"); SameBounds();
            steps.Add("Native Choose source opened and dismissed its owned popup without choosing an endpoint");
            probe.Capture(probe.Main, "09-actual-main-complete.png"); probe.CheckErrors();

            void Click(string caption) { probe.Click(probe.Main, caption); buttons++; }
            void SameBounds()
            {
                Check(Title(probe.Main) == "Spectrum", "A workspace or utility action cleared the native Spectrum window title.");
                Check(Bounds(probe.Main) == stable, "Main bounds changed during a workspace or utility action. Expected=" + stable + "; observed=" + Bounds(probe.Main));
            }
        }
        catch (Exception exception) { failure = exception.GetBaseException().Message; }
        finally
        {
            if (child != null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        if (probe != null)
                            foreach (var window in probe.Windows().Where(h => h != probe.Main)) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        if (probe != null && probe.Main != IntPtr.Zero) PostMessage(probe.Main, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        orderly = child.WaitForExit(5000);
                        if (!orderly) { child.Kill(); child.WaitForExit(2000); failure ??= "The owned Spectrum process did not close orderly and required termination."; }
                    }
                    else if (failure == null) failure = "Spectrum exited before the native UI proof requested shutdown.";
                    if (child.HasExited) exit = child.ExitCode;
                }
                catch (Exception cleanup) { failure ??= "Owned-process cleanup failed: " + cleanup.Message; }
                child.Dispose();
            }
            SetThreadDpiAwarenessContext(oldContext);
        }
        if (exit != 0 || !orderly) failure ??= "The owned executable did not complete orderly with exit code 0.";
        var json = "{\"helper\":\"MainNativeWindowVerification\",\"passed\":" + Bool(failure == null) + ",\"actualProgramLaunch\":true," +
            "\"executable\":" + Quote(executable) + ",\"applicationSha256\":" + Quote(Hash(executable)) + ",\"configSha256\":" + Quote(Hash(executable + ".config")) +
            ",\"loadedAssembly\":" + Quote(loadedAssembly) + ",\"loadedAssemblyMatchesRelease\":true" +
            ",\"perMonitorV2\":" + Bool(perMonitorV2) + ",\"dpiAwareness\":" + awareness + ",\"windowDpi\":" + dpi +
            ",\"initialBounds\":" + RectangleJson(initialBounds) + ",\"exercisedBounds\":" + RectangleJson(exercisedBounds) + ",\"startupChromeVerified\":" + Bool(startupChromeVerified) +
            ",\"maximizeRestoreVerified\":" + Bool(maximizeRestoreVerified) +
            ",\"nativeButtonClicks\":" + buttons + ",\"unexpectedErrorDialogs\":" + (probe?.ErrorDialogs ?? 0) + ",\"orderlyClose\":" + Bool(orderly) +
            ",\"exitCode\":" + (exit?.ToString(CultureInfo.InvariantCulture) ?? "null") + ",\"elapsedSeconds\":" + started.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) +
            ",\"sourceSelectionActionPerformed\":false,\"screenshotMethods\":[" + string.Join(",", (probe?.ScreenshotMethods ?? new List<string>()).Select(Quote)) +
            "],\"steps\":[" + string.Join(",", steps.Select(Quote)) + "],\"error\":" + (failure == null ? "null" : Quote(failure)) + "}";
        File.WriteAllText(Path.Combine(output, "main-native-window.json"), json, new UTF8Encoding(false));
        if (failure != null) throw new InvalidOperationException(failure + " Evidence: " + Path.Combine(output, "main-native-window.json"));
        return json;
    }

    private sealed class Probe
    {
        internal readonly Process Child; private readonly string _output; private readonly Stopwatch _clock = Stopwatch.StartNew();
        internal IntPtr Main; internal int ErrorDialogs; private IntPtr _maximizeRestoreButton;
        internal readonly List<string> ScreenshotMethods = new();
        internal Probe(Process child, string output) { Child = child; _output = output; }
        internal IntPtr[] Windows()
        {
            var windows = new List<IntPtr>(); EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var pid); if (pid == Child.Id) windows.Add(h); return true; }, IntPtr.Zero); return windows.ToArray();
        }
        internal IntPtr[] Children(IntPtr owner)
        { var children = new List<IntPtr>(); EnumChildWindows(owner, (h, _) => { GetWindowThreadProcessId(h, out var pid); if (pid == Child.Id) children.Add(h); return true; }, IntPtr.Zero); return children.ToArray(); }
        internal void WriteWindows(string filename, Rectangle anchor)
        {
            var report = new StringBuilder("Owned process=" + Child.Id + "; source button bounds=" + anchor + "\r\n");
            foreach (var window in Windows())
            {
                report.Append("hwnd=0x").Append(window.ToInt64().ToString("X", CultureInfo.InvariantCulture));
                try
                {
                    report.Append("; visible=").Append(IsWindowVisible(window)).Append("; class=").Append(Class(window)).Append("; caption=").Append(Quote(Title(window)))
                        .Append("; bounds=").Append(Bounds(window)).Append("; owner=0x").Append(GetWindow(window, 4).ToInt64().ToString("X", CultureInfo.InvariantCulture))
                        .Append("; ownedByMain=").Append(OwnerChain(window, Main)).Append("; style=0x").Append(GetWindowLongPtr(window, -16).ToInt64().ToString("X", CultureInfo.InvariantCulture));
                }
                catch (Exception error) { report.Append("; snapshotError=").Append(error.Message); }
                report.AppendLine();
            }
            File.WriteAllText(Path.Combine(_output, filename), report.ToString(), new UTF8Encoding(false));
        }
        internal IntPtr Button(IntPtr owner, string caption, bool required = true)
        {
            var matches = Children(owner).Where(h => IsWindowVisible(h) && IsWindowEnabled(h) && Class(h).StartsWith("WindowsForms10.BUTTON", StringComparison.OrdinalIgnoreCase) && Title(h) == caption).ToArray();
            if (matches.Length > 1 || (required && matches.Length != 1)) throw new InvalidOperationException("Expected one visible native button '" + caption + "', found " + matches.Length + ".");
            return matches.Length == 1 ? matches[0] : IntPtr.Zero;
        }
        internal bool HasAnalysisTabs()
        {
            foreach (var tab in Children(Main).Where(h => IsWindowVisible(h) && Class(h).IndexOf("SysTabControl", StringComparison.OrdinalIgnoreCase) >= 0))
                if (SendMessageTimeout(tab, 0x1304, IntPtr.Zero, IntPtr.Zero, 2, 500, out var count) != IntPtr.Zero && count.ToInt64() >= 3) return true;
            return false;
        }
        internal void VerifyStartupChrome(uint dpi)
        {
            var mainBounds = Bounds(Main); var scale = dpi / 96d;
            var nativeButtons = Children(Main).Where(h => IsWindowVisible(h) && Class(h).StartsWith("WindowsForms10.BUTTON", StringComparison.OrdinalIgnoreCase)).ToArray();
            var report = new StringBuilder("Initial main bounds=" + mainBounds + "; actual window DPI=" + dpi + "\r\nVisible native button geometry (screen coordinates):\r\n");
            foreach (var button in nativeButtons)
                report.AppendLine("hwnd=0x" + button.ToInt64().ToString("X", CultureInfo.InvariantCulture) + "; parent=0x" + GetParent(button).ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                    "; caption=" + Quote(Title(button)) + "; enabled=" + IsWindowEnabled(button) + "; bounds=" + Bounds(button));
            var path = Path.Combine(_output, "00-startup-native-button-bounds.txt");
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            // The custom-painted caption actions intentionally have empty native Text. Identify
            // their one three-button parent by compact size; never click an unnamed candidate.
            var groups = nativeButtons.Where(h => Title(h).Length == 0 && Bounds(h).Width >= 16 * scale && Bounds(h).Width <= 30 * scale &&
                    Bounds(h).Height >= 16 * scale && Bounds(h).Height <= 32 * scale)
                .GroupBy(GetParent).Where(group => group.Key != IntPtr.Zero && group.Count() == 3).ToArray();
            Check(groups.Length == 1, "Expected one visible three-button native caption group; found " + groups.Length + ". Diagnostic: " + path);
            var chrome = groups[0].OrderBy(h => Bounds(h).Left).ToArray();
            _maximizeRestoreButton = chrome[1];
            var parentBounds = Bounds(groups[0].Key);
            Check(mainBounds.Contains(parentBounds) && parentBounds.Top - mainBounds.Top <= 24 * scale &&
                mainBounds.Right - parentBounds.Right >= 0 && mainBounds.Right - parentBounds.Right <= 24 * scale,
                "Native caption group is not contained near the main window top-right. Parent=" + parentBounds + ". Diagnostic: " + path);
            for (var index = 0; index < chrome.Length; index++)
            {
                var bounds = Bounds(chrome[index]);
                Check(IsWindowEnabled(chrome[index]) && parentBounds.Contains(bounds), "A native caption action is disabled or clipped by its parent. Bounds=" + bounds + ". Diagnostic: " + path);
                foreach (var other in nativeButtons.Where(h => h != chrome[index]))
                    Check(!bounds.IntersectsWith(Bounds(other)), "A native caption action overlaps another visible button: " + Quote(Title(other)) + ". Diagnostic: " + path);
            }
            var navigation = new[] { "Visualize", "Analyze", "Compare", "Sessions", "Browse views" }.Select(text => Button(Main, text)).ToArray();
            foreach (var button in navigation)
            {
                var bounds = Bounds(button);
                Check(mainBounds.Contains(bounds) && !bounds.IntersectsWith(parentBounds), "Startup navigation is clipped or overlaps the native caption group: " + Title(button) + ". Diagnostic: " + path);
                foreach (var other in navigation.Where(h => h != button))
                    Check(!bounds.IntersectsWith(Bounds(other)), "Startup navigation buttons overlap: " + Title(button) + "/" + Title(other) + ". Diagnostic: " + path);
            }
            File.AppendAllText(path, "Verified three enabled, fully contained, non-overlapping top-right caption buttons and non-overlapping navigation.\r\n", new UTF8Encoding(false));
        }
        internal void ClickMaximizeRestore()
        {
            CheckErrors(); GetWindowThreadProcessId(_maximizeRestoreButton, out var pid);
            Check(_maximizeRestoreButton != IntPtr.Zero && pid == Child.Id && IsWindowVisible(_maximizeRestoreButton) && IsWindowEnabled(_maximizeRestoreButton),
                "The geometrically identified native middle caption button is unavailable or outside the owned process.");
            SetForegroundWindow(Main);
            Check(PostMessage(_maximizeRestoreButton, 0x00F5, IntPtr.Zero, IntPtr.Zero), "Native Maximize/Restore BM_CLICK failed.");
            Pause(70);
        }
        internal void WaitForPin(bool expected, string stage)
        {
            var samples = new List<string>(); string latest = "No sample recorded.";
            try
            {
                Wait(() =>
                {
                    var style = GetWindowLongPtr(Main, -20).ToInt64(); var nativePinned = (style & 8) != 0;
                    var captions = Children(Main).Where(h => IsWindowVisible(h) && Class(h).StartsWith("WindowsForms10.BUTTON", StringComparison.OrdinalIgnoreCase))
                        .Select(Title).Where(text => text == "Pin" || text == "Pinned").ToArray();
                    var expectedCaption = expected ? "Pinned" : "Pin";
                    var captionMatches = captions.Length == 1 && captions[0] == expectedCaption && Button(Main, expectedCaption, false) != IntPtr.Zero;
                    latest = "elapsedMs=" + _clock.ElapsedMilliseconds + "; hwnd=0x" + Main.ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                        "; expectedCaption=" + expectedCaption + "; observedCaptions=[" + string.Join(",", captions) + "]; WS_EX_TOPMOST=" + nativePinned +
                        "; GWL_EXSTYLE=0x" + style.ToString("X", CultureInfo.InvariantCulture) + "; windowValid=" + IsWindow(Main) + "; bounds=" + Bounds(Main);
                    if (samples.Count < 128) samples.Add(latest);
                    return captionMatches && nativePinned == expected;
                }, stage + " matching caption and WS_EX_TOPMOST");
                File.WriteAllText(Path.Combine(_output, stage + ".txt"), "Matched caption/native state.\r\n" + string.Join("\r\n", samples), new UTF8Encoding(false));
            }
            catch (TimeoutException)
            {
                var path = Path.Combine(_output, stage + ".txt");
                File.WriteAllText(path, "Caption/native state did not agree before timeout.\r\n" + string.Join("\r\n", samples) + "\r\nLast sample: " + latest, new UTF8Encoding(false));
                throw new TimeoutException(stage + " did not reach matching caption and WS_EX_TOPMOST. " + latest + ". Diagnostic: " + path);
            }
        }
        internal void Click(IntPtr owner, string caption)
        {
            CheckErrors(); var button = Button(owner, caption); SetForegroundWindow(owner);
            Check(PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero), "Native BM_CLICK failed: " + caption); Pause(70);
        }
        internal void Pause(int milliseconds) { var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < milliseconds) { CheckErrors(); Thread.Sleep(15); } }
        internal void Wait(Func<bool> predicate, string stage, int timeout = 4000)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                CheckErrors(); if (predicate()) return;
                if (timer.ElapsedMilliseconds > timeout || _clock.ElapsedMilliseconds > 25000) throw new TimeoutException("Timed out waiting for " + stage + ".");
                Thread.Sleep(20);
            }
        }
        internal void CheckErrors()
        {
            if (Child.HasExited) throw new InvalidOperationException("Spectrum.exe exited during native verification (exit=" + Child.ExitCode + ").");
            foreach (var window in Windows().Where(IsWindowVisible))
            {
                var title = Title(window);
                if (title == "Unexpected error" || title == "Fatal error")
                {
                    ErrorDialogs++;
                    var detail = new StringBuilder("Owned executable error dialog: " + title + "\r\nProcess=" + Child.Id + "\r\n");
                    foreach (var child in new[] { window }.Concat(Children(window)))
                    {
                        var text = FullText(child);
                        if (!string.IsNullOrWhiteSpace(text)) detail.AppendLine("[" + Class(child) + "] " + text);
                    }
                    var stem = "unexpected-error-" + ErrorDialogs;
                    var textPath = Path.Combine(_output, stem + ".txt");
                    File.WriteAllText(textPath, detail.ToString(), new UTF8Encoding(false));
                    try { CaptureCore(window, stem + ".png"); }
                    catch (Exception capture) { File.AppendAllText(textPath, "\r\nError-dialog screenshot failed: " + capture.GetBaseException().Message, new UTF8Encoding(false)); }
                    throw new InvalidOperationException("The owned executable showed " + title + ". " + detail + " Diagnostic: " + textPath);
                }
            }
        }
        internal void Capture(IntPtr window, string filename)
        {
            CheckErrors(); CaptureCore(window, filename);
        }
        private void CaptureCore(IntPtr window, string filename)
        {
            GetWindowThreadProcessId(window, out var pid); Check(pid == Child.Id, "Screenshot target is outside the owned process.");
            var bounds = Bounds(window); Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Width <= 12000 && bounds.Height <= 8000, "Unexpected owned-window screenshot dimensions.");
            var task = Task.Run(() =>
            {
                using var image = new Bitmap(bounds.Width, bounds.Height); using var graphics = Graphics.FromImage(image);
                var dc = graphics.GetHdc(); bool captured;
                try { captured = PrintWindow(window, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                if (!captured) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                ScreenshotMethods.Add(filename + ": " + (captured ? "PrintWindow" : "owned-window-bounds screen capture"));
                image.Save(Path.Combine(_output, filename), ImageFormat.Png);
            });
            if (!task.Wait(2000)) throw new TimeoutException("Owned-window screenshot timed out: " + filename);
            task.GetAwaiter().GetResult();
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Bool(bool value) => value ? "true" : "false";
    private static string RectangleJson(Rectangle? bounds) => bounds.HasValue ? "{\"x\":" + bounds.Value.X + ",\"y\":" + bounds.Value.Y + ",\"width\":" + bounds.Value.Width + ",\"height\":" + bounds.Value.Height + "}" : "null";
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    private static string Hash(string path) { if (!File.Exists(path)) return ""; using var sha = SHA256.Create(); using var file = File.OpenRead(path); return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
    private static string Title(IntPtr window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    private static string FullText(IntPtr window)
    {
        // WM_GETTEXT is a system message; Windows marshals it across this owned process boundary.
        var text = new StringBuilder(65536);
        if (SendMessageText(window, 0x000D, new IntPtr(text.Capacity), text, 2, 750, out _) != IntPtr.Zero)
            return text.ToString();
        GetWindowText(window, text, text.Capacity); return text.ToString();
    }
    private static string Class(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    private static Rectangle Bounds(IntPtr window) { if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("The owned window bounds could not be read."); return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom); }
    private static bool OwnerChain(IntPtr child, IntPtr owner) { for (var depth = 0; depth < 12 && child != IntPtr.Zero; depth++, child = GetWindow(child, 4)) if (child == owner) return true; return false; }
    private static bool IsNearAnchor(Rectangle popup, Rectangle anchor, uint dpi)
    {
        var tolerance = (int)Math.Ceiling(28 * dpi / 96d);
        var horizontal = popup.Right >= anchor.Left - tolerance && popup.Left <= anchor.Right + tolerance;
        var vertical = Math.Abs(popup.Top - anchor.Bottom) <= tolerance || Math.Abs(popup.Bottom - anchor.Top) <= tolerance;
        return horizontal && vertical;
    }
    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8 ? GetWindowLong64(window, index) : new IntPtr(GetWindowLong32(window, index));
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr owner, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter, IntPtr value, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageText(IntPtr window, uint message, IntPtr parameter, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong64(IntPtr window, int index);
}
