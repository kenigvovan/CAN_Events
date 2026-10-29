using System;
using Vintagestory.API.Common;

namespace canevents.src
{
    // Logger with a subsystem prefix like "[bots]".
    public sealed class Log
    {
        private readonly ILogger logger;
        private readonly string prefix;

        private Log(ILogger logger, string tag)
        {
            this.logger = logger;
            prefix = "[" + tag + "] ";
        }

        // Null logger is fine, output is just dropped.
        public static Log For(ILogger logger, string tag) => new Log(logger, tag);

        public void Notification(string message, params object[] args)
            => logger?.Notification(prefix + message, args);

        public void Warning(string message, params object[] args)
            => logger?.Warning(prefix + message, args);

        public void Error(string message, params object[] args)
            => logger?.Error(prefix + message, args);

        public void Debug(string message, params object[] args)
            => logger?.VerboseDebug(prefix + message, args);

        // Logs and swallows, so one failing step can't strand a match.
        public void Safely(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Error("{0} failed: {1}", what, e); }
        }
    }
}
