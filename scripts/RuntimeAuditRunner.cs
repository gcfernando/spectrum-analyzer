using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Compile with the .NET Framework C# compiler /platform:x86. No execution-policy changes are needed.
internal static class RuntimeAuditRunner
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Shown-form helpers must register native controls with the same
            // visual-style startup as Program.Main, before loading the test UI.
            Application.EnableVisualStyles();
            if (args.Length < 4)
                throw new ArgumentException("Usage: RuntimeAuditRunner.exe <test-dll|packaged-exe> <Capture|Desktop|Package|AnalysisUpgrade|WorkbenchDialogs|SessionLibrary|WorkspacePerformance|NativeWorkspace|Navigation> <seconds> <output-directory> [quiet-signal]");
            var assemblyPath = Path.GetFullPath(args[0]);
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
            var output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(output);
            if (string.Equals(args[1], "Package", StringComparison.OrdinalIgnoreCase))
                return VerifyPackage(assemblyPath, int.Parse(args[2]), output);
            Directory.SetCurrentDirectory(assemblyDirectory);
            // The legacy project does not transitively copy these runtime packages into
            // the SDK test output. Resolve the unchanged application copies explicitly.
            var root = new DirectoryInfo(assemblyDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Spectrum.sln"))) root = root.Parent;
            var applicationDirectory = root == null ? assemblyDirectory : Path.Combine(root.FullName, "Spectrum", "bin", "Release");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                var name = new AssemblyName(eventArgs.Name).Name;
                if (name != "System.Resources.Extensions" && name != "System.Memory" && name != "System.Buffers" &&
                    name != "System.Runtime.CompilerServices.Unsafe" && name != "System.Numerics.Vectors") return null;
                var local = Path.Combine(assemblyDirectory, name + ".dll");
                return Assembly.LoadFrom(File.Exists(local) ? local : Path.Combine(applicationDirectory, name + ".dll"));
            };
            var assembly = Assembly.LoadFrom(assemblyPath);
            if (string.Equals(args[1], "AnalysisUpgrade", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "WorkbenchDialogs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "SessionLibrary", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "WorkspacePerformance", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "NativeWorkspace", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "Navigation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[1], "RolesTruth", StringComparison.OrdinalIgnoreCase))
            {
                var dialogs = string.Equals(args[1], "WorkbenchDialogs", StringComparison.OrdinalIgnoreCase);
                var helper = string.Equals(args[1], "RolesTruth", StringComparison.OrdinalIgnoreCase) ? "RolesTruthVerification"
                    : dialogs ? "NativeWorkbenchDialogVerification" : args[1] == "SessionLibrary" ? "SessionLibraryVerification"
                    : args[1] == "WorkspacePerformance" ? "MainWorkspacePerformanceVerification"
                    : args[1] == "NativeWorkspace" ? "MainNativeWindowVerification"
                    : string.Equals(args[1], "Navigation", StringComparison.OrdinalIgnoreCase) ? "AnalysisNavigationVerification" : "AnalysisUpgradeVerification";
                var proof = assembly.GetType("Spectrum.Tests." + helper, true);
                var result = (string)proof.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { output });
                File.WriteAllText(Path.Combine(output, helper + ".json"), result);
                Console.WriteLine("Completed " + helper + ". Evidence: " + output);
                return 0;
            }
            var capture = string.Equals(args[1], "Capture", StringComparison.OrdinalIgnoreCase);
            if (!capture && !string.Equals(args[1], "Desktop", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Kind must be Capture or Desktop.");
            var type = assembly.GetType("Spectrum.Tests." + (capture ? "LiveCaptureVerification" : "DesktopStabilityVerification"), true);
            var reportPath = Path.Combine(output, capture ? "capture.json" : "desktop.json");
            var parameters = capture
                ? new object[] { int.Parse(args[2]), reportPath, args.Length > 4 && args[4] == "quiet-signal" }
                : new object[] { int.Parse(args[2]), output };
            var json = (string)type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, parameters);
            File.WriteAllText(reportPath, json);
            Console.WriteLine("Completed " + args[1] + " audit. Report: " + reportPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetBaseException().ToString());
            return 1;
        }
    }

    private static int VerifyPackage(string executable, int seconds, string output)
    {
        if (seconds < 5 || seconds > 60) throw new ArgumentOutOfRangeException("seconds", "Use 5–60 seconds for package startup smoke.");
        if (Process.GetProcessesByName("Spectrum").Length != 0)
            throw new InvalidOperationException("Another Spectrum process is running; package smoke cannot distinguish a single-instance dialog from startup. Existing processes were left running.");
        using (var process = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        }))
        {
            var watch = Stopwatch.StartNew();
            var window = IntPtr.Zero;
            while (!process.HasExited && watch.Elapsed.TotalSeconds < seconds)
            {
                EnumWindows((handle, ignored) =>
                {
                    uint owner;
                    GetWindowThreadProcessId(handle, out owner);
                    if (owner != process.Id) return true;
                    var title = new StringBuilder(256);
                    GetWindowText(handle, title, title.Capacity);
                    if (title.ToString() == "Audio Spectrum Analyzer") window = handle;
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(100);
            }
            var remainedRunning = !process.HasExited;
            var foundMainWindow = window != IntPtr.Zero;
            if (foundMainWindow && remainedRunning) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            var orderly = process.WaitForExit(5000);
            if (!orderly)
            {
                // Only this runner-created child is terminated if startup/shutdown is broken.
                process.Kill();
                process.WaitForExit();
            }
            var passed = remainedRunning && foundMainWindow && orderly && process.ExitCode == 0;
            var json = "{\"packagedExecutable\":\"Spectrum.exe\",\"mainWindowFound\":" + foundMainWindow.ToString().ToLowerInvariant() +
                ",\"remainedRunning\":" + remainedRunning.ToString().ToLowerInvariant() + ",\"orderlyClose\":" + orderly.ToString().ToLowerInvariant() +
                ",\"exitCode\":" + process.ExitCode + ",\"passed\":" + passed.ToString().ToLowerInvariant() + "}";
            var report = Path.Combine(output, "package-smoke.json");
            File.WriteAllText(report, json);
            Console.WriteLine("Package smoke " + (passed ? "passed" : "FAILED") + ". Report: " + report);
            return passed ? 0 : 1;
        }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
