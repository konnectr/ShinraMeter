using Data;
using Data.Actions.Notify;
using Data.Actions.Notify.SoundElements;
using Data.Events;
using Data.Events.Abnormality;
using Lang;
using Nostrum;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Tera.Game;
using DataAction = Data.Actions.Action;

namespace DamageMeter.UI.Windows
{
    /// <summary>
    /// Sub-header of the "Combat notifications" list: "Common" or the player's class.
    /// </summary>
    public class CombatNotificationGroupVM
    {
        public CombatNotificationGroupVM(string title) { Title = title; }

        public string Title { get; }
    }

    /// <summary>
    /// One row of the "Combat notifications" list in the Events settings tab: a single abnormality
    /// id, abnormality category or cooldown skill of a data-driven combat event
    /// (resources/config/events/events-common.xml and events-&lt;class&gt;.xml).
    /// <para>
    /// Rows show the readable name and icon straight from the abnormality/skill databases (see
    /// <see cref="GameNames"/>, which loads them before login too), with a dim second line naming the
    /// trigger and the raw id. The checkbox switches this one id on or off through
    /// <see cref="CombatEventToggle"/>, and Test replays the event's own balloon/sound so the user
    /// hears the real thing. Content and triggers stay in the Events editor.
    /// </para>
    /// </summary>
    public class CombatNotificationVM : TSPropertyChanged
    {
        private readonly Event _event;
        private readonly List<DataAction> _actions;
        private readonly CombatEventToggle _toggle;
        private readonly string _token;
        private readonly int _abnormalityId;

        public string Name { get; }

        /// <summary>Dim second line: trigger kind and the raw id, e.g. "Applied - ID 6001".</summary>
        public string Details { get; }

        public string Tooltip { get; }
        public BitmapImage Icon { get; }
        public bool CanTest => _actions.OfType<NotifyAction>().Any();
        public ICommand TestCommand { get; }

        public bool IsOn
        {
            get => _toggle.IsEnabled(_token);
            set
            {
                if (_toggle.IsEnabled(_token) == value) { return; }
                _toggle.Set(_token, value);
            }
        }

        /// <param name="token">
        /// The id as the events xml spells it, or null for an event that has no id at all. This is
        /// what ends up in the disabled_ids attribute.
        /// </param>
        public CombatNotificationVM(Event ev, List<DataAction> actions, CombatEventToggle toggle, string token, int abnormalityId, string name, string typeLabel,
            string iconName)
        {
            _event = ev;
            _actions = actions ?? new List<DataAction>();
            _toggle = toggle;
            _token = token;
            _abnormalityId = abnormalityId;

            Name = name;
            Details = BuildDetails(typeLabel, token);
            Tooltip = BuildTooltip(ev, name, Details);
            Icon = LoadIcon(iconName);
            TestCommand = new RelayCommand(_ => Test());

            _toggle.Changed += () => NotifyPropertyChanged(nameof(IsOn));
        }

        private static BitmapImage LoadIcon(string iconName)
        {
            // GetImage already answers the 1x1 empty bitmap for an unknown or missing icon.
            try { return BasicTeraData.Instance.Icons?.GetImage(iconName ?? string.Empty); }
            catch { return null; }
        }

        private static string BuildDetails(string typeLabel, string token)
        {
            if (string.IsNullOrEmpty(token)) { return typeLabel; }
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? $"{typeLabel}  ·  ID {token}"
                : $"{typeLabel}  ·  {LP.CombatNotifyCategory}";
        }

        private static string BuildTooltip(Event ev, string name, string details)
        {
            var lines = new List<string> { name, details, $"{LP.CombatNotifyPriority}: {ev.Priority}" };
            lines.Add(ev.InGame ? LP.CombatNotifyInGame : LP.CombatNotifyOutOfGame);
            if (ev.OutOfCombat) { lines.Add(LP.CombatNotifyOutOfCombat); }
            if (ev is AbnormalityEvent abnormality) { lines.Add($"{LP.CombatNotifyTarget}: {abnormality.Target}"); }
            return string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        /// <summary>
        /// Pushes the event's first notify action through the real popup pipeline, with the same
        /// placeholders the notify processor would have substituted at trigger time.
        /// AddNotification already honours MuteSound.
        /// </summary>
        private void Test()
        {
            var notifyAction = _actions.OfType<NotifyAction>().FirstOrDefault()?.Clone();
            if (notifyAction == null) { return; }

            var replacements = BuildReplacements();

            if (notifyAction.Balloon != null)
            {
                notifyAction.Balloon.TitleText = Substitute(notifyAction.Balloon.TitleText, replacements);
                notifyAction.Balloon.BodyText = Substitute(notifyAction.Balloon.BodyText, replacements);
                // The balloon keeps the event type it was parsed/edited with, which is what decides
                // the popup colour when the event really fires.
                notifyAction.Balloon.Icon = ResolveIconName() ?? notifyAction.Balloon.Icon;
                if (notifyAction.Balloon.DisplayTime < 500) { notifyAction.Balloon.DisplayTime = 3000; }
            }
            else
            {
                notifyAction.Balloon = new Balloon(LP.CombatNotificationsSection, Name, 3000, EventTypeOf(_event))
                {
                    Icon = ResolveIconName()
                };
            }

            if (notifyAction.Sound is TextToSpeech tts)
            {
                tts.Text = Substitute(tts.Text, replacements);
            }

            App.HudContainer.Notifications.AddNotification(
                new NotifyFlashMessage(notifyAction.Sound, notifyAction.Balloon, _event.Priority));
        }

        private static string Substitute(string text, Dictionary<string, string> replacements)
        {
            if (string.IsNullOrEmpty(text)) { return text; }
            foreach (var (placeholder, value) in replacements)
            {
                text = text.Replace(placeholder, value);
            }
            return text;
        }

        private Dictionary<string, string> BuildReplacements()
        {
            var replacements = new Dictionary<string, string>
            {
                { "{player_name}", PlayerName() },
                { "{time_left}", "0" },
                { "{boss_hp}", "100" },
                { "{next_hp}", "90" },
                { "{stack}", "1" },
            };

            switch (_event)
            {
                case AbnormalityEvent abnormality:
                    replacements["{abnormality_name}"] = Name;
                    if (_abnormalityId > 0 && abnormality.Ids.TryGetValue(_abnormalityId, out var stack))
                    {
                        replacements["{stack}"] = Math.Max(1, stack).ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case CooldownEvent cooldown:
                    replacements["{skill_name}"] = Name;
                    replacements["{skill_id}"] = cooldown.SkillId.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            return replacements;
        }

        private static string PlayerName()
        {
            try { return PacketProcessor.Instance?.EntityTracker?.MeterUser?.Name ?? LP.CombatNotifyTestPlayer; }
            catch { return LP.CombatNotifyTestPlayer; }
        }

        /// <summary>
        /// Only used for events whose notify action has no balloon of its own. Mirrors what the
        /// events parser stamps on a balloon it does build.
        /// </summary>
        private static EventType EventTypeOf(Event ev)
        {
            return ev switch
            {
                AbnormalityEvent ab => ab.Trigger == AbnormalityTriggerType.MissingDuringFight
                    ? EventType.MissingAb
                    : EventType.AddRemoveAb,
                CooldownEvent => EventType.Cooldown,
                _ => EventType.AFK
            };
        }

        private string ResolveIconName()
        {
            return _event switch
            {
                AbnormalityEvent when _abnormalityId > 0 => GameNames.AbnormalityIcon(_abnormalityId),
                CooldownEvent cooldown => GameNames.SkillIcon(cooldown.SkillId, CombatNotificationRows.PlayerClassForNames()),
                _ => null
            };
        }
    }

    /// <summary>
    /// Shared by every row of one event, because the ids of an event are stored as one
    /// <see cref="Event.Active"/> flag plus a set of ids that are off.
    /// <para>
    /// The rule both ways is: Active=false means every id is off and the disabled set is empty, so
    /// Active=true always leaves at least one id on. Checking a row of an inactive event therefore
    /// activates the event and turns its other ids off, and unchecking the last enabled row
    /// deactivates the event instead of listing every id as disabled.
    /// </para>
    /// </summary>
    public class CombatEventToggle
    {
        private readonly Event _event;
        private readonly List<string> _tokens;
        private readonly Action _onChanged;

        public event Action Changed;

        public CombatEventToggle(Event ev, IEnumerable<string> tokens, Action onChanged)
        {
            _event = ev;
            _tokens = tokens?.ToList() ?? new List<string>();
            _onChanged = onChanged;
        }

        public bool IsEnabled(string token)
        {
            if (!_event.Active) { return false; }
            return string.IsNullOrEmpty(token) || !_event.IsTokenDisabled(token);
        }

        public void Set(string token, bool enabled)
        {
            if (string.IsNullOrEmpty(token) || _tokens.Count == 0)
            {
                _event.Active = enabled;
                _event.DisabledIds.Clear();
            }
            else if (enabled)
            {
                if (_event.Active) { _event.DisabledIds.Remove(token); }
                else
                {
                    // Everything was off, so turning this one on must not drag the others back in.
                    _event.Active = true;
                    _event.DisabledIds.Clear();
                    foreach (var other in _tokens.Where(x => !string.Equals(x, token, StringComparison.OrdinalIgnoreCase)))
                    {
                        _event.DisabledIds.Add(other);
                    }
                }
            }
            else
            {
                if (!_event.Active) { return; }
                _event.DisabledIds.Add(token);
                if (_tokens.All(_event.IsTokenDisabled))
                {
                    _event.Active = false;
                    _event.DisabledIds.Clear();
                }
            }

            Changed?.Invoke();
            _onChanged?.Invoke();
        }
    }

    /// <summary>
    /// Turns the loaded event sets into the grouped, one-row-per-id list the Events tab shows.
    /// </summary>
    public static class CombatNotificationRows
    {
        /// <summary>
        /// Class the skill names are looked up with: the logged in character when there is one,
        /// otherwise the class whose events file is loaded (Common before login).
        /// </summary>
        public static PlayerClass PlayerClassForNames()
        {
            try
            {
                var meterUser = PacketProcessor.Instance?.EntityTracker?.MeterUser;
                if (meterUser != null) { return meterUser.RaceGenderClass.Class; }
            }
            catch { /* the packet processor is not running yet */ }

            return BasicTeraData.Instance.EventsData.CurrentClass;
        }

        /// <summary>One row per id of <paramref name="ev"/>, or a single row when it has none.</summary>
        public static IEnumerable<CombatNotificationVM> Build(Event ev, List<DataAction> actions, Action onToggled)
        {
            switch (ev)
            {
                case AbnormalityEvent abnormality:
                    return BuildAbnormalityRows(abnormality, actions, onToggled);
                case CooldownEvent cooldown:
                    return BuildCooldownRows(cooldown, actions, onToggled);
                default:
                    return Enumerable.Empty<CombatNotificationVM>();
            }
        }

        private static IEnumerable<CombatNotificationVM> BuildAbnormalityRows(AbnormalityEvent ev, List<DataAction> actions, Action onToggled)
        {
            var typeLabel = TriggerLabel(ev.Trigger);
            var tokens = EventsData.AbnormalityTokens(ev).ToList();
            var toggle = new CombatEventToggle(ev, tokens, onToggled);

            if (tokens.Count == 0)
            {
                yield return new CombatNotificationVM(ev, actions, toggle, null, 0, LP.CombatNotifyNoAbnormality, typeLabel, null);
                yield break;
            }

            foreach (var id in ev.Ids.Keys)
            {
                var token = id.ToString(CultureInfo.InvariantCulture);
                yield return new CombatNotificationVM(ev, actions, toggle, token, id, AbnormalityName(ev, id), typeLabel, GameNames.AbnormalityIcon(id));
            }

            foreach (var type in ev.Types)
            {
                yield return new CombatNotificationVM(ev, actions, toggle, type.ToString(), 0, type.ToString(), typeLabel, null);
            }
        }

        private static IEnumerable<CombatNotificationVM> BuildCooldownRows(CooldownEvent ev, List<DataAction> actions, Action onToggled)
        {
            var typeLabel = ev.OnlyResetted ? LP.CombatNotifyCooldownReset : LP.CombatNotifyCooldown;
            var token = ev.SkillId > 0 ? ev.SkillId.ToString(CultureInfo.InvariantCulture) : null;
            var toggle = new CombatEventToggle(ev, token == null ? Enumerable.Empty<string>() : new[] { token }, onToggled);
            var playerClass = PlayerClassForNames();
            var name = ev.SkillId > 0
                ? GameNames.SkillName(ev.SkillId, playerClass) ?? Fallback(ev, ev.SkillId.ToString(CultureInfo.InvariantCulture))
                : LP.CombatNotifyAnySkill;

            yield return new CombatNotificationVM(ev, actions, toggle, token, 0, name, typeLabel, GameNames.SkillIcon(ev.SkillId, playerClass));
        }

        /// <summary>
        /// Database name first, then the xml comment that names the event in the shipped files, then
        /// the raw id. The id is on the row's second line either way, so nothing is lost.
        /// </summary>
        private static string AbnormalityName(AbnormalityEvent ev, int id)
        {
            return GameNames.AbnormalityName(id) ?? Fallback(ev, id.ToString(CultureInfo.InvariantCulture));
        }

        private static string Fallback(Event ev, string id)
        {
            return string.IsNullOrWhiteSpace(ev.Comment) ? id : ev.Comment;
        }

        private static string TriggerLabel(AbnormalityTriggerType trigger)
        {
            return trigger switch
            {
                AbnormalityTriggerType.Added => LP.CombatNotifyApplied,
                AbnormalityTriggerType.Removed => LP.CombatNotifyRemoved,
                AbnormalityTriggerType.Ending => LP.CombatNotifyExpiring,
                _ => LP.CombatNotifyMissing
            };
        }
    }
}
