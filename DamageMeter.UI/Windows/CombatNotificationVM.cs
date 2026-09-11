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
using Tera.Game;
using DataAction = Data.Actions.Action;

namespace DamageMeter.UI.Windows
{
    /// <summary>
    /// One row of the "Combat notifications" list in the Events settings tab.
    /// Wraps a data-driven combat event (resources/config/events/events-common.xml and
    /// events-&lt;class&gt;.xml) with a readable label, a checkbox bound to its Active flag and a
    /// Test button that replays the event's own balloon/sound so the user hears the real thing.
    /// Only the opt-out flag is exposed here: content and triggers stay in the Events editor.
    /// </summary>
    public class CombatNotificationVM : TSPropertyChanged
    {
        private const int MaxNamesInLabel = 3;

        private readonly Event _event;
        private readonly List<DataAction> _actions;
        private readonly Action _onToggled;
        private bool _isOn;

        public string Label { get; }
        public string Details { get; }
        public bool CanTest => _actions.OfType<NotifyAction>().Any();
        public ICommand TestCommand { get; }

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) { return; }
                _isOn = value;
                _event.Active = value;
                NotifyPropertyChanged();
                _onToggled?.Invoke();
            }
        }

        public CombatNotificationVM(Event ev, List<DataAction> actions, Action onToggled)
        {
            _event = ev;
            _actions = actions ?? new List<DataAction>();
            _onToggled = onToggled;
            _isOn = ev.Active;
            Label = BuildLabel(ev);
            Details = BuildDetails(ev);
            TestCommand = new RelayCommand(_ => Test());
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

            var replacements = BuildReplacements(_event);

            if (notifyAction.Balloon != null)
            {
                notifyAction.Balloon.TitleText = Substitute(notifyAction.Balloon.TitleText, replacements);
                notifyAction.Balloon.BodyText = Substitute(notifyAction.Balloon.BodyText, replacements);
                // The balloon keeps the event type it was parsed/edited with, which is what decides
                // the popup colour when the event really fires.
                notifyAction.Balloon.Icon = ResolveIcon(_event) ?? notifyAction.Balloon.Icon;
                if (notifyAction.Balloon.DisplayTime < 500) { notifyAction.Balloon.DisplayTime = 3000; }
            }
            else
            {
                notifyAction.Balloon = new Balloon(LP.CombatNotificationsSection, Label, 3000, EventTypeOf(_event))
                {
                    Icon = ResolveIcon(_event)
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

        private static Dictionary<string, string> BuildReplacements(Event ev)
        {
            var playerName = PlayerName();
            var replacements = new Dictionary<string, string>
            {
                { "{player_name}", playerName },
                { "{time_left}", "0" },
                { "{boss_hp}", "100" },
                { "{next_hp}", "90" },
                { "{stack}", "1" },
            };

            switch (ev)
            {
                case AbnormalityEvent abnormality:
                    replacements["{abnormality_name}"] = FirstAbnormalityName(abnormality);
                    if (abnormality.Ids.Count > 0)
                    {
                        replacements["{stack}"] = Math.Max(1, abnormality.Ids.First().Value).ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case CooldownEvent cooldown:
                    replacements["{skill_name}"] = SkillName(cooldown.SkillId);
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

        private static string ResolveIcon(Event ev)
        {
            switch (ev)
            {
                case AbnormalityEvent abnormality when abnormality.Ids.Count > 0:
                    return BasicTeraData.Instance.HotDotDatabase?.Get(abnormality.Ids.First().Key)?.EffectIcon;
                case CooldownEvent cooldown:
                    return ResolveSkill(cooldown.SkillId)?.IconName;
                default:
                    return null;
            }
        }

        private static string BuildLabel(Event ev)
        {
            switch (ev)
            {
                case AbnormalityEvent abnormality:
                    return $"{TriggerLabel(abnormality.Trigger)}: {AbnormalityNames(abnormality)}";
                case CooldownEvent cooldown:
                    var prefix = cooldown.OnlyResetted ? LP.CombatNotifyCooldownReset : LP.CombatNotifyCooldown;
                    return $"{prefix}: {SkillName(cooldown.SkillId)}";
                default:
                    return ev.GetType().Name;
            }
        }

        private static string BuildDetails(Event ev)
        {
            var lines = new List<string> { $"{LP.CombatNotifyPriority}: {ev.Priority}" };
            lines.Add(ev.InGame ? LP.CombatNotifyInGame : LP.CombatNotifyOutOfGame);
            if (ev.OutOfCombat) { lines.Add(LP.CombatNotifyOutOfCombat); }

            if (ev is AbnormalityEvent abnormality)
            {
                lines.Add($"{LP.CombatNotifyTarget}: {abnormality.Target}");
                var ids = abnormality.Ids.Keys.Select(x => x.ToString(CultureInfo.InvariantCulture))
                    .Concat(abnormality.Types.Select(x => x.ToString()))
                    .ToList();
                if (ids.Count > 0) { lines.Add("ID: " + string.Join(", ", ids)); }
            }
            else if (ev is CooldownEvent cooldown && cooldown.SkillId > 0)
            {
                lines.Add($"ID: {cooldown.SkillId}");
            }

            return string.Join("\n", lines);
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

        private static string AbnormalityNames(AbnormalityEvent abnormality)
        {
            var names = abnormality.Ids.Keys
                .Select(AbnormalityName)
                .Concat(abnormality.Types.Select(x => x.ToString()))
                .Distinct()
                .ToList();

            if (names.Count == 0) { return LP.CombatNotifyNoAbnormality; }
            return names.Count <= MaxNamesInLabel
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(MaxNamesInLabel)) + $" (+{names.Count - MaxNamesInLabel})";
        }

        private static string FirstAbnormalityName(AbnormalityEvent abnormality)
        {
            return abnormality.Ids.Count > 0
                ? AbnormalityName(abnormality.Ids.First().Key)
                : LP.CombatNotifyNoAbnormality;
        }

        private static string AbnormalityName(int id)
        {
            var name = BasicTeraData.Instance.HotDotDatabase?.Get(id)?.Name;
            return string.IsNullOrWhiteSpace(name) ? id.ToString(CultureInfo.InvariantCulture) : name;
        }

        private static string SkillName(int skillId)
        {
            if (skillId <= 0) { return LP.CombatNotifyAnySkill; }
            var name = ResolveSkill(skillId)?.Name;
            return string.IsNullOrWhiteSpace(name) ? skillId.ToString(CultureInfo.InvariantCulture) : name;
        }

        private static Tera.Game.Skill ResolveSkill(int skillId)
        {
            if (skillId <= 0) { return null; }
            var database = BasicTeraData.Instance.SkillDatabase;
            if (database == null) { return null; }

            try
            {
                var meterUser = PacketProcessor.Instance?.EntityTracker?.MeterUser;
                if (meterUser != null) { return database.GetOrNull(meterUser, skillId); }
            }
            catch { /* the packet processor is not running yet */ }

            var playerClass = BasicTeraData.Instance.EventsData.CurrentClass;
            return database.GetOrNull(new RaceGenderClass(Race.Common, Gender.Common, playerClass), skillId);
        }
    }
}
