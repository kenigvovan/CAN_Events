using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    // Outcome of one place edit: what to tell the caller, and whether it happened.
    public readonly struct PlaceEditResult
    {
        public readonly bool Ok;
        public readonly string Message;

        private PlaceEditResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public static PlaceEditResult Success(string message) => new PlaceEditResult(true, message);
        public static PlaceEditResult Fail(string message) => new PlaceEditResult(false, message);
    }
}
