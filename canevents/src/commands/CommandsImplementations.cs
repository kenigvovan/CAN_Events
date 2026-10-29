using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    // Handlers only parse args; the work is in the same services the admin panel uses.
    public partial class CommandsImplementations
    {
        ICoreServerAPI sapi;
        PlaceTargeting targeting => sapi.ModLoader.GetModSystem<canevents>()?.PlaceTargeting;
        DefaultEventPlacesManager placeManager;
        CANEventManager canEventManager;
        // Not cached: registration order could capture a null.
        PlaceConfigService placeConfig => canEventManager?.placeConfig;
        AdminActions adminActions => canEventManager?.AdminActions;

        public CommandsImplementations(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            placeManager = this.sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager.eventPlacesManager;
            canEventManager = this.sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
        }

        private static TextCommandResult Wrap((bool ok, string message) r) => CommandArgs.Report(r);

        private static TextCommandResult Wrap(PlaceEditResult r)
        {
            return r.Ok ? TextCommandResult.Success(r.Message) : TextCommandResult.Error(r.Message);
        }

        // Online players first, then anyone the server has seen.
        private bool TryResolvePlayerArg(TextCommandCallingArgs args, int parserIndex,
            out string uid, out string name, out TextCommandResult error)
        {
            name = (string)args.Parsers[parserIndex].GetValue();
            uid = PlayerModDataAccess.UidOfName(sapi, name);
            if (uid == null)
            {
                error = TextCommandResult.Error(Lang.Get("canevents:player-not-found", name));
                return false;
            }
            name = PlayerModDataAccess.NameOf(sapi, uid);
            error = null;
            return true;
        }
    }
}
