// Reused from the tested NativeWorkbenchDialogVerification owner-scoped native driver.
// Adds an explicit Cancel mode and Action trigger for header/shortcut entry points.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Spectrum;
namespace Spectrum.Tests
{
internal static class SessionLibraryNativeDialogDriver
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < 100) { Application.DoEvents(); Thread.Sleep(5); } }
    internal static int Completed;
    internal static void Run(Form form, Action trigger, string buttonName, string expectedTitle, string path, bool cancel)
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
                if (cancel)
                {
                    var cancelButtons = Children(dialog).Where(h => Class(h) == "Button" && GetDlgCtrlID(h) == 2 && IsWindowVisible(h) && IsWindowEnabled(h)).ToArray();
                    if (cancelButtons.Length != 1) throw new InvalidOperationException("Owned Cancel button not found. " + Describe(dialog, IntPtr.Zero));
                    SetForegroundWindow(dialog);
                    if (!PostMessage(cancelButtons[0], 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("Owned Cancel click failed.");
                    var cancelClock = Stopwatch.StartNew();
                    while (IsWindow(dialog) && cancelClock.ElapsedMilliseconds < 6000) Thread.Sleep(25);
                    if (IsWindow(dialog)) throw new TimeoutException("Owned dialog did not cancel.");
                    submitted = true; return;
                }
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
            finally { CancelOwned(owner); }
        }) { IsBackground = true, Name = "Spectrum owned-file-dialog acceptance" };
        worker.Start();
        trigger();
        if (!worker.Join(15000)) throw new TimeoutException("The owned file-dialog acceptance worker did not finish.");
        if (error != null) throw new InvalidOperationException("File-dialog workflow failed: " + buttonName, error);
        Check(found && submitted, "Owned dialog must appear and complete: " + buttonName); Completed++; Pump();
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
    internal static void CancelOwned(IntPtr owner)
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
}
