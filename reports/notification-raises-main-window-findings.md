# eM Client 10: new-mail notification brings the main window to the front

**Status: fix written and verified by decompile, NOT yet confirmed live** (Stage 20 / patch 10, mandatory).
Reproduced before patch 9 existed and again with it deployed, so it is unrelated to that patch.

## Symptom
A new mail arrives, the toast appears, and at the same time the main window is raised/focused, even when the user
isn't touching the PC.

## What the app code does (not the cause)
`FormGenericNotification.Show` shows both toast windows with `ShowWindow(SW_SHOWNOACTIVATE)` and
`SetWindowPos(HWND_TOPMOST, ..., SWP_NOACTIVATE)`. `FormNotificationPresenter` has no activate/focus/BringToFront
calls. Nothing in app code activates the main window.

## Evidence (Bottles, `WINEDEBUG=+focus,+event,+win`, one new mail; 0x40206 = toast form 465x188,
0x40202 = its shadow `LayeredForm` 483x206, 0x600e4 = main window)
1. Toast form gets an **activating** show: `show_window hwnd=0x40206, cmd=5` (SW_SHOW) while its children are created,
   then `NtUserSetWindowPos 0x40206 flags 0x43` (`SWP_NOSIZE|SWP_NOMOVE|SWP_SHOWWINDOW`, no `SWP_NOACTIVATE`) ->
   `set_foreground_window hwnd 0x40206`, `set_active_window 0x40206`.
2. Shortly after: `show_window hwnd=0x40206, cmd=0` (SW_HIDE) + `SetWindowPos ... 0x83` (`SWP_HIDEWINDOW`).
3. Because the hidden window was the active one: `activate_other_window win = 0x600e4 fg = 0x40206` ->
   `set_foreground_window hwnd 0x600e4` (the main window). X11 side: `set_focus setting foreground window to 0x600e4`.
4. Only afterwards does the app's own proper `show_window cmd=4` (SW_SHOWNOACTIVATE) at the final position happen.

So the toast is briefly activated by a WinForms/Wine path (not `FormGenericNotification.Show`), and hiding it makes
Wine hand activation to the main window.

## Not determined
Which managed call makes the activating `SW_SHOW`/`SWP_SHOWWINDOW` (the trace has no managed stack). The fix therefore
removes the *ability* of the toast to be activated instead of finding that call.

## Fix (`--patch-notification-noactivate`, Stage 20 / patch 10)
In `FormGenericNotification`:
- `CreateParams.ExStyle |= 128` -> `|= 128 | WS_EX_NOACTIVATE (0x08000000)`.
- new override `protected override bool ShowWithoutActivation => true;` (WinForms then uses `SW_SHOWNOACTIVATE`).
`LayeredBaseForm.WndProc` already answers `WM_MOUSEACTIVATE` with `MA_NOACTIVATE`, so the toast was never meant to
activate; this makes the other paths agree.

Verified by decompile on the live `eM-Client-10-(Win8)` install directory: the type decompiles with
`ExStyle |= 134217856` and the `ShowWithoutActivation` override; a second run refuses ("already overridden").

## If it still raises the main window
Capture the same `+focus,+event,+win` trace again. If the toast is still activated, the activating call isn't governed
by `WS_EX_NOACTIVATE`/`ShowWithoutActivation` in this Wine build; next step would be a diagnostic patch logging
`Environment.StackTrace` from `FormGenericNotification.OnVisibleChanged`/`SetVisibleCore`. If the toast is no longer
activated but main still rises, the trigger is elsewhere (e.g. the `LayeredForm` shadow window, 0x40202).
