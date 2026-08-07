using System.Collections.Generic;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// The storm schedule of a shrinking-zone mode: phases with a deadline, target radius and damage
    /// per second outside. Pure arithmetic over elapsed time - no world access, no messaging.
    /// </summary>
    public sealed class ShrinkingZone
    {
        private struct Phase
        {
            public float EndsAtSeconds;
            public float Radius;
            public float OutsideDps;
        }

        private readonly List<Phase> phases = new List<Phase>();

        /// <summary>Radius before the first phase begins to shrink it.</summary>
        public float InitialRadius { get; set; } = 50f;

        /// <summary>
        /// Config phase radii describe the storm's shape for a reference arena; scaling by
        /// InitialRadius/maxPhaseRadius keeps it proportional. Without it, an arena smaller than the
        /// config's first phase made the zone GROW for that whole phase.
        /// </summary>
        private float RadiusScale
        {
            get
            {
                float maxR = 0f;
                for (int i = 0; i < phases.Count; i++) maxR = System.Math.Max(maxR, phases[i].Radius);
                return maxR > 0f ? InitialRadius / maxR : 1f;
            }
        }

        public int PhaseCount => phases.Count;
        public bool HasPhases => phases.Count > 0;

        /// <summary>Reads the phase list from the event's attributes.</summary>
        public void ParsePhases(JsonObject attributes)
        {
            phases.Clear();
            var phasesAttr = attributes["zonePhases"];
            if (phasesAttr == null || !phasesAttr.IsArray()) return;

            foreach (var p in phasesAttr.AsArray())
            {
                phases.Add(new Phase
                {
                    EndsAtSeconds = p["endsAtSeconds"].AsFloat(0f),
                    Radius = p["radius"].AsFloat(0f),
                    OutsideDps = p["outsideDps"].AsFloat(1f)
                });
            }
            // Order matters for interpolation.
            phases.Sort((a, b) => a.EndsAtSeconds.CompareTo(b.EndsAtSeconds));
        }

        /// <summary>
        /// Radius at elapsed seconds, interpolated linearly between phase radii so the zone closes
        /// smoothly rather than jumping.
        /// </summary>
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

        /// <summary>
        /// The linear shrink segment in force at elapsed - enough for a client to interpolate locally
        /// instead of being fed per-tick updates. Past the schedule the segment is flat.
        /// </summary>
        public (float radiusNow, float radiusTarget, float secondsToTarget) SegmentAt(float elapsed)
        {
            float now = RadiusAt(elapsed);
            int idx = PhaseIndexAt(elapsed);
            if (idx < 0) return (now, now, 0f);
            return (now, phases[idx].Radius * RadiusScale, phases[idx].EndsAtSeconds - elapsed);
        }

        /// <summary>
        /// Damage per second for standing outside: dps of the phase in force, or the last phase's
        /// once the schedule has run out.
        /// </summary>
        public float DpsAt(float elapsed)
        {
            int idx = PhaseIndexAt(elapsed);
            if (idx >= 0) return phases[idx].OutsideDps;
            return phases.Count > 0 ? phases[phases.Count - 1].OutsideDps : 0f;
        }

        /// <summary>
        /// Index of the phase in force - the first one whose deadline is still ahead - or -1 when the
        /// schedule has run out. Single source of truth for the other lookups here.
        /// </summary>
        public int PhaseIndexAt(float elapsed)
        {
            for (int i = 0; i < phases.Count; i++)
                if (elapsed <= phases[i].EndsAtSeconds) return i;
            return -1;
        }

        /// <summary>Target radius (scaled to this arena) and outside-dps of a phase, for announcing it.</summary>
        public (float radius, float dps) PhaseAt(int index)
            => (phases[index].Radius * RadiusScale, phases[index].OutsideDps);
    }
}
