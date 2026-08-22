; Pawse installer - NSIS script. One of two defines picks which installer to build:
;
;   makensis /DVERSION=<v> pawse.nsi                 -> Pawse-Setup-<v>.exe       (standard)
;   makensis /DVERSION=<v> /DFULL_ONLY pawse.nsi     -> Pawse-Setup-<v>-full.exe  (all-in-one)
;   makensis /DVERSION=<v> /DMINIMAL_ONLY pawse.nsi  -> Pawse-Setup-<v>-min.exe   (true minimal)
;
; build.bat <v>  (or ./build.sh <v>) runs all three in one step. Run it from THIS folder
; after downloading BOTH release exes (Pawse.exe and Pawse-min.exe) into it - the FULL_ONLY
; build needs only Pawse.exe, the MINIMAL_ONLY build only Pawse-min.exe.
;
; The standard installer bundles both builds and asks which to deploy (the chosen exe
; installs as Pawse.exe). The two single-build installers skip that page: FULL_ONLY carries
; the self-contained exe and depends on nothing; MINIMAL_ONLY carries the launcher and
; ensures the .NET 8 Desktop Runtime (via winget, else points to the download page).

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
; Both names are hard-coded in the app too - change them here and in
; src/Pawse/App.xaml.cs (mutex) / src/Pawse/Core/QuitSignal.cs (event) together.
!define MUTEX_NAME "Local\Pawse-single-instance-2b8f9c"
!define QUIT_EVENT "Local\Pawse-quit-2b8f9c"
!define ERROR_ACCESS_DENIED 5

!ifdef MINIMAL_ONLY
  !ifdef FULL_ONLY
    !error "MINIMAL_ONLY and FULL_ONLY are mutually exclusive - build them one at a time."
  !endif
  !define VARIANT " (Minimal)"
  !define OUTSUFFIX "-min"
  ; SINGLE_BUILD = this installer carries one build, so there is nothing to ask.
  !define SINGLE_BUILD
!else
  !ifdef FULL_ONLY
    !define VARIANT " (Full)"
    !define OUTSUFFIX "-full"
    !define SINGLE_BUILD
  !else
    !define VARIANT ""
    !define OUTSUFFIX ""
  !endif
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
; Install for the current user unless the user asks otherwise - without this MultiUser
; preselects per-machine for anyone holding an admin token.
!define MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER
!include "MultiUser.nsh"

; Deliberately AFTER the include, to override the "highest" that MULTIUSER_EXECUTIONLEVEL
; Highest emits. "highest" makes Windows elevate an administrator at launch - a UAC prompt
; just to open the installer, before anyone has chosen anything, for what is by default a
; per-user install that needs no privileges at all. asInvoker means no prompt ever unless
; per-machine is actually picked, and then ElevateForAllUsers asks for exactly that.
; The Highest define stays: MULTIUSER_PAGE_INSTALLMODE refuses to compile without it.
RequestExecutionLevel user

!ifndef SINGLE_BUILD
Var BuildChoice   ; "full" | "min"
Var RbFull
Var RbMin
!endif

Var PawseRunning     ; "1" | "0" - set by ${UN}PawseIsRunning, shared by both halves
Var RealPrivileges   ; account type as Windows reports it, before we fib to MultiUser
Var PrevInstDir      ; a previous install's folder, when its settings need carrying over
!ifndef FULL_ONLY
Var DotnetFound      ; "1" | "0" - set by DotnetPresent
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
; Elevate the moment "anyone who uses this computer" is actually chosen - see
; ElevateForAllUsers. The define is consumed by the page macro below.
!define MULTIUSER_PAGE_CUSTOMFUNCTION_LEAVE ElevateForAllUsers
!insertmacro MULTIUSER_PAGE_INSTALLMODE
!ifndef SINGLE_BUILD
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
!ifndef SINGLE_BUILD
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
  ; A machine-wide Pawse lives under Program Files with its ARP entry in HKLM - neither is
  ; removable without admin. "highest" hands a standard user their own token and no prompt,
  ; so say what's needed up front instead of failing one delete at a time and leaving a
  ; half-removed install behind. (Now reachable by non-admins too, since they can choose a
  ; machine-wide install - see ElevateForAllUsers.)
  ${If} $MultiUser.InstallMode == "AllUsers"
  ${AndIf} $MultiUser.Privileges != "Admin"
  ${AndIf} $MultiUser.Privileges != "Power"
    ; We run asInvoker (see RequestExecutionLevel above), so nobody lands here elevated -
    ; not even an administrator launching this from "Installed apps". Hand off to an
    ; elevated copy of the installed uninstaller. Target $INSTDIR rather than $EXEPATH:
    ; NSIS runs uninstallers from a copy in $TEMP, and that copy is not what we want to
    ; re-launch. The elevated copy has a full admin token, so it never comes back here.
    ; Forward /S: the QuietUninstallString runs this silently, and a handoff that
    ; dropped the flag would turn that "quiet" uninstall into a full interactive
    ; wizard behind the UAC prompt (see EnsurePawseClosed: a silent caller must
    ; never block on a dialog).
    ClearErrors
    ${If} ${Silent}
      ExecShell "runas" "$INSTDIR\uninstall.exe" "/S"
    ${Else}
      ExecShell "runas" "$INSTDIR\uninstall.exe"
    ${EndIf}
    ${IfNot} ${Errors}
      Quit
    ${EndIf}
    MessageBox MB_OK|MB_ICONSTOP|MB_TOPMOST|MB_SETFOREGROUND "Pawse was installed for everyone on this computer, so removing it needs administrator rights.$\n$\nRight-click uninstall.exe in the Pawse folder and choose 'Run as administrator'." /SD IDOK
    Quit
  ${EndIf}
FunctionEnd

Function LaunchApp
  ; launch via Explorer so a per-machine (elevated) install still starts the app
  ; as the normal, non-elevated user (Pawse runs asInvoker).
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

; ---- ensure .NET 8 Desktop Runtime for the minimal build ----
; Skipped entirely in a FULL_ONLY build: nothing there can call these, and makensis -WX
; treats an unreferenced function as an error.
!ifndef FULL_ONLY
; Sets $DotnetFound. Asks the .NET host where it lives rather than assuming: a runtime
; installed anywhere but the default folder used to read as "missing" and trigger a
; download of something already on the machine.
Function DotnetPresent
  Push $0
  Push $1
  Push $2
  StrCpy $DotnetFound "0"

  ; This installer is 32-bit, so a plain HKLM read is redirected into WOW6432Node while
  ; the x64 runtime records itself in the native view.
  SetRegView 64
  ReadRegStr $0 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64" "InstallLocation"
  SetRegView default
  ${If} $0 == ""
    StrCpy $0 "$PROGRAMFILES64\dotnet"   ; nothing recorded - fall back to the usual spot
  ${EndIf}

  FindFirst $1 $2 "$0\shared\Microsoft.WindowsDesktop.App\8.*"
  FindClose $1
  ${If} $2 != ""
    StrCpy $DotnetFound "1"
    DetailPrint ".NET 8 Desktop Runtime found ($2 in $0)."
  ${EndIf}

  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function EnsureDotnet
  Call DotnetPresent
  ${If} $DotnetFound == "1"
    Return
  ${EndIf}
  DetailPrint ".NET 8 Desktop Runtime (x64) not found."

  ; Ask first. This pulls roughly 55 MB down and installs it machine-wide; doing that
  ; unannounced because someone picked the small build is not a decision Setup gets to make.
  ; /SD IDYES so a scripted /S deploy still provisions the runtime without a prompt.
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_TOPMOST|MB_SETFOREGROUND "Pawse (minimal build) needs the .NET 8 Desktop Runtime (x64), which isn't installed on this PC.$\n$\nDownload and install it now? That's about 55 MB, fetched and installed machine-wide by winget.$\n$\nChoose No to handle it yourself - the minimal build won't start until the runtime is present." /SD IDYES IDNO dn_manual

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
    Return
  ${EndIf}
  ; Trust but verify - winget can report success without the runtime we actually need
  ; being on disk afterwards.
  Call DotnetPresent
  ${If} $DotnetFound != "1"
    DetailPrint "winget reported success but no .NET 8 Desktop Runtime is present."
    Call DotnetManual
  ${EndIf}
  Return

 dn_manual:
  Call DotnetManual
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
  ; /SD IDNO: NSIS does NOT suppress message boxes in silent mode, so without a silent
  ; default a /S install on a machine with no winget would block here forever on a dialog
  ; nobody can see.
  MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "Pawse (minimal build) needs the .NET 8 Desktop Runtime (x64), and it isn't installed on this PC.$\n$\nOpen the download page now? Pawse will finish installing either way, but the minimal build won't start until the runtime is there." /SD IDNO IDNO +2
  ExecShell "open" "${DOTNET_URL}"
FunctionEnd
!endif ; FULL_ONLY

; ---- running instance: detect, ask, close cleanly ----
; Pawse is a tray app with no window, so nothing here can send it a WM_CLOSE: plain
; taskkill does nothing, and taskkill /F skips App.OnExit - the only code that reverts the
; Win+L policy value and the Keyboard Filter rules. So we ask the app to quit itself over a
; named event (src/Pawse/Core/QuitSignal.cs) and force it only if the user says to.
;
; Defined as a macro and instantiated twice because NSIS has no other way to share code
; between the installer and the uninstaller (the latter needs "un." on every function).
!macro PAWSE_CLOSE_FUNCS UN ACTION

; Sets $PawseRunning to "1" or "0".
Function ${UN}PawseIsRunning
  Push $0
  Push $1
  StrCpy $PawseRunning "0"

  ; 1) The app's single-instance mutex. Exact, and it still finds an instance whose exe
  ;    was renamed (a portable copy) - no image-name check can do that. Local\ is
  ;    per-session, which is the session whose files we're about to touch.
  ;    "Access denied" means the mutex is there but owned by a token we can't open
  ;    (Pawse running elevated while we aren't) - that is still a running Pawse.
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${MUTEX_NAME}") p .r0 ?e'
  Pop $1
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    StrCpy $PawseRunning "1"
    Goto pir_done
  ${ElseIf} $1 = ${ERROR_ACCESS_DENIED}
    StrCpy $PawseRunning "1"
    Goto pir_done
  ${EndIf}

  ; 2) Image names. Catches an instance in another user's session (visible only when we're
  ;    elevated) and any case where the mutex probe above was refused.
  Push "Pawse.exe"
  Call ${UN}PawseImageRunning
  Pop $1
  ${If} $1 == "1"
    StrCpy $PawseRunning "1"
    Goto pir_done
  ${EndIf}
  Push "Pawse-min.exe"
  Call ${UN}PawseImageRunning
  Pop $1
  ${If} $1 == "1"
    StrCpy $PawseRunning "1"
  ${EndIf}

 pir_done:
  Pop $1
  Pop $0
FunctionEnd

; Pop an image name, push "1" if tasklist reports a process with it.
Function ${UN}PawseImageRunning
  Exch $0     ; image name
  Push $1
  Push $2
  ; /TIMEOUT so a wedged tasklist (it can crawl on a loaded box) can't stall the install.
  nsExec::ExecToStack /TIMEOUT=10000 '"$SYSDIR\tasklist.exe" /NH /FO CSV /FI "IMAGENAME eq $0"'
  Pop $1      ; exit code - 0 even when nothing matched
  Pop $2      ; output
  ; A match is a CSV row: "Pawse.exe","1234",... The no-match line is
  ; "INFO: No tasks are running ...", which is translated on localised Windows but is
  ; never quoted - so test the first character instead of matching English text.
  StrCpy $2 $2 1
  ${If} $1 == 0
  ${AndIf} $2 == '"'
    StrCpy $0 "1"
  ${Else}
    StrCpy $0 "0"
  ${EndIf}
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

; Ask Pawse to quit, then wait up to ~10s for it to actually go. Leaves $PawseRunning set.
Function ${UN}PawseRequestQuit
  Push $0
  Push $1
  ; EVENT_MODIFY_STATE = 0x0002. The app arms this event at startup. Setting an event is
  ; a WRITE, and Windows' integrity policy forbids writing "up" - so an un-elevated
  ; installer cannot signal an elevated Pawse. That case reports access denied rather
  ; than a missing channel, and is worth saying out loud because taskkill will fail too.
  System::Call 'kernel32::OpenEventW(i 0x0002, i 0, w "${QUIT_EVENT}") p .r0 ?e'
  Pop $1
  ${If} $0 != 0
    System::Call 'kernel32::SetEvent(p r0)'
    System::Call 'kernel32::CloseHandle(p r0)'
    DetailPrint "Asked Pawse to close..."
  ${ElseIf} $1 = ${ERROR_ACCESS_DENIED}
    DetailPrint "Pawse is running with higher privileges - it cannot be asked to close."
  ${Else}
    ; No channel - either it just exited, or it's a build from before the channel existed.
    DetailPrint "Pawse offers no quit channel (build older than this installer)."
  ${EndIf}

  StrCpy $1 0
 prq_loop:
  Call ${UN}PawseIsRunning
  ${If} $PawseRunning == "0"
    ; The app releases its mutex in OnExit, a moment before the process actually dies and
    ; its exe stops being mapped. Let that finish, or the File overwrite below can still
    ; lose a race we just declared won.
    Sleep 500
    DetailPrint "Pawse closed."
    Goto prq_done
  ${EndIf}
  ${If} $1 >= 20
    Goto prq_done
  ${EndIf}
  Sleep 500
  IntOp $1 $1 + 1
  Goto prq_loop
 prq_done:
  Pop $1
  Pop $0
FunctionEnd

Function ${UN}PawseForceClose
  Push $0
  ; Pawse-min.exe is by definition a portable copy living somewhere we don't manage - but
  ; it holds the same single-instance mutex, so it IS the Pawse in the way, and leaving it
  ; alive just means the file write below fails instead.
  DetailPrint "Force-closing Pawse..."
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse.exe'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM Pawse-min.exe'
  Pop $0
  Pop $0
FunctionEnd

; Called before anything is written or deleted, so aborting here leaves the machine
; exactly as it was.
Function ${UN}EnsurePawseClosed
  Call ${UN}PawseIsRunning
  ${If} $PawseRunning == "0"
    Return
  ${EndIf}

  ; /SD answers for silent runs (/S, and the QuietUninstallString): try a clean quit, then
  ; force it. A silent caller must never block on a dialog, and a scripted uninstall that
  ; suddenly started failing here would be a regression.
  MessageBox MB_YESNOCANCEL|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "Pawse is running and has to close before ${ACTION} can continue.$\n$\nYes - close Pawse now. It shuts down cleanly and hands back the keyboard.$\nNo - leave it to me; I'll quit it from the tray.$\nCancel - stop and change nothing." /SD IDYES IDYES epc_ask IDNO epc_retry
  Abort "Cancelled - Pawse is still running."

 epc_ask:
  Call ${UN}PawseRequestQuit
  ${If} $PawseRunning == "0"
    Return
  ${EndIf}

 epc_retry:
  Call ${UN}PawseIsRunning
  ${If} $PawseRunning == "0"
    Return
  ${EndIf}
  MessageBox MB_ABORTRETRYIGNORE|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "Pawse is still running.$\n$\nRetry - I've quit it from the tray (right-click the paw, then Quit); check again.$\nIgnore - force it closed. Pawse won't get to undo its Win+L and media-key blocks; it repairs those the next time it starts.$\nAbort - stop and change nothing." /SD IDIGNORE IDRETRY epc_retry IDIGNORE epc_force
  Abort "Cancelled - Pawse is still running."

 epc_force:
  Call ${UN}PawseForceClose
  Call ${UN}PawseIsRunning
  ${If} $PawseRunning == "0"
    Return
  ${EndIf}

  ; taskkill was refused - nearly always because Pawse was restarted as administrator from
  ; its tray menu and we are not elevated. Offer to run just the kill behind a UAC prompt;
  ; there is no elevated way to send the polite quit signal, so this stays a force-close.
  ; /SD IDNO so a silent run never raises a UAC prompt nobody is there to answer.
  MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "Pawse is running as administrator, so it can't be closed from here.$\n$\nClose it using administrator rights? Pawse won't get to undo its Win+L and media-key blocks; it repairs those the next time it starts." /SD IDNO IDNO epc_stuck
  ClearErrors
  ExecShellWait "runas" "$SYSDIR\taskkill.exe" "/F /IM Pawse.exe" SW_HIDE
  ${IfNot} ${Errors}
    ExecShellWait "runas" "$SYSDIR\taskkill.exe" "/F /IM Pawse-min.exe" SW_HIDE
  ${EndIf}
  Call ${UN}PawseIsRunning
  ${If} $PawseRunning == "0"
    Return
  ${EndIf}

 epc_stuck:
  ; Still there: UAC declined, no admin available, or it is in another user's session.
  ; /SD IDCANCEL so a silent run stops rather than bouncing between force and retry.
  MessageBox MB_RETRYCANCEL|MB_ICONSTOP|MB_TOPMOST|MB_SETFOREGROUND "Pawse could not be closed.$\n$\nQuit it from the tray, or re-run this as administrator, then Retry." /SD IDCANCEL IDRETRY epc_retry
  Abort "Pawse could not be closed."
FunctionEnd

!macroend

!insertmacro PAWSE_CLOSE_FUNCS ""    "Setup"
!insertmacro PAWSE_CLOSE_FUNCS "un." "the uninstaller"

; ---- sections ----
Section "-Core" SEC_CORE
  SectionIn RO
  ; Close any running instance so the exe can be overwritten - cleanly if it will, and
  ; only ever by force if the user picks that. Aborts before any file is written.
  Call EnsurePawseClosed

  ; Note where a previous install lives before the registry below is rewritten. Pawse keeps
  ; pawse.json next to its exe (falling back to %APPDATA%\Pawse when that folder isn't
  ; writable), so an install landing in a NEW folder would start from defaults. The
  ; %APPDATA% fallback already covers per-machine installs; this covers the rest - a
  ; portable copy being adopted, or an install that moved between the two modes.
  StrCpy $PrevInstDir ""
  ReadRegStr $0 HKCU "${UNINST_KEY}" "InstallLocation"
  ${If} $0 == ""
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
  ${EndIf}
  ${If} $0 != ""
  ${AndIf} $0 != "$INSTDIR"
  ${AndIf} ${FileExists} "$0\pawse.json"
    StrCpy $PrevInstDir $0
  ${EndIf}

  SetOutPath "$INSTDIR"
  File "pawse.ico"
  File /oname=LICENSE.txt "..\LICENSE"
!ifdef MINIMAL_ONLY
  File /oname=${EXE} "Pawse-min.exe"
!else
  !ifdef FULL_ONLY
  File /oname=${EXE} "Pawse.exe"
  !else
  ${If} $BuildChoice == "min"
    File /oname=${EXE} "Pawse-min.exe"
  ${Else}
    File /oname=${EXE} "Pawse.exe"
  ${EndIf}
  !endif
!endif

  ; Bring the old settings along, but never over a config that's already here.
  ${If} $PrevInstDir != ""
  ${AndIfNot} ${FileExists} "$INSTDIR\pawse.json"
    DetailPrint "Carrying settings over from $PrevInstDir"
    CopyFiles /SILENT "$PrevInstDir\pawse.json" "$INSTDIR"
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
  ; installed size (KB) so Add/Remove Programs shows a Size for Pawse
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD SHCTX "${UNINST_KEY}" "EstimatedSize" $0
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoRepair" 1

; The FULL_ONLY build carries the self-contained exe, so there is no runtime to ensure.
!ifdef MINIMAL_ONLY
  Call EnsureDotnet
!else
  !ifndef FULL_ONLY
  ${If} $BuildChoice == "min"
    Call EnsureDotnet
  ${EndIf}
  !endif
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
!ifndef SINGLE_BUILD
  StrCpy $BuildChoice "full"
!endif
  !insertmacro MULTIUSER_INIT
  ; Stock MultiUser doesn't just disable the per-machine option for non-admins, it skips
  ; the whole page (MultiUser.nsh:444-447) - so a standard user who knows an admin password
  ; could never install machine-wide. Remember what we really are, then claim Admin purely
  ; so the choice renders; ElevateForAllUsers does the real elevating if it's picked.
  ; We run asInvoker (RequestExecutionLevel user, see above), so even an administrator
  ; arrives here UNELEVATED with a filtered token that MultiUser reads as non-admin -
  ; the fib below therefore matters for admins too, not just standard users.
  StrCpy $RealPrivileges $MultiUser.Privileges
  ${If} $RealPrivileges != "Admin"
  ${AndIf} $RealPrivileges != "Power"
    StrCpy $MultiUser.Privileges "Admin"
  ${EndIf}

  ; Upgrade in place. Making per-user the default (v0.3.1) means a machine already carrying
  ; a per-machine Pawse would otherwise get a SECOND copy in %LOCALAPPDATA% - two installs,
  ; two Add/Remove entries, and whichever the Run key last pointed at starting at login. If
  ; there's no per-user install but there is a per-machine one, match it. This must come
  ; after the fib above: MultiUser.InstallMode.AllUsers checks $MultiUser.Privileges.
  ; The mode page still appears, just preselected on all-users, and ElevateForAllUsers asks
  ; for the rights when the user moves past it.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "UninstallString"
  ${If} $0 == ""
    ReadRegStr $0 HKLM "${UNINST_KEY}" "UninstallString"
    ${If} $0 != ""
      Call MultiUser.InstallMode.AllUsers
    ${EndIf}
  ${EndIf}

  ; Silent runs never see the mode page, so nothing would ever elevate them. Fail loudly
  ; rather than half-install into Program Files with a token that can't write there - a
  ; deployment script upgrading a machine-wide install is expected to run elevated.
  ${If} ${Silent}
  ${AndIf} $MultiUser.InstallMode == "AllUsers"
  ${AndIf} $RealPrivileges != "Admin"
  ${AndIf} $RealPrivileges != "Power"
    SetErrorLevel 2
    Quit
  ${EndIf}

  SectionSetFlags ${SEC_DESK} 0     ; Desktop shortcut off by default
FunctionEnd

; Called by MULTIUSER_PAGE_INSTALLMODE's leave handler, after MultiUser has applied the
; choice. If a non-admin picked all-users, hand the install to an elevated copy of
; ourselves rather than marching on toward Program Files with a token that can't write it.
Function ElevateForAllUsers
  ${If} $MultiUser.InstallMode != "AllUsers"
    Return
  ${EndIf}
  ${If} $RealPrivileges == "Admin"
  ${OrIf} $RealPrivileges == "Power"
    Return                        ; already elevated at launch - nothing to do
  ${EndIf}

  ; /AllUsers is honoured by MultiUser's command-line handling, and the elevated instance
  ; really is an admin, so it never comes back through here (no elevation loop).
  ClearErrors
  ExecShell "runas" "$EXEPATH" "/AllUsers"
  ${IfNot} ${Errors}
    Quit                          ; the elevated copy takes over
  ${EndIf}

  ; UAC declined, or no admin account available.
  Call MultiUser.InstallMode.CurrentUser
  MessageBox MB_OK|MB_ICONINFORMATION|MB_TOPMOST|MB_SETFOREGROUND "Administrator rights weren't granted, so Pawse will be installed for you only." /SD IDOK
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_SM}   "Add a Pawse shortcut to the Start Menu."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESK} "Add a Pawse shortcut to the Desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- uninstall ----
Section "Uninstall"
  ; Ask Pawse to close before deleting anything. This matters more here than on install:
  ; a forced kill leaves the Keyboard Filter rules enabled with no Pawse left to sweep
  ; them on next start (the Win+L value below is recoverable from the marker; WEKF is not).
  Call un.EnsurePawseClosed
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
