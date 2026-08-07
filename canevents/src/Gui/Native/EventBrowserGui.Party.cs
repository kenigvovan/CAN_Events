using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Group tab: invites, roster, leader actions, and the saved class defaults underneath.
    public partial class EventBrowserGui
    {
        private string[] inviteCandidates = new string[0];
        private int inviteIndex;

        /// <summary>Member whose kick is armed and waiting for the confirming second click.</summary>
        private string kickArmed;

        /// <summary>
        /// Engine waypoint icon marking the group leader. A drawn vector, not a character: the game
        /// font has no glyph for a unicode star and renders an empty box.
        /// </summary>
        private const string LeaderIcon = "wpStar1";

        private void ComposePartyTab()
        {
            var state = networkHandler.PartyState.Value;

            // Result of the last group action, coloured so a failure does not read as a confirmation.
            var result = networkHandler.LastPartyResult;
            if (result != null && !string.IsNullOrEmpty(result.Message))
                AddCard(result.Message, accent: result.Success ? Palette.Go : Palette.Stop);

            // Invites come first, whether or not you are in a group. Two cards each (accept, decline)
            // because a tile carries only one action.
            if (state?.PendingInvitesForMe != null && state.PendingInvitesForMe.Length > 0)
            {
                AddHeader(Lang.Get("canevents:gui-section-invites"));
                foreach (var inv in state.PendingInvitesForMe)
                {
                    string inviter = inv.InviterName;
                    AddCard(Lang.Get("canevents:gui-invite-from", inviter, (int)inv.SecondsRemaining),
                        Lang.Get("canevents:gui-invite-detail"),
                        Lang.Get("canevents:gui-accept"), Palette.Go,
                        () => networkHandler.SendPartyAccept(inviter));
                    AddCard(Lang.Get("canevents:gui-invite-refuse", inviter), null,
                        Lang.Get("canevents:gui-decline"), Palette.Stop,
                        () => networkHandler.SendPartyDecline(inviter));
                }
            }

            // Size and leader go in the heading; a whole tile for them pushed the roster below the fold.
            AddHeader(state != null && state.InParty
                ? Lang.Get("canevents:gui-party-heading", state.Members?.Length ?? 0, state.MaxSize,
                    state.Members?.FirstOrDefault(m => m.IsLeader)?.Name ?? "?")
                : Lang.Get("canevents:gui-section-party"));

            if (state == null || !state.InParty)
            {
                AddCard(Lang.Get("canevents:gui-not-in-party"), null,
                    Lang.Get("canevents:gui-create-party"), Palette.Go,
                    () => networkHandler.SendPartyCreate());
                AddDefaultsCards();
                ComposeCards();
                ComposePartyControls(inviteRow: false);
                return;
            }

            bool iAmLeader = state.LeaderUid == state.SelfUid;
            int memberCount = state.Members?.Length ?? 0;

            if (state.Members != null)
            {
                foreach (var m in state.Members)
                {
                    // Only what the row does not already say: the icon marks the leader, so the detail
                    // line is just whether the OTHERS are online.
                    bool isSelf = m.Uid == state.SelfUid;
                    string detail = isSelf ? null
                        : Lang.Get(m.Online ? "canevents:gui-member-online" : "canevents:gui-member-offline");

                    var accent = m.IsLeader ? Palette.Title : (m.Online ? Palette.Go : Palette.Idle);

                    // Only the leader can kick, never themselves. Two clicks, because the tile's whole
                    // face is the button and a stray click must not eject someone mid-queue.
                    if (iAmLeader && m.Uid != state.SelfUid)
                    {
                        string target = m.Name;
                        bool armed = kickArmed == m.Uid;
                        string uid = m.Uid;
                        AddCard(m.Name, detail,
                            Lang.Get(armed ? "canevents:gui-kick-confirm" : "canevents:gui-kick"),
                            armed ? Palette.Stop : accent,
                            () =>
                            {
                                if (armed) { networkHandler.SendPartyKick(target); kickArmed = null; }
                                else kickArmed = uid;
                                RecomposeIfOpen();
                            },
                            titleIcon: m.IsLeader ? LeaderIcon : null);
                    }
                    else AddCard(m.Name, detail, accent: accent,
                        titleIcon: m.IsLeader ? LeaderIcon : null);
                }
            }

            AddDefaultsCards();
            ComposeCards();

            // Candidates for the invite dropdown: everyone online who is not already in.
            var memberUids = new HashSet<string>();
            if (state.Members != null) foreach (var m in state.Members) memberUids.Add(m.Uid);
            inviteCandidates = capi.World.AllOnlinePlayers
                .Where(pl => pl?.PlayerName != null && !memberUids.Contains(pl.PlayerUID))
                .Select(pl => pl.PlayerName)
                .ToArray();
            if (inviteIndex >= inviteCandidates.Length) inviteIndex = 0;

            ComposePartyControls(iAmLeader && memberCount < state.MaxSize && inviteCandidates.Length > 0);
        }

        // Outside the scrolling list: these are the tab's actions, and a dropdown inside a clipped
        // list draws through the clip.
        private void ComposePartyControls(bool inviteRow)
        {
            var state = networkHandler.PartyState.Value;
            double y = ControlsY;

            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-refresh"), () =>
            {
                networkHandler.RequestPartyState();
                return true;
            }, ButtonAt(0, y), EnumButtonStyle.Normal);

            if (state?.InParty == true)
            {
                bool iAmLeader = state.LeaderUid == state.SelfUid;
                SingleComposer.AddSmallButton(
                    Lang.Get(iAmLeader ? "canevents:gui-disband" : "canevents:gui-leave-party"),
                    () =>
                    {
                        if (iAmLeader) networkHandler.SendPartyDisband();
                        else networkHandler.SendPartyLeave();
                        return true;
                    }, ButtonAt(110, y), EnumButtonStyle.Normal);
            }

            if (!inviteRow)
            {
                // Say why there is no invite row: not the leader, group full, or nobody else online.
                if (state?.InParty == true)
                {
                    string why = state.LeaderUid != state.SelfUid
                        ? Lang.Get("canevents:gui-invite-leader-only")
                        : (state.Members?.Length ?? 0) >= state.MaxSize
                            ? Lang.Get("canevents:gui-invite-party-full")
                            : Lang.Get("canevents:gui-invite-nobody");
                    SingleComposer.AddStaticText(why, CairoFont.WhiteDetailText(),
                        ElementBounds.Fixed(250, y + 4, DialogWidth - 250, 22));
                }
                return;
            }

            SingleComposer
                .AddDropDown(inviteCandidates, inviteCandidates, inviteIndex,
                    (code, selected) => inviteIndex = System.Array.IndexOf(inviteCandidates, code),
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 250, y, 200, 26), "invitetarget")
                .AddSmallButton(Lang.Get("canevents:gui-send-invite"), () =>
                {
                    if (inviteIndex >= 0 && inviteIndex < inviteCandidates.Length)
                        networkHandler.SendPartyInvite(inviteCandidates[inviteIndex]);
                    return true;
                }, ButtonAt(462, y), EnumButtonStyle.Normal);
        }

        private void AddDefaultsCards()
        {
            AddHeader(Lang.Get("canevents:gui-section-defaults"));

            var defaults = networkHandler.LoadoutDefaults.Value;
            if (defaults?.Entries == null || defaults.Entries.Length == 0)
            {
                AddCard(Lang.Get("canevents:gui-no-defaults"), accent: Palette.Idle);
                return;
            }

            foreach (var e in defaults.Entries)
            {
                string code = e.EventCode;
                AddCard((code ?? "").ToUpperInvariant(), e.SetName ?? "",
                    Lang.Get("canevents:gui-clear"), Palette.Stop,
                    () => networkHandler.SendClearLoadoutDefault(code));
            }
        }
    }
}
