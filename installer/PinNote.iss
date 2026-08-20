#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
AppId={{D22C7AC1-81AE-47F8-BDDC-F05816BF5634}
AppName=PinNote
AppVersion={#AppVersion}
AppPublisher=Kratosmax
AppPublisherURL=https://github.com/Kratosmax/PinNote
AppSupportURL=https://github.com/Kratosmax/PinNote/issues
DefaultDirName={localappdata}\Programs\PinNote
DefaultGroupName=PinNote
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#SourcePath}\..\temp\assets\pinnote.ico
UninstallDisplayIcon={app}\PinNote.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
VersionInfoVersion={#AppVersion}.0

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PinNote"; Filename: "{app}\PinNote.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\PinNote"; Filename: "{app}\PinNote.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Run]
Filename: "{app}\PinNote.exe"; Description: "启动 PinNote"; Flags: nowait postinstall skipifsilent

#ifdef RequireDesktopRuntime
[Code]
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
  RuntimeRoot: String;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');
  if FindFirst(RuntimeRoot, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime8;
  if not Result then
  begin
    if MsgBox('PinNote Lite 需要 .NET 8 Desktop Runtime (x64)。是否打开微软官方下载页面？',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
#endif
