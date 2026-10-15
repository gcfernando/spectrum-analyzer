// Developer :: > Gehan Fernando
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spectrum
{

    // Thin wrapper over the Windows ITaskbarList3 COM API for progress on the taskbar button
    public static class Taskbar
    {
        // Values mirror the native TBPFLAG constants, so don't renumber them
        public enum TaskbarStates
        {
            NoProgress = 0,
            Indeterminate = 0x1,
            Normal = 0x2,
            Error = 0x4,
            Paused = 0x8
        }

        // Only declaring the vtable slots we need; the order has to match the native interface exactly
        [ComImportAttribute()]
        [GuidAttribute("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceTypeAttribute(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            [PreserveSig]
            int HrInit();
            [PreserveSig]
            int AddTab(IntPtr hwnd);
            [PreserveSig]
            int DeleteTab(IntPtr hwnd);
            [PreserveSig]
            int ActivateTab(IntPtr hwnd);
            [PreserveSig]
            int SetActiveAlt(IntPtr hwnd);

            [PreserveSig]
            int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

            [PreserveSig]
            int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            [PreserveSig]
            int SetProgressState(IntPtr hwnd, TaskbarStates state);
        }

        // Shell's own coclass for the taskbar; we just cast it to the interface above
        [GuidAttribute("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterfaceAttribute(ClassInterfaceType.None)]
        [ComImportAttribute()]
        private class TaskbarInstance { }

        // Per-thread because the COM object is apartment bound (STA only)
        [ThreadStatic]
        private static ITaskbarList3 taskbarInstance;

        // Once COM fails on this thread we stop trying instead of spamming failures
        [ThreadStatic]
        private static bool taskbarUnavailable;

        // Sets the taskbar progress mode (normal, error, paused...)
        public static void SetState(IntPtr windowHandle, TaskbarStates taskbarState)
        {
            InvokeOptional(windowHandle, taskbar => taskbar.SetProgressState(windowHandle, taskbarState));
        }

        // Bail on junk numbers up front, the native call takes ulongs and would misbehave
        public static void SetValue(IntPtr windowHandle, double progressValue, double progressMax)
        {
            if (double.IsNaN(progressValue) || double.IsInfinity(progressValue) ||
                double.IsNaN(progressMax) || double.IsInfinity(progressMax) ||
                progressValue < 0 || progressMax <= 0 || progressMax >= (double)ulong.MaxValue)
                return;

            InvokeOptional(windowHandle, taskbar =>
                taskbar.SetProgressValue(windowHandle, (ulong)Math.Min(progressValue, progressMax), (ulong)progressMax));
        }

        // Taskbar progress is a nice-to-have, so any failure just turns it off quietly
        private static void InvokeOptional(IntPtr windowHandle, Func<ITaskbarList3, int> operation)
        {
            if (windowHandle == IntPtr.Zero || taskbarUnavailable ||
                Environment.OSVersion.Version < new Version(6, 1) ||
                Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return;

            if (!TryExecuteOptional(() =>
            {
                // Lazy create + init the COM object on first use; negative HRESULT means failure
                if (taskbarInstance == null)
                {
                    var instance = (ITaskbarList3)new TaskbarInstance();
                    var result = instance.HrInit();
                    if (result < 0)
                        return result;
                    taskbarInstance = instance;
                }
                return operation(taskbarInstance);
            }))
            {
                taskbarUnavailable = true;
                taskbarInstance = null;
            }
        }

        // Runs a COM call and treats the expected COM/platform failures as "not available" rather than crashing
        internal static bool TryExecuteOptional(Func<int> operation)
        {
            try
            {
                return operation() >= 0;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}