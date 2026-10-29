using System;
using System.Collections.Generic;
using System.Globalization;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // The settings card that replaces the event table for one event: every scalar setting its behaviors
    // read, applied on edit like the server settings table.
    public sealed partial class EventsPage
    {
        // Event whose settings replace the table; null shows the table.
        private string settingsCode;
        private EventSettingsMsg syncedSettings;
        private const string SettingsTableKey = "ev-settings";

        private void OpenSettings(string code)
        {
            settingsCode = code;
            Net.SendRequestEventSettings(code);
            Rebuild();
        }

        private void BuildSettings(PageBuildContext ctx, double y)
        {
            var compo = ctx.Compo;
            double x0 = ctx.Main.fixedX;
            double width = ctx.Width;
            string code = settingsCode;

            string back = Lang.Get("canevents:admin-events-settings-back");
            string reload = Lang.Get("canevents:admin-srv-reload");
            var row = new ButtonRow(compo, ElementBounds.Fixed(x0, y, width, 0), width, 0, "ev-set-btn");
            row.Add(back, () => { settingsCode = null; Rebuild(); });
            row.Add(reload, () => Net.SendRequestEventSettings(code));
            y += ButtonRow.HeightFor(width, 0, back, reload) + 4;

            string hint = Lang.Get("canevents:admin-events-settings-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);
            compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(x0, y, width, hintHeight), "ev-set-hint");
            y += hintHeight + 6;

            var msg = Net.EventSettings.Value;
            if (msg?.Rows == null || !string.Equals(msg.EventCode, code, StringComparison.OrdinalIgnoreCase))
            {
                compo.AddStaticText(Lang.Get("canevents:admin-srv-loading"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Muted),
                    ElementBounds.Fixed(x0, y, width, 20), "ev-set-loading");
                return;
            }

            var rows = SettingRows(msg);
            SyncSettingsEditLine(msg, rows);

            var inner = Card.Frame(compo, ctx.Column(0, width), y, ctx.Bottom - y,
                Lang.Get("canevents:admin-events-settings-title", code.ToUpperInvariant()));
            SettingsTable.Add(ctx, inner, SettingsTableKey, rows);
        }

        // Same as the server settings table: a rejected value must not stay in the edit field.
        private void SyncSettingsEditLine(EventSettingsMsg msg, List<SettingRow> rows)
        {
            if (ReferenceEquals(syncedSettings, msg)) return;
            syncedSettings = msg;

            State.Drafts.Remove(SettingsTableKey + "-value");
            if (!State.SettingsEditKey.TryGetValue(SettingsTableKey, out string key)) return;
            var row = rows.Find(r => r.Id == key);
            if (row == null) State.SettingsEditKey.Remove(SettingsTableKey);
            else State.SettingsEditValue[SettingsTableKey] = row.CurrentValue();
        }

        // Grouped by behavior. A key gets a readable label only if the lang file has one.
        private List<SettingRow> SettingRows(EventSettingsMsg msg)
        {
            var rows = new List<SettingRow>();
            string group = null;
            string code = msg.EventCode;
            foreach (var r in msg.Rows)
            {
                if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                if (r.Behavior != group)
                {
                    group = r.Behavior;
                    rows.Add(SettingRow.Group(group ?? ""));
                }

                var src = r;
                string behavior = r.Behavior, key = r.Key;
                Action<string> send = v =>
                {
                    src.Value = v;
                    Net.SendSetEventSetting(code, behavior, key, v);
                };

                string labelKey = "canevents:setting-" + key;
                string label = Lang.HasTranslation(labelKey) ? Lang.Get(labelKey) + "  (" + key + ")" : key;
                string tip = Lang.Get("canevents:setting-default", r.Default ?? "");
                string id = behavior + "." + key;

                switch ((SettingType)r.Type)
                {
                    case SettingType.Bool:
                        rows.Add(SettingRow.Flag(id, label, () => src.Value == "true", v => send(v ? "true" : "false"), tip));
                        break;
                    case SettingType.Int:
                        rows.Add(SettingRow.Int(id, label,
                            () => int.TryParse(src.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
                            v => send(v.ToString(CultureInfo.InvariantCulture)), tooltip: tip));
                        break;
                    case SettingType.Float:
                        rows.Add(SettingRow.Dbl(id, label,
                            () => double.TryParse(src.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0,
                            v => send(v.ToString("R", CultureInfo.InvariantCulture)), tooltip: tip));
                        break;
                    default:
                        rows.Add(SettingRow.Text(id, label, () => src.Value, v => send((v ?? "").Trim()), tooltip: tip));
                        break;
                }
            }
            return rows;
        }
    }
}
