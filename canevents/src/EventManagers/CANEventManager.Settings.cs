using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers
{
    // One editor for every behavior's scalar settings: what each behavior reads in Initialize is
    // the list, so a new key shows up here without any wiring. Saved to the event JSON in ModConfig
    // and applied from the next match on; a running match keeps the values it started with.
    public partial class CANEventManager
    {
        public sealed class EventSettingValue
        {
            public string Behavior;
            public KnownSetting Setting;
            public string Value;
            public string Default;
        }

        public List<EventSettingValue> EventSettings(string code)
        {
            var result = new List<EventSettingValue>();
            var ev = FindEventByCode(code);
            if (ev == null) return result;

            foreach (var beh in ev.Behaviors)
                foreach (var s in beh.KnownSettings)
                    result.Add(new EventSettingValue
                    {
                        Behavior = beh.Name,
                        Setting = s,
                        Value = Format(s.Type, ValueOf(beh.Attributes?[s.Key], s)),
                        Default = Format(s.Type, s.Fallback)
                    });
            return result;
        }

        // behavior may be null when only one behavior of the event has that key.
        public (bool ok, string message) SetEventSetting(string code, string behavior, string key, string value)
        {
            var ev = FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));

            var matches = EventSettings(ev.Code).Where(s => string.Equals(s.Setting.Key, key, StringComparison.OrdinalIgnoreCase)
                && (behavior == null || string.Equals(s.Behavior, behavior, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0) return (false, Lang.Get("canevents:setting-unknown", key, ev.Code));
            if (matches.Count > 1)
                return (false, Lang.Get("canevents:setting-ambiguous", key, string.Join(", ", matches.Select(m => m.Behavior))));

            var target = matches[0];
            if (!TryParse(target.Setting.Type, value, out JToken token))
                return (false, Lang.Get("canevents:setting-bad-value", value, TypeName(target.Setting.Type)));

            string realKey = target.Setting.Key;
            var (ok, message) = EditEventBehaviorAttributes(ev.Code, target.Behavior, attr => attr[realKey] = token,
                "canevents:setting-saved");
            if (!ok) return (false, message);
            // protectArena and friends: the guard caches which arenas are covered.
            sapi.ModLoader.GetModSystem<canevents>()?.ArenaProtection?.Invalidate();
            string shown = Format(target.Setting.Type, ValueOf(new JsonObject(token), target.Setting));
            // message says whether the file write went through too.
            return (true, Lang.Get("canevents:setting-set", target.Behavior, realKey, shown) + " " + message);
        }

        private static object ValueOf(JsonObject node, KnownSetting s)
        {
            if (node?.Exists != true) return s.Fallback;
            switch (s.Type)
            {
                case SettingType.Int: return node.AsInt((int)s.Fallback);
                case SettingType.Float: return node.AsFloat((float)s.Fallback);
                case SettingType.Bool: return node.AsBool((bool)s.Fallback);
                default: return node.AsString(s.Fallback as string);
            }
        }

        public static string Format(SettingType type, object value)
        {
            switch (type)
            {
                case SettingType.Bool: return value is bool b && b ? "true" : "false";
                case SettingType.Float: return Convert.ToSingle(value ?? 0f).ToString("R", CultureInfo.InvariantCulture);
                case SettingType.Int: return Convert.ToInt32(value ?? 0).ToString(CultureInfo.InvariantCulture);
                default: return value as string ?? "";
            }
        }

        // Comma accepted as a decimal point: admins type in their own locale.
        public static bool TryParse(SettingType type, string text, out JToken token)
        {
            token = null;
            text = (text ?? "").Trim();
            switch (type)
            {
                case SettingType.Int:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return false;
                    token = new JValue(i);
                    return true;
                case SettingType.Float:
                    if (!float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                        || float.IsNaN(f) || float.IsInfinity(f)) return false;
                    token = new JValue(f);
                    return true;
                case SettingType.Bool:
                    switch (text.ToLowerInvariant())
                    {
                        case "true": case "on": case "1": case "yes": token = new JValue(true); return true;
                        case "false": case "off": case "0": case "no": token = new JValue(false); return true;
                        default: return false;
                    }
                default:
                    token = new JValue(text);
                    return true;
            }
        }

        private static string TypeName(SettingType type) => Lang.Get("canevents:setting-type-" + type.ToString().ToLowerInvariant());
    }
}
