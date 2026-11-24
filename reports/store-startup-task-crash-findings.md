# Settings -> General crashes on the Windows 10/11 Store-variant install

## Symptom
Opening Settings crashes eM Client 10.4.5674 (Windows 10 / build 19045 bottle, Bottles Flatpak).
`bug.20261102020328.txt`: `COMException 0x80040111 (CLASS_E_CLASSNOTAVAILABLE)`

    StartupTask.GetAsync <- MicrosoftStoreAPI.IsRegisteredRunAtStartup
     <- Integration.IsRegisteredRunAtStartup <- ControlSettingsGeneral.LoadSettings
     <- formSettings.LoadContent <- controlPanelSwitcher_BeforePanelActivated

General is the first Settings page, so this is hit on opening Settings regardless of which page was clicked
(the user reported it as "settings / PGP page"; no PGP-specific problem was found).

## Root cause
On Windows 10/11 the installer ships the Store variant, so `MicrosoftStoreAPI.IsRunningAsStoreApp()` is true and
`Integration.{IsRegistered,Register,Unregister}RunAtStartup` use the WinRT `StartupTask` API, which Wine does not
implement. Their handlers only catch SecurityException / UnauthorizedAccessException.

## Fix (Stage 18 / patch 8, mandatory)
`--patch-store-startup-task-bypass`: in those three methods only, replace `call IsRunningAsStoreApp()` with
`ldc.i4.0` (stack-neutral), so they always use the classic `HKCU\...\Run` registry code that follows. Chosen over
adding catch clauses because these methods use filter-based handlers (fragile to extend) and over patching
`IsRunningAsStoreApp` itself because licensing code uses that too. Verified: each method has exactly one call site
(refuses otherwise), decompile shows no remaining reference, `--dump-handlers` OK for all three.

## Status
Confirmed working by the user on the Bottles bottle (2026-11-02).

## Not done / caveats
- eM Client 11 has a different shape (`IsPackagedApp`, async register/unregister); not patched, no crash observed there.
- Deployed by hand to the Bottles bottle `eM-Client-10` (backup `eM-Client-10-backup-storestartup/`); confirmed
  working by the user (Settings opens without crashing).   Full `deploy.sh` run with Stage 18 not done (only `bash -n`).
