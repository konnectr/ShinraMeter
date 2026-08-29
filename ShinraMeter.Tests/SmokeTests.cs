using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Data.Actions.Notify.SoundElements;
using Tera;
using Tera.Game;
using Tera.Game.Messages;

namespace ShinraMeter.Tests;

public class SmokeTests
{
    [Fact]
    public void HarnessRuns()
    {
        Assert.Equal(4, 2 + 2);
    }

    [Fact]
    public void EachSkillResult_ClassicPlus10002_ReadsAmountFromToolboxV14Layout()
    {
        var opcodeNamer = new OpCodeNamer(new Dictionary<ushort, string>
        {
            { 1, "S_EACH_SKILL_RESULT" },
        });
        var factory = new MessageFactory(opcodeNamer, "EUC", 387400);
        factory.ReleaseVersion = 10002;

        var message = new Message(
            DateTime.UtcNow,
            MessageDirection.ServerToClient,
            new ArraySegment<byte>(BuildEachSkillResultPacket(opcode: 1, amount: 123L))
        );

        var parsed = (EachSkillResultServerMessage)factory.Create(message);

        Assert.Equal(123L, parsed.Amount);
        Assert.Equal(0x1234567, parsed.SkillId);
        Assert.Equal(7, parsed.HitId);
    }

    [Fact]
    public void LoginArbiter_ClassicPlusAlwaysPinsReleaseVersion10002()
    {
        var opcodeNamer = new OpCodeNamer(new Dictionary<ushort, string>
        {
            { 2, "C_LOGIN_ARBITER" },
        });
        var factory = new MessageFactory(opcodeNamer, "EUC", 387400);
        factory.ReleaseVersion = 9901;

        var message = new Message(
            DateTime.UtcNow,
            MessageDirection.ClientToServer,
            new ArraySegment<byte>(BuildLoginArbiterPacket(opcode: 2, language: 6, fallbackVersion: 2707))
        );

        _ = (C_LOGIN_ARBITER)factory.Create(message);

        Assert.Equal(10002, factory.ReleaseVersion);
    }

    [Fact]
    public void CrestInfo_TruncatedMirrorFrame_IsIgnoredInsteadOfCrashingPacketLoop()
    {
        var opcodeNamer = new OpCodeNamer(new Dictionary<ushort, string>
        {
            { 3, "S_CREST_INFO" },
        });
        var factory = new MessageFactory(opcodeNamer, "EUC", 387400);
        var message = new Message(
            DateTime.UtcNow,
            MessageDirection.ServerToClient,
            new ArraySegment<byte>(BuildCrestInfoPacket(opcode: 3, includeGlyph: false))
        );

        Assert.IsType<UnknownMessage>(factory.Create(message));
    }

    [Fact]
    public void CrestInfo_ValidClassicPlusFrame_StillParsesGlyphs()
    {
        var opcodeNamer = new OpCodeNamer(new Dictionary<ushort, string>
        {
            { 3, "S_CREST_INFO" },
        });
        var factory = new MessageFactory(opcodeNamer, "EUC", 387400);
        var message = new Message(
            DateTime.UtcNow,
            MessageDirection.ServerToClient,
            new ArraySegment<byte>(BuildCrestInfoPacket(opcode: 3, includeGlyph: true))
        );

        var parsed = Assert.IsType<S_CREST_INFO>(factory.Create(message));
        Assert.Equal((uint)55, parsed.PointsMax);
        Assert.Equal((uint)10, parsed.PointsUsed);
        Assert.True(parsed.Glyphs[123456]);
    }

    [Fact]
    public void TeraSniffer_UsesMirrorSocketPath_NotPacketSnifferPath()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "DamageMeter.Sniffing",
                "TeraSniffer.cs"
            )
        );

        Assert.Contains("_socketHost = \"127.0.0.1\"", source);
        Assert.Contains("ConnectToMirrorAsync(CancellationToken token)", source);
        Assert.Contains("for (var port = 7804; port <= 8002; port++)", source);
        Assert.Contains("TryConnectToMirrorPortAsync(7803, token)", source);
        Assert.Contains("Task.WhenAny(connectTask, Task.Delay(MirrorConnectTimeoutMs, token))", source);
        Assert.Contains("if (direction == 1)", source);
        Assert.Contains("else if (direction == 2)", source);
        Assert.DoesNotContain("new TcpSniffer(_ipSniffer)", source);
    }

    [Fact]
    public void ClassicPlusServerId2800_ResolvesToElinu()
    {
        var serverDatabase = new ServerDatabase(ProjectPath("resources", "data"))
        {
            Language = LangEnum.EU_EN,
        };

        Assert.Equal("Elinu", serverDatabase.GetServerName(2800));
    }

    [Fact]
    public void ClassicPlusRuntimeData_MatchesPackagedElinuData()
    {
        var runtimeData = ProjectPath("resources", "data");
        var packagedData = ProjectPath("DamageMeter.UI", "Resources", "data");

        foreach (var relativePath in ClassicPlusDataFiles())
        {
            Assert.Equal(
                ReadNormalizedDataFile(Path.Combine(packagedData, relativePath)),
                ReadNormalizedDataFile(Path.Combine(runtimeData, relativePath))
            );
        }
    }

    [Fact]
    public void ClassicPlusSkillRows_UseShinraClassesAndUniqueRuntimeKeys()
    {
        foreach (var dataRoot in new[]
        {
            ProjectPath("resources", "data"),
            ProjectPath("DamageMeter.UI", "Resources", "data"),
        })
        {
            foreach (var language in ClassicPlusLanguages())
            {
                var seen = new HashSet<string>();
                var path = Path.Combine(dataRoot, "skills", $"skills-{language}.tsv");

                foreach (var line in File.ReadLines(path))
                {
                    var parts = line.Split('\t');
                    Assert.True(parts.Length >= 8, $"{path} has malformed skill row: {line}");
                    Assert.True(Enum.TryParse<PlayerClass>(parts[3], out _), $"{path} has unknown class {parts[3]} in row: {line}");
                    Assert.True(seen.Add(string.Join('\t', parts.Take(4))), $"{path} has duplicate skill key: {line}");
                }
            }
        }
    }

    [Fact]
    public void ClassicPlusHotDotRows_HaveExpectedFieldCount()
    {
        foreach (var dataRoot in new[]
        {
            ProjectPath("resources", "data"),
            ProjectPath("DamageMeter.UI", "Resources", "data"),
        })
        {
            foreach (var language in ClassicPlusLanguages())
            {
                var path = Path.Combine(dataRoot, "hotdot", $"hotdot-{language}.tsv");

                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) { continue; }

                    var parts = line.Split('\t');
                    Assert.True(parts.Length >= 15, $"{path} has malformed hotdot row: {line}");
                    Assert.True(int.TryParse(parts[0], out _), $"{path} has non-numeric hotdot id: {line}");
                    Assert.True(bool.TryParse(parts[3], out _), $"{path} has non-boolean hotdot isBuff: {line}");
                    Assert.True(bool.TryParse(parts[14], out _), $"{path} has non-boolean hotdot isShow: {line}");
                }
            }
        }
    }

    [Fact]
    public void ClassicPlusMonsterRows_ParseAndUseUniqueZoneTemplateKeys()
    {
        foreach (var dataRoot in new[]
        {
            ProjectPath("resources", "data"),
            ProjectPath("DamageMeter.UI", "Resources", "data"),
        })
        {
            foreach (var language in ClassicPlusLanguages())
            {
                var path = Path.Combine(dataRoot, "monsters", $"monsters-{language}.xml");
                var seen = new HashSet<string>();
                var document = XDocument.Load(path);

                foreach (var zone in document.Root!.Elements("Zone"))
                {
                    var zoneId = zone.Attribute("id")?.Value;
                    Assert.False(string.IsNullOrWhiteSpace(zoneId), $"{path} has a zone without id");

                    foreach (var monster in zone.Elements("Monster"))
                    {
                        var monsterId = monster.Attribute("id")?.Value;
                        Assert.False(string.IsNullOrWhiteSpace(monster.Attribute("name")?.Value), $"{path} has a monster without name");
                        Assert.True(int.TryParse(monsterId, out _), $"{path} has non-numeric monster id");
                        Assert.True(long.TryParse(monster.Attribute("hp")?.Value, out _), $"{path} has non-numeric monster hp");
                        Assert.True(int.TryParse(monster.Attribute("speciesId")?.Value, out _), $"{path} has non-numeric monster speciesId");
                        Assert.True(bool.TryParse(monster.Attribute("isBoss")?.Value, out _), $"{path} has non-boolean monster isBoss");
                        Assert.True(seen.Add($"{zoneId}:{monsterId}"), $"{path} has duplicate monster key {zoneId}:{monsterId}");
                    }
                }
            }
        }
    }

    [Fact]
    public void PackagedServerOverrides_IncludeClassicPlusMirror()
    {
        var overrides = File.ReadAllText(ProjectPath("DamageMeter.UI", "Resources", "config", "server-overrides.txt"));

        Assert.Contains("127.0.0.1 EUC Classic+", overrides);
        Assert.Contains("88.99.102.67 EUC Classic+", overrides);
    }

    [Fact]
    public void ClassicPlusElinuArcherOverrides_CoverKnownDamageIds()
    {
        foreach (var dataRoot in new[]
        {
            ProjectPath("resources", "data"),
            ProjectPath("DamageMeter.UI", "Resources", "data"),
        })
        {
            foreach (var skillId in new[] { 80101, 80601, 80700, 81206, 51053 })
            {
                Assert.True(ContainsArcherSkill(dataRoot, "EU-EN", skillId), $"{dataRoot} missing Archer skill {skillId}");
            }
        }
    }

    [Fact]
    public void TextToSpeech_CanBeDisabledPerAlert()
    {
        var tts = new TextToSpeech("Use Nostrum", VoiceGender.Female, VoiceAge.Adult, 0, "en-US", 30, 0, false);

        Assert.False(tts.Enabled);
    }

    [Fact]
    public void EventsParser_ReadsTextToSpeechEnabledAttributeWithDefaultEnabled()
    {
        var source = File.ReadAllText(ProjectPath("Data", "EventsData.cs"));

        Assert.Contains("tts.Attribute(\"enabled\")?.Value ?? \"True\"", source);
        Assert.Contains("new TextToSpeech(text, voiceGender, voiceAge, voicePosition, culture, volume, rate, enabled)", source);
        Assert.Contains("new XAttribute(\"enabled\", textToSpeech.Enabled)", source);
    }

    [Fact]
    public void EventsEditor_ExposesPerTextToSpeechToggle()
    {
        var viewModelSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "TtsDataVM.cs"));
        var editorSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorViewModel.cs"));
        var xamlSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorWindow.xaml"));

        Assert.Contains("public bool Enabled", viewModelSource);
        Assert.Contains("_data.Save();", editorSource);
        Assert.Contains("IsOn=\"{Binding Enabled, Mode=TwoWay}\"", xamlSource);
    }

    [Fact]
    public void EventsEditor_UsesVirtualizedSearchableList()
    {
        var xamlSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorWindow.xaml"));
        var windowSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorWindow.xaml.cs"));

        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xamlSource);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", xamlSource);
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", xamlSource);
        Assert.Contains("ItemsSource=\"{Binding VisibleEvents}\"", xamlSource);
        Assert.Contains("Delay=200", xamlSource);
        Assert.Contains("No events match", xamlSource);
        Assert.Contains("WindowInteropHelper", windowSource);
        Assert.Contains("Topmost =", windowSource);
    }

    [Fact]
    public void EventsEditor_SearchIndexesNotificationAndSoundText()
    {
        var actionSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "ActionVM.cs"));
        var eventSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "BaseEventViewModel.cs"));

        Assert.Contains("public string SearchText", actionSource);
        Assert.Contains("BalloonTitle", actionSource);
        Assert.Contains("BalloonText", actionSource);
        Assert.Contains("TtsDataVM tts", actionSource);
        Assert.Contains("Actions.Select(a => a.SearchText)", eventSource);
    }

    [Fact]
    public void EventsEditor_FiltersResultsOffTheEditorDispatcher()
    {
        var editorSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorViewModel.cs"));
        var utilsSource = File.ReadAllText(ProjectPath("DamageMeter.Core", "Utils.cs"));

        Assert.Contains("Task.Run", editorSource);
        Assert.Contains("CancellationTokenSource", editorSource);
        Assert.Contains("VisibleEvents.ReplaceWith", editorSource);
        Assert.DoesNotContain("CollectionViewSource.GetDefaultView", editorSource);
        Assert.Contains("public void ReplaceWith(IEnumerable<T> items)", utilsSource);
        Assert.Contains("NotifyCollectionChangedAction.Reset", utilsSource);
    }

    [Fact]
    public void EventsEditor_RunsOnDedicatedDispatcherThread()
    {
        var settingsSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "Windows", "SettingsWindowViewModel.cs"));
        var serviceSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorService.cs"));
        var baseEventSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "BaseEventViewModel.cs"));
        var baseSoundSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "BaseSoundVM.cs"));
        var utilsSource = File.ReadAllText(ProjectPath("DamageMeter.Core", "Utils.cs"));

        Assert.Contains("EventsEditorService.Show", settingsSource);
        Assert.Contains("SetApartmentState(ApartmentState.STA)", serviceSource);
        Assert.Contains("Dispatcher.Run()", serviceSource);
        Assert.Contains("BeginInvokeShutdown", serviceSource);
        Assert.Contains("WindowInteropHelper", serviceSource);
        Assert.Contains("base(Dispatcher.CurrentDispatcher)", baseEventSource);
        Assert.Contains("new SynchronizedObservableCollection<ActionVM>(dispatcher)", baseEventSource);
        Assert.Contains("base(Dispatcher.CurrentDispatcher)", baseSoundSource);
        Assert.Contains("TSPropertyChanged(Dispatcher dispatcher)", utilsSource);
    }

    [Fact]
    public void EventsEditor_ExplainsAbnormalityIdsInExpandedRows()
    {
        var abnormalitySource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "AbnormalityVM.cs"));
        var eventSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "AbnormalityEventViewModel.cs"));
        var xamlSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorWindow.xaml"));

        Assert.Contains("public string DisplayName", abnormalitySource);
        Assert.Contains("public string DetailsText", abnormalitySource);
        Assert.Contains("HotDotDatabase?.Get(AbnormalityId)", abnormalitySource);
        Assert.Contains("item.PropertyChanged", eventSource);
        Assert.Contains("Text=\"{Binding DisplayName}\"", xamlSource);
        Assert.Contains("ToolTip=\"{Binding DetailsText}\"", xamlSource);
    }

    [Fact]
    public void EventsEditor_CanAddRemoveAndResetCommonEvents()
    {
        var editorSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorViewModel.cs"));
        var dataSource = File.ReadAllText(ProjectPath("Data", "EventsData.cs"));
        var xamlSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "EventsEditor", "EventsEditorWindow.xaml"));

        Assert.Contains("AddAbnormalityEventCommand", editorSource);
        Assert.Contains("RemoveEventCommand", editorSource);
        Assert.Contains("ResetToDefaultCommand", editorSource);
        Assert.Contains("new AbnormalityEvent(", editorSource);
        Assert.Contains("new NotifyAction(null, null)", editorSource);
        Assert.Contains("public void AddCommonEvent(Event ev, List<Action> actions)", dataSource);
        Assert.Contains("public void RemoveCommonEvent(Event ev)", dataSource);
        Assert.Contains("public void ResetCommonToDefault()", dataSource);
        Assert.Contains("LP.events_common", dataSource);
        Assert.Contains("Command=\"{Binding AddAbnormalityEventCommand}\"", xamlSource);
        Assert.Contains("Command=\"{Binding DataContext.RemoveEventCommand", xamlSource);
        Assert.Contains("Command=\"{Binding ResetToDefaultCommand}\"", xamlSource);
    }

    [Fact]
    public void TtsSettingsTab_OpensPerAlertTtsEditor()
    {
        var settingsSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "Windows", "SettingsWindow.xaml"));

        var ttsTabStart = settingsSource.IndexOf("<!--TTS-->", StringComparison.Ordinal);
        var hpBarTabStart = settingsSource.IndexOf("<!--HP bar-->", StringComparison.Ordinal);
        var ttsTab = settingsSource.Substring(ttsTabStart, hpBarTabStart - ttsTabStart);

        Assert.Contains("Configure TTS alerts", ttsTab);
        Assert.Contains("OpenEventEditorCommand", ttsTab);
    }

    [Fact]
    public void ManualJsonAndExcelExportsRevealTheCreatedFileInExplorer()
    {
        var jsonSource = File.ReadAllText(ProjectPath("DamageMeter.Core", "Exporter", "JsonExporter.cs"));
        var excelSource = File.ReadAllText(ProjectPath("DamageMeter.Core", "Exporter", "ExcelExporter.cs"));
        var revealerSource = File.ReadAllText(ProjectPath("DamageMeter.Core", "Exporter", "ExportFileRevealer.cs"));

        Assert.Contains("if (manual) { ExportFileRevealer.Reveal(fname); }", jsonSource);
        Assert.Contains("if (manual) { ExportFileRevealer.Reveal(fname); }", excelSource);
        Assert.Contains("explorer.exe", revealerSource);
        Assert.Contains("/select,", revealerSource);
    }

    [Fact]
    public void WpfAppExitPersistsSettingsWhenMainWindowIsClosedExternally()
    {
        var appSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "App.xaml.cs"));

        var appExitBody = appSource.Substring(appSource.IndexOf("private void App_OnExit", StringComparison.Ordinal));

        Assert.Contains("HudContainer?.SaveWindowsPos()", appExitBody);
        Assert.Contains("BasicTeraData.Instance.WindowData.Save()", appExitBody);
        Assert.Contains("BasicTeraData.Instance.WindowData.Close()", appExitBody);
        Assert.Contains("BasicTeraData.Instance.HotkeysData.Save()", appExitBody);
    }

    [Fact]
    public void LauncherCloseMessageClosesShinraWithoutConfirmDialog()
    {
        var appSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "App.xaml.cs"));

        Assert.Contains("ShinraMeter.ClassicPlus.RequestClose", appSource);
        Assert.Contains("RegisterWindowMessage", appSource);
        Assert.Contains("ChangeWindowMessageFilter", appSource);
        Assert.Contains("ChangeWindowMessageFilterEx", appSource);
        Assert.Contains("new HwndSourceParameters(\"ShinraMeterClassicPlusCloseReceiver\")", appSource);
        Assert.Contains("InstallLauncherCloseMessageReceiver();", appSource);
        Assert.Contains("VerifyClose(true)", appSource);

        var mainWindowSource = File.ReadAllText(ProjectPath("DamageMeter.UI", "Windows", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("InstallLauncherCloseMessageReceiver(this)", mainWindowSource);
    }

    [Fact]
    public void PacketBacklog_DoesNotForceTooSlowPause()
    {
        var source = File.ReadAllText(ProjectPath("DamageMeter.Core", "PacketProcessor.cs"));
        var overloadStart = source.IndexOf("if (packetsWaiting > 5000)", StringComparison.Ordinal);
        var needPauseStart = source.IndexOf("if (NeedPause)", StringComparison.Ordinal);
        Assert.True(overloadStart >= 0, "Packet backlog guard was not found.");
        Assert.True(needPauseStart > overloadStart, "NeedPause guard should follow the packet backlog guard.");

        var overloadBlock = source.Substring(
            overloadStart,
            needPauseStart - overloadStart
        );

        Assert.DoesNotContain("Pause();", overloadBlock);
        Assert.DoesNotContain("RaisePause(true);", overloadBlock);
    }

    private static string ProjectPath(params string[] parts)
    {
        var pathParts = new List<string>
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
        };
        pathParts.AddRange(parts);

        return Path.GetFullPath(Path.Combine(pathParts.ToArray()));
    }

    private static IEnumerable<string> ClassicPlusDataFiles()
    {
        foreach (var language in ClassicPlusLanguages())
        {
            yield return Path.Combine("skills", $"skills-{language}.tsv");
            yield return Path.Combine("skills", $"skills-override-{language}.tsv");
            yield return Path.Combine("skills", $"pets-skills-{language}.tsv");
            yield return Path.Combine("hotdot", $"hotdot-{language}.tsv");
            yield return Path.Combine("regions", $"regions-{language}.tsv");
            yield return Path.Combine("monsters", $"monsters-{language}.xml");
            yield return Path.Combine("world_map", $"world_map-{language}.xml");
        }

        foreach (var protocol in new[] { "387166", "387396", "387400", "387463" })
        {
            yield return Path.Combine("opcodes", $"protocol.{protocol}.map");
        }

        yield return "servers.txt";
    }

    private static IEnumerable<string> ClassicPlusLanguages()
    {
        return new[] { "EU-EN", "EU-FR", "EU-GER", "RU" };
    }

    private static string ReadNormalizedDataFile(string path)
    {
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static bool ContainsArcherSkill(string dataRoot, string language, int skillId)
    {
        var expectedStart = $"{skillId}\t";
        foreach (var fileName in new[] { $"skills-{language}.tsv", $"skills-override-{language}.tsv" })
        {
            var path = Path.Combine(dataRoot, "skills", fileName);
            if (File.ReadLines(path).Any(line =>
            {
                if (!line.StartsWith(expectedStart, StringComparison.Ordinal)) { return false; }
                var parts = line.Split('\t');
                return parts.Length >= 4 && parts[3] == "Archer";
            }))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildEachSkillResultPacket(ushort opcode, long amount)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(opcode);
        writer.Write(0); // skip 4
        writer.Write((ulong)1); // source
        writer.Write((ulong)0); // owner
        writer.Write((ulong)2); // target
        writer.Write(999); // template id
        writer.Write((ulong)0x1234567); // skill id
        writer.Write(7); // hit id
        writer.Write(11); // area
        writer.Write((uint)22); // id
        writer.Write(33); // time
        writer.Write(amount); // amount (int64)
        writer.Write((short)1); // type = damage
        writer.Write((short)0); // noctEffect
        writer.Write((byte)0); // crit
        writer.Write((byte)0); // stackExplode
        writer.Write((byte)0); // superArmor
        writer.Write(0); // super armor id
        writer.Write(0); // hit cylinder id
        writer.Write(new byte[4]); // reaction bools
        writer.Write((short)0); // damage type
        writer.Write(0f); // pos x
        writer.Write(0f); // pos y
        writer.Write(0f); // pos z
        writer.Write((short)0); // heading

        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildLoginArbiterPacket(ushort opcode, uint language, int fallbackVersion)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(opcode);
        writer.Write(new byte[11]);
        writer.Write(language);
        writer.Write(fallbackVersion);

        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildCrestInfoPacket(ushort opcode, bool includeGlyph)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(opcode);
        writer.Write((ushort)1); // count
        writer.Write((ushort)16); // entry offset includes TERA size/opcode header
        writer.Write((uint)55); // points max
        writer.Write((uint)10); // points used
        if (includeGlyph)
        {
            writer.Write((uint)0); // current/next member offsets
            writer.Write((uint)123456); // glyph id
            writer.Write((byte)1); // enabled
        }

        writer.Flush();
        return ms.ToArray();
    }
}
