// Diagnostic DOTNET_STARTUP_HOOKS assembly: captures the managed stack the WinForms UI thread is
// sitting in when it is busy-looping (not blocked). No IL patching, no changes to the app's DLLs.
//
// Mechanism: a background thread posts a tiny callback onto the UI thread's message queue
// (Control.BeginInvoke on the first open form) once a second. If the UI thread is spinning
// inside a message-dispatching loop, the callback runs NESTED on top of that loop, so
// Environment.StackTrace from inside it shows the loop's frames below it. If the UI thread is
// not pumping at all, the ping never runs and that is logged as such.
//
// Captures a stack when (a) the ping took >1s to run, or (b) C:\claude-diag\trigger exists (the
// next 5 pings are captured, then the file is deleted). Log: C:\claude-diag\ui-stack.log
// Enable:  DOTNET_STARTUP_HOOKS=C:\claude-diag\UiStackHook.dll  in the process environment.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal class StartupHook
{
    const string Dir = @"C:\claude-diag";
    static readonly string LogPath = Path.Combine(Dir, "ui-stack.log");
    static readonly string TriggerPath = Path.Combine(Dir, "trigger");

    static int s_pingPending;            // 1 while a posted ping has not run yet
    static long s_postedTicks;
    static int s_captureRemaining;
    static bool s_stallLogged;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Log("hook loaded, pid=" + Environment.ProcessId);
            var t = new Thread(Watch) { IsBackground = true, Name = "UiStackHook" };
            t.Start();
        }
        catch { }
    }

    static void Log(string s)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\n"); } catch { }
    }

    static object FirstForm()
    {
        var swf = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "System.Windows.Forms");
        var app = swf?.GetType("System.Windows.Forms.Application");
        var forms = app?.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IEnumerable;
        if (forms == null) return null;
        foreach (var f in forms)
        {
            var ctrl = f.GetType();
            // IsHandleCreated lives on Control; walk up so non-public derived types still resolve.
            var p = ctrl.GetProperty("IsHandleCreated", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && (bool)p.GetValue(f)) return f;
        }
        return null;
    }

    static void Watch()
    {
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                if (File.Exists(TriggerPath))
                {
                    s_captureRemaining = 5;
                    try { File.Delete(TriggerPath); } catch { }
                    Log("trigger file seen: capturing next 5 pings");
                }

                if (Volatile.Read(ref s_pingPending) == 1)
                {
                    double waited = (DateTime.UtcNow.Ticks - Volatile.Read(ref s_postedTicks)) / 10000000.0;
                    if (waited >= 3 && !s_stallLogged)
                    {
                        s_stallLogged = true;
                        Log("UI thread NOT pumping: ping pending for " + waited.ToString("F1") + "s");
                    }
                    continue;
                }

                var form = FirstForm();
                if (form == null) continue;
                var begin = form.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate) });
                if (begin == null) continue;

                long posted = DateTime.UtcNow.Ticks;
                Volatile.Write(ref s_postedTicks, posted);
                Volatile.Write(ref s_pingPending, 1);
                begin.Invoke(form, new object[] { (Action)(() => OnUiThread(posted)) });
            }
            catch (Exception ex) { Log("watch error: " + ex.GetType().Name + ": " + ex.Message); Volatile.Write(ref s_pingPending, 0); }
        }
    }

    static void OnUiThread(long posted)
    {
        double latency = (DateTime.UtcNow.Ticks - posted) / 10000000.0;
        Volatile.Write(ref s_pingPending, 0);
        if (s_stallLogged) { Log("UI thread pumping again after " + latency.ToString("F1") + "s"); s_stallLogged = false; }

        bool slow = latency > 1.0;
        bool wanted = s_captureRemaining > 0;
        if (!slow && !wanted) return;
        if (wanted) s_captureRemaining--;
        Log((slow ? "SLOW ping, latency " + latency.ToString("F2") + "s" : "ping, latency " + latency.ToString("F2") + "s")
            + " managed thread " + Environment.CurrentManagedThreadId + "\n" + Environment.StackTrace + "\n----");
    }
}
