using ImGuiNET;

namespace canevents.src.Gui
{
    // The Help tab: static prose only, no state and no server calls.
    public partial class AdminPanelDialog
    {
        private void DrawHelpTab()
        {
            ImGui.TextColored(ColAccent, "CAN Events - Admin Guide");
            ImGui.TextDisabled("Press F7 to toggle this panel. Server-controlserver privilege required.");
            ImGui.Spacing();

            if (ImGui.CollapsingHeader("Quick Start", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.BulletText("Go to the 'New Arena' tab and pick the event you want to run.");
                ImGui.BulletText("It walks you through every step that event needs and ticks them off as you go.");
                ImGui.BulletText("When it says READY, announce the event from there or from the Events tab.");
                ImGui.Spacing();
                ImGui.TextDisabled("Arenas is where you edit what already exists - bounds, spawns, mode setup.");
                ImGui.TextDisabled("Actions print a status line at the bottom; the Dashboard keeps a log of recent ones.");
            }

            if (ImGui.CollapsingHeader("Where things live", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.BulletText("Dashboard - scheduler state, running matches, action log, Stop all.");
                ImGui.BulletText("Events - every event, whether it can start right now, enable/disable, announce.");
                ImGui.BulletText("Arenas - the places themselves. Select one to configure it; mode-specific");
                ImGui.BulletText("   setup (hill, checkpoints, zone, platform) appears there based on its types.");
                ImGui.BulletText("New Arena - guided setup from nothing to a ready arena.");
                ImGui.BulletText("Content - loadouts, arena templates, schematics.");
                ImGui.BulletText("Economy - rewards per event and global reward limits.");
            }

            if (ImGui.CollapsingHeader("Concepts"))
            {
                ImGui.TextWrapped("CAN Events has four core concepts:");
                ImGui.Spacing();
                ImGui.BulletText("Event - a game mode definition (e.g. 'arena_1x1', 'deathmatch'). Loaded from JSON in assets/canevents/config/events/.");
                ImGui.BulletText("Place - a physical arena in the world. You create these in-game; they are saved to disk per server.");
                ImGui.BulletText("Template - a schematic + place-recipe. Lets you stamp a pre-built arena anywhere with a click.");
                ImGui.BulletText("Loadout - a named gear set. 'Inline' loadouts come from event JSON; 'Managed' loadouts are edited in-game and override inline ones.");
                ImGui.Spacing();
                ImGui.TextWrapped("An event runs on any place that has all the types the event requires. e.g. event 'arena_1x1' requires type ARENA1X1; tag your places accordingly.");
                ImGui.Spacing();
                ImGui.TextColored(ColAccent, "About bounds");
                ImGui.TextWrapped("Bounds are a place's footprint, marked from two opposite corners. They are NOT a barrier - nothing stops a player walking out of them. What they actually do:");
                ImGui.BulletText("Address the place: /canep commands act on whichever place contains you (X/Z only, height ignored).");
                ImGui.BulletText("Define the Spleef platform's size and floor level.");
                ImGui.BulletText("Provide a fallback centre when a KOTH hill or Hunger Games zone was never set.");
                ImGui.BulletText("Generate two opposite spawn points when you set them.");
                ImGui.TextWrapped("Select a place in the Arenas tab to see its outline drawn in the world.");
            }

            if (ImGui.CollapsingHeader("Workflows"))
            {
                if (ImGui.TreeNode("1v1 dueling arena (ARENA1X1)"))
                {
                    ImGui.BulletText("New Arena tab > pick event '1x1' > name it > create.");
                    ImGui.BulletText("Mark two opposite corners of the floor, then add a spawn per side.");
                    ImGui.BulletText("Events tab > Announce, then players queue from the K browser.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Team Deathmatch (DEATHMATCH)"))
                {
                    ImGui.BulletText("New Arena tab > pick event 'deathmatch'.");
                    ImGui.BulletText("Add one spawn point per team, standing where that team should start.");
                    ImGui.BulletText("Loadouts tab > choose event 'deathmatch' > create at least one set, or rely on inline JSON.");
                    ImGui.BulletText("Tweak timeLimitSeconds / killLimit in event_deathmatch.json.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Spleef (SPLEEF)"))
                {
                    ImGui.BulletText("New Arena tab > pick a spleef event. Bounds should wrap the destructible PLATFORM, not the whole room.");
                    ImGui.BulletText("Select the place > 'Edit Platform' >fill the platform with snow/wood (Fill Sq./Circle/Flood).");
                    ImGui.BulletText("Optional: '+Cage' to wrap the platform in a glass cage that prevents griefing the surroundings.");
                    ImGui.BulletText("Spawns auto-generate on opposite long edges (Mark1/Mark2 corners).");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("King of the Hill (KOTH)"))
                {
                    ImGui.BulletText("New Arena tab > pick event 'koth'. Add one spawn point per team (2-4 teams).");
                    ImGui.BulletText("Arenas tab > select the place > King of the Hill section > stand on the hill >Set hill.");
                    ImGui.BulletText("The block under your feet is the center. Stored as BehaviorData['KingOfTheHill'] = { X, Y, Z, Radius, Height }.");
                    ImGui.BulletText("Without a hill set, the place's bounds center is used instead - check the server log.");
                    ImGui.BulletText("The team outnumbering all others inside the zone scores; equal numbers score nothing.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Parkour (PARKOUR)"))
                {
                    ImGui.BulletText("New Arena tab > pick event 'parkour'. Templates auto-derive bounds from the schematic.");
                    ImGui.BulletText("Arenas tab > select the place > Parkour section > 'Mark CP N' each, 'Mark Finish' last.");
                    ImGui.BulletText("Set 'Per-place Lives' (default 3). Re-index reorders by their numeric label.");
                    ImGui.BulletText("Spawn = the player's position when the place was created (course start).");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Capture the Flag (CTF)"))
                {
                    ImGui.BulletText("New Arena tab > pick event 'ctf'. Add one spawn point per team (2 teams).");
                    ImGui.BulletText("Arenas tab > select the place > Capture the Flag section > stand on each post > Set post.");
                    ImGui.BulletText("Or in chat: /canep ctfpost <team> (1-based) at the block under your feet.");
                    ImGui.BulletText("Flags render on the posts; carry the enemy flag home while yours is in place.");
                    ImGui.BulletText("Tweak captureLimit / timeLimitSeconds in event_ctf.json.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Hunger Games (HUNGERGAMES)"))
                {
                    ImGui.TextWrapped("Battle royale: one life each, loot from chests on the map, a storm ring that shrinks until one tribute is left. Placement is by how long you survived, not by kills.");
                    ImGui.Spacing();
                    ImGui.TextColored(ColAccent, "Fastest route");
                    ImGui.BulletText("New Arena tab > pick event 'hungergames' > mark bounds around the playable area.");
                    ImGui.BulletText("Press 'Generate layout': it lays a ring of spawns, the zone centre and all loot spots");
                    ImGui.BulletText("   (cornucopia in the middle, mid ring, outer ring) in one click. 'Snap to ground'");
                    ImGui.BulletText("   follows uneven terrain; turn it off for a flat schematic floor, and for a roofed");
                    ImGui.BulletText("   arena (it would snap onto the roof). Generating over an existing layout asks twice.");
                    ImGui.Spacing();
                    ImGui.TextColored(ColAccent, "Or by hand");
                    ImGui.BulletText("One spawn per tribute (12 for the default event).");
                    ImGui.BulletText("Hunger Games section > Set zone (block under your feet = centre) + radius.");
                    ImGui.BulletText("Add loot spots one by one, each with a tier; the tier dropdown previews its contents.");
                    ImGui.BulletText("Spots can be re-tiered or removed individually from the list.");
                    ImGui.BulletText("Without a zone set, the place's bounds center is used instead.");
                    ImGui.Spacing();
                    ImGui.TextColored(ColAccent, "Checking it");
                    ImGui.BulletText("The readiness checklist flags missing bounds/spawns/zone/loot and spawns outside the zone.");
                    ImGui.BulletText("'Preview zone & markers in world' draws the ring, spawns and loot markers for you only.");
                    ImGui.BulletText("Zone phases and loot tables are editable right in the panel (Save writes the event JSON).");
                    ImGui.BulletText("Feast, sponsor drops, grace period and the opening freeze live in event_hungergames.json.");
                    ImGui.BulletText("hungergames_solotest runs the whole thing solo at 3x speed - use it to test an arena.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Juggernaut (DEATHMATCH place)"))
                {
                    ImGui.BulletText("Runs on DEATHMATCH-tagged places - reuse an existing deathmatch arena.");
                    ImGui.BulletText("FFA with respawns; one player holds the crown (bonus HP + damage).");
                    ImGui.BulletText("Tweak crown bonuses / scoreLimit in event_juggernaut.json.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Murder Mystery (MURDERMYSTERY place)"))
                {
                    ImGui.TextColored(ColAccent, "Weapon damage");
                    ImGui.BulletText("sheriffBowDamage / murdererBowDamage set what an arrow is worth per role;");
                    ImGui.BulletText("  bowDamage is the fallback for both, murdererMeleeDamage covers the knife.");
                    ImGui.BulletText("0 leaves the item's own damage alone.");
                    ImGui.BulletText("Edit it in Arenas tab > Murder Mystery > Settings, or in event_murdermystery.json.");
                    ImGui.BulletText("Saving writes the event JSON in ModConfig and applies to the next match.");
                    ImGui.Spacing();
                    ImGui.TextColored(ColAccent, "Weapon spawns during the match");
                    ImGui.BulletText("bowPickupItem names an ORDINARY item to drop (no new item needs making).");
                    ImGui.BulletText("Players collect it: every bowPickupRequired of them (10 by default) turn");
                    ImGui.BulletText("  into bowPickupReward - a bow and arrows. Leftovers above that stay in the bag.");
                    ImGui.BulletText("Carried parts are real items: they drop on death and can be looted.");
                    ImGui.BulletText("Timers: bowPickupFirstSeconds, then every bowPickupIntervalSeconds.");
                    ImGui.BulletText("bowPickupMaxActive caps how many lie around; bowPickupMinDistance keeps");
                    ImGui.BulletText("  two of them apart, so one corner cannot become a stash.");
                    ImGui.BulletText("bowPickupLifetimeSeconds expires an untouched one (0 = it waits).");
                    ImGui.BulletText("bowPickupMakesSheriff turns an innocent finder into the sheriff.");
                    ImGui.Spacing();
                    ImGui.TextColored(ColAccent, "Marking the spots");
                    ImGui.BulletText("Arenas tab > select the place > Murder Mystery section, or /canep mmpickup.");
                    ImGui.BulletText("Marked spots are drawn in the world in yellow while the place is selected.");
                    ImGui.BulletText("No spots marked = no weapon spawns; the mode plays as it always did.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Schedule auto-running events"))
                {
                    ImGui.BulletText("Dashboard > Auto-scheduler >Turn ON/OFF and pick an interval.");
                    ImGui.BulletText("Pick interval (5/10/15/30/60 min). Events rotate through the configured list.");
                    ImGui.BulletText("Or use /eventadmin schedule on / off / interval <minutes> in chat.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Loadouts (inline vs managed)"))
                {
                    ImGui.BulletText("Inline = sets defined in event JSON. Always present; treated as fallback.");
                    ImGui.BulletText("Managed = sets edited in-game (Loadouts tab). Stored in mod config/loadouts/<eventCode>.json.");
                    ImGui.BulletText("If managed sets exist for an event, they REPLACE inline sets entirely (not merged).");
                    ImGui.BulletText("'Import from JSON' button copies inline sets into managed storage so you can tweak them.");
                    ImGui.BulletText("Players see a popup at match start to pick a set if more than one exists; they can save a default.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Templates (reusable arena recipes)"))
                {
                    ImGui.BulletText("Schematics tab > 'Create Template from Schematic' on a .json schematic file.");
                    ImGui.BulletText("Templates tab > 'Place Here' stamps the schematic at your position and creates a Place.");
                    ImGui.BulletText("'Edit' opens an in-world session for adjusting spawn offsets - Save/Cancel/Finish at the top.");
                    ImGui.BulletText("'On Demand' templates are placed at event start and removed when the event ends.");
                    ImGui.TreePop();
                }
            }

            if (ImGui.CollapsingHeader("Troubleshooting"))
            {
                if (ImGui.TreeNode("\"No place found\" when starting an event"))
                {
                    ImGui.TextWrapped("The event needs a place tagged with all of its required types. Open Arenas tab and check that at least one place has the right Types ticked AND is Enabled AND has spawn points.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("KOTH never scores"))
                {
                    ImGui.TextWrapped("Hill geometry is missing, so the bounds center is being used and it may sit off the playable floor. Select the place in the Arenas tab - the King of the Hill section shows whether a hill is set - stand on the intended block and set it. Check the server log for the [KOTH] line at start.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Spleef cage spawns inside the wrong area"))
                {
                    ImGui.TextWrapped("'+Cage' centres the cage on where YOU are standing, not on the place's bounds. Stand in the middle of the intended cage, then click +Cage again -'Clear Cage' first if one is already stored.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Hunger Games: the storm hurts everyone from the start"))
                {
                    ImGui.TextWrapped("The zone is centred somewhere off the arena, or its radius is smaller than the spawn ring, so tributes begin outside it. Select the place, turn on the in-world preview and check the readiness list - it warns when spawns sit outside the zone. Re-run 'Generate layout' or set the zone from the middle of the arena with a radius that covers the spawns.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Hunger Games: chests are empty or missing"))
                {
                    ImGui.TextWrapped("A loot spot that already had a chest, sign or other block entity on it is skipped on purpose (placing there would destroy its contents) - the server log names the position. A spot marked at the wrong height ends up buried; use 'Snap to ground' or re-mark it. The feast only tops up chests this match placed, and only into their free slots.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Players don't get loadouts"))
                {
                    ImGui.TextWrapped("Check (1) the event JSON has 'loadoutSets' or 'loadout', or (2) the Loadouts tab has at least one managed set for this event code. The set name in the player's pick must exist in the active source.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Mark 1 / Mark 2 doesn't react"))
                {
                    ImGui.TextWrapped("Marking prefers the block you are LOOKING AT. The panel grabs the mouse while open, so if nothing is targeted it falls back to the block under your feet - the line above the buttons always says which one will be used.");
                    ImGui.TreePop();
                }
                if (ImGui.TreeNode("Loadout item shows 'not found' in server log"))
                {
                    ImGui.TextWrapped("The item code is wrong or the providing mod isn't loaded. Format is 'domain:code', e.g. 'game:sword-iron'. Use creative inventory to copy the exact code.");
                    ImGui.TreePop();
                }
            }

            if (ImGui.CollapsingHeader("Useful chat commands"))
            {
                ImGui.TextDisabled("All require server-controlserver privilege.");
                ImGui.BulletText("/canep new | addname <n> | addtype <T> | mark1 | mark2 | save  - manual place creation");
                ImGui.BulletText("/canep delete <placeId>  - remove a place");
                ImGui.BulletText("/canep kothhill <radius> [height]  - set KOTH hill at the block under your feet");
                ImGui.BulletText("/canep cp <index> | cpfinish | cplist | cpremove | cpedit <idx> | cplives <n>  - parkour checkpoints");
                ImGui.BulletText("/canep hgzone <radius> | hgloot [tier] | hglootlist | hglootclear  - Hunger Games zone and loot");
                ImGui.BulletText("/canep ctfpost <team> | ctfposts | ctfclear  - CTF flag posts");
                ImGui.BulletText("/canep mmpickup | mmpickuplist | mmpickupremove <idx> | mmpickupclear  - Murder Mystery weapon spawns");
                ImGui.BulletText("/eventadmin start <eventCode>  - announce an event");
                ImGui.BulletText("/eventadmin stop  - abort all running events");
                ImGui.BulletText("/eventadmin schedule on | off | interval <minutes>");
            }
        }
    }
}
