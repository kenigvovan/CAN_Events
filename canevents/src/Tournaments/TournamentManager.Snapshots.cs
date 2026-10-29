using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Read-side views. Packets are built in NetworkHandler.Tournament.cs.
    public partial class TournamentManager
    {
        // Hidden drafts filtered server-side for non-admins. Returns live objects, don't modify.
        public List<Tournament> ListFor(string viewerUid, bool includeHistory = false)
        {
            var list = store.ActiveSnapshot();
            if (includeHistory) list.AddRange(store.Read(d => new List<Tournament>(d.History)));

            if (!IsAdmin(viewerUid))
                list.RemoveAll(t => t.State == TournamentState.Draft && !t.ShowBeforeSignup);
            return list;
        }

        // Live object, don't modify.
        public Tournament FindForView(long id)
            => store.FindActive(id) ?? store.Read(d => d.History.FirstOrDefault(x => x.Id == id));

        // Offline uids count as non-admin.
        private bool IsAdmin(string viewerUid)
            => sapi.World.PlayerByUid(viewerUid) is IServerPlayer p
               && p.HasPrivilege(Privilege.controlserver);

        public long NewestId()
        {
            var active = store.ActiveSnapshot();
            return active.Count == 0 ? 0 : active.Max(t => t.Id);
        }

        // InvariantCulture so parsing doesn't depend on the server locale.
        private static readonly string[] TimeOfDayFormats = { "H:mm", "HH:mm" };

        // Minutes of day -> "19:00;21:30".
        internal static string PackTimesOfDay(List<int> minutes)
        {
            if (minutes == null || minutes.Count == 0) return "";
            return string.Join(";", minutes.Select(m =>
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m)).ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        // Accepts ";" or ",". Null if any part is invalid.
        internal static List<int> ParseTimesOfDay(string packed)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(packed)) return result;
            foreach (var part in packed.Split(';', ','))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                if (!TimeOnly.TryParseExact(part.Trim(), TimeOfDayFormats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
                result.Add(time.Hour * 60 + time.Minute);
            }
            result.Sort();
            return result;
        }
    }
}
