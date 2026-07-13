; Pawse installer - NSIS script.
;
; Build locally on Windows (or with makensis) after downloading BOTH release exes
; (Pawse.exe and Pawse-min.exe) into this folder. One call builds TWO installers:
;
;     makensis /DVERSION=0.1.1 pawse.nsi
;         ->  Pawse-Setup-0.1.1.exe       (standard: bundles both, asks which)
;         ->  Pawse-Setup-0.1.1-min.exe   (true minimal: only Pawse-min.exe)
;
; The standard installer bundles both builds and asks which to deploy; the chosen
; exe is installed as Pawse.exe. The minimal installer only carries Pawse-min.exe
; (no choice page). Either way, the minimal build ensures the .NET 8 Desktop
; Runtime (via winget, else points to the download page).
;
; The minimal installer is produced by the standard compile shelling out to
; makensis with -DMINIMAL_ONLY (so makensis must be on PATH).

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

!ifdef MINIMAL_ONLY
  !define VARIANT " (Minimal)"
  !define OUTSUFFIX "-min"
!else
  !define VARIANT ""
  !define OUTSUFFIX ""
!endif

Name "${APP} ${VERSION}${VARIANT}"
OutFile "Pawse-Setup-${VERSION}${OUTSUFFIX}.exe"
BrandingText "${APP} ${VERSION}${VARIANT}"

; Standard compile also emits the true-minimal installer in the same run.
!ifndef MINIMAL_ONLY
  !execute 'makensis -DVERSION=${VERSION} -DMINIMAL_ONLY "${__FILE__}"' = 0
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"   ; ${GetSize} for the Add/Remove "Size" field

; ---- per-user / per-machine ----
!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_INSTALLMODE_COMMANDLINE
!define MULTIUSER_USE_PROGRAMFILES64
!define MULTIUSER_INSTALLMODE_INSTDIR "${APP}"
!define MULTIUSER_INSTALLMODE_INSTALL_REGISTRY_KEY "${APP}"
!define MULTIUSER_INSTALLMODE_INSTALL_REGISTRY_VALUENAME "UninstallString"
!include "MultiUser.nsh"

!ifndef MINIMAL_ONLY
Var BuildChoice   ; "full" | "min"
Var RbFull
Var RbMin
!endif

; ---- UI ----
!define MUI_ICON "pawse.ico"
!define MUI_UNICON "pawse.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch Pawse now"
!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchApp"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MULTIUSER_PAGE_INSTALLMODE
!ifndef MINIMAL_ONLY
Page custom BuildPageCreate BuildPageLeave
!endif
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---- custom "which build" page (standard installer only) ----
!ifndef MINIMAL_ONLY
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
!endif

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

  ; Resolve winget's real path via System32's where.exe - both fully qualified so a
  ; planted where.exe / winget.exe in the (possibly elevated) installer's folder can't run.
  nsExec::ExecToStack '"$SYSDIR\where.exe" winget'
  Pop $0      ; exit code
  Pop $2      ; output: absolute path(s), one per line
  ${If} $0 != 0
    Call DotnetManual
    Return
  ${EndIf}
  Push $2
  Call FirstLine
  Pop $3      ; $3 = absolute path to winget.exe
  ${If} $3 == ""
    Call DotnetManual
    Return
  ${EndIf}

  DetailPrint "Installing .NET 8 Desktop Runtime via winget..."
  nsExec::ExecToLog '"$3" install --id Microsoft.DotNet.DesktopRuntime.8 -e --silent --accept-package-agreements --accept-source-agreements'
  Pop $0
  ${If} $0 != 0
    Call DotnetManual
  ${EndIf}
FunctionEnd

; Pop a string, push its first line (up to the first CR/LF). Used to read one path
; out of where.exe's output without trusting PATH search order.
Function FirstLine
  Exch $0
  Push $1
  Push $2
  Push $3
  StrCpy $1 ""
  StrCpy $2 0
  fl_loop:
    StrCpy $3 $0 1 $2
    StrCmp $3 "" fl_done
    StrCmp $3 "$\r" fl_done
    StrCmp $3 "$\n" fl_done
    StrCpy $1 "$1$3"
    IntOp $2 $2 + 1
    Goto fl_loop
  fl_done:
  StrCpy $0 $1
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

Function DotnetManual
  MessageBox MB_YESNO|MB_ICONEXCLAMATION "Pawse (minimal build) needs the .NET 8 Desktop Runtime (x64), which isn't installed and couldn't be installed automatically.$\n$\nOpen the download page now?" IDNO +2
  ExecShell "open" "${DOTNET_URL}"
FunctionEnd

; ---- sections ----
Section "-Core" SEC_CORE
  SectionIn RO
  ; stop a running instance so the exe can be overwritten (tray app, no window)
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse.exe'
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse-min.exe'

  SetOutPath "$INSTDIR"
  File "pawse.ico"
  File /oname=LICENSE.txt "..\LICENSE"
!ifdef MINIMAL_ONLY
  File /oname=${EXE} "Pawse-min.exe"
!else
  ${If} $BuildChoice == "min"
    File /oname=${EXE} "Pawse-min.exe"
  ${Else}
    File /oname=${EXE} "Pawse.exe"
  ${EndIf}
!endif

  WriteUninstaller "$INSTDIR\uninstall.exe"
  ; Add/Remove Programs (SHCTX = HKLM for all-users, HKCU for current-user)
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayName"     "${APP}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\pawse.ico"
  WriteRegStr   SHCTX "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   SHCTX "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr   SHCTX "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  ; installed size (KB) so Add/Remove Programs shows a Size for Pawse
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD SHCTX "${UNINST_KEY}" "EstimatedSize" $0
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoRepair" 1

!ifdef MINIMAL_ONLY
  Call EnsureDotnet
!else
  ${If} $BuildChoice == "min"
    Call EnsureDotnet
  ${EndIf}
!endif
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
!ifndef MINIMAL_ONLY
  StrCpy $BuildChoice "full"
!endif
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
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse.exe'
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\pawse.ico"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  DeleteRegValue HKCU  "${RUN_KEY}" "${APP}"
  DeleteRegKey   SHCTX "${UNINST_KEY}"
  ; user settings/log in %APPDATA%\Pawse are left in place on purpose
SectionEnd
