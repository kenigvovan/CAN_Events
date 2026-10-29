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
    // Spectating. Privilege is re-checked on every packet, regardless of what the client UI shows.
    public partial class ServerNetworkHandler
    {
        // Camera offset behind and above the followed fighter.
        private const double FollowBackOff = 3.5;
        private const double FollowHeight = 2.0;

        private void RegisterSpectateHandlers()
        {
            Handle<SpectateRequestMsg>(ProtocolChannel.Main, false, OnSpectateRequest);
            Handle<SpectateFollowMsg>(ProtocolChannel.Main, false, OnSpectateFollow);
        }

        private static bool MaySpectate(IServerPlayer player)
            => player != null && player.HasPrivilege(CanEventsPrivileges.Spectate);

        private void OnSpectateRequest(IServerPlayer player, SpectateRequestMsg msg)
        {
            if (msg == null) return;

            if (!msg.Start)
            {
                // No privilege check: anyone in the seat must be able to leave it.
                eventManager.LeaveSpectating(player, notifyClient: true);
                return;
            }

            if (!MaySpectate(player))
            {
                SendSpectateState(player, false, 0, Lang.Get("canevents:spectate-no-privilege"));
                return;
            }

            // StartObserving sends the state on success only.
            var (ok, message) = eventManager.StartObserving(player, msg.EventId, notifyClient: true);
            if (!ok) SendSpectateState(player, false, 0, message);
        }

        // Only writer of the client's IsObserving; every seat/unseat path must call this.
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

        // Hotkey path: step to the next/previous fighter.
        private void OnSpectateFollow(IServerPlayer player, SpectateFollowMsg msg)
        {
            if (msg == null || player?.Entity == null) return;
            if (!MaySpectate(player)) return;

            var re = eventManager.FindRunningEvent(msg.EventId);
            // Must be seated at this match, or it's a free teleport.
            if (re == null || re.EventState == EventState.Finished) return;
            if (!re.Observers.Contains(player.PlayerUID)) return;

            if (!FollowCooldownElapsed(player.PlayerUID)) return;

            MoveCameraTo(player, re, StepTarget(re, msg.Step, player.PlayerUID));
        }

        // /canspectate follow path. Returns a localized message.
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

            var pos = body.Pos;

            // Facing is (sin(yaw), cos(yaw)), so subtract to get behind them.
            float yaw = pos.Yaw;
            double x = pos.X - Math.Sin(yaw) * FollowBackOff;
            double z = pos.Z - Math.Cos(yaw) * FollowBackOff;

            // Teleport is async, turn the camera only after landing.
            EventTeleport.Send(sapi, player, x, pos.Y + FollowHeight, z, pos.Dimension,
                "camera onto " + eventManager.NameOf(targetUid),
                () => serverChannel.SendPacket(new SpectateCameraMsg
                {
                    // Fighter's facing, level pitch.
                    Yaw = yaw,
                    Pitch = 0f
                }, player));

            lastFollowed[player.PlayerUID] = targetUid;

            string message = Lang.Get("canevents:spectate-following", eventManager.NameOf(targetUid));
            player.SendMessage(0, message, EnumChatType.Notification);
            return (true, message);
        }

        // observer uid -> last followed uid, for next/previous stepping.
        private readonly Dictionary<string, string> lastFollowed = new Dictionary<string, string>();

        private readonly Dictionary<string, long> lastFollowMs = new Dictionary<string, long>();

        private const long FollowCooldownMs = 200;

        private bool FollowCooldownElapsed(string playerUid)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (lastFollowMs.TryGetValue(playerUid, out long last) && now - last < FollowCooldownMs)
                return false;
            lastFollowMs[playerUid] = now;
            return true;
        }

        private string StepTarget(RunningEvent re, int direction, string observerUid)
        {
            // Live fighters only; full roster once everyone is out.
            var candidates = re.ParticipantsBefore.Where(re.IsActive).ToList();
            if (candidates.Count == 0) candidates = re.ParticipantsBefore.ToList();
            if (candidates.Count == 0) return null;

            int step = Math.Sign(direction);
            if (step == 0) step = 1;

            int index = lastFollowed.TryGetValue(observerUid, out var previous)
                ? candidates.IndexOf(previous)
                : -1;

            // Wraps both ways. Dead previous target gives -1, so start over.
            int next = index < 0
                ? (step > 0 ? 0 : candidates.Count - 1)
                : ((index + step) % candidates.Count + candidates.Count) % candidates.Count;

            return candidates[next];
        }

        // Called from the match tick.
        public void SendSpectatorTelemetry(RunningEvent re)
        {
            if (re == null || re.Observers.Count == 0) return;
            if (re.EventState != EventState.Running) return;

            var recipients = new List<IServerPlayer>();
            foreach (var uid in re.Observers)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer observer)) continue;
                // Checked per send so a revoked privilege applies mid-match.
                if (!MaySpectate(observer)) continue;
                recipients.Add(observer);
            }
            if (recipients.Count == 0) return;

            if (!ShouldSendTelemetry(re)) return;

            var msg = BuildTelemetry(re);
            foreach (var observer in recipients) serverChannel.SendPacket(msg, observer);
        }

        // Last send per match, paced by spectatorTelemetryIntervalMs.
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
                xs.Add(body.Pos.X);
                ys.Add(body.Pos.Y);
                zs.Add(body.Pos.Z);
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

        // Called whenever an observer leaves the seat.
        internal void ForgetSpectator(string playerUid)
        {
            lastFollowed.Remove(playerUid);
            lastFollowMs.Remove(playerUid);
        }

        internal void ForgetMatchTelemetry(long eventId) => lastTelemetryMs.Remove(eventId);
    }
}
