using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Data.Actions.Notify;
using Data.Actions.Notify.SoundElements;
using Data.Events;
using Data.Events.Abnormality;
using Lang;
using Tera.Game;
using Action = Data.Actions.Action;

namespace Data
{
    public enum EventType
    {
        MissingAb,
        AddRemoveAb,
        Cooldown,
        AFK,
        Whisper,
        MatchingSuccess,
        ReadyCheck,
        OtherUserApply,
        Broker,
        PartyInvite,
        Trade,
        GenericContract,
        VanguardCredits,
        WakeUp,
        Mention
    }
    public class EventsData
    {
        private readonly BasicTeraData _basicData;

        /// <summary>
        /// Load() runs on the packet thread (login / class change) while Save() and
        /// RefreshActiveEvents() run on the UI thread, so the class events and the class they belong
        /// to are read and published as one unit. The collections NotifyProcessor iterates are still
        /// read without the lock: they are swapped by reference, never mutated in place.
        /// </summary>
        private readonly object _sync = new object();

        /// <summary>Toggle state (Active + disabled ids) of every class event as it was read from events-&lt;class&gt;.xml.</summary>
        private Dictionary<Event, string> _classStateOnDisk = new Dictionary<Event, string>();

        public EventsData(BasicTeraData basicData)
        {
            _basicData = basicData;
            EventsCommon = new Dictionary<Event, List<Action>>();
            var eventsdir = Path.Combine(_basicData.ResourceDirectory, "config/events");
            try
            {
                Directory.CreateDirectory(eventsdir);
                foreach (var pclass in Enum.GetNames(typeof(PlayerClass)))
                {
                    var fname = Path.Combine(_basicData.ResourceDirectory, "config/events/events-" + pclass.ToLowerInvariant() + ".xml");
                    if (!File.Exists(fname)) { File.WriteAllText(fname, LP.ResourceManager.GetString("events_" + pclass.ToLowerInvariant()), Encoding.UTF8); }
                }
            }
            catch (Exception ex) { BasicTeraData.LogError(ex.Message, true); }
            var windowFile = Path.Combine(_basicData.ResourceDirectory, "config/events/events-common.xml");
            XDocument xml;

            try
            {
                var filestreamCommon = new FileStream(windowFile, FileMode.Open, FileAccess.Read);
                xml = XDocument.Load(filestreamCommon);
            }
            catch (Exception ex) when (ex is XmlException || ex is InvalidOperationException)
            {
                BasicTeraData.LogError(ex.Message, true, true);
                Save();
                return;
            }
            catch (Exception ex)
            {
                BasicTeraData.LogError(ex.Message, true, true);
                return;
            }
            EventsClass = new Dictionary<Event, List<Action>>();
            ParseAbnormalities(EventsCommon, xml);
            ParseCooldown(EventsCommon, xml);
            ParseCommonAFK(EventsCommon, xml);
            RefreshActiveEvents();
        }

        // loaded from file - made public for VM
        public Dictionary<Event, List<Action>> EventsCommon { get; }
        public Dictionary<Event, List<Action>> EventsClass { get; set; } = new Dictionary<Event, List<Action>>();

        // used by NotifyProcessor
        public Dictionary<AbnormalityEvent, List<Action>> MissingAbnormalities { get; private set; } = new Dictionary<AbnormalityEvent, List<Action>>();
        public Dictionary<AbnormalityEvent, List<Action>> AddedRemovedAbnormalities { get; private set; } = new Dictionary<AbnormalityEvent, List<Action>>();
        public Dictionary<Event, List<Action>> Events { get; private set; } = new Dictionary<Event, List<Action>>();
        public Dictionary<Event, List<Action>> Cooldown { get; private set; } = new Dictionary<Event, List<Action>>();
        public Tuple<Event, List<Action>> AFK { get; private set; }

        /// <summary>
        /// Class whose events-&lt;class&gt;.xml is currently loaded into <see cref="EventsClass"/>.
        /// Stays <see cref="PlayerClass.Common"/> until the meter user logs in.
        /// </summary>
        public PlayerClass CurrentClass { get; private set; } = PlayerClass.Common;


        public void Load(PlayerClass playerClass)
        {
            var windowFile = Path.Combine(_basicData.ResourceDirectory, "config/events/events-" + playerClass.ToString().ToLowerInvariant() + ".xml");
            XDocument xml;
            try
            {
                var filestreamClass = new FileStream(windowFile, FileMode.Open, FileAccess.Read);
                xml = XDocument.Load(filestreamClass);
            }
            catch (Exception ex) when (ex is XmlException || ex is InvalidOperationException)
            {
                BasicTeraData.LogError(ex.Message, true, true);
                Save();
                return;
            }
            catch (Exception ex)
            {
                BasicTeraData.LogError(ex.Message, true, true);
                return;
            }
            // Parse into a private dictionary first: a parse error midway through must not leave a
            // half filled set published under the previously loaded class, which Save() would then
            // write over that class' file. This also keeps the settings window from enumerating the
            // set while this (packet) thread is still filling it.
            var classEvents = new Dictionary<Event, List<Action>>();
            ParseAbnormalities(classEvents, xml);
            ParseCooldown(classEvents, xml);
            lock (_sync)
            {
                EventsClass = classEvents;
                CurrentClass = playerClass;
                _classStateOnDisk = SnapshotState(classEvents);
                RefreshActiveEventsCore();
            }
        }

        /// <summary>
        /// The collections NotifyProcessor iterates are built once, at load time, from the events whose
        /// Active flag was set then. Toggling Active later (Events editor, or the combat notification
        /// checkboxes in the settings window) would otherwise only take effect after a restart, so
        /// rebuild them from the full EventsCommon/EventsClass sets. Event instances are reused, so
        /// per-event state such as NextChecks survives.
        /// </summary>
        public void RefreshActiveEvents()
        {
            lock (_sync) { RefreshActiveEventsCore(); }
        }

        private void RefreshActiveEventsCore()
        {
            if (EventsCommon == null) { return; }

            var active = new ActiveEvents();
            AssociateEvent(EventsCommon, CurrentClass, active);
            if (EventsClass != null) { AssociateEvent(EventsClass, CurrentClass, active); }

            // The packet thread enumerates these through the properties, so publish fully built
            // collections by reference swap instead of clearing and refilling the live ones.
            MissingAbnormalities = active.MissingAbnormalities;
            AddedRemovedAbnormalities = active.AddedRemovedAbnormalities;
            Cooldown = active.Cooldown;
            Events = active.Events;
            AFK = active.AFK;
        }

        private sealed class ActiveEvents
        {
            public readonly Dictionary<AbnormalityEvent, List<Action>> MissingAbnormalities = new Dictionary<AbnormalityEvent, List<Action>>();
            public readonly Dictionary<AbnormalityEvent, List<Action>> AddedRemovedAbnormalities = new Dictionary<AbnormalityEvent, List<Action>>();
            public readonly Dictionary<Event, List<Action>> Cooldown = new Dictionary<Event, List<Action>>();
            public readonly Dictionary<Event, List<Action>> Events = new Dictionary<Event, List<Action>>();
            public Tuple<Event, List<Action>> AFK;
        }

        private static void AssociateEvent(Dictionary<Event, List<Action>> rootEvents, PlayerClass playerClass, ActiveEvents target)
        {
            foreach (var e in rootEvents)
            {
                if (!e.Key.Active) { continue; }
                if (playerClass != PlayerClass.Common && e.Key.IgnoreClasses.Contains(playerClass)) { continue; }

                // Ids the user unchecked one by one are dropped here, so nothing downstream of this
                // method (trigger logic, notify content) has to know about them.
                var published = WithoutDisabledIds(e.Key);
                if (published == null) { continue; }

                target.Events.Add(published, e.Value);
                var evAbnormalities = published as AbnormalityEvent;
                if (evAbnormalities != null)
                {
                    if (evAbnormalities.Trigger == AbnormalityTriggerType.MissingDuringFight || evAbnormalities.Trigger == AbnormalityTriggerType.Ending)
                    {
                        target.MissingAbnormalities.Add(evAbnormalities, e.Value);
                    }
                    else { target.AddedRemovedAbnormalities.Add(evAbnormalities, e.Value); }
                }

                var evCooldown = published as CooldownEvent;
                if (evCooldown != null) { target.Cooldown.Add(published, e.Value); }

                var evAFK = published as CommonAFKEvent;
                if (evAFK != null) { target.AFK = new Tuple<Event, List<Action>>(published, e.Value); }
            }
        }

        /// <summary>
        /// The event instance the notify processor should see, given the ids the user unchecked
        /// individually in the settings window.
        /// <list type="bullet">
        /// <item>Nothing unchecked: the very same instance, so identity and per-event state such as
        /// NextChecks are untouched - this is the normal case.</item>
        /// <item>Some ids unchecked: a shallow copy carrying only the enabled ids. It shares the
        /// NextChecks dictionary with the original, so rewarn timers keep ticking across a refresh.</item>
        /// <item>Every id unchecked: null, i.e. the same as Active=false. The settings window keeps
        /// Active and the disabled set consistent so this should not happen, but a hand written file
        /// can still say it.</item>
        /// </list>
        /// </summary>
        public static Event WithoutDisabledIds(Event ev)
        {
            if (ev.DisabledIds.Count == 0) { return ev; }

            if (ev is CooldownEvent cooldown) { return cooldown.IsIdDisabled(cooldown.SkillId) ? null : cooldown; }

            if (ev is AbnormalityEvent abnormality)
            {
                var ids = abnormality.Ids.Where(x => !abnormality.IsIdDisabled(x.Key)).ToDictionary(x => x.Key, x => x.Value);
                var types = abnormality.Types.Where(x => !abnormality.IsTokenDisabled(x.ToString())).ToList();
                if (ids.Count == 0 && types.Count == 0) { return null; }
                if (ids.Count == abnormality.Ids.Count && types.Count == abnormality.Types.Count) { return abnormality; }

                return new AbnormalityEvent(abnormality.InGame, abnormality.Active, abnormality.Priority, abnormality.AreaBossBlackList, ids, types,
                    abnormality.Target, abnormality.Trigger, abnormality.RemainingSecondBeforeTrigger, abnormality.RewarnTimeoutSeconds, abnormality.OutOfCombat,
                    abnormality.IgnoreClasses) { NextChecks = abnormality.NextChecks, Comment = abnormality.Comment };
            }

            return ev;
        }

        public void Save()
        {
            lock (_sync)
            {
                SaveEvents(EventsCommon, "events-common.xml");
                // Class events are editable too (their Active flag and their per-id checkboxes back the
                // "Combat notifications" list), so they have to round-trip to their own file or the
                // toggles would be silently reverted by the next Load().
                // Only when something actually differs from what was loaded, though: Save() runs on
                // every exit even when nothing in there was touched.
                if (CurrentClass != PlayerClass.Common && EventsClass != null && EventsClass.Count > 0 && ClassToggleStateChanged())
                {
                    SaveEvents(EventsClass, "events-" + CurrentClass.ToString().ToLowerInvariant() + ".xml");
                    _classStateOnDisk = SnapshotState(EventsClass);
                }
            }
        }

        private static Dictionary<Event, string> SnapshotState(Dictionary<Event, List<Action>> events)
        {
            var snapshot = new Dictionary<Event, string>();
            foreach (var e in events) { snapshot[e.Key] = ToggleStateKey(e.Key); }
            return snapshot;
        }

        /// <summary>Active flag plus the unchecked ids: everything this app changes on a class event.</summary>
        private static string ToggleStateKey(Event ev)
        {
            return ev.Active + "|" + string.Join(",", ev.DisabledIds.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// True when a class event was enabled, disabled, or had one of its ids unchecked since
        /// events-&lt;class&gt;.xml was read.
        /// </summary>
        private bool ClassToggleStateChanged()
        {
            if (_classStateOnDisk.Count != EventsClass.Count) { return true; }
            foreach (var e in EventsClass)
            {
                if (!_classStateOnDisk.TryGetValue(e.Key, out var wasState) || wasState != ToggleStateKey(e.Key)) { return true; }
            }
            return false;
        }

        private void SaveEvents(Dictionary<Event, List<Action>> events, string fileName)
        {
            if (events == null) { return; }
            var eventsDir = Path.Combine(_basicData.ResourceDirectory, "config/events");
            Directory.CreateDirectory(eventsDir);

            var root = new XElement("events",
                new XAttribute("active", true),
                new XAttribute("priority", 5));

            foreach (var eventActions in events)
            {
                // The comment is the only human readable name some events have, and the combat
                // notification list falls back to it, so write it back instead of dropping it.
                var comment = SerializeComment(eventActions.Key.Comment);
                if (comment != null) { root.Add(comment); }
                root.Add(SerializeEvent(eventActions.Key, eventActions.Value));
            }

            var xml = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
            var file = Path.Combine(eventsDir, fileName);
            File.WriteAllText(file, xml.Declaration + Environment.NewLine + xml, Encoding.UTF8);
        }

        public void AddCommonEvent(Event ev, List<Action> actions)
        {
            EventsCommon.Add(ev, actions);
            RefreshActiveEvents();
        }

        public void RemoveCommonEvent(Event ev)
        {
            if (!EventsCommon.Remove(ev)) { return; }
            RefreshActiveEvents();
        }

        public void ResetCommonToDefault()
        {
            var eventsDir = Path.Combine(_basicData.ResourceDirectory, "config/events");
            Directory.CreateDirectory(eventsDir);
            var file = Path.Combine(eventsDir, "events-common.xml");
            File.WriteAllText(file, LP.events_common, Encoding.UTF8);

            EventsCommon.Clear();

            var xml = XDocument.Parse(LP.events_common);
            ParseAbnormalities(EventsCommon, xml);
            ParseCooldown(EventsCommon, xml);
            ParseCommonAFK(EventsCommon, xml);
            RefreshActiveEvents();
        }

        private XElement SerializeEvent(Event ev, List<Action> actions)
        {
            if (ev is CommonAFKEvent)
            {
                return new XElement("common_afk",
                    new XAttribute("active", ev.Active),
                    SerializeActions(actions));
            }

            if (ev is CooldownEvent cooldown)
            {
                var cooldownElement = new XElement("cooldown",
                    new XAttribute("active", cooldown.Active),
                    new XAttribute("ingame", cooldown.InGame),
                    new XAttribute("priority", cooldown.Priority),
                    new XAttribute("skill_id", cooldown.SkillId),
                    new XAttribute("only_resetted", cooldown.OnlyResetted));
                AddDisabledIds(cooldownElement, cooldown, new[] { cooldown.SkillId.ToString(CultureInfo.InvariantCulture) });
                cooldownElement.Add(SerializeActions(actions));
                return cooldownElement;
            }

            if (ev is AbnormalityEvent abnormality)
            {
                var element = new XElement("abnormality",
                    new XAttribute("active", abnormality.Active),
                    new XAttribute("ingame", abnormality.InGame),
                    new XAttribute("priority", abnormality.Priority),
                    new XAttribute("target", abnormality.Target),
                    new XAttribute("trigger", abnormality.Trigger),
                    new XAttribute("out_of_combat", abnormality.OutOfCombat));

                if (abnormality.IgnoreClasses.Any())
                {
                    element.Add(new XAttribute("ignore_classes", string.Join(",", abnormality.IgnoreClasses)));
                }

                if (abnormality.Trigger == AbnormalityTriggerType.MissingDuringFight || abnormality.Trigger == AbnormalityTriggerType.Ending)
                {
                    element.Add(new XAttribute("remaining_seconds_before_trigger", abnormality.RemainingSecondBeforeTrigger));
                    element.Add(new XAttribute("rewarn_timeout_seconds", abnormality.RewarnTimeoutSeconds));
                }

                AddDisabledIds(element, abnormality, AbnormalityTokens(abnormality));

                element.Add(SerializeAreaBossBlacklist(abnormality.AreaBossBlackList));
                element.Add(SerializeAbnormalities(abnormality));
                element.Add(SerializeActions(actions));
                return element;
            }

            throw new ArgumentOutOfRangeException(nameof(ev), ev.GetType().Name, "Unsupported event type");
        }

        /// <summary>Every id/category of an abnormality event, spelled the way the xml spells it.</summary>
        public static IEnumerable<string> AbnormalityTokens(AbnormalityEvent abnormality)
        {
            return abnormality.Ids.Keys.Select(x => x.ToString(CultureInfo.InvariantCulture))
                .Concat(abnormality.Types.Select(x => x.ToString()));
        }

        private static void AddDisabledIds(XElement element, Event ev, IEnumerable<string> knownTokens)
        {
            var value = SerializeDisabledIds(ev, knownTokens);
            if (value != null) { element.SetAttributeValue("disabled_ids", value); }
        }

        /// <summary>
        /// The disabled_ids attribute value for an event, or null when there is nothing to write:
        /// the ids the user unchecked individually, pruned to the ids the event still has (the events
        /// editor can delete one) and ordered like the event's own ids so the file stays stable.
        /// </summary>
        public static string SerializeDisabledIds(Event ev, IEnumerable<string> knownTokens)
        {
            if (ev.DisabledIds.Count == 0) { return null; }
            var kept = knownTokens.Where(ev.IsTokenDisabled).ToList();
            return kept.Count == 0 ? null : string.Join(",", kept);
        }

        /// <summary>XComment rejects "--" and a trailing "-", so keep the text but not those.</summary>
        private static XComment SerializeComment(string comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) { return null; }
            var text = comment.Trim().Replace("--", "-");
            return new XComment(" " + text + " ");
        }

        private static XElement SerializeAreaBossBlacklist(List<BlackListItem> blacklist)
        {
            var element = new XElement("area_boss_blacklist");
            foreach (var item in blacklist)
            {
                element.Add(new XElement("blacklist",
                    new XAttribute("area_id", item.AreaId),
                    new XAttribute("boss_id", item.BossId)));
            }
            return element;
        }

        private static XElement SerializeAbnormalities(AbnormalityEvent abnormality)
        {
            var element = new XElement("abnormalities");
            foreach (var id in abnormality.Ids)
            {
                element.Add(new XElement("abnormality",
                    new XAttribute("stack", id.Value),
                    id.Key));
            }
            foreach (var type in abnormality.Types)
            {
                element.Add(new XElement("abnormality", type));
            }
            return element;
        }

        private static XElement SerializeActions(List<Action> actions)
        {
            var element = new XElement("actions");
            foreach (var action in actions.OfType<NotifyAction>())
            {
                var notify = new XElement("notify");
                if (action.Balloon != null)
                {
                    notify.Add(new XElement("balloon",
                        new XAttribute("title_text", action.Balloon.TitleText),
                        new XAttribute("body_text", action.Balloon.BodyText),
                        new XAttribute("display_time", action.Balloon.DisplayTime)));
                }

                var sound = SerializeSound(action.Sound);
                if (sound != null)
                {
                    notify.Add(sound);
                }
                element.Add(notify);
            }
            return element;
        }

        private static XElement SerializeSound(SoundInterface sound)
        {
            switch (sound)
            {
                case Music music:
                    return new XElement("music",
                        new XAttribute("file", music.File),
                        new XAttribute("volume", (music.Volume * 100).ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("duration", music.Duration));
                case Beeps beeps:
                    var element = new XElement("beeps");
                    foreach (var beep in beeps.BeepList)
                    {
                        element.Add(new XElement("beep",
                            new XAttribute("frequency", beep.Frequency),
                            new XAttribute("duration", beep.Duration)));
                    }
                    return element;
                case TextToSpeech textToSpeech:
                    return new XElement("text_to_speech",
                        new XAttribute("text", textToSpeech.Text),
                        new XAttribute("voice_gender", textToSpeech.VoiceGender),
                        new XAttribute("voice_age", textToSpeech.VoiceAge),
                        new XAttribute("voice_position", textToSpeech.VoicePosition),
                        new XAttribute("culture", textToSpeech.CultureInfo),
                        new XAttribute("volume", textToSpeech.Volume),
                        new XAttribute("rate", textToSpeech.Rate),
                        new XAttribute("enabled", textToSpeech.Enabled));
                default:
                    return null;
            }
        }

        private void ParseCommonAFK(Dictionary<Event, List<Action>> events, XDocument xml)
        {
            var root = xml.Root;
            var default_active = root.Attribute("active")?.Value ?? "True";
            var commonAfk = root.Element("common_afk");
            if (commonAfk == null) { return; }

            var active = bool.Parse(commonAfk.Attribute("active")?.Value ?? default_active);
            var ev = new CommonAFKEvent(active) { Comment = PrecedingComment(commonAfk) };
            events.Add(ev, new List<Action>());
            ParseActions(commonAfk, events, ev, EventType.AFK);
        }


        private void ParseActions(XElement root, Dictionary<Event, List<Action>> events, Event ev, EventType evType)
        {
            foreach (var notify in root.Element("actions").Elements("notify"))
            {
                Balloon ballonData = null;
                var balloon = notify.Element("balloon");
                if (balloon != null)
                {
                    var titleText = balloon.Attribute("title_text").Value;
                    var bodyText = balloon.Attribute("body_text").Value;
                    var displayDuration = int.Parse(balloon.Attribute("display_time").Value);
                    ballonData = new Balloon(titleText, bodyText, displayDuration, evType);
                }

                SoundInterface soundInterface = null;
                var music = notify.Descendants("music");
                var beeps = notify.Descendants("beeps");
                var textToSpeech = notify.Descendants("text_to_speech");
                if (music.Any() && beeps.Any() || music.Any() && textToSpeech.Any() || textToSpeech.Any() && beeps.Any())
                {
                    throw new Exception("Only 1 type of sound allowed by notifyAction");
                }
                if (music.Any())
                {
                    var musicFile = music.First().Attribute("file").Value;
                    var volume = float.Parse(music.First().Attribute("volume").Value, CultureInfo.InvariantCulture) / 100;
                    var duration = int.Parse(music.First().Attribute("duration").Value);
                    soundInterface = new Music(musicFile, volume, duration);
                }
                if (beeps.Any())
                {
                    var beepsList = new List<Beep>();
                    foreach (var beep in beeps.First().Elements())
                    {
                        var frequency = int.Parse(beep.Attribute("frequency").Value);
                        var duration = int.Parse(beep.Attribute("duration").Value);
                        beepsList.Add(new Beep(frequency, duration));
                    }
                    soundInterface = new Beeps(beepsList);
                }

                if (textToSpeech.Any())
                {
                    var tts = textToSpeech.First();
                    var text = tts.Attribute("text").Value;
                    var voiceGender = (VoiceGender)Enum.Parse(typeof(VoiceGender), tts.Attribute("voice_gender")?.Value ?? "Female", true);
                    var voiceAge = (VoiceAge)Enum.Parse(typeof(VoiceAge), tts.Attribute("voice_age")?.Value ?? "Adult", true);

                    var culture = tts.Attribute("culture")?.Value ?? LP.Culture.ToString();
                    var voicePosition = int.Parse(tts.Attribute("voice_position")?.Value ?? "0");
                    var volume = int.Parse(tts.Attribute("volume")?.Value ?? "30");
                    var rate = int.Parse(tts.Attribute("rate")?.Value ?? "0");
                    var enabled = bool.Parse(tts.Attribute("enabled")?.Value ?? "True");
                    soundInterface = new TextToSpeech(text, voiceGender, voiceAge, voicePosition, culture, volume, rate, enabled);
                }

                var notifyAction = new NotifyAction(soundInterface, ballonData);
                events[ev].Add(notifyAction);
            }
        }

        private List<BlackListItem> ParseAreaBossBlackList(XElement root)
        {
            var areaBossBlacklist = new List<BlackListItem>();
            if (root.Element("area_boss_blacklist") == null) { return areaBossBlacklist; }
            foreach (var blacklist in root.Element("area_boss_blacklist").Elements("blacklist"))
            {
                var areaId = int.Parse(blacklist.Attribute("area_id").Value);
                var bossId = int.Parse(blacklist.Attribute("boss_id")?.Value ?? "-1");
                areaBossBlacklist.Add(new BlackListItem { AreaId = areaId, BossId = bossId });
            }
            return areaBossBlacklist;
        }

        /// <summary>
        /// Reads disabled_ids="70221,70211": the ids of this event the user unchecked one by one in
        /// the combat notification list. Unknown or malformed entries are simply kept as written and
        /// matched by string, so a hand edited file never loses anything.
        /// </summary>
        public static void ParseDisabledIds(Event ev, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return; }
            foreach (var token in value.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length > 0) { ev.DisabledIds.Add(trimmed); }
            }
        }

        /// <summary>The xml comment right above an event, which is how the shipped files name them.</summary>
        private static string PrecedingComment(XElement element)
        {
            for (var node = element.PreviousNode; node != null; node = node.PreviousNode)
            {
                if (node is XComment comment) { return comment.Value.Trim(); }
                if (node is XElement) { return null; }
            }
            return null;
        }

        private List<PlayerClass> ParseIgnoreClasses(string str)
        {
            var res = new List<PlayerClass>();
            if (string.IsNullOrWhiteSpace(str)) return res;
            var arr = str.Split(',');
            foreach (var cl in arr)
            {
                if (Enum.TryParse(cl, out PlayerClass pClass)) res.Add(pClass);
            }
            return res;
        }

        private void ParseCooldown(Dictionary<Event, List<Action>> events, XDocument xml)
        {
            var root = xml.Root;
            var default_active = root.Attribute("active")?.Value ?? "True";
            var default_priority = root.Attribute("priority")?.Value ?? "5";
            foreach (var abnormality in root.Elements("cooldown"))
            {
                var skillId = int.Parse(abnormality.Attribute("skill_id")?.Value ?? "0");
                var onlyResetted = bool.Parse(abnormality.Attribute("only_resetted")?.Value ?? "True");
                var active = bool.Parse(abnormality.Attribute("active")?.Value ?? default_active);
                var ingame = bool.Parse(abnormality.Attribute("ingame")?.Value ?? "true");
                var priority = int.Parse(abnormality.Attribute("priority")?.Value ?? default_priority);
                ParseAreaBossBlackList(abnormality);
                var cooldownEvent = new CooldownEvent(ingame, active, priority, skillId, onlyResetted) { Comment = PrecedingComment(abnormality) };
                ParseDisabledIds(cooldownEvent, abnormality.Attribute("disabled_ids")?.Value);
                events.Add(cooldownEvent, new List<Action>());
                ParseActions(abnormality, events, cooldownEvent, EventType.Cooldown);
            }
        }

        private void ParseAbnormalities(Dictionary<Event, List<Action>> events, XDocument xml)
        {
            var root = xml.Root;
            var default_active = root.Attribute("active")?.Value ?? "True";
            var default_priority = root.Attribute("priority")?.Value ?? "5";
            var default_blacklist = ParseAreaBossBlackList(root);
            foreach (var abnormality in root.Elements("abnormality"))
            {
                var ids = new Dictionary<int, int>();
                var types = new List<HotDot.Types>();
                var abnormalities = abnormality.Element("abnormalities");
                foreach (var abnormalityId in abnormalities.Elements("abnormality"))
                {
                    var idElement = abnormalityId.Value;
                    int id;

                    if (int.TryParse(idElement, out id))
                    {
                        var stack = int.Parse(abnormalityId.Attribute("stack")?.Value ?? "0");
                        ids[id] = stack;
                        continue;
                    }
                    HotDot.Types type;
                    if (!Enum.TryParse(idElement, true, out type)) { throw new Exception(idElement + " is not an acceptable value."); }
                    types.Add(type);
                }
                var ingame = bool.Parse(abnormality.Attribute("ingame")?.Value ?? "true");
                var outOfCombat = bool.Parse(abnormality.Attribute("out_of_combat")?.Value ?? "false");
                var active = bool.Parse(abnormality.Attribute("active")?.Value ?? default_active);
                var priority = int.Parse(abnormality.Attribute("priority")?.Value ?? default_priority);
                var ignoreClasses = ParseIgnoreClasses(abnormality.Attribute("ignore_classes")?.Value ?? "");
                AbnormalityTargetType target;
                AbnormalityTriggerType trigger;
                Enum.TryParse(abnormality.Attribute("target").Value, true, out target);
                Enum.TryParse(abnormality.Attribute("trigger").Value, true, out trigger);
                var remainingSecondsBeforeTrigger = 0;
                var rewarnTimeoutSeconds = 0;
                if (trigger == AbnormalityTriggerType.MissingDuringFight || trigger == AbnormalityTriggerType.Ending)
                {
                    remainingSecondsBeforeTrigger = int.Parse(abnormality.Attribute("remaining_seconds_before_trigger").Value);
                    rewarnTimeoutSeconds = int.Parse(abnormality.Attribute("rewarn_timeout_seconds")?.Value ?? "0");
                }
                var blacklist = ParseAreaBossBlackList(abnormality);
                var abnormalityEvent = new AbnormalityEvent(ingame, active, priority, blacklist.Any() ? blacklist : default_blacklist, ids, types, target, trigger,
                    remainingSecondsBeforeTrigger, rewarnTimeoutSeconds, outOfCombat, ignoreClasses) { Comment = PrecedingComment(abnormality) };
                ParseDisabledIds(abnormalityEvent, abnormality.Attribute("disabled_ids")?.Value);
                events.Add(abnormalityEvent, new List<Action>());
                var t = trigger == AbnormalityTriggerType.MissingDuringFight ? EventType.MissingAb : EventType.AddRemoveAb;
                ParseActions(abnormality, events, abnormalityEvent, t);
            }
        }
    }
}
