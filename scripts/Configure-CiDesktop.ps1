# Provision only a disposable GitHub-hosted machine, never a developer workstation.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script can only provision a disposable GitHub-hosted runner.'
}

$tools = Join-Path $env:RUNNER_TEMP 'spectrum-display'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
$packages = @(
    @{
        Name = 'driver'
        Url = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
        Hash = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
    },
    @{
        Name = 'nefcon'
        Url = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
        Hash = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'
    }
)
foreach ($package in $packages) {
    $archive = Join-Path $tools ($package.Name + '.zip')
    Invoke-WebRequest -Uri $package.Url -OutFile $archive -UseBasicParsing
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) {
        throw "Checksum mismatch for $($package.Name)."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $tools $package.Name) -Force
}

$driverSource = Join-Path $tools 'driver\VirtualDisplayDriver'
$installer = Join-Path $tools 'nefcon\x64\nefconc.exe'
foreach ($file in @((Join-Path $driverSource 'mttvdd.cat'), $installer)) {
    if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') {
        throw "The display dependency has an invalid signature: $file"
    }
}
# The driver's documented configuration location is separate from the application payload.
$driverDirectory = 'C:\VirtualDisplayDriver'
if (Test-Path -LiteralPath $driverDirectory) { throw 'A virtual display is already provisioned on this runner.' }
New-Item -ItemType Directory -Path $driverDirectory | Out-Null
Get-ChildItem -LiteralPath $driverSource -File | Copy-Item -Destination $driverDirectory
$settingsPath = Join-Path $driverDirectory 'vdd_settings.xml'
[xml]$settings = Get-Content -LiteralPath $settingsPath -Raw
$settings.vdd_settings.monitors.count = '1'
$settings.vdd_settings.gpu.friendlyname = 'Microsoft Basic Render Driver'
$settings.Save($settingsPath)

$inf = Join-Path $driverDirectory 'MttVDD.inf'
& $installer install $inf 'Root\MttVDD'
if ($LASTEXITCODE -ne 0) { throw "Virtual display installation failed: $LASTEXITCODE" }

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Explicit, Size = 220)]
public struct CiDisplayMode {
    [FieldOffset(68)] public ushort Size;
    [FieldOffset(72)] public uint Fields;
    [FieldOffset(76)] public int X;
    [FieldOffset(80)] public int Y;
    [FieldOffset(168)] public uint BitsPerPixel;
    [FieldOffset(172)] public uint Width;
    [FieldOffset(176)] public uint Height;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CiDisplayDevice {
    public int Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint State;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
}
public static class CiDesktop {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref CiDisplayDevice display, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int index, ref CiDisplayMode mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref CiDisplayMode mode, IntPtr window, uint flags, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ApplySettings(IntPtr device, IntPtr mode, IntPtr window, uint flags, IntPtr parameter);

    public static bool Configure() {
        var devices = new List<CiDisplayDevice>();
        CiDisplayDevice virtualDevice = new CiDisplayDevice();
        for (uint index = 0; ; index++) {
            var device = new CiDisplayDevice();
            device.Size = Marshal.SizeOf(typeof(CiDisplayDevice));
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            devices.Add(device);
            Console.WriteLine(device.Name + ": " + device.Description + ", flags=" + device.State);
            if (device.Description.IndexOf("Virtual Display", StringComparison.OrdinalIgnoreCase) >= 0)
                virtualDevice = device;
        }
        if (String.IsNullOrEmpty(virtualDevice.Name)) return false;
        // Stage a real 2560x1440 primary monitor. Do not fake screen metrics or alter tests.
        var primary = new CiDisplayMode();
        primary.Size = 220;
        primary.Fields = 0x20 | 0x40000 | 0x80000 | 0x100000;
        primary.Width = 2560; primary.Height = 1440; primary.BitsPerPixel = 32;
        int result = ChangeDisplaySettingsEx(virtualDevice.Name, ref primary, IntPtr.Zero, 0x10000011, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException("Cannot configure virtual primary display: " + result);
        int x = 2560;
        foreach (var device in devices) {
            if (device.Name == virtualDevice.Name || (device.State & 1) == 0) continue;
            var mode = new CiDisplayMode(); mode.Size = 220;
            if (!EnumDisplaySettings(device.Name, -1, ref mode))
                throw new InvalidOperationException("Cannot read display mode for " + device.Name);
            mode.Fields = 0x20; mode.X = x; mode.Y = 0;
            result = ChangeDisplaySettingsEx(device.Name, ref mode, IntPtr.Zero, 0x10000001, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("Cannot position secondary display: " + result);
            x += (int)mode.Width;
        }
        result = ApplySettings(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException("Cannot apply display topology: " + result);
        return true;
    }
}
'@

$configured = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    Start-Sleep -Seconds 2
    if ([CiDesktop]::Configure()) { $configured = $true; break }
}
if (!$configured) { throw 'The signed virtual display did not become available.' }
Start-Sleep -Seconds 2
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
Write-Output "Primary desktop: $($screen.DeviceName); bounds: $($screen.Bounds); working area: $($screen.WorkingArea)"
if ($screen.WorkingArea.Width -lt 1920 -or $screen.WorkingArea.Height -lt 1280) {
    throw 'The provisioned desktop cannot contain the native UI test windows.'
}
