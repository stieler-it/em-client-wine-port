# eM Client 10 frozen after clicking a notification: focus/activation loop in `LayeredForm`

**Status: fix written and verified by decompile, NOT yet confirmed live** (needs a real notification + click on a
patched bottle). Root-cause mechanism is inferred from the stack and the app's own code, not directly observed.

## Symptom
After a new-mail notification popup appears and the user clicks it, the whole app stops responding. It looks like a
deadlock but is not one. Seen several times on `eM-Client-10-(Win8)` (Bottles, soda-11.0-10 runner, patch mask 4095).

## What it is not
- Not a lock/wait: every worker thread (IMAP/SMTP/CalDAV/thread-pool/CEF) is idle in a normal wait. The UI thread is in
  state `R`, ~50-70% CPU, never sleeps.
- Not the hook: `il-patches/uistack-startup-hook` (`DOTNET_STARTUP_HOOKS`) loaded fine and saw its trigger, but the
  `BeginInvoke` pings it posts to the main form never ran, so the UI thread is not servicing posted messages.

## Evidence (live process, gdb + /proc, two separate hangs)
- Dumped the UI thread's whole stack (~128 KB of a 1 MB stack) and resolved return addresses against `/proc/pid/maps`.
  Both hangs: exactly **64 nested cycles** of `ntdll.dll+0x11d70` (user-callback dispatcher) and
  `win32u.dll+0x12608`, depth stable across samples (not a runaway stack overflow).
- `win32u.dll+0x12608` identified from the PE export table: it is the **`NtUserSetFocus`** syscall stub
  (`mov eax,0x1557; call`). Other frames seen in other samples: `NtUserSetActiveWindow` (`+0x123b8`), `NtUserGetProp`
  (`+0x11418`), `NtUserGetMessage` (`+0x11248`, outermost).
- => a SetFocus/SetActiveWindow call whose resulting activation message makes managed code call it again, re-entrantly.

## Suspect (app-original code)
`MailClient.UI.Forms.LayeredForm.WndProc` (the notification's bitmap/shadow window, `FormGenericNotification.layeredWindow`):
```csharp
else if (m.Msg == 6 /*WM_ACTIVATE*/ && ((int)m.WParam & 0xFFFF) != 0)
    Win32.SetActiveWindow(m.LParam != IntPtr.Zero ? m.LParam : IntPtr.Zero); // hand activation back
```
`LayeredBaseForm.WndProc` (the notification form `this`) answers `WM_MOUSEACTIVATE` (33) with `MA_NOACTIVATE`, but
`layeredWindow` has no such guard, so a click on it activates it, which triggers the bounce above. On real Windows this is
a one-shot; under Wine the nested `SetActiveWindow` re-enters activation handling.
**Not confirmed:** which two windows actually bounce (no X11 inspection tools were available), nor that this handler is the
origin rather than a participant.

## Fix (`--patch-layered-activate-guard`, Stage 19 / patch 9, mandatory)
Inserted at the very top of `LayeredForm.WndProc`:
```csharp
if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
if (m.Msg == 6)    { base.WndProc(ref m); return; }    // WM_ACTIVATE: skip the SetActiveWindow bounce
```
Click detection is unaffected: it runs off `WM_SETCURSOR` (HIWORD = mouse message), not activation. `LayeredForm` is found
by type name across `MailClient*.dll`, so the same flag should also apply to eM Client 11 (type lives in
`MailClient.Common.UI.dll` there) -- **not wired into the 11.x deploy script and untested there.**

Verified: decompile of the patched type shows exactly the code above followed by the original body; no exception
handlers in the method; `--dump-il` shows correct long-form branch targets; re-running the flag on its own output refuses
("already contains an m.Msg == 0x21 check"). Applied against a copy of the live bottle's already-patched `MailClient.dll`
(stages 1-18, including `--patch-notification-hover-forward`, which also edits this method's top).

## Gotchas hit while building it
- A `MailClient*.dll` glob can match non-managed files (`BadImageFormatException`): the loop now skips them.
- `module.Write` needs the **whole** install directory as input (it resolves `System.Windows.Forms` for enum constants);
  a directory with only `MailClient*.dll` fails at write time with `AssemblyResolutionException`.

## If it still hangs after this
Re-capture with the method in `il-patches/uistack-startup-hook` (trigger file) and a gdb dump of the UI stack; if the cycle
is still `NtUserSetFocus`, the loop is elsewhere (the main window or the settings dropdown `contextMenuSettings`, whose
`Activate()` call is in `performMouseClick`). Next bisect step would be redeploying with patch 4 (notification chain) off.
