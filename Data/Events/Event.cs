using System;
using System.Collections.Generic;
using System.Globalization;
using Tera.Game;

namespace Data.Events
{
    public struct BlackListItem
    {
        public int AreaId;
        public int BossId;
    }

    public abstract class Event
    {
        protected Event(bool inGame, bool active, int priority, List<BlackListItem> areaBossBlackList, bool outOfCombat = false, List<PlayerClass> ignoreClasses=null)
        {
            NextChecks = new Dictionary<EntityId, DateTime>();
            InGame = inGame;
            Active = active;
            Priority = priority;
            AreaBossBlackList = areaBossBlackList;
            OutOfCombat = outOfCombat;
            IgnoreClasses = ignoreClasses ?? new List<PlayerClass>();
            DisabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<EntityId, DateTime> NextChecks { get; set; }
        public bool InGame { get; set; }
        public bool Active { get; set; }
        public bool OutOfCombat { get; set; }

        public List<BlackListItem> AreaBossBlackList { get; set; }
        public List<PlayerClass> IgnoreClasses { get; set; }

        public int Priority { get; set; }

        /// <summary>
        /// Ids the user unchecked one by one in Settings &gt; Events &gt; Combat notifications, written
        /// exactly as they appear in the events xml: an abnormality id, a <see cref="HotDot.Types"/>
        /// name or a cooldown skill id. Persisted as disabled_ids="70221,70211" on the event element.
        /// <para>
        /// Active and this set are kept consistent: Active=false always means "every id off" and then
        /// the set is empty, so Active=true always leaves at least one id enabled.
        /// </para>
        /// </summary>
        public HashSet<string> DisabledIds { get; }

        /// <summary>
        /// The xml comment that precedes the event in the shipped events files ("Adrenaline Rush",
        /// "Food"...). For a few ids the abnormality database does not know, it is the only human
        /// readable name there is, so it is parsed, kept and written back out by the serializer.
        /// </summary>
        public string Comment { get; set; }

        public bool IsIdDisabled(int id) => IsTokenDisabled(id.ToString(CultureInfo.InvariantCulture));

        public bool IsTokenDisabled(string token) => token != null && DisabledIds.Count > 0 && DisabledIds.Contains(token);
    }
}
