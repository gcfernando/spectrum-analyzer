# Inspect metadata in the same 32-bit .NET Framework runtime used by the application.
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this verification with 32-bit Windows PowerShell (.NET Framework).'
}

$resolve = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name)
    $directory = Split-Path -Parent $eventArgs.RequestingAssembly.Location
    $dependency = Join-Path $directory ($name.Name + '.dll')
    if (Test-Path -LiteralPath $dependency) {
        return [Reflection.Assembly]::ReflectionOnlyLoadFrom($dependency)
    }
    return [Reflection.Assembly]::ReflectionOnlyLoad($eventArgs.Name)
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolve)
try {
    foreach ($path in @('Spectrum\bin\Release\Spectrum.exe',
                        'Spectrum.Tests\bin\Release\net48\Spectrum.Tests.dll')) {
        $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Resolve-Path -LiteralPath $path).Path)
        if ($assembly.GetName().ProcessorArchitecture -ne [Reflection.ProcessorArchitecture]::X86) {
            throw "The assembly must require x86: $path"
        }
        $framework = @($assembly.GetCustomAttributesData() | Where-Object {
            $_.AttributeType.FullName -eq 'System.Runtime.Versioning.TargetFrameworkAttribute'
        })
        if ($framework.Count -ne 1 -or
            $framework[0].ConstructorArguments[0].Value -ne '.NETFramework,Version=v4.8') {
            throw "The assembly must target .NET Framework 4.8: $path"
        }
        Write-Output "Verified .NET Framework 4.8, x86 required: $path"
    }
}
finally {
    [AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolve)
}

[xml]$configuration = Get-Content -LiteralPath 'Spectrum\bin\Release\Spectrum.exe.config' -Raw
if ($configuration.configuration.startup.supportedRuntime.sku -ne '.NETFramework,Version=v4.8') {
    throw 'The application runtime configuration must declare .NET Framework 4.8.'
}
Write-Output 'Verified application runtime configuration: .NET Framework 4.8.'
