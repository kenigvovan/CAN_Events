using System;
using Vintagestory.API.Common;

namespace canevents.src
{
    /// <summary>
    /// A logger carrying its subsystem prefix ("[bots]", "[arena]"), so the tag is a property of the
    /// component instead of hand-typed into every format string. Deliberately thin.
    /// </summary>
    public sealed class Log
    {
        private readonly ILogger logger;
        private readonly string prefix;

        private Log(ILogger logger, string tag)
        {
            this.logger = logger;
            prefix = "[" + tag + "] ";
        }

        /// <summary>A logger tagged for one subsystem. Null-safe: no logger, no output, no crash.</summary>
        public static Log For(ILogger logger, string tag) => new Log(logger, tag);

        public void Notification(string message, params object[] args)
            => logger?.Notification(prefix + message, args);

        public void Warning(string message, params object[] args)
            => logger?.Warning(prefix + message, args);

        public void Error(string message, params object[] args)
            => logger?.Error(prefix + message, args);

        public void Debug(string message, params object[] args)
            => logger?.VerboseDebug(prefix + message, args);

        /// <summary>Runs something that must not take its caller down, logging what broke: one failing
        /// part must never strand a match.</summary>
        public void Safely(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Error("{0} failed: {1}", what, e); }
        }
    }
}
