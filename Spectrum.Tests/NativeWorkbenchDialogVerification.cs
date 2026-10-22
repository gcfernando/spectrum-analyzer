using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace Spectrum.Tests;

// Explicit acceptance helper, outside automatic discovery. It touches only dialogs owned by
// this runner's shown workbench and writes unique files inside the requested evidence folder.
public static class NativeWorkbenchDialogVerification
{
    public static string Run(string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Run the shown dialog acceptance helper on an STA thread.");
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        var prefix = "dialog-" + Guid.NewGuid().ToString("N");
        var csv = Path.Combine(output, prefix + ".csv");
        var session = Path.Combine(output, prefix + ".spectrum-session.xml");
        var custom = Path.Combine(output, prefix + ".spectrum-workspace.xml");
        var wave = Path.Combine(output, prefix + ".wav"); AnalysisUpgradeVerification.WriteWave(wave);
        var workspace = ModernWorkbenchProbe.Fixture();
        using var analyzer = new Analyzer(false);
        using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
        form.Show(); ModernWorkbenchProbe.Pump();
        try
        {
            Submit(form, "analysisOpenAudio", "Analyze an audio file", wave);
            ModernWorkbenchProbe.WaitForFile(form);
            Assert.True(workspace.IsOffline && !workspace.IsSavedSession);
            Assert.Equal(288000, workspace.Measurements.TotalFrames);
            Assert.True(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
            Assert.Equal("OFFLINE ANALYSIS", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
            ModernWorkbenchProbe.Reveal(form, "analysisSessionGroup", "analysisSessionPanel");
            Submit(form, "analysisExportCsv", "Export numerical measurements", csv);
            Assert.True(File.Exists(csv)); Assert.Equal(84, File.ReadAllLines(csv).Length);
            Assert.Contains("integrated_lufs", File.ReadLines(csv).First());
            var power = (double[])workspace.Latest.Power.Clone();
            Submit(form, "analysisSaveSession", "Save measurement session · audio is not recorded", session);
            Assert.True(File.Exists(session));
            workspace.Reset(); ModernWorkbenchProbe.Refresh(form); Assert.Null(workspace.Latest);
            Submit(form, "analysisOpenSession", "Open measurement session", session);
            Assert.Equal(power, workspace.Latest.Power); Assert.True(workspace.IsSavedSession);
            Assert.Equal("SAVED SESSION", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
            ModernWorkbenchProbe.Find<TextBox>(form, "analysisWorkspaceName").Text = "Native dialog acceptance";
            ModernWorkbenchProbe.Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-120";
            Submit(form, "analysisSaveWorkspace", "Save or export a custom workspace", custom);
            Assert.True(File.Exists(custom));
            ModernWorkbenchProbe.Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-60";
            workspace.WorkspaceName = "Temporary changed name";
            Submit(form, "analysisLoadWorkspace", "Load or import a custom workspace", custom);
            Assert.Equal(-120, workspace.Settings.FloorDb); Assert.Equal("Native dialog acceptance", workspace.WorkspaceName);
            Assert.Contains(workspace.WorkspaceName, ModernWorkbenchProbe.Find<ComboBox>(form, "analysisWorkspaceSelector").Text);
            Assert.False(ModernWorkbenchProbe.Find<Label>(form, "analysisError").Visible);
            ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "native-file-dialog-workflows.png"));
            var json = "{\"helper\":\"NativeWorkbenchDialogVerification\",\"workflowsPassed\":6,\"nativeDialogsSubmitted\":6," +
                "\"scope\":\"Exact runner process, workbench owner chain and expected dialog title\",\"csv\":" + Quote(csv) +
                ",\"audio\":" + Quote(wave) + ",\"session\":" + Quote(session) + ",\"workspace\":" + Quote(custom) + "}";
            File.WriteAllText(Path.Combine(output, "native-file-dialog-workflows.json"), json, new UTF8Encoding(false));
            return json;
        }
        finally { CancelOwnedDialogs(form.Handle); form.Close(); ModernWorkbenchProbe.Pump(); }
    }

    private static void Submit(AnalysisWorkbenchForm form, string buttonName, string expectedTitle, string path)
    {
        var owner = form.Handle; Exception error = null; var found = false; var submitted = false;
        var worker = new Thread(() =>
        {
            try
            {
                var timer = Stopwatch.StartNew(); IntPtr dialog = IntPtr.Zero;
                while (dialog == IntPtr.Zero && timer.ElapsedMilliseconds < 10000)
                {
                    var matches = OwnedDialogs(owner).Where(h => Title(h) == expectedTitle && IsWindowVisible(h)).ToArray();
                    if (matches.Length > 1) throw new InvalidOperationException("More than one runner-owned dialog has the expected title.");
                    if (matches.Length == 1) dialog = matches[0]; else Thread.Sleep(25);
                }
                if (dialog == IntPtr.Zero) throw new TimeoutException("The expected runner-owned file dialog did not appear: " + expectedTitle);
                found = true;
                IntPtr fileName = IntPtr.Zero;
                while (fileName == IntPtr.Zero && timer.ElapsedMilliseconds < 12000)
                {
                    // Both classic and Vista common dialogs expose the filename ComboBoxEx at 1148.
                    var candidates = Children(dialog).Where(h => GetDlgCtrlID(h) == 1148).ToArray();
                    foreach (var candidate in candidates)
                    {
                        fileName = Class(candidate) == "Edit" ? candidate : Children(candidate).FirstOrDefault(h => Class(h) == "Edit");
                        if (fileName != IntPtr.Zero) break;
                    }
                    if (fileName == IntPtr.Zero)
                    {
                        // Vista Save dialogs use an unnumbered filename ComboBox with Edit 1001.
                        // The address/search editors have different native parent classes.
                        var vista = Children(dialog).Where(h => Class(h) == "Edit" && GetDlgCtrlID(h) == 1001 &&
                            Class(GetParent(h)) == "ComboBox" && IsWindowVisible(h) && IsWindowEnabled(h)).ToArray();
                        if (vista.Length == 1) fileName = vista[0];
                    }
                    if (fileName == IntPtr.Zero) Thread.Sleep(25);
                }
                if (fileName == IntPtr.Zero) throw new InvalidOperationException("The owned common dialog did not expose its filename edit control. " + Describe(dialog, fileName));
                if (SendMessageTimeoutValue(fileName, 0x00B1, IntPtr.Zero, new IntPtr(-1), 2, 1000, out _) == IntPtr.Zero ||
                    SendMessageTimeout(fileName, 0x00C2, new IntPtr(1), path, 2, 1000, out _) == IntPtr.Zero)
                    throw new TimeoutException("Setting the owned dialog filename timed out.");
                var combo = GetParent(fileName);
                if (Class(combo) == "ComboBox")
                {
                    // Notify the DirectUI host of a committed filename edit (CBN_EDITCHANGE).
                    SendMessageTimeoutValue(combo, 0x0111, new IntPtr(GetDlgCtrlID(fileName) | (0x0300 << 16)), fileName, 2, 1000, out _);
                    SendMessageTimeoutValue(GetParent(combo), 0x0111, new IntPtr(GetDlgCtrlID(combo) | (5 << 16)), combo, 2, 1000, out _);
                }
                if (!string.Equals(Title(fileName), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The owned dialog filename did not retain the requested unique path. " + Describe(dialog, fileName));
                IntPtr[] accept;
                do
                {
                    accept = Children(dialog).Where(h => Class(h) == "Button" && GetDlgCtrlID(h) == 1 && IsWindowVisible(h) && IsWindowEnabled(h)).ToArray();
                    if (accept.Length == 0) Thread.Sleep(25);
                } while (accept.Length == 0 && timer.ElapsedMilliseconds < 14000);
                if (accept.Length != 1) throw new InvalidOperationException("The owned dialog did not expose one enabled acceptance button. " + Describe(dialog, fileName));
                // Vista IFileDialog routes acceptance through its actual button notification.
                // A synthetic top-level WM_COMMAND alone can be ignored by its implementation.
                SetForegroundWindow(dialog);
                if (!PostMessage(accept[0], 0x00F5, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException("The owned dialog acceptance button could not be clicked. " + Describe(dialog, fileName));
                submitted = true; var closing = Stopwatch.StartNew();
                while (IsWindow(dialog) && closing.ElapsedMilliseconds < 6000) Thread.Sleep(25);
                if (IsWindow(dialog)) throw new TimeoutException("The owned file dialog did not complete after clicking its acceptance button. " + Describe(dialog, fileName));
            }
            catch (Exception exception) { error = new InvalidOperationException("Workflow=" + buttonName + "; " + exception.Message); }
            finally { CancelOwnedDialogs(owner); }
        }) { IsBackground = true, Name = "Spectrum owned-file-dialog acceptance" };
        worker.Start();
        ModernWorkbenchProbe.Find<Button>(form, buttonName).PerformClick();
        if (!worker.Join(15000)) throw new TimeoutException("The owned file-dialog acceptance worker did not finish.");
        if (error != null) throw new InvalidOperationException("File-dialog workflow failed: " + buttonName, error);
        Assert.True(found && submitted); ModernWorkbenchProbe.Pump();
        Assert.False(ModernWorkbenchProbe.Find<Label>(form, "analysisError").Visible,
            ModernWorkbenchProbe.Find<Label>(form, "analysisError").Text);
    }
    private static IEnumerable<IntPtr> OwnedDialogs(IntPtr owner)
    {
        var result = new List<IntPtr>(); var process = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == process && Class(window) == "#32770")
            {
                var ancestor = GetWindow(window, 4);
                for (var depth = 0; ancestor != IntPtr.Zero && depth < 12; depth++, ancestor = GetWindow(ancestor, 4))
                    if (ancestor == owner) { result.Add(window); break; }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private static IEnumerable<IntPtr> Children(IntPtr parent)
    { var children = new List<IntPtr>(); EnumChildWindows(parent, (h, _) => { children.Add(h); return true; }, IntPtr.Zero); return children; }
    private static void CancelOwnedDialogs(IntPtr owner)
    { foreach (var dialog in OwnedDialogs(owner)) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    private static string Describe(IntPtr dialog, IntPtr edit)
        => "Dialog='" + Title(dialog) + "'; filename='" + Title(edit) + "'; buttons=[" +
            string.Join("; ", Children(dialog).Where(h => Class(h) == "Button").Select(h =>
                "id=" + GetDlgCtrlID(h) + ", class=" + Class(h) + ", text='" + Title(h) + "', visible=" + IsWindowVisible(h) + ", enabled=" + IsWindowEnabled(h))) +
            "]; edit/combo controls=[" + string.Join("; ", Children(dialog).Where(h => Class(h) == "Edit" || Class(h).StartsWith("ComboBox", StringComparison.Ordinal)).Select(h =>
                "id=" + GetDlgCtrlID(h) + ", class=" + Class(h) + ", parent=" + Class(GetParent(h)) + "/" + GetDlgCtrlID(GetParent(h)) + ", visible=" + IsWindowVisible(h))) + "]";
    private static string Class(IntPtr handle) { var text = new StringBuilder(256); GetClassName(handle, text, text.Capacity); return text.ToString(); }
    private static string Title(IntPtr handle) { var text = new StringBuilder(512); GetWindowText(handle, text, text.Capacity); return text.ToString(); }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter, string text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern IntPtr SendMessageTimeoutValue(IntPtr window, uint message, IntPtr parameter, IntPtr value, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
