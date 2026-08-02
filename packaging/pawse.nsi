; Pawse installer - NSIS script. The MINIMAL_ONLY define picks which installer to build:
;
;   makensis /DVERSION=<v> pawse.nsi                 -> Pawse-Setup-<v>.exe      (standard)
;   makensis /DVERSION=<v> /DMINIMAL_ONLY pawse.nsi  -> Pawse-Setup-<v>-min.exe  (true minimal)
;
; build.bat <v>  (or ./build.sh <v>) runs both in one step. Run it from THIS folder
; after downloading BOTH release exes (Pawse.exe and Pawse-min.exe) into it.
;
; The standard installer bundles both builds and asks which to deploy (the chosen exe
; installs as Pawse.exe); the minimal installer carries only Pawse-min.exe (no choice
; page). Either way the minimal build ensures the .NET 8 Desktop Runtime (via winget,
; else points to the download page).

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
; Previous-install detection must read the key -Core actually writes (and the
; uninstaller deletes) - a bare "${APP}" here would probe a key nobody creates.
!define MULTIUSER_INSTALLMODE_INSTALL_REGISTRY_KEY "${UNINST_KEY}"
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

; No "start at login" section: under an elevated per-machine install it would write the
; *admin's* HKCU Run key, not the target user's (the same reason LaunchApp shells through
; Explorer). The app itself owns autostart - Settings → "Start Pawse at login" writes the
; same Run value as the signed-in user (Core/Autostart.cs).

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
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- uninstall ----
Section "Uninstall"
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse.exe'
  SetOutPath "$TEMP"   ; move CWD out of $INSTDIR so the folder can be removed

  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\pawse.ico"
  Delete "$INSTDIR\LICENSE.txt"
  ; App-generated files (the app writes config + log next to the exe) - removed so
  ; $INSTDIR can actually be deleted below. The %APPDATA%\Pawse fallback (used only
  ; when $INSTDIR isn't writable, e.g. per-machine installs) is left in place.
  Delete "$INSTDIR\pawse.json"
  Delete "$INSTDIR\pawse.json.bad"
  Delete "$INSTDIR\pawse.json.tmp"
  Delete "$INSTDIR\pawse.log"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"

  ; Restore Win+L if Pawse still holds it (killed or uninstalled while locked -
  ; otherwise the policy value would disable Win+L for this user forever).
  ; Mirrors Core/WorkstationLock.cs: marker 0|1 = pre-Pawse value, 2 = was absent.
  ; Known limitation: an elevated per-machine uninstall reads the *admin's* HKCU,
  ; same as the RUN_KEY cleanup below; per-user installs are fully cleaned.
  ClearErrors
  ReadRegDWORD $0 HKCU "Software\Pawse" "PrevDisableLockWorkstation"
  ${IfNot} ${Errors}
    ${If} $0 == 2
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Policies\System" "DisableLockWorkstation"
    ${Else}
      WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Policies\System" "DisableLockWorkstation" $0
    ${EndIf}
    DeleteRegValue HKCU "Software\Pawse" "PrevDisableLockWorkstation"
  ${EndIf}
  DeleteRegKey /ifempty HKCU "Software\Pawse"

  DeleteRegValue HKCU "${RUN_KEY}" "${APP}"
  ; Remove the Add/Remove Programs entry from whichever hive it was written to - SHCTX can
  ; be wrong if MultiUser's mode detection is off, so clear both (HKLM is a no-op un-elevated).
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKLM "${UNINST_KEY}"

  ; Remove the uninstaller + folder. A running uninstall.exe can't delete itself, so if it's
  ; still there hand off to a detached cmd that waits for us to exit, then cleans up.
  Delete "$INSTDIR\uninstall.exe"
  RMDir  "$INSTDIR"
  IfFileExists "$INSTDIR\uninstall.exe" 0 +2
    Exec '"$SYSDIR\cmd.exe" /c ping 127.0.0.1 -n 3 >nul & del /f /q "$INSTDIR\uninstall.exe" & rmdir "$INSTDIR"'
SectionEnd
