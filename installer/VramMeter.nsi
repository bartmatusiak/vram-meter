; VRAM Meter's setup: VramMeterSetup.exe. Installs for this user only (no admin), like install.ps1 did:
; %LOCALAPPDATA%\Programs\VRAM Meter, the Start menu, optionally the desktop and starting with Windows in the tray,
; and Settings > Apps > Installed apps, where it uninstalls.
;   makensis -DVERSION=1.0.0 -DEXE=..\publish\VramMeter.exe installer\VramMeter.nsi   (installer/build.sh does this)
; Silent: VramMeterSetup.exe /S (all options on), uninstall "Uninstall.exe" /S.
; VRAM Meter updating itself runs VramMeterSetup.exe /S /RELAUNCH=window|tray /D=<its folder>: the same install, which
; then opens the new VRAM Meter as the old one was (see src/Updater.cs).

Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma

!ifndef VERSION
  !error "makensis -DVERSION=x.y.z"
!endif
!ifndef EXE
  !define EXE "..\publish\VramMeter.exe"
!endif

!define NAME "VRAM Meter"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\VramMeter"
!define RUNKEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define SITE "https://bartmatusiak.com/tech/vram-meter/"
!define REPO "https://github.com/bartmatusiak/vram-meter"

Name "${NAME}"
OutFile "VramMeterSetup.exe"
InstallDir "$LOCALAPPDATA\Programs\${NAME}"
InstallDirRegKey HKCU "${REGKEY}" "InstallLocation"
BrandingText "${NAME} ${VERSION} · bartmatusiak.com"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${NAME} Setup"
VIAddVersionKey "CompanyName" "Bart Matusiak"
VIAddVersionKey "LegalCopyright" "© Bart Matusiak"

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON "..\VramMeter.ico"
!define MUI_UNICON "..\VramMeter.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC
!define MUI_FINISHPAGE_RUN "$INSTDIR\VramMeter.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open ${NAME}"
!define MUI_FINISHPAGE_LINK "bartmatusiak.com/tech/vram-meter"
!define MUI_FINISHPAGE_LINK_LOCATION "${SITE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; A running VRAM Meter holds VramMeter.exe open: close it first (only this user's; no admin, so no one else's).
!macro CloseApp
  nsExec::Exec 'taskkill /IM VramMeter.exe /F'
  Pop $0
  ; Then wait (up to 10 s) until Windows lets go of VramMeter.exe, so it can be replaced: a silent update can't
  ; ask, and a file it can't write is skipped, which would leave the old version in place.
  StrCpy $R9 0
  waitloop:
    IfFileExists "$INSTDIR\VramMeter.exe" 0 waitdone
    ClearErrors
    FileOpen $R8 "$INSTDIR\VramMeter.exe" a
    IfErrors 0 waitopen
    IntOp $R9 $R9 + 1
    IntCmp $R9 40 waitdone
    Sleep 250
    Goto waitloop
  waitopen:
    FileClose $R8
  waitdone:
!macroend

Section "${NAME}" SecApp
  SectionIn RO
  !insertmacro CloseApp
  SetOutPath "$INSTDIR"
  File "${EXE}"
  File "..\VramMeter.ico"
  ; Left behind by an install.ps1 install, which this replaces.
  Delete "$INSTDIR\install.ps1"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${NAME}.lnk" "$INSTDIR\VramMeter.exe" "" "$INSTDIR\VramMeter.exe" 0

  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${REGKEY}" "Publisher" "Bart Matusiak"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\VramMeter.exe,0"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "URLInfoAbout" "${SITE}"
  WriteRegStr HKCU "${REGKEY}" "HelpLink" "${REPO}"
  WriteRegStr HKCU "${REGKEY}" "Comments" "An overlay with your game's VRAM, FPS and GPU load on every display, and a window to end apps and choose the GPU each runs on"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${REGKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${REGKEY}" "EstimatedSize" $0
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${NAME}.lnk" "$INSTDIR\VramMeter.exe" "" "$INSTDIR\VramMeter.exe" 0
SectionEnd

; The same entry the tray menu's Start with Windows turns on and off.
Section "Start with Windows (in the tray)" SecStartup
  WriteRegStr HKCU "${RUNKEY}" "${NAME}" '"$INSTDIR\VramMeter.exe" --tray'
SectionEnd

; Reopening VRAM Meter after an update it started (/RELAUNCH=window or tray); a setup run by hand has its Finish page.
Function .onInstSuccess
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/RELAUNCH=" $1
  IfErrors done
  StrCmp $1 "tray" 0 +3
    Exec '"$INSTDIR\VramMeter.exe" --tray'
    Goto done
  Exec '"$INSTDIR\VramMeter.exe"'
  done:
FunctionEnd

Function .onInit
  ; Updating: keep the choice made last time, instead of turning Start with Windows back on.
  ReadRegStr $0 HKCU "${REGKEY}" "InstallLocation"
  StrCmp $0 "" done
  ReadRegStr $1 HKCU "${RUNKEY}" "${NAME}"
  StrCmp $1 "" 0 +2
    SectionSetFlags ${SecStartup} 0
  IfFileExists "$DESKTOP\${NAME}.lnk" +2
    SectionSetFlags ${SecDesktop} 0
  done:
FunctionEnd

Section "Uninstall"
  !insertmacro CloseApp
  ; If Windows was set to start on the power saving GPU's display, put the fast GPU's display back alone.
  IfFileExists "$INSTDIR\VramMeter.exe" 0 +2
    ExecWait '"$INSTDIR\VramMeter.exe" --displays-normal'

  Delete "$SMPROGRAMS\${NAME}.lnk"
  Delete "$DESKTOP\${NAME}.lnk"
  DeleteRegValue HKCU "${RUNKEY}" "${NAME}"
  DeleteRegKey HKCU "${REGKEY}"
  ; Its settings (the GPU choices made stay in Windows' Graphics settings, as Windows' own).
  RMDir /r "$LOCALAPPDATA\${NAME}"

  ; Only its own files, then the folder if that leaves it empty: never a whole folder someone pointed it at.
  Delete "$INSTDIR\VramMeter.exe"
  Delete "$INSTDIR\VramMeter.ico"
  Delete "$INSTDIR\install.ps1"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
