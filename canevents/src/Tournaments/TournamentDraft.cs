using System.Collections.Generic;
using canevents.src.EventManagers.events;
using canevents.src.Tournaments;

// Old namespace kept, the client serializes this type.
namespace canevents.src.Network
{
    // Config edit from the admin panel (JSON in TournamentAdminActionMsg) or /tournament set.
    // Null = leave unchanged. All limits live in ApplyTo.
    public class TournamentDraft
    {
        // Upper bounds guard allocations against bad packets.
        public const int MaxTeamSize = 16;
        public const int MinEntrantCount = 2;
        public const int MaxEntrantCount = 256;
        public const int MaxSides = 16;
        public const int MaxAdvance = 16;
        public const int MaxConcurrent = 32;
        public const int MaxSignupWindowMinutes = 7 * 24 * 60;
        public const int MaxRepeatMinutes = 30 * 24 * 60;

        public string Name { get; set; }
        public List<string> EventCodes { get; set; }
        public List<long> PlaceWhitelist { get; set; }
        public int? TeamSize { get; set; }
        public int? MaxEntrants { get; set; }
        public List<RewardItem> EntryFee { get; set; }
        public int? RakePercent { get; set; }
        public List<int> PotSplitPercents { get; set; }
        public List<RewardTier> PlacementPrizes { get; set; }
        public bool? ThirdPlaceMatch { get; set; }
        public bool? LockLoadout { get; set; }
        public List<string> AllowedLoadouts { get; set; }
        public bool? BringOwnGear { get; set; }
        public bool? ManualMatchStart { get; set; }
        public int? PartiesPerMatch { get; set; }
        public int? AdvancePerMatch { get; set; }
        public int? AutoStartEntrants { get; set; }
        public int? AutoStartDelaySeconds { get; set; }
        public int? ReadyCheckSeconds { get; set; }
        public int? MaxConcurrentMatches { get; set; }
        public int? SignupDeadlineMinutes { get; set; }
        public int? SeedMode { get; set; }
        public int? RatedMode { get; set; }
        public bool? PauseScheduler { get; set; }

        // Minutes from now, the client clock isn't trusted.
        public int? OpenInMinutes { get; set; }
        public int? SignupWindowMinutes { get; set; }
        public int? RepeatEveryMinutes { get; set; }
        // "19:00;21:30", "" clears.
        public string RepeatAtPacked { get; set; }
        public bool? ShowBeforeSignup { get; set; }

        // Clamps out-of-range values. Call inside TournamentManager.Configure.
        public (bool ok, string message) ApplyTo(Tournament cfg)
        {
            var d = this;
            if (!string.IsNullOrWhiteSpace(d.Name)) cfg.Name = d.Name.Trim();
            if (d.EventCodes != null && d.EventCodes.Count > 0) cfg.EventCodes = d.EventCodes;
            if (d.PlaceWhitelist != null) cfg.PlaceWhitelist = d.PlaceWhitelist;
            if (d.TeamSize.HasValue) cfg.TeamSize = System.Math.Clamp(d.TeamSize.Value, 1, MaxTeamSize);
            if (d.MaxEntrants.HasValue) cfg.MaxEntrants = System.Math.Clamp(d.MaxEntrants.Value, MinEntrantCount, MaxEntrantCount);
            if (d.EntryFee != null) cfg.EntryFee = d.EntryFee;
            if (d.RakePercent.HasValue) cfg.RakePercent = System.Math.Clamp(d.RakePercent.Value, 0, 100);
            if (d.PotSplitPercents != null && d.PotSplitPercents.Count > 0) cfg.PotSplitPercents = d.PotSplitPercents;
            if (d.PlacementPrizes != null) cfg.PlacementPrizes = d.PlacementPrizes;
            if (d.ThirdPlaceMatch.HasValue) cfg.ThirdPlaceMatch = d.ThirdPlaceMatch.Value;
            if (d.LockLoadout.HasValue) cfg.LockLoadout = d.LockLoadout.Value;
            if (d.AllowedLoadouts != null)
            {
                cfg.AllowedLoadouts = d.AllowedLoadouts;
                // Drop picks that are no longer allowed.
                foreach (var e in cfg.Entrants)
                    foreach (var uid in new List<string>(e.LoadoutChoices.Keys))
                        if (!cfg.AllowsLoadout(e.LoadoutChoices[uid])) e.LoadoutChoices.Remove(uid);
            }
            if (d.BringOwnGear.HasValue) cfg.BringOwnGear = d.BringOwnGear.Value;
            if (d.ManualMatchStart.HasValue) cfg.ManualMatchStart = d.ManualMatchStart.Value;
            if (d.PartiesPerMatch.HasValue)
                cfg.PartiesPerMatch = d.PartiesPerMatch.Value <= 0 ? 0 : System.Math.Clamp(d.PartiesPerMatch.Value, 2, MaxSides);
            if (d.AdvancePerMatch.HasValue) cfg.AdvancePerMatch = System.Math.Clamp(d.AdvancePerMatch.Value, 1, MaxAdvance);
            if (d.AutoStartEntrants.HasValue) cfg.AutoStartEntrants = System.Math.Max(0, d.AutoStartEntrants.Value);
            if (d.AutoStartDelaySeconds.HasValue)
                cfg.AutoStartDelaySeconds = System.Math.Max(0, d.AutoStartDelaySeconds.Value);
            if (d.ReadyCheckSeconds.HasValue) cfg.ReadyCheckSeconds = System.Math.Max(0, d.ReadyCheckSeconds.Value);
            if (d.MaxConcurrentMatches.HasValue)
                cfg.MaxConcurrentMatches = System.Math.Clamp(d.MaxConcurrentMatches.Value, 1, MaxConcurrent);
            if (d.SignupDeadlineMinutes.HasValue)
                cfg.SignupDeadlineUnix = d.SignupDeadlineMinutes.Value <= 0
                    ? 0 : TournamentManager.Now + d.SignupDeadlineMinutes.Value * 60;

            if (d.OpenInMinutes.HasValue)
                cfg.OpenAtUnix = d.OpenInMinutes.Value <= 0
                    ? 0 : TournamentManager.Now + d.OpenInMinutes.Value * 60;
            if (d.SignupWindowMinutes.HasValue)
                cfg.SignupWindowMinutes = System.Math.Clamp(d.SignupWindowMinutes.Value, 0, MaxSignupWindowMinutes);
            if (d.RepeatEveryMinutes.HasValue)
                cfg.RepeatEveryMinutes = System.Math.Clamp(d.RepeatEveryMinutes.Value, 0, MaxRepeatMinutes);
            if (d.ShowBeforeSignup.HasValue) cfg.ShowBeforeSignup = d.ShowBeforeSignup.Value;
            if (d.RepeatAtPacked != null)
            {
                // Refuse the whole schedule if any entry is malformed.
                var times = TournamentManager.ParseTimesOfDay(d.RepeatAtPacked);
                if (times == null) return (false, "Repeat times must be HH:MM, separated by ';'.");
                cfg.RepeatAtMinutesOfDay = times;
            }
            if (d.SeedMode.HasValue) cfg.SeedMode = d.SeedMode.Value;
            if (d.RatedMode.HasValue) cfg.RatedMode = d.RatedMode.Value;
            if (d.PauseScheduler.HasValue) cfg.PauseScheduler = d.PauseScheduler.Value;
            return (true, "Saved.");
        }
    }
}
