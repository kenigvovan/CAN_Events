using System;
using Vintagestory.API.Client;

namespace canevents.src
{
    // Breadcrumbs for silent client crashes (GL driver, stack overflow). The game's log writer
    // flushes every line (DisposableWriter.WriteLine), so the last "[trace]" line marks the crash.
    // Enabled via Config.diagnosticsLogging or /cantrace.
    public static class ClientTrace
    {
        private static ICoreClientAPI capi;

        // Unhandled exceptions are logged regardless.
        public static bool Enabled { get; private set; }

        public static void Install(ICoreClientAPI api)
        {
            if (capi != null || api == null) return;
            capi = api;

            // Process dies right after this handler returns, so log from inside it.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }

        public static void Uninstall()
        {
            if (capi == null) return;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            capi = null;
            Enabled = false;
        }

        public static void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            Enabled = on;

            capi?.Logger.Notification("[trace] client diagnostics " + (on ? "ON" : "off"));
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
            => capi?.Logger.Error("[trace] UNHANDLED EXCEPTION{0}: {1}",
                e.IsTerminating ? " (terminating)" : "", e.ExceptionObject);

        public static void Mark(string what)
        {
            if (!Enabled || what == null) return;
            capi?.Logger.Notification("[trace] " + what);
        }

        public static void Mark(Func<string> what)
        {
            if (!Enabled || what == null) return;
            capi?.Logger.Notification("[trace] " + what());
        }

        // A "->" without a matching "<-" means the crash happened inside the step.
        public static void Step(string what, Action action)
        {
            if (!Enabled) { action(); return; }

            Mark("-> " + what);
            try
            {
                action();
                Mark("<- " + what);
            }
            catch (Exception e)
            {
                capi?.Logger.Error("[trace] !! {0} threw: {1}", what, e);
                throw;
            }
        }
    }
}
