using System;
using Vintagestory.API.Common;

namespace canevents.src.commands
{
    // An omitted optional parser yields null, so every read is defensive rather than a cast that
    // throws on a bare subcommand.
    public static class CommandArgs
    {
        public static object Raw(TextCommandCallingArgs args, int index)
            => args.Parsers != null && args.Parsers.Count > index ? args.Parsers[index].GetValue() : null;

        public static string Str(TextCommandCallingArgs args, int index) => Raw(args, index) as string;

        public static long Id(TextCommandCallingArgs args, int index)
        {
            var raw = Raw(args, index);
            return raw == null ? 0 : Convert.ToInt64(raw);
        }

        public static int Num(TextCommandCallingArgs args, int index)
        {
            var raw = Raw(args, index);
            return raw == null ? 0 : Convert.ToInt32(raw);
        }

        public static TextCommandResult Report((bool ok, string message) result)
            => result.ok ? TextCommandResult.Success(result.message) : TextCommandResult.Error(result.message);
    }
}
