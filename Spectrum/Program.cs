// Developer :: > Gehan Fernando
using System;
using System.Threading;
using System.Windows.Forms;

namespace Spectrum
{
    // App entry point, keeps things to a single running instance
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            // Named mutex is how we spot a second copy already running
            bool createdNew;
            using var mutex = new Mutex(true, "Spectrum_SingleInstance_Mutex", out createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Application is already running.",
                    "Spectrum",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                RunApplication();
            }
            finally
            {
                // Always release, even if the app blows up
                mutex.ReleaseMutex();
            }
        }

        // Wires up global error dialogs, then starts the main window
        private static void RunApplication()
        {
            Application.SetCompatibleTextRenderingDefault(false);

            // Route UI-thread exceptions to our handler instead of the default crash dialog
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (s, e) =>
            {
                MessageBox.Show(
                    e.Exception.ToString(),
                    "Unexpected error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            // Last-resort catch for exceptions from background threads
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                MessageBox.Show(
                    e.ExceptionObject?.ToString() ?? "Unhandled exception",
                    "Fatal error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            Application.Run(new FormAudioSpectrum());
        }
    }
}
