using System;
using System.Linq;
using canevents.src.EventPlacesManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    public static class ArenaPackCommands
    {
        public static void Register(ICoreServerAPI sapi, CommandArgumentParsers parsers)
        {
            var service = new ArenaPackService(sapi);

            sapi.ChatCommands.Get("caneventplace")
                .BeginSub("pack")
                    .WithDesc("Ready-made arena packs (config/arenapacks). With Manifold they go to their own dimension.")
                    .BeginSub("list")
                        .HandleWith(_ => List(service))
                        .WithDesc("List the installed arena packs.")
                    .EndSub()
                    .BeginSub("setup")
                        .WithArgs(parsers.Word("pack"), parsers.OptionalAll("templates"))
                        .HandleWith(args => Setup(sapi, service, args))
                        .WithDesc("Build a pack's arenas and create their places; built ones are skipped. Optional: only these template codes.")
                    .EndSub()
                    .BeginSub("tp")
                        .HandleWith(args => Tp(service, args))
                        .WithDesc("Go to the arena dimension (needs Manifold).")
                    .EndSub()
                    .BeginSub("back")
                        .HandleWith(Back)
                        .WithDesc("Leave the arena dimension for the overworld (needs Manifold).")
                    .EndSub()
                .EndSub();
        }

        private static TextCommandResult List(ArenaPackService service)
        {
            var packs = service.LoadPacks();
            if (packs.Count == 0) return TextCommandResult.Success(Lang.Get("canevents:pack-none"));
            var lines = packs.Select(p => string.Format("{0} ({1}): {2}", p.Code, string.Join(", ", p.Templates), p.Description ?? ""));
            string where = Lang.Get(ArenaPackService.HasDimension ? "canevents:pack-target-dim" : "canevents:pack-target-here");
            return TextCommandResult.Success(string.Join("\n", lines) + "\n" + where);
        }

        private static TextCommandResult Setup(ICoreServerAPI sapi, ArenaPackService service, TextCommandCallingArgs args)
        {
            var manager = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            if (manager?.SchematicPlacer == null) return TextCommandResult.Error(Lang.Get("canevents:pack-not-ready"));

            var only = (args[1] as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (ok, message) = service.Setup((IServerPlayer)args.Caller.Player, manager, (string)args[0], only);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        private static TextCommandResult Tp(ArenaPackService service, TextCommandCallingArgs args)
        {
            if (!ArenaPackService.HasDimension) return TextCommandResult.Error(Lang.Get("canevents:pack-no-dim"));
            service.SendToDimension((IServerPlayer)args.Caller.Player);
            return TextCommandResult.Success();
        }

        private static TextCommandResult Back(TextCommandCallingArgs args)
        {
            if (!ArenaPackService.HasDimension) return TextCommandResult.Error(Lang.Get("canevents:pack-no-dim"));
            ArenaPackService.Host.SendBack((IServerPlayer)args.Caller.Player);
            return TextCommandResult.Success();
        }
    }
}
