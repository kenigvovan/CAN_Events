using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.Network;
using Newtonsoft.Json.Linq;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    // Radius is a % of the arena's zone radius.
    public sealed class HgPhaseRow
    {
        public float End;
        public float Radius;
        public float Dps;
    }

    public sealed class HgLootRow
    {
        public string Code = "";
        public int Min = 1;
        public int Max = 1;
        public float Weight = 1f;
    }

    public sealed class HgTierEdit
    {
        public string Name = "";
        public readonly List<HgLootRow> Rows = new List<HgLootRow>();
    }

    // HG section state kept across rebuilds, plus config parse/serialize.
    public sealed class HgSettingsBuffer
    {
        // Shared by the zone card and the layout generator.
        public float Radius = 40f;

        // Used only when the server sent no tier list.
        public string LootTier = "";
        public int LootTierIdx;

        // Last seen server radius per place, to notice server-side changes.
        public long ZoneRadiusFor = -1;
        public float ZoneRadiusSeen = -1f;

        // Typed but unsent radius; stops a late setup packet from overwriting it before Generate.
        public long RadiusTypedFor = -1;

        // -1 asks on the next build.
        public long SetupReqPlace = -1;
        public int SetupReqFingerprint;

        // Off for schematic floors.
        public bool SnapTerrain = true;
        // 0 until seeded; the server then uses the tribute cap.
        public int GenSpawns;
        public long GenSpawnsFor = -1;

        public readonly List<HgPhaseRow> EditPhases = new List<HgPhaseRow>();
        public readonly List<HgTierEdit> EditTiers = new List<HgTierEdit>();

        // Keyed by event code, not packet: the server re-pushes setup after every loot-spot edit.
        public string EditLoadedFor;

        public string CfgEventCode;
        public bool CfgApplyToAll;
        // Packet the variant re-request was sent for: one re-ask per server push.
        public object CfgReqAfterMsg;

        public int PhaseSel = -1;
        public double PhaseEndInput = 60, PhaseRadiusInput = 10, PhaseDpsInput = 1;

        public int TierSel;
        public string NewTierName = "";
        public string ItemCodeInput = "game:";
        public int ItemMinInput = 1, ItemMaxInput = 1;
        public double ItemWeightInput = 1;

        private string loadedSnapshot;

        public bool IsDirty => EditLoadedFor != null && loadedSnapshot != null && Snapshot() != loadedSnapshot;

        public string ScoreboardVisibility = BehaviorConfigKeys.ScoreboardVisibilities[0];

        private string Snapshot() => PhasesToJson() + "|" + LootToJson() + "|" + ScoreboardVisibility;

        public void MarkSaved() => loadedSnapshot = Snapshot();

        // Buffers stay, so unsaved edits survive switching tabs.
        public void Invalidate()
        {
            SetupReqPlace = -1;
            CfgReqAfterMsg = null;
        }

        public void Load(HgSetupMsg s)
        {
            EditPhases.Clear();
            EditTiers.Clear();
            try
            {
                if (!string.IsNullOrWhiteSpace(s.ZonePhasesJson) && JToken.Parse(s.ZonePhasesJson) is JArray pa)
                {
                    foreach (var p in pa)
                    {
                        if (!(p is JObject po)) continue;
                        EditPhases.Add(new HgPhaseRow
                        {
                            End = po[ShrinkingZone.EndsAtKey]?.Value<float>() ?? 0f,
                            Radius = po[ShrinkingZone.RadiusKey]?.Value<float>() ?? 0f,
                            Dps = po[ShrinkingZone.OutsideDpsKey]?.Value<float>() ?? ShrinkingZone.DefaultOutsideDps
                        });
                    }
                }
                // ShrinkingZone scales the largest phase to the arena radius, so edit as % of arena.
                float toPct = ShrinkingZone.ScaleFor(100f, EditPhases.Count > 0 ? EditPhases.Max(r => r.Radius) : 0f);
                foreach (var r in EditPhases)
                    r.Radius = (float)Math.Round(r.Radius * toPct, 1);
            }
            catch { /* leave phases empty on malformed json */ }

            try
            {
                if (!string.IsNullOrWhiteSpace(s.LootTablesJson) && JToken.Parse(s.LootTablesJson) is JObject lo)
                {
                    foreach (var prop in lo.Properties())
                    {
                        var te = new HgTierEdit { Name = prop.Name };
                        if (prop.Value is JArray ea)
                        {
                            foreach (var it in ea)
                            {
                                if (!(it is JObject eo)) continue;
                                te.Rows.Add(new HgLootRow
                                {
                                    Code = eo["code"]?.Value<string>() ?? "",
                                    Min = eo["minQty"]?.Value<int>() ?? 1,
                                    Max = eo["maxQty"]?.Value<int>() ?? 1,
                                    Weight = eo["weight"]?.Value<float>() ?? 1f
                                });
                            }
                        }
                        EditTiers.Add(te);
                    }
                }
            }
            catch { /* leave tiers empty on malformed json */ }

            ScoreboardVisibility = string.IsNullOrEmpty(s.ScoreboardVisibility)
                ? BehaviorConfigKeys.ScoreboardVisibilities[0] : s.ScoreboardVisibility;

            EditLoadedFor = s.EventCode;
            PhaseSel = -1;
            if (TierSel >= EditTiers.Count) TierSel = 0;
            loadedSnapshot = Snapshot();
        }

        public string PhasesToJson()
        {
            var arr = new JArray();
            foreach (var p in EditPhases)
                arr.Add(new JObject
                {
                    [ShrinkingZone.EndsAtKey] = p.End,
                    [ShrinkingZone.RadiusKey] = p.Radius,
                    [ShrinkingZone.OutsideDpsKey] = p.Dps
                });
            return arr.ToString();
        }

        public string LootToJson()
        {
            var obj = new JObject();
            foreach (var te in EditTiers)
            {
                if (string.IsNullOrWhiteSpace(te.Name)) continue;
                var arr = new JArray();
                foreach (var r in te.Rows)
                {
                    if (string.IsNullOrWhiteSpace(r.Code)) continue;
                    arr.Add(new JObject
                    {
                        ["code"] = r.Code.Trim(),
                        ["minQty"] = r.Min,
                        ["maxQty"] = r.Max,
                        ["weight"] = r.Weight
                    });
                }
                obj[te.Name] = arr;
            }
            return obj.ToString();
        }
    }
}
