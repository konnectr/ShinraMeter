using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tera.Game;

namespace Data
{
    /// <summary>
    /// Abnormality and skill names for UI that has to read well before the meter is attached to a
    /// game client.
    /// <para>
    /// <see cref="BasicTeraData.HotDotDatabase"/> and <see cref="BasicTeraData.SkillDatabase"/> are
    /// only filled by <see cref="TeraData"/> once the region is known, which happens on login. Until
    /// then they are null and everything the settings window could show is a raw numeric id. This
    /// loads the very same databases through the very same loader classes, for the language the user
    /// configured, and caches them for the rest of the process.
    /// </para>
    /// <para>
    /// The live databases always win once they exist, and nothing here is ever written back into
    /// <see cref="BasicTeraData"/>, so the packet processor sees exactly what it saw before.
    /// </para>
    /// </summary>
    public static class GameNames
    {
        /// <summary>What <see cref="HotDotDatabase.Get"/> returns for an id it does not know.</summary>
        private const string UnknownHotDotName = "Unknown DOT";

        private static readonly string[] KnownLanguages = { "EU-EN", "EU-FR", "EU-GER", "RU", "JP", "KR", "TW" };
        private static readonly object Sync = new object();

        private static HotDotDatabase _hotDots;
        private static SkillDatabase _skills;
        private static bool _hotDotsLoaded;
        private static bool _skillsLoaded;

        /// <summary>Data language the fallback databases were loaded with, for the error log.</summary>
        public static string FallbackLanguage { get; private set; }

        /// <summary>
        /// Reads the tsv files on a background thread so the first settings window that asks for a
        /// name does not pay for it. Safe to call more than once.
        /// </summary>
        public static void Preload()
        {
            Task.Run(() =>
            {
                try
                {
                    HotDots();
                    Skills();
                }
                catch (Exception ex) { BasicTeraData.LogError("Combat notification names preload: " + ex, true, true); }
            });
        }

        /// <summary>Readable name of an abnormality, or null when no database knows the id.</summary>
        public static string AbnormalityName(int id)
        {
            var hotDot = HotDot(id);
            if (hotDot == null) { return null; }
            return string.IsNullOrWhiteSpace(hotDot.Name) || hotDot.Name == UnknownHotDotName ? null : hotDot.Name;
        }

        /// <summary>Icon name of an abnormality for <see cref="IconsDatabase.GetImage"/>, or null.</summary>
        public static string AbnormalityIcon(int id)
        {
            var hotDot = HotDot(id);
            if (hotDot == null) { return null; }
            var icon = string.IsNullOrWhiteSpace(hotDot.EffectIcon) ? hotDot.IconName : hotDot.EffectIcon;
            return string.IsNullOrWhiteSpace(icon) ? null : icon;
        }

        /// <summary>Readable name of a skill, or null when no database knows the id.</summary>
        public static string SkillName(int skillId, PlayerClass playerClass)
        {
            var skill = Skill(skillId, playerClass);
            return string.IsNullOrWhiteSpace(skill?.Name) ? null : skill.Name;
        }

        /// <summary>Icon name of a skill for <see cref="IconsDatabase.GetImage"/>, or null.</summary>
        public static string SkillIcon(int skillId, PlayerClass playerClass)
        {
            var skill = Skill(skillId, playerClass);
            return string.IsNullOrWhiteSpace(skill?.IconName) ? null : skill.IconName;
        }

        private static HotDot HotDot(int id)
        {
            var database = HotDots();
            // Get() inserts an "Unknown DOT" placeholder for ids it has never seen, so it never throws.
            try { return database?.Get(id); }
            catch { return null; }
        }

        private static Skill Skill(int skillId, PlayerClass playerClass)
        {
            if (skillId <= 0) { return null; }
            var database = Skills();
            if (database == null) { return null; }
            try { return database.GetOrNull(new RaceGenderClass(Race.Common, Gender.Common, playerClass), skillId); }
            catch { return null; }
        }

        private static HotDotDatabase HotDots()
        {
            var live = BasicTeraData.Instance.HotDotDatabase;
            if (live != null) { return live; }

            lock (Sync)
            {
                if (_hotDotsLoaded) { return _hotDots; }
                _hotDotsLoaded = true;
                var language = ResolveLanguage("hotdot", "hotdot-{0}.tsv");
                if (language == null) { return null; }
                try { _hotDots = new HotDotDatabase(DataDirectory, language); }
                catch (Exception ex) { BasicTeraData.LogError("Combat notification names (hotdot-" + language + "): " + ex.Message, true, true); }
                return _hotDots;
            }
        }

        private static SkillDatabase Skills()
        {
            var live = BasicTeraData.Instance.SkillDatabase;
            if (live != null) { return live; }

            lock (Sync)
            {
                if (_skillsLoaded) { return _skills; }
                _skillsLoaded = true;
                var language = ResolveLanguage("skills", "skills-{0}.tsv");
                if (language == null) { return null; }
                try { _skills = new SkillDatabase(DataDirectory, language); }
                catch (Exception ex) { BasicTeraData.LogError("Combat notification names (skills-" + language + "): " + ex.Message, true, true); }
                return _skills;
            }
        }

        private static string DataDirectory => Path.Combine(BasicTeraData.Instance.ResourceDirectory, "data/");

        /// <summary>
        /// First configured language whose data file is actually next to the executable. The app runs
        /// from bin against DamageMeter.UI\Resources, which does not necessarily ship every language.
        /// </summary>
        private static string ResolveLanguage(string folder, string fileNamePattern)
        {
            foreach (var candidate in LanguageCandidates().Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Exists(folder, fileNamePattern, candidate)) { FallbackLanguage = candidate; return candidate; }
            }

            // Last resort: whatever language the install does have.
            foreach (var candidate in KnownLanguages)
            {
                if (Exists(folder, fileNamePattern, candidate)) { FallbackLanguage = candidate; return candidate; }
            }

            BasicTeraData.LogError("Combat notification names: no " + folder + " data found in " + DataDirectory, true, true);
            return null;
        }

        private static bool Exists(string folder, string fileNamePattern, string language)
        {
            try { return File.Exists(Path.Combine(DataDirectory, folder, string.Format(fileNamePattern, language))); }
            catch { return false; }
        }

        private static IEnumerable<string> LanguageCandidates()
        {
            var windowData = BasicTeraData.Instance.WindowData;
            yield return ToDataLanguage(windowData?.Language);
            yield return ToDataLanguage(windowData?.UILanguage);
            yield return ToDataLanguage(CultureInfo.CurrentUICulture?.Name);
            yield return "EU-EN";
        }

        /// <summary>
        /// Maps a region setting ("EUC-EN", "NA", "Auto"...) or a culture ("ru-RU", "de") onto one of
        /// the languages the data folder is keyed by. Null when nothing matches.
        /// </summary>
        private static string ToDataLanguage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            var normalized = value.Trim().Replace('_', '-');
            if (normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase)) { return null; }

            var known = KnownLanguages.FirstOrDefault(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (known != null) { return known; }

            switch (normalized.ToUpperInvariant())
            {
                case "NA":
                case "EUC-EN":
                case "EN":
                case "EN-US":
                case "EN-GB":
                    return "EU-EN";
                case "EUC-FR":
                case "FR":
                case "FR-FR":
                    return "EU-FR";
                case "EUC-GER":
                case "GER":
                case "DE":
                case "DE-DE":
                    return "EU-GER";
                case "JPC":
                case "JA":
                case "JA-JP":
                    return "JP";
                case "KRC":
                case "KR-PTS":
                case "KO":
                case "KO-KR":
                    return "KR";
                case "ZH":
                case "ZH-TW":
                case "ZH-CN":
                    return "TW";
                case "RU-RU":
                    return "RU";
                default:
                    return null;
            }
        }
    }
}
