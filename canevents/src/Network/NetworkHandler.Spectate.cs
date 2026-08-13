using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Bots;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.Network
{
    // Spectating: joining and leaving the seat, the broadcaster's live readout of the field, and
    // moving the camera onto a fighter. Everything here re-checks the privilege on arrival - a
    // client can send any packet at any time, whatever its own UI is willing to show.
    public partial class ServerNetworkHandler
    {
        // How far behind and above a followed participant the camera lands. Behind, so the first
        // frame after the jump looks at the fighter rather than out of the back of their head.
        private const double FollowBackOff = 3.5;
        private const double FollowHeight = 2.0;

        private void RegisterSpectateHandlers()
        {
            SetHandler<SpectateRequestMsg>(OnSpectateRequest);
            SetHandler<SpectateFollowMsg>(OnSpectateFollow);
        }

        /// <summary>May this player watch a match they are not in? Registered with adminAutoGrant, so
        /// admins have it and a moderator role can be given it separately.</summary>
        private static bool MaySpectate(IServerPlayer player)
            => player != null && player.HasPrivilege(CanEventsPrivileges.Spectate);

        private void OnSpectateRequest(IServerPlayer player, SpectateRequestMsg msg)
        {
            if (msg == null) return;

            if (!msg.Start)
            {
                // Leaving is unprivileged on purpose: a player who somehow ended up in the seat must
                // always be able to get out of it, even after their privilege was revoked.
                // StopObserving pushes the state packet itself; only the "you weren't watching"
                // answer has no state change behind it and has to be said here.
                if (!eventManager.StopObserving(player, notifyClient: true))
                    SendSpectateState(player, false, 0, Lang.Get("canevents:spectate-not-watching"));
                return;
            }

            if (!MaySpectate(player))
            {
                SendSpectateState(player, false, 0, Lang.Get("canevents:spectate-no-privilege"));
                return;
            }

            // On success StartObserving sends the state; a refusal never changes state, so it is
            // reported from here.
            var (ok, message) = eventManager.StartObserving(player, msg.EventId, notifyClient: true);
            if (!ok) SendSpectateState(player, false, 0, message);
        }

        /// <summary>
        /// Tells one client what its spectator seat is now. The single writer of the client's
        /// <c>IsObserving</c>, so every path that seats or unseats someone - packet, chat command,
        /// auto-spectate, the match ending - has to go through it, or the broadcaster panel is left
        /// closed when it should be open (or open when the match is gone).
        /// </summary>
        internal void SendSpectateState(IServerPlayer player, bool observing, long eventId, string message = null)
        {
            if (player == null) return;
            serverChannel.SendPacket(new SpectateStateMsg
            {
                Observing = observing,
                EventId = eventId,
                Message = message,
                Privileged = MaySpectate(player)
            }, player);
        }

        // Broadcaster camera, hotkey path: one fighter along the line-up. Every field is re-derived
        // here - the packet only says which direction.
        private void OnSpectateFollow(IServerPlayer player, SpectateFollowMsg msg)
        {
            if (msg == null || player?.Entity == null) return;
            if (!MaySpectate(player)) return;

            var re = eventManager.FindRunningEvent(msg.EventId);
            // Only for a match this player is actually seated at: otherwise this is a teleport to
            // arbitrary coordinates dressed up as a camera control.
            if (re == null || re.EventState == EventState.Finished) return;
            if (!re.Observers.Contains(player.PlayerUID)) return;

            // A client can send this as fast as it likes. Nothing here is dangerous - they only ever
            // move themselves, and only inside their own match - but a teleport per tick is server
            // work on demand, and no camera operator needs more than a few cuts a second.
            if (!FollowCooldownElapsed(player.PlayerUID)) return;

            MoveCameraTo(player, re, StepTarget(re, msg.Step, player.PlayerUID));
        }

        /// <summary>
        /// Puts an observer's camera on a named participant. The chat path (/canspectate follow),
        /// which is how a specific fighter gets picked - see <see cref="SpectateFollowMsg"/> for why
        /// the panel cannot do it. Returns a localized result for the command to print.
        /// </summary>
        internal (bool ok, string message) FollowParticipant(IServerPlayer player, string targetUid)
        {
            if (player?.Entity == null) return (false, Lang.Get("canevents:spectate-not-watching"));
            if (!MaySpectate(player)) return (false, Lang.Get("canevents:spectate-no-privilege"));

            long eventId = eventManager.SpectatorManager?.SeatedEventId(player.PlayerUID) ?? 0;
            var re = eventId > 0 ? eventManager.FindRunningEvent(eventId) : null;
            if (re == null || !re.Observers.Contains(player.PlayerUID))
                return (false, Lang.Get("canevents:spectate-not-watching"));

            if (!re.IsParticipant(targetUid))
                return (false, Lang.Get("canevents:spectate-follow-not-in-match"));

            return MoveCameraTo(player, re, targetUid);
        }

        // The teleport itself, shared by both paths. Null target and a body that is no longer in the
        // world are the two ways this can come to nothing, and each has its own answer.
        private (bool ok, string message) MoveCameraTo(IServerPlayer player, RunningEvent re, string targetUid)
        {
            if (targetUid == null)
            {
                string none = Lang.Get("canevents:spectate-follow-nobody");
                player.SendMessage(0, none, EnumChatType.Notification);
                return (false, none);
            }

            var body = re.BodyOf(targetUid);
            if (body == null)
            {
                string gone = Lang.Get("canevents:spectate-follow-gone");
                player.SendMessage(0, gone, EnumChatType.Notification);
                return (false, gone);
            }

            var pos = body.ServerPos;

            // Over-the-shoulder: back along the fighter's own facing, which in Vintage Story is
            // (sin(yaw), cos(yaw)) - so behind them is that vector SUBTRACTED. Adding it, as this
            // first did, parks the camera in front of their face looking away from the fight.
            float yaw = pos.Yaw;
            double x = pos.X - Math.Sin(yaw) * FollowBackOff;
            double z = pos.Z - Math.Cos(yaw) * FollowBackOff;

            // The camera turn has to wait for the teleport: it is asynchronous (the target column is
            // loaded first), and turning before landing leaves the observer facing the old spot.
            player.Entity.TeleportToDouble(x, pos.Y + FollowHeight, z,
                () => serverChannel.SendPacket(new SpectateCameraMsg
                {
                    // Same facing as the fighter, so they are centred in the shot and the observer is
                    // looking where the fight is going. Level, whatever the observer was doing
                    // before - a camera that keeps a leftover downward tilt looks broken.
                    Yaw = yaw,
                    Pitch = 0f
                }, player));

            lastFollowed[player.PlayerUID] = targetUid;

            string message = Lang.Get("canevents:spectate-following", eventManager.NameOf(targetUid));
            player.SendMessage(0, message, EnumChatType.Notification);
            return (true, message);
        }

        // Who each observer was last sent to, so "next fighter" has somewhere to count from. Dropped
        // when they stop watching; a stale entry only ever costs one wrong step.
        private readonly Dictionary<string, string> lastFollowed = new Dictionary<string, string>();

        // When each observer's last camera cut landed, for the rate limit above.
        private readonly Dictionary<string, long> lastFollowMs = new Dictionary<string, long>();

        /// <summary>Shortest gap between two camera cuts by the same observer.</summary>
        private const long FollowCooldownMs = 200;

        private bool FollowCooldownElapsed(string playerUid)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (lastFollowMs.TryGetValue(playerUid, out long last) && now - last < FollowCooldownMs)
                return false;
            lastFollowMs[playerUid] = now;
            return true;
        }

        // The next fighter along from wherever this observer's camera last was.
        private string StepTarget(RunningEvent re, int direction, string observerUid)
        {
            // The line-up as the observer sees it, live fighters only: stepping through it should
            // walk the fight, not a list of corpses. Falls back to the full roster once everyone is
            // out, so the last moments of a match are still watchable.
            var candidates = re.ParticipantsBefore.Where(re.IsActive).ToList();
            if (candidates.Count == 0) candidates = re.ParticipantsBefore.ToList();
            if (candidates.Count == 0) return null;

            int step = Math.Sign(direction);
            if (step == 0) step = 1;

            int index = lastFollowed.TryGetValue(observerUid, out var previous)
                ? candidates.IndexOf(previous)
                : -1;

            // Wraps both ways; a target that has since died is not in the list, so index is -1 and
            // the walk restarts from the top rather than stalling.
            int next = index < 0
                ? (step > 0 ? 0 : candidates.Count - 1)
                : ((index + step) % candidates.Count + candidates.Count) % candidates.Count;

            return candidates[next];
        }

        /// <summary>
        /// Pushes the live field readout to this match's privileged observers. Called from the match
        /// tick; returns immediately when nobody is watching, which is the normal case.
        /// </summary>
        public void SendSpectatorTelemetry(RunningEvent re)
        {
            if (re == null || re.Observers.Count == 0) return;
            if (re.EventState != EventState.Running) return;

            var recipients = new List<IServerPlayer>();
            foreach (var uid in re.Observers)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer observer)) continue;
                // Re-checked per send, not once at join: a privilege revoked mid-match must take
                // effect without waiting for the observer to leave the seat.
                if (!MaySpectate(observer)) continue;
                recipients.Add(observer);
            }
            if (recipients.Count == 0) return;

            if (!ShouldSendTelemetry(re)) return;

            var msg = BuildTelemetry(re);
            foreach (var observer in recipients) serverChannel.SendPacket(msg, observer);
        }

        // Last send per match, so the packet rate follows the configured interval rather than the
        // match tick (which is faster, and is not ours to slow down).
        private readonly Dictionary<long, long> lastTelemetryMs = new Dictionary<long, long>();

        private bool ShouldSendTelemetry(RunningEvent re)
        {
            int interval = Math.Max(100, re.Config?.spectatorTelemetryIntervalMs ?? 500);
            long now = sapi.World.ElapsedMilliseconds;

            if (lastTelemetryMs.TryGetValue(re.Id, out long last) && now - last < interval) return false;
            lastTelemetryMs[re.Id] = now;
            return true;
        }

        private SpectatorTelemetryMsg BuildTelemetry(RunningEvent re)
        {
            var uids = new List<string>();
            var names = new List<string>();
            var teams = new List<int>();
            var xs = new List<double>();
            var ys = new List<double>();
            var zs = new List<double>();
            var health = new List<float>();
            var maxHealth = new List<float>();
            var alive = new List<bool>();
            var isBot = new List<bool>();

            foreach (var uid in re.ParticipantsBefore)
            {
                var body = re.BodyOf(uid);
                if (body == null) continue;   // offline player, or a bot body that already despawned

                var hp = body.GetBehavior<EntityBehaviorHealth>();

                uids.Add(uid);
                names.Add(eventManager.NameOf(uid));
                teams.Add(re.TeamIndexOf(uid));
                xs.Add(body.ServerPos.X);
                ys.Add(body.ServerPos.Y);
                zs.Add(body.ServerPos.Z);
                health.Add(hp?.Health ?? 0f);
                maxHealth.Add(hp?.MaxHealth ?? 0f);
                alive.Add(re.IsActive(uid) && body.Alive);
                isBot.Add(BotIdentity.IsBot(uid));
            }

            return new SpectatorTelemetryMsg
            {
                EventId = re.Id,
                Uids = uids.ToArray(),
                Names = names.ToArray(),
                TeamIds = teams.ToArray(),
                X = xs.ToArray(),
                Y = ys.ToArray(),
                Z = zs.ToArray(),
                Health = health.ToArray(),
                MaxHealth = maxHealth.ToArray(),
                Alive = alive.ToArray(),
                IsBot = isBot.ToArray()
            };
        }

        /// <summary>Drops the per-observer camera state. Called when they leave the seat - by choice,
        /// by disconnect, or because the match ended - so the next "next fighter" starts from the top
        /// instead of from a match they no longer watch, and neither dictionary grows without bound.</summary>
        internal void ForgetSpectator(string playerUid)
        {
            lastFollowed.Remove(playerUid);
            lastFollowMs.Remove(playerUid);
        }

        /// <summary>Drops a finished match's telemetry pacing entry, so the dictionary does not grow
        /// for the life of the server.</summary>
        internal void ForgetMatchTelemetry(long eventId) => lastTelemetryMs.Remove(eventId);
    }
}
