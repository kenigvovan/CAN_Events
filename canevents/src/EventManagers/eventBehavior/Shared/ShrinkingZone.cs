using System.Collections.Generic;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Storm schedule: phases with deadline, target radius and outside dps. Pure maths, no world access.
    public sealed class ShrinkingZone
    {
        private struct Phase
        {
            public float EndsAtSeconds;
            public float Radius;
            public float OutsideDps;
        }

        // Used when a phase omits "outsideDps"; the setup check and panel read it too.
        public const float DefaultOutsideDps = 1f;

        public const string PhasesKey = "zonePhases";
        public const string EndsAtKey = "endsAtSeconds";
        public const string RadiusKey = "radius";
        public const string OutsideDpsKey = "outsideDps";

        private readonly List<Phase> phases = new List<Phase>();

        public float InitialRadius { get; set; } = 50f;

        // Phase radii are relative; unscaled, a small arena made the zone grow in phase one.
        private float RadiusScale
        {
            get
            {
                float maxR = 0f;
                for (int i = 0; i < phases.Count; i++) maxR = System.Math.Max(maxR, phases[i].Radius);
                return ScaleFor(InitialRadius, maxR);
            }
        }

        // Largest phase maps to initialRadius. Pass 100 to get percentages.
        public static float ScaleFor(float initialRadius, float largestPhaseRadius)
            => largestPhaseRadius > 0f ? initialRadius / largestPhaseRadius : 1f;

        public int PhaseCount => phases.Count;
        public bool HasPhases => phases.Count > 0;

        public void ParsePhases(JsonObject attributes)
        {
            phases.Clear();
            var phasesAttr = attributes[PhasesKey];
            if (phasesAttr == null || !phasesAttr.IsArray()) return;

            foreach (var p in phasesAttr.AsArray())
            {
                phases.Add(new Phase
                {
                    EndsAtSeconds = p[EndsAtKey].AsFloat(0f),
                    Radius = p[RadiusKey].AsFloat(0f),
                    OutsideDps = p[OutsideDpsKey].AsFloat(DefaultOutsideDps)
                });
            }
            phases.Sort((a, b) => a.EndsAtSeconds.CompareTo(b.EndsAtSeconds));
        }

        // Linear between phase radii.
        public float RadiusAt(float elapsed)
        {
            if (phases.Count == 0) return InitialRadius;
            if (elapsed <= 0f) return InitialRadius;

            float k = RadiusScale;
            float prevEnd = 0f;
            float prevRadius = InitialRadius; // == k * maxPhaseRadius, so the chain is consistent
            for (int i = 0; i < phases.Count; i++)
            {
                var ph = phases[i];
                float phRadius = ph.Radius * k;
                if (elapsed <= ph.EndsAtSeconds)
                {
                    float span = ph.EndsAtSeconds - prevEnd;
                    if (span <= 0f) return phRadius;
                    float t = (elapsed - prevEnd) / span;
                    return prevRadius + (phRadius - prevRadius) * t;
                }
                prevEnd = ph.EndsAtSeconds;
                prevRadius = phRadius;
            }
            return prevRadius;
        }

        // Current segment, for client-side interpolation. Flat past the schedule.
        public (float radiusNow, float radiusTarget, float secondsToTarget) SegmentAt(float elapsed)
        {
            float now = RadiusAt(elapsed);
            int idx = PhaseIndexAt(elapsed);
            if (idx < 0) return (now, now, 0f);
            return (now, phases[idx].Radius * RadiusScale, phases[idx].EndsAtSeconds - elapsed);
        }

        // Past the schedule the last phase's dps stays.
        public float DpsAt(float elapsed)
        {
            int idx = PhaseIndexAt(elapsed);
            if (idx >= 0) return phases[idx].OutsideDps;
            return phases.Count > 0 ? phases[phases.Count - 1].OutsideDps : 0f;
        }

        // -1 once the schedule has run out.
        public int PhaseIndexAt(float elapsed)
        {
            for (int i = 0; i < phases.Count; i++)
                if (elapsed <= phases[i].EndsAtSeconds) return i;
            return -1;
        }

        // Radius is scaled to this arena.
        public (float radius, float dps) PhaseAt(int index)
            => (phases[index].Radius * RadiusScale, phases[index].OutsideDps);
    }
}
