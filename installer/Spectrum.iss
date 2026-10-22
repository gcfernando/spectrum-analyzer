#ifndef SourceDir
  #define SourceDir "..\Spectrum\bin\Release"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer"
#endif
#ifndef BuildId
  #define BuildId "local"
#endif
#define AppVersion GetVersionNumbersString(SourceDir + "\Spectrum.exe")

[Setup]
AppId={{CB9C7107-C434-4C82-9E54-933F19BD7BAC}
AppName=Spectrum
AppVersion={#AppVersion}
AppVerName=Spectrum {#AppVersion} (build {#BuildId})
AppPublisher=Gehan Fernando
AppPublisherURL=https://github.com/gcfernando/spectrum-analyzer
AppSupportURL=https://github.com/gcfernando/spectrum-analyzer/issues
AppUpdatesURL=https://github.com/gcfernando/spectrum-analyzer/releases/latest
DefaultDirName={localappdata}\Programs\Spectrum
DefaultGroupName=Spectrum
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#InstallerOutputDir}
OutputBaseFilename=SpectrumSetup
SetupIconFile=..\Spectrum\Ref_Files\Spectrum.ico
UninstallDisplayIcon={app}\Spectrum.exe
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Spectrum installer (build {#BuildId})
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
InfoBeforeFile={#SourceDir}\THIRD_PARTY_NOTICES.md

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Spectrum"; Filename: "{app}\Spectrum.exe"

[Run]
Filename: "{app}\Spectrum.exe"; Description: "Launch Spectrum"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(
      'Spectrum requires .NET Framework 4.8 or later. Install it from ' +
      'https://dotnet.microsoft.com/download/dotnet-framework/net48 and run this installer again.',
      mbCriticalError, MB_OK, IDOK);
end;
