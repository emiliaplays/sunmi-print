Unicode True
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "Sections.nsh"

!define APP_NAME "SUNMI Print"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SunmiPrint"

Name "${APP_NAME}"
OutFile "${SETUP_PATH}"
InstallDir "$LOCALAPPDATA\Programs\SUNMI Print"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1031 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1031 "FileDescription" "SUNMI Print Windows-Installer"
VIAddVersionKey /LANG=1031 "FileVersion" "${APP_VERSION}"

!define MUI_FINISHPAGE_RUN "$INSTDIR\SunmiPrint.exe"
!define MUI_FINISHPAGE_RUN_TEXT "SUNMI Print starten"
!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "German"

Section "SUNMI Print (erforderlich)" AppSection
    SectionIn RO
    SetOutPath "$INSTDIR"
    File "${INPUT_DIR}\SunmiPrint.exe"
    File "${SOURCE_DIR}\THIRD-PARTY-NOTICES.txt"
    File "${SOURCE_DIR}\licenses\Apache-2.0.txt"
    File /oname=Muster-Verkaufsbeleg-AI-Rack.txt "${SOURCE_DIR}\Verkaufsbeleg-Test-AI-Rack.txt"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateShortcut "$SMPROGRAMS\SUNMI Print.lnk" "$INSTDIR\SunmiPrint.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\SunmiPrint.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "Android Platform-Tools (ADB) herunterladen" AdbSection
    DetailPrint "Lade Android Platform-Tools von Google …"
    InitPluginsDir
    File /oname=$PLUGINSDIR\Install-Adb.ps1 "Install-Adb.ps1"
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "$PLUGINSDIR\Install-Adb.ps1"'
    Pop $0
    ${If} $0 != 0
        DetailPrint "ADB konnte nicht installiert werden. Die App wurde installiert."
        IfSilent skipAdbMessage
        MessageBox MB_OK|MB_ICONEXCLAMATION "Die App wurde installiert. ADB konnte nicht geladen werden. Die offiziellen Platform-Tools findest du unter developer.android.com/tools/releases/platform-tools."
        skipAdbMessage:
    ${EndIf}
SectionEnd

Section /o "Desktop-Verknüpfung" DesktopSection
    CreateShortcut "$DESKTOP\SUNMI Print.lnk" "$INSTDIR\SunmiPrint.exe"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${AppSection} "Installiert die Druck-App, den Musterbeleg und einen Startmenü-Eintrag. Die Android-Druckbrücke ist in der App enthalten."
    !insertmacro MUI_DESCRIPTION_TEXT ${AdbSection} "Optionaler Download von Google. Mit der Auswahl akzeptierst du die Android SDK-Lizenz (developer.android.com/tools/releases/platform-tools)."
    !insertmacro MUI_DESCRIPTION_TEXT ${DesktopSection} "Legt eine Verknüpfung auf dem Desktop an."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
    SetShellVarContext current
    ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
    ${If} $0 < 528040
        IfSilent frameworkRequired
        MessageBox MB_OK|MB_ICONSTOP "SUNMI Print benötigt .NET Framework 4.8 oder neuer."
        frameworkRequired:
        SetErrorLevel 1
        Abort
    ${EndIf}
FunctionEnd

Section "Uninstall"
    SetShellVarContext current
    ClearErrors
    Delete "$INSTDIR\SunmiPrint.exe"
    ${If} ${Errors}
        IfSilent uninstallFailed
        MessageBox MB_OK|MB_ICONEXCLAMATION "Schließe SUNMI Print und starte die Deinstallation erneut."
        uninstallFailed:
        SetErrorLevel 1
        Abort
    ${EndIf}
    Delete "$SMPROGRAMS\SUNMI Print.lnk"
    Delete "$DESKTOP\SUNMI Print.lnk"
    Delete "$INSTDIR\Muster-Verkaufsbeleg-AI-Rack.txt"
    Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
    Delete "$INSTDIR\Apache-2.0.txt"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
