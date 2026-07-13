; Pawse installer - NSIS script.
;
; Build locally on Windows (or with makensis) after downloading BOTH release exes
; (Pawse.exe and Pawse-min.exe) into this folder:
;
;     makensis /DVERSION=0.1.1 pawse.nsi        ->  Pawse-Setup-0.1.1.exe
;
; One installer bundles both builds and asks which to deploy. The chosen exe is
; installed as Pawse.exe. For the minimal build it ensures the .NET 8 Desktop
; Runtime (via winget, else points to the download page).

Unicode true

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!define APP "Pawse"
!define PUBLISHER "Pawse"
!define EXE "Pawse.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define DOTNET_URL "https://dotnet.microsoft.com/download/dotnet/8.0"

Name "${APP} ${VERSION}"
OutFile "Pawse-Setup-${VERSION}.exe"
BrandingText "${APP} ${VERSION}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"

; ---- per-user / per-machine ----
!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_INSTALLMODE_COMMANDLINE
!define MULTIUSER_USE_PROGRAMFILES64
!define MULTIUSER_INSTALLMODE_INSTDIR "${APP}"
!define MULTIUSER_INSTALLMODE_INSTALL_REGISTRY_KEY "${APP}"
!define MULTIUSER_INSTALLMODE_INSTALL_REGISTRY_VALUENAME "UninstallString"
!include "MultiUser.nsh"

Var BuildChoice   ; "full" | "min"
Var RbFull
Var RbMin

; ---- UI ----
!define MUI_ICON "pawse.ico"
!define MUI_UNICON "pawse.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch Pawse now"
!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchApp"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MULTIUSER_PAGE_INSTALLMODE
Page custom BuildPageCreate BuildPageLeave
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---- custom "which build" page ----
Function BuildPageCreate
  !insertmacro MUI_HEADER_TEXT "Choose build" "Pick which Pawse build to install."
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 34u "Pawse ships as two builds. The full build bundles the .NET runtime and needs nothing installed. The minimal build is tiny but requires the .NET 8 Desktop Runtime (x64)."
  Pop $0
  ${NSD_CreateRadioButton} 0 40u 100% 12u "Full - runtime bundled (~63 MB). Just works, nothing to install."
  Pop $RbFull
  ${NSD_CreateRadioButton} 0 56u 100% 12u "Minimal - tiny (~0.2 MB). Needs .NET 8 Desktop Runtime (installed via winget if missing)."
  Pop $RbMin
  ${If} $BuildChoice == "min"
    ${NSD_Check} $RbMin
  ${Else}
    ${NSD_Check} $RbFull
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function BuildPageLeave
  ${NSD_GetState} $RbMin $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $BuildChoice "min"
  ${Else}
    StrCpy $BuildChoice "full"
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro MULTIUSER_UNINIT
FunctionEnd

Function LaunchApp
  ; launch via Explorer so a per-machine (elevated) install still starts the app
  ; as the normal, non-elevated user (Pawse runs asInvoker).
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

; ---- ensure .NET 8 Desktop Runtime for the minimal build ----
Function EnsureDotnet
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\8.*"
  FindClose $0
  ${If} $1 != ""
    DetailPrint ".NET 8 Desktop Runtime found ($1)."
    Return
  ${EndIf}
  DetailPrint ".NET 8 Desktop Runtime (x64) not found."
  nsExec::ExecToStack 'where winget'
  Pop $0      ; exit code
  Pop $2      ; output (ignored)
  ${If} $0 == 0
    DetailPrint "Installing .NET 8 Desktop Runtime via winget..."
    nsExec::ExecToLog 'winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --silent --accept-package-agreements --accept-source-agreements'
    Pop $0
    ${If} $0 != 0
      Call DotnetManual
    ${EndIf}
  ${Else}
    Call DotnetManual
  ${EndIf}
FunctionEnd

Function DotnetManual
  MessageBox MB_YESNO|MB_ICONEXCLAMATION "Pawse (minimal build) needs the .NET 8 Desktop Runtime (x64), which isn't installed and couldn't be installed automatically.$\n$\nOpen the download page now?" IDNO +2
  ExecShell "open" "${DOTNET_URL}"
FunctionEnd

; ---- sections ----
Section "-Core" SEC_CORE
  SectionIn RO
  ; stop a running instance so the exe can be overwritten (tray app, no window)
  nsExec::ExecToLog 'taskkill /F /IM Pawse.exe'
  nsExec::ExecToLog 'taskkill /F /IM Pawse-min.exe'

  SetOutPath "$INSTDIR"
  File "pawse.ico"
  ${If} $BuildChoice == "min"
    File /oname=${EXE} "Pawse-min.exe"
  ${Else}
    File /oname=${EXE} "Pawse.exe"
  ${EndIf}

  WriteUninstaller "$INSTDIR\uninstall.exe"
  ; Add/Remove Programs (SHCTX = HKLM for all-users, HKCU for current-user)
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayName"     "${APP}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\pawse.ico"
  WriteRegStr   SHCTX "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   SHCTX "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr   SHCTX "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoRepair" 1

  ${If} $BuildChoice == "min"
    Call EnsureDotnet
  ${EndIf}
SectionEnd

Section "Start Menu shortcut" SEC_SM
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\pawse.ico" 0
SectionEnd

Section "Desktop shortcut" SEC_DESK
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\pawse.ico" 0
SectionEnd

Section "Start Pawse at login" SEC_AUTO
  ; same HKCU Run value the app's own Settings toggle uses
  WriteRegStr HKCU "${RUN_KEY}" "${APP}" '"$INSTDIR\${EXE}"'
SectionEnd

; placed after the sections so ${SEC_DESK} is defined
Function .onInit
  StrCpy $BuildChoice "full"
  !insertmacro MULTIUSER_INIT
  SectionSetFlags ${SEC_DESK} 0     ; Desktop shortcut off by default
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_SM}   "Add a Pawse shortcut to the Start Menu."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESK} "Add a Pawse shortcut to the Desktop."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTO} "Start Pawse automatically when you sign in."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- uninstall ----
Section "Uninstall"
  nsExec::ExecToLog 'taskkill /F /IM Pawse.exe'
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\pawse.ico"
  Delete "$INSTDIR\uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  DeleteRegValue HKCU  "${RUN_KEY}" "${APP}"
  DeleteRegKey   SHCTX "${UNINST_KEY}"
  ; user settings/log in %APPDATA%\Pawse are left in place on purpose
SectionEnd
