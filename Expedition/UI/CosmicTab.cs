using System.Numerics;
using Dalamud.Bindings.ImGui;

using Expedition.PlayerState;

namespace Expedition.UI;

/// <summary>
/// Draws the Cosmic Exploration tab. Provides ICE plugin integration for
/// automated Cosmic Exploration workflows with mode selection, stop conditions,
/// and progress tracking.
/// </summary>
public static class CosmicTab
{
    // Job definitions: (ClassJobId, Abbreviation, DisplayName, IconId, IsGatherer)
    private static readonly (uint Id, string Abbr, string Name, uint Icon, bool IsGatherer)[] AllJobs =
    [
        // Gatherers
        (16, "MIN", "Miner",        62116, true),
        (17, "BTN", "Botanist",     62117, true),
        (18, "FSH", "Fisher",       62118, true),
        // Crafters
        ( 8, "CRP", "Carpenter",    62108, false),
        ( 9, "BSM", "Blacksmith",   62109, false),
        (10, "ARM", "Armorer",      62110, false),
        (11, "GSM", "Goldsmith",    62111, false),
        (12, "LTW", "Leatherworker",62112, false),
        (13, "WVR", "Weaver",       62113, false),
        (14, "ALC", "Alchemist",    62114, false),
        (15, "CUL", "Culinarian",   62115, false),
    ];

    // Session state
    private static readonly HashSet<uint> selectedJobIds = new();
    private static bool sessionActive;
    private static DateTime sessionStart;
    private static readonly Dictionary<uint, int> startLevels = new();
    private static string lastIceState = "Idle";

    // Config-backed local state (loaded once from config)
    private static bool configLoaded;
    private static int targetLevel = 100;
    private static int iceMode; // maps to CosmicIpc.ModeStandard/Relic/Level/Agenda
    private static bool stopAfterCurrent;
    private static bool stopOnCosmoCredits = true;
    private static int cosmoCreditsCap = 4000;
    private static bool stopOnLunarCredits = true;
    private static int lunarCreditsCap = 4000;
    private static bool onlyGrabMission;

    // Relic mode settings
    private static bool turninRelic;
    private static bool farmAllRelics;
    private static bool relicCraftersFirst = true;
    private static bool stopOnceRelicFinished;
    private static bool relicSwapJob;
    private static int relicBattleJob;
    private static bool relicStylist = true;

    // Additional stop conditions
    private static bool stopWhenLevel;
    private static bool stopOnceHitCosmicScore;
    private static int cosmicScoreCap = 500_000;

    // Polling
    private static DateTime lastPollTime;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Loads persisted settings from Configuration into local state (once).
    /// </summary>
    private static void EnsureConfigLoaded(Configuration config)
    {
        if (configLoaded) return;
        configLoaded = true;

        targetLevel = config.CosmicTargetLevel;
        iceMode = config.CosmicIceMode;
        stopAfterCurrent = config.CosmicStopAfterCurrent;
        stopOnCosmoCredits = config.CosmicStopOnCosmoCredits;
        cosmoCreditsCap = config.CosmicCosmoCreditsCap;
        stopOnLunarCredits = config.CosmicStopOnLunarCredits;
        lunarCreditsCap = config.CosmicLunarCreditsCap;
        onlyGrabMission = config.CosmicOnlyGrabMission;
        turninRelic = config.CosmicTurninRelic;
        farmAllRelics = config.CosmicFarmAllRelics;
        relicCraftersFirst = config.CosmicRelicCraftersFirst;
        stopOnceRelicFinished = config.CosmicStopOnceRelicFinished;
        relicSwapJob = config.CosmicRelicSwapJob;
        relicBattleJob = (int)config.CosmicRelicBattleJob;
        relicStylist = config.CosmicRelicStylist;
        stopWhenLevel = config.CosmicStopWhenLevel;
        stopOnceHitCosmicScore = config.CosmicStopOnceHitCosmicScore;
        cosmicScoreCap = config.CosmicCosmicScoreCap;
    }

    /// <summary>
    /// Saves current local state back to Configuration.
    /// </summary>
    private static void SaveConfig(Configuration config)
    {
        config.CosmicTargetLevel = targetLevel;
        config.CosmicIceMode = iceMode;
        config.CosmicStopAfterCurrent = stopAfterCurrent;
        config.CosmicStopOnCosmoCredits = stopOnCosmoCredits;
        config.CosmicCosmoCreditsCap = cosmoCreditsCap;
        config.CosmicStopOnLunarCredits = stopOnLunarCredits;
        config.CosmicLunarCreditsCap = lunarCreditsCap;
        config.CosmicOnlyGrabMission = onlyGrabMission;
        config.CosmicTurninRelic = turninRelic;
        config.CosmicFarmAllRelics = farmAllRelics;
        config.CosmicRelicCraftersFirst = relicCraftersFirst;
        config.CosmicStopOnceRelicFinished = stopOnceRelicFinished;
        config.CosmicRelicSwapJob = relicSwapJob;
        config.CosmicRelicBattleJob = (uint)relicBattleJob;
        config.CosmicRelicStylist = relicStylist;
        config.CosmicStopWhenLevel = stopWhenLevel;
        config.CosmicStopOnceHitCosmicScore = stopOnceHitCosmicScore;
        config.CosmicCosmicScoreCap = cosmicScoreCap;
        config.Save();
    }

    public static void Draw(Expedition plugin)
    {
        EnsureConfigLoaded(Expedition.Config);
        PollIceState(plugin);
        DrawHeader(plugin);
        ImGui.Spacing();
        DrawSubTabs(plugin);
    }

    // ──────────────────────────────────────────────
    // Header Banner
    // ──────────────────────────────────────────────

    private static void DrawHeader(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        const float headerH = 48f;

        // Banner background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + headerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.10f, 0.16f, 1.00f)),
            4f);

        // Purple accent line
        var accentColor = new Vector4(0.56f, 0.36f, 0.90f, 1.00f);
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + 2),
            ImGui.ColorConvertFloat4ToU32(accentColor),
            4f);

        // Title
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 6f),
            ImGui.ColorConvertFloat4ToU32(accentColor), "Cosmic Exploration  \u2014  Workflow Automation");

        // Status line
        var statusText = "";
        if (!ice.IsAvailable)
            statusText = "ICE plugin not detected";
        else if (sessionActive)
        {
            var elapsed = DateTime.UtcNow - sessionStart;
            var missionId = ice.GetCurrentMission();
            var missionText = missionId > 0 ? $"Mission #{missionId}" : "No mission";
            statusText = $"Session: {elapsed:hh\\:mm\\:ss}  |  ICE: {lastIceState}  |  {IPC.CosmicIpc.GetModeName(iceMode)}  |  {missionText}";
        }
        else
            statusText = $"{IPC.CosmicIpc.GetModeName(iceMode)}  |  ICE: {(ice.GetIsRunning() ? "Running" : "Idle")}";

        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 26f),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), statusText);

        // Start/Stop button
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - 140, pos.Y + 10f));
        if (sessionActive)
        {
            if (Theme.DangerButton("Stop##cosmic", new Vector2(120, 28)))
                StopSession(plugin);
        }
        else
        {
            var isLevelMode = iceMode == IPC.CosmicIpc.ModeLevel;
            var canStart = ice.IsAvailable && (!isLevelMode || selectedJobIds.Count > 0);
            if (!canStart) ImGui.BeginDisabled();
            if (Theme.PrimaryButton("Start##cosmic", new Vector2(120, 28)))
                StartSession(plugin);
            if (!canStart) ImGui.EndDisabled();
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + headerH));
        ImGui.Dummy(new Vector2(avail, 0));
    }

    // ──────────────────────────────────────────────
    // Sub-tabs
    // ──────────────────────────────────────────────

    private static void DrawSubTabs(Expedition plugin)
    {
        if (ImGui.BeginTabBar("CosmicSubTabs"))
        {
            if (ImGui.BeginTabItem("Workflow"))
            {
                DrawWorkflowTab(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status"))
            {
                DrawStatusTab(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab(plugin);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ──────────────────────────────────────────────
    // Workflow Tab (Mode Selection + Quick Actions + Stop Conditions)
    // ──────────────────────────────────────────────

    private static void DrawWorkflowTab(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;

        ImGui.Spacing();
        ImGui.BeginChild("CosmicWorkflow", Vector2.Zero, false);

        // ── Mode Selection Card ──
        Theme.BeginCardAuto("ModeSelection");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Mode Selection", Theme.Accent);
            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.TextColored(Theme.TextSecondary, "ICE Mode");
            ImGui.SameLine(160);
            ImGui.SetNextItemWidth(200);

            // Map between combo index and actual ICE mode values
            // Index: 0=Standard(0), 1=Relic(1), 2=Level(2), 3=Agenda(10)
            int comboIndex = iceMode switch
            {
                IPC.CosmicIpc.ModeRelic => 1,
                IPC.CosmicIpc.ModeLevel => 2,
                IPC.CosmicIpc.ModeAgenda => 3,
                _ => 0,
            };
            string[] modeNames = ["Standard", "Relic XP Grind", "Level Mode", "Agenda Mode"];
            if (ImGui.Combo("##iceMode", ref comboIndex, modeNames, modeNames.Length))
            {
                iceMode = comboIndex switch
                {
                    1 => IPC.CosmicIpc.ModeRelic,
                    2 => IPC.CosmicIpc.ModeLevel,
                    3 => IPC.CosmicIpc.ModeAgenda,
                    _ => IPC.CosmicIpc.ModeStandard,
                };
                SaveConfig(Expedition.Config);
            }

            ImGui.Spacing();

            // Mode description
            var modeDesc = iceMode switch
            {
                IPC.CosmicIpc.ModeRelic =>
                    "ICE ignores your mission list and auto-selects missions that give the " +
                    "most Cosmic Tool research data. Ideal for upgrading relic tools fast.",
                IPC.CosmicIpc.ModeLevel =>
                    "ICE filters missions to match your current job's level bracket " +
                    "(Lv10-49, Lv50-89, Lv90+). Best for leveling specific jobs.",
                IPC.CosmicIpc.ModeAgenda =>
                    "ICE follows a playlist of goals you've set in its settings " +
                    "(relic stages, job levels, credit targets). Configure the agenda in ICE's UI.",
                _ =>
                    "ICE runs missions you've manually enabled in its mission list. " +
                    "Good for score grinding, credit farming, or targeting specific missions.",
            };
            ImGui.TextWrapped(modeDesc);

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Quick Actions Card ──
        if (ice.IsAvailable)
        {
            Theme.BeginCardAuto("QuickActions");
            {
                ImGui.Spacing();
                Theme.SectionHeader("Quick Actions", Theme.Accent);
                Theme.HelpMarker(
                    "Send commands to ICE immediately.\n" +
                    "These take effect right away, regardless of session state.");
                ImGui.Spacing();
                ImGui.Spacing();

                // Mode quick-switch
                ImGui.TextColored(Theme.TextSecondary, "Mode");
                ImGui.Spacing();

                var currentMode = ice.GetMode();
                ImGui.TextColored(Theme.TextMuted, $"  ICE is currently in: {IPC.CosmicIpc.GetModeName(currentMode)}");
                ImGui.Spacing();

                if (Theme.SecondaryButton("Standard", new Vector2(100, 28)))
                    ice.SetMode(IPC.CosmicIpc.ModeStandard);
                ImGui.SameLine(0, Theme.PadSmall);
                if (Theme.SecondaryButton("Relic XP", new Vector2(100, 28)))
                    ice.SetMode(IPC.CosmicIpc.ModeRelic);
                ImGui.SameLine(0, Theme.PadSmall);
                if (Theme.SecondaryButton("Level", new Vector2(80, 28)))
                    ice.SetMode(IPC.CosmicIpc.ModeLevel);
                ImGui.SameLine(0, Theme.PadSmall);
                if (Theme.SecondaryButton("Agenda", new Vector2(90, 28)))
                    ice.SetMode(IPC.CosmicIpc.ModeAgenda);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Apply all settings + Clear missions
                if (Theme.PrimaryButton("Apply Settings to ICE", new Vector2(200, 28)))
                    ApplyAllSettingsToIce(plugin);
                ImGui.SameLine(0, Theme.PadLarge);
                ImGui.TextColored(Theme.TextMuted, "Sends all settings to ICE via IPC");

                ImGui.Spacing();

                if (Theme.DangerButton("Clear All Missions", new Vector2(180, 28)))
                    ice.ClearAllMissions();
                ImGui.SameLine(0, Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, "Disables all missions in ICE");

                ImGui.Spacing();
            }
            Theme.EndCardAuto();

            ImGui.Spacing();
            ImGui.Spacing();
        }

        // ── Stop Conditions Card ──
        Theme.BeginCardAuto("StopConditions");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Stop Conditions", Theme.Gold);
            Theme.HelpMarker(
                "Configure when ICE should automatically stop.\n" +
                "These are applied to ICE when you click Start or Apply Settings.");
            ImGui.Spacing();
            ImGui.Spacing();

            var config = Expedition.Config;

            // Stop after current mission
            if (ImGui.Checkbox("Stop after current mission##stopAfter", ref stopAfterCurrent))
                SaveConfig(config);
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, "Finishes current mission, then stops");

            ImGui.Spacing();

            // Cosmo credits cap
            if (ImGui.Checkbox("Stop at Cosmo Credit cap##cosmoCap", ref stopOnCosmoCredits))
                SaveConfig(config);
            if (stopOnCosmoCredits)
            {
                ImGui.SameLine(280);
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("##cosmoCapVal", ref cosmoCreditsCap, 500, 1000))
                {
                    cosmoCreditsCap = Math.Clamp(cosmoCreditsCap, 0, 99999);
                    SaveConfig(config);
                }
            }

            ImGui.Spacing();

            // Lunar credits cap
            if (ImGui.Checkbox("Stop at Lunar Credit cap##lunarCap", ref stopOnLunarCredits))
                SaveConfig(config);
            if (stopOnLunarCredits)
            {
                ImGui.SameLine(280);
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("##lunarCapVal", ref lunarCreditsCap, 500, 1000))
                {
                    lunarCreditsCap = Math.Clamp(lunarCreditsCap, 0, 99999);
                    SaveConfig(config);
                }
            }

            ImGui.Spacing();

            // Stop at level
            if (ImGui.Checkbox("Stop at job level##stopLevel", ref stopWhenLevel))
                SaveConfig(config);
            if (stopWhenLevel)
            {
                ImGui.SameLine(280);
                ImGui.SetNextItemWidth(100);
                if (ImGui.SliderInt("##targetLvlStop", ref targetLevel, 10, 100))
                    SaveConfig(config);
            }

            ImGui.Spacing();

            // Stop at cosmic score
            if (ImGui.Checkbox("Stop at Cosmic Score##cosmicScore", ref stopOnceHitCosmicScore))
                SaveConfig(config);
            if (stopOnceHitCosmicScore)
            {
                ImGui.SameLine(280);
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("##cosmicScoreVal", ref cosmicScoreCap, 50000, 100000))
                {
                    cosmicScoreCap = Math.Clamp(cosmicScoreCap, 0, 9999999);
                    SaveConfig(config);
                }
            }

            ImGui.Spacing();

            // Stop once relic finished
            if (ImGui.Checkbox("Stop once relic finished##stopRelic", ref stopOnceRelicFinished))
                SaveConfig(config);
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, farmAllRelics ? "Stops when ALL enabled jobs are maxed" : "Stops when current job's relic is maxed");

            ImGui.Spacing();

            // Only grab mission (debug)
            if (ImGui.Checkbox("Only grab mission (skip execution)##onlyGrab", ref onlyGrabMission))
                SaveConfig(config);
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, "Debug: accepts mission but doesn't run it");

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Relic Mode Settings Card ──
        if (iceMode == IPC.CosmicIpc.ModeRelic)
        {
            Theme.BeginCardAuto("RelicSettings");
            {
                ImGui.Spacing();
                Theme.SectionHeader("Relic Mode Settings", new Vector4(0.56f, 0.36f, 0.90f, 1.00f));
                Theme.HelpMarker(
                    "Settings specific to Relic XP Grind mode.\n" +
                    "These control auto-turnin, job rotation, and job swapping during turnin.");
                ImGui.Spacing();
                ImGui.Spacing();

                // Turnin relic
                if (ImGui.Checkbox("Turnin if relic is complete##turninRelic", ref turninRelic))
                    SaveConfig(Expedition.Config);
                ImGui.SameLine(0, Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, "Auto-report at Researchingway when XP bars are full");

                ImGui.Spacing();

                // Farm all relics (auto rotation)
                if (!turninRelic) ImGui.BeginDisabled();
                if (ImGui.Checkbox("Farm All Relics (Auto Rotation)##farmAll", ref farmAllRelics))
                    SaveConfig(Expedition.Config);
                ImGui.SameLine(0, Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, "Rotate between jobs, prioritizing lowest tier");
                if (!turninRelic) ImGui.EndDisabled();

                if (farmAllRelics && turninRelic)
                {
                    ImGui.Indent(Theme.PadLarge);
                    ImGui.TextColored(Theme.TextMuted, "Tiers: Novice (0-8), Intermediate (9-13), Advanced (14-17)");
                    ImGui.TextColored(Theme.TextMuted, "Finishes one job's tier first to unlock the XP buff for others");
                    ImGui.Unindent(Theme.PadLarge);
                }

                ImGui.Spacing();

                // Crafters first option (independent of auto rotation)
                if (ImGui.Checkbox("Crafters First (DoH before DoL)##craftersFirst", ref relicCraftersFirst))
                    SaveConfig(Expedition.Config);
                ImGui.SameLine(0, Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, "Do all 8 crafters before 3 gatherers per tier");
                Theme.HelpMarker(
                    "Prioritizes all Disciples of the Hand (CRP, BSM, ARM, GSM, LTW, WVR, ALC, CUL)\n" +
                    "before Disciples of the Land (MIN, BTN, FSH) within each tier.\n\n" +
                    "Crafters are generally faster and unlock quality bonuses earlier,\n" +
                    "often reducing total grinding time by 30-50% on later tools.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Swap jobs during turnin
                if (ImGui.Checkbox("Swap to battle job during turnin##swapJob", ref relicSwapJob))
                    SaveConfig(Expedition.Config);
                ImGui.SameLine(0, Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, "Switch to a battle job before talking to NPC");

                if (relicSwapJob)
                {
                    ImGui.Spacing();
                    ImGui.Indent(Theme.PadLarge);

                    // Battle job selector
                    ImGui.TextColored(Theme.TextSecondary, "Battle Job");
                    ImGui.SameLine(120);
                    ImGui.SetNextItemWidth(180);

                    string currentBattleJobName = "None";
                    foreach (var kvp in BattleJobs)
                    {
                        if (kvp.Value == (uint)relicBattleJob)
                        {
                            currentBattleJobName = kvp.Key;
                            break;
                        }
                    }

                    if (ImGui.BeginCombo("##battleJob", currentBattleJobName))
                    {
                        foreach (var kvp in BattleJobs)
                        {
                            bool isSelected = (uint)relicBattleJob == kvp.Value;
                            if (ImGui.Selectable(kvp.Key, isSelected))
                            {
                                relicBattleJob = (int)kvp.Value;
                                SaveConfig(Expedition.Config);
                            }
                            if (isSelected)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.Spacing();

                    // Stylist
                    if (ImGui.Checkbox("Use Stylist to re-equip tools##stylist", ref relicStylist))
                        SaveConfig(Expedition.Config);
                    ImGui.SameLine(0, Theme.Pad);
                    ImGui.TextColored(Theme.TextMuted, "Runs /stylist after turnin to slot upgraded tool");

                    ImGui.Unindent(Theme.PadLarge);
                }

                ImGui.Spacing();
            }
            Theme.EndCardAuto();

            ImGui.Spacing();
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    private static readonly Dictionary<string, uint> BattleJobs = new()
    {
        { "Paladin", 19 }, { "Warrior", 21 }, { "Dark Knight", 32 }, { "Gunbreaker", 37 },
        { "White Mage", 24 }, { "Scholar", 28 }, { "Astrologian", 33 }, { "Sage", 40 },
        { "Monk", 20 }, { "Dragoon", 22 }, { "Ninja", 30 }, { "Samurai", 34 },
        { "Reaper", 39 }, { "Viper", 41 },
        { "Bard", 23 }, { "Machinist", 31 }, { "Dancer", 38 },
        { "Black Mage", 25 }, { "Summoner", 27 }, { "Red Mage", 35 }, { "Pictomancer", 42 },
    };

    // ──────────────────────────────────────────────
    // Status Tab (Session Overview + Job Progress)
    // ──────────────────────────────────────────────

    private static void DrawStatusTab(Expedition plugin)
    {
        ImGui.Spacing();
        ImGui.BeginChild("CosmicStatus", Vector2.Zero, false);

        // Session overview
        Theme.BeginCardAuto("SessionOverview");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Session Overview", Theme.Accent);
            ImGui.Spacing();
            ImGui.Spacing();

            if (sessionActive)
            {
                var ice = plugin.Ipc.Cosmic;
                var elapsed = DateTime.UtcNow - sessionStart;
                Theme.KeyValue("Duration:", $"{elapsed:hh\\:mm\\:ss}");
                Theme.KeyValue("ICE State:", lastIceState);
                Theme.KeyValue("Mode:", IPC.CosmicIpc.GetModeName(iceMode));

                var missionId = ice.GetCurrentMission();
                if (missionId > 0)
                    Theme.KeyValue("Current Mission:", $"#{missionId}");

                ImGui.Spacing();

                // Show active stop conditions
                if (stopOnCosmoCredits)
                    Theme.KeyValue("Cosmo Cap:", $"{cosmoCreditsCap:N0}");
                if (stopOnLunarCredits)
                    Theme.KeyValue("Lunar Cap:", $"{lunarCreditsCap:N0}");

                ImGui.Spacing();
                Theme.StatusDot(Theme.Success, "Running");
            }
            else
            {
                Theme.StatusDot(Theme.TextMuted, "Stopped");
            }

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // Job progress — show when Level Mode is active or when there's session data
        var showJobProgress = iceMode == IPC.CosmicIpc.ModeLevel || startLevels.Count > 0;
        if (showJobProgress)
        {
            // Collapsible in non-Level modes
            var defaultOpen = iceMode == IPC.CosmicIpc.ModeLevel;
            if (defaultOpen)
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);

            if (ImGui.CollapsingHeader("Job Progress", defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                ImGui.Spacing();

                if (!sessionActive && startLevels.Count == 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
                    ImGui.TextColored(Theme.TextMuted, "No session data yet.");
                }
                else
                {
                    // Table header
                    ImGui.TextColored(Theme.TextMuted, "  Job");
                    ImGui.SameLine(180);
                    ImGui.TextColored(Theme.TextMuted, "Start");
                    ImGui.SameLine(240);
                    ImGui.TextColored(Theme.TextMuted, "Current");
                    ImGui.SameLine(310);
                    ImGui.TextColored(Theme.TextMuted, "Gained");
                    ImGui.SameLine(380);
                    ImGui.TextColored(Theme.TextMuted, "Target");
                    ImGui.Separator();

                    foreach (var job in AllJobs)
                    {
                        if (!selectedJobIds.Contains(job.Id) && !startLevels.ContainsKey(job.Id))
                            continue;

                        var currentLvl = JobSwitchManager.GetPlayerJobLevel(job.Id);
                        var startLvl = startLevels.TryGetValue(job.Id, out var sl) ? sl : currentLvl;
                        var gained = currentLvl >= 0 && startLvl >= 0 ? currentLvl - startLvl : 0;
                        var atTarget = currentLvl >= targetLevel;

                        var startY = ImGui.GetCursorPosY();

                        // Icon + name
                        ImGui.SetCursorPosY(startY + (28 - 24) / 2);
                        MainWindow.DrawGameIcon(job.Icon, new Vector2(24, 24));
                        ImGui.SameLine(0, Theme.PadSmall);
                        ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2);
                        ImGui.TextColored(Theme.TextPrimary, job.Abbr);

                        // Start level
                        ImGui.SameLine(180);
                        ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2);
                        ImGui.TextColored(Theme.TextMuted, startLvl >= 0 ? $"Lv{startLvl}" : "\u2014");

                        // Current level
                        ImGui.SameLine(240);
                        ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2);
                        ImGui.TextColored(atTarget ? Theme.Success : Theme.TextSecondary,
                            currentLvl >= 0 ? $"Lv{currentLvl}" : "\u2014");

                        // Levels gained
                        ImGui.SameLine(310);
                        ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2);
                        if (gained > 0)
                            ImGui.TextColored(Theme.Success, $"+{gained}");
                        else
                            ImGui.TextColored(Theme.TextMuted, "\u2014");

                        // Target
                        ImGui.SameLine(380);
                        ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2);
                        if (atTarget)
                            ImGui.TextColored(Theme.Success, "Done");
                        else
                            ImGui.TextColored(Theme.Gold, $"Lv{targetLevel}");

                        ImGui.SetCursorPosY(startY + 30);
                    }
                }
            }
        }
        else
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
            ImGui.TextColored(Theme.TextMuted, "Job progress tracking is available in Level Mode.");
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Settings Tab (ICE Status + Level Mode + Gear Guide + How It Works)
    // ──────────────────────────────────────────────

    private static void DrawSettingsTab(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;

        ImGui.Spacing();
        ImGui.BeginChild("CosmicSettings", Vector2.Zero, false);

        // ── ICE Plugin Status Card ──
        Theme.BeginCardAuto("IceStatus");
        {
            ImGui.Spacing();
            Theme.SectionHeader("ICE Plugin Status", Theme.Accent);
            ImGui.Spacing();
            ImGui.Spacing();

            if (!ice.IsAvailable)
            {
                Theme.StatusDot(Theme.Error, "ICE Not Detected");
                ImGui.Spacing();
                ImGui.TextColored(Theme.TextMuted,
                    "Install ICE (Ice's Cosmic Exploration) to use Cosmic Exploration automation.");
                ImGui.TextColored(Theme.TextMuted,
                    "Repo: https://puni.sh/api/repository/ice");
            }
            else
            {
                var running = ice.GetIsRunning();
                var state = ice.GetCurrentState();
                var missionId = ice.GetCurrentMission();

                Theme.StatusDot(
                    running ? Theme.Success : Theme.TextMuted,
                    running ? $"ICE Running: {state}" : "ICE Idle");

                if (missionId > 0)
                {
                    ImGui.Spacing();
                    Theme.KeyValue("Current Mission:", $"#{missionId}");
                }
            }

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Level Mode Settings Card ──
        Theme.BeginCardAuto("LevelModeSettings");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Level Mode Settings", Theme.Gold);
            ImGui.Spacing();
            ImGui.Spacing();

            if (iceMode != IPC.CosmicIpc.ModeLevel)
            {
                ImGui.TextColored(Theme.TextMuted,
                    "These settings are used when ICE is in Level Mode. Switch to Level Mode in the Workflow tab to use them.");
                ImGui.Spacing();
            }

            // Target level slider
            ImGui.TextColored(Theme.TextSecondary, "Target Level");
            ImGui.SameLine(160);
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("##targetLvl", ref targetLevel, 10, 100))
                SaveConfig(Expedition.Config);

            ImGui.Spacing();
            ImGui.Spacing();

            // Quick select buttons
            if (Theme.SecondaryButton("All Gatherers", new Vector2(130, 24)))
            {
                foreach (var j in AllJobs)
                    if (j.IsGatherer) selectedJobIds.Add(j.Id);
            }
            ImGui.SameLine(0, Theme.Pad);
            if (Theme.SecondaryButton("All Crafters", new Vector2(130, 24)))
            {
                foreach (var j in AllJobs)
                    if (!j.IsGatherer) selectedJobIds.Add(j.Id);
            }
            ImGui.SameLine(0, Theme.Pad);
            if (Theme.SecondaryButton("All Jobs", new Vector2(100, 24)))
            {
                foreach (var j in AllJobs)
                    selectedJobIds.Add(j.Id);
            }
            ImGui.SameLine(0, Theme.Pad);
            if (Theme.SecondaryButton("Clear", new Vector2(80, 24)))
                selectedJobIds.Clear();

            ImGui.Spacing();
            ImGui.Spacing();

            // Gatherers section
            Theme.LabeledSeparator("Gatherers");
            ImGui.Spacing();
            DrawJobCards(true);

            ImGui.Spacing();
            ImGui.Spacing();

            // Crafters section
            Theme.LabeledSeparator("Crafters");
            ImGui.Spacing();
            DrawJobCards(false);

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // Gear guide
        DrawGearGuide();

        ImGui.Spacing();
        ImGui.Spacing();

        // ── How It Works Card ──
        Theme.BeginCardAuto("CosmicInfo");
        {
            ImGui.Spacing();
            Theme.SectionHeader("How It Works", Theme.TextSecondary);
            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.TextColored(Theme.TextMuted, "1. Choose an ICE mode in the Workflow tab");
            ImGui.TextColored(Theme.TextMuted, "2. Configure stop conditions (credit caps, etc.)");
            ImGui.TextColored(Theme.TextMuted, "3. For Level Mode: select jobs and set a target level in Settings");
            ImGui.TextColored(Theme.TextMuted, "4. Click Start \u2014 Expedition sends settings and starts ICE");
            ImGui.TextColored(Theme.TextMuted, "5. ICE handles: mission selection, gathering, crafting, turn-in");
            ImGui.TextColored(Theme.TextMuted, "6. Track progress in the Status tab");
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextMuted, "Required plugins: ICE, Artisan, VNavmesh");
            ImGui.TextColored(Theme.TextMuted, "Optional: AutoHook (for fishing), PandorasBox, Lifestream");

            ImGui.Spacing();
        }
        Theme.EndCardAuto();

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Gear Guide
    // ──────────────────────────────────────────────

    private static void DrawGearGuide()
    {
        Theme.BeginCardAuto("GearGuide");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Gear Guide", Theme.Warning);
            Theme.HelpMarker(
                "Cosmic Exploration does NOT sync your gear stats.\n" +
                "Your actual Gathering/Perception/Craftsmanship/Control\n" +
                "directly affect success rate, collectability, and mission score.\n" +
                "Undergeared = lower scores = fewer credits & less XP.");
            ImGui.Spacing();
            ImGui.Spacing();

            // Warning banner
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var avail = ImGui.GetContentRegionAvail().X;
            const float bannerH = 38f;

            drawList.AddRectFilled(
                pos,
                new Vector2(pos.X + avail, pos.Y + bannerH),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.16f, 0.06f, 0.80f)),
                4f);
            drawList.AddRectFilled(
                pos,
                new Vector2(pos.X + 3, pos.Y + bannerH),
                ImGui.ColorConvertFloat4ToU32(Theme.Warning),
                4f);
            drawList.AddText(new Vector2(pos.X + 14, pos.Y + 4),
                ImGui.ColorConvertFloat4ToU32(Theme.Warning),
                "Gear directly affects your performance \u2014 stats are NOT synced!");
            drawList.AddText(new Vector2(pos.X + 14, pos.Y + 22),
                ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary),
                "Keep your gear within ~10 levels of your current job level for best results.");

            ImGui.Dummy(new Vector2(avail, bannerH));
            ImGui.Spacing();
            ImGui.Spacing();

            // Gear tier table — each row is a compact 2-line block
            DrawGearTierRow("Lv 10-49", "iLvl \u2014", "Level-appropriate NQ gear", "Source: Godgyth NPC (Sinus Ardorum)", false);
            DrawGearTierRow("Lv 50-69", "iLvl 130+", "Lv50-60 HQ or scrip gear", "Source: Vendor / Market Board", false);
            DrawGearTierRow("Lv 70-79", "iLvl 385+", "Lv70 scrip or crafted gear", "Source: Yellow Scrip Exchange", false);
            DrawGearTierRow("Lv 80-89", "iLvl 500+", "Lv80 White Scrip gear", "Source: White Scrip Exchange", false);
            DrawGearTierRow("Lv 90-99", "iLvl 620+", "Purple Scrip gear", "Source: Purple Scrip Exchange", true);
            DrawGearTierRow("Lv 100", "iLvl 690+", "Orange Scrip or pentamelded crafted", "Source: Scrip Exchange / Crafting", false);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Quick tips
            ImGui.TextColored(Theme.Accent, "Where to get gear:");
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Godgyth NPC  \u2014 sells NQ leveling gear in Sinus Ardorum (X:21.8, Y:21.8)");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Scrip Exchange  \u2014 Purple/Orange scrip gear from Mesouaidonque (same location)");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Market Board  \u2014 HQ crafted gear gives highest stats per level");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Custom Deliveries  \u2014 easy weekly source of Purple Gatherer/Crafter Scrips");
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, "Tip: Purple Scrip gear (iLvl 620) is free and covers Lv90-99 comfortably.");
            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }

    private static void DrawGearTierRow(string levelRange, string ilvl, string gear, string source, bool highlight)
    {
        var accentColor = highlight ? Theme.Gold : Theme.Accent;
        var textColor = highlight ? Theme.Gold : Theme.TextPrimary;
        var mutedColor = highlight ? Theme.GoldDim : Theme.TextMuted;

        // Row background for highlighted tier
        if (highlight)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var avail = ImGui.GetContentRegionAvail().X;
            drawList.AddRectFilled(
                new Vector2(pos.X, pos.Y - 2),
                new Vector2(pos.X + avail, pos.Y + 34),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.17f, 0.06f, 0.50f)),
                3f);
        }

        // Line 1: Level range + iLvl tag + gear name
        var prefix = highlight ? "\u25b6 " : "  ";
        ImGui.TextColored(accentColor, $"{prefix}{levelRange}");
        ImGui.SameLine(0, Theme.Pad);
        Theme.InlineBadge(ilvl, accentColor);
        ImGui.TextColored(textColor, gear);

        // Line 2: Source (indented)
        ImGui.TextColored(mutedColor, $"      {source}");

        ImGui.Spacing();
    }

    private static void DrawJobCards(bool gatherers)
    {
        foreach (var job in AllJobs)
        {
            if (job.IsGatherer != gatherers) continue;

            var level = JobSwitchManager.GetPlayerJobLevel(job.Id);
            var isSelected = selectedJobIds.Contains(job.Id);
            var atTarget = level >= targetLevel;

            ImGui.PushID((int)job.Id);

            if (atTarget) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);

            // Checkbox
            var startY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(startY + (36 - ImGui.GetFrameHeight()) / 2);
            if (ImGui.Checkbox("##sel", ref isSelected))
            {
                if (isSelected) selectedJobIds.Add(job.Id);
                else selectedJobIds.Remove(job.Id);
            }

            ImGui.SameLine(0, Theme.PadSmall);

            // Job icon
            ImGui.SetCursorPosY(startY + (36 - 28) / 2);
            MainWindow.DrawGameIcon(job.Icon, new Vector2(28, 28));

            ImGui.SameLine(0, Theme.Pad);

            // Name + level
            ImGui.SetCursorPosY(startY + (36 - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TextPrimary, job.Name);

            ImGui.SameLine(200);
            ImGui.SetCursorPosY(startY + (36 - ImGui.GetTextLineHeight()) / 2);
            if (level >= 0)
            {
                var lvlColor = atTarget ? Theme.Success : Theme.TextSecondary;
                ImGui.TextColored(lvlColor, $"Lv{level}");
                if (atTarget)
                {
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.TextColored(Theme.Success, "(at target)");
                }
                else
                {
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.TextColored(Theme.TextMuted, $"-> Lv{targetLevel}");
                }
            }
            else
            {
                ImGui.TextColored(Theme.TextMuted, "\u2014");
            }

            ImGui.SetCursorPosY(startY + 38);

            if (atTarget) ImGui.PopStyleVar();

            ImGui.PopID();
        }
    }

    // ──────────────────────────────────────────────
    // Settings Application
    // ──────────────────────────────────────────────

    /// <summary>
    /// Sends all configured settings to ICE via IPC.
    /// Called on session start and when the user clicks "Apply Settings to ICE".
    /// </summary>
    private static void ApplyAllSettingsToIce(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;
        if (!ice.IsAvailable) return;

        // Mode — uses our custom ChangeMode IPC (forked ICE)
        ice.SetMode(iceMode);

        // Stop conditions
        ice.ChangeSetting("StopAfterCurrent", stopAfterCurrent);
        ice.ChangeSetting("OnlyGrabMission", onlyGrabMission);
        ice.ChangeSetting("StopWhenLevel", stopWhenLevel);
        ice.ChangeSetting("StopOnceHitCosmicScore", stopOnceHitCosmicScore);
        ice.ChangeSetting("StopOnceRelicFinished", stopOnceRelicFinished);

        // Credit caps
        ice.ChangeSetting("StopOnceHitCosmoCredits", stopOnCosmoCredits);
        if (stopOnCosmoCredits)
            ice.ChangeSettingAmount("CosmoCreditsCap", cosmoCreditsCap);

        ice.ChangeSetting("StopOnceHitLunarCredits", stopOnLunarCredits);
        if (stopOnLunarCredits)
            ice.ChangeSettingAmount("LunarCreditsCap", lunarCreditsCap);

        // Level / score caps
        if (stopWhenLevel)
            ice.ChangeSettingAmount("TargetLevel", targetLevel);
        if (stopOnceHitCosmicScore)
            ice.ChangeSettingAmount("CosmicScoreCap", cosmicScoreCap);

        // Relic mode settings
        ice.ChangeSetting("TurninRelic", turninRelic);
        ice.ChangeSetting("FarmAllRelics", farmAllRelics);
        ice.ChangeSetting("RelicCraftersFirst", relicCraftersFirst);
        ice.ChangeSetting("Relic_SwapJob", relicSwapJob);
        ice.ChangeSetting("Relic_Stylist", relicStylist);
        if (relicSwapJob && relicBattleJob > 0)
            ice.ChangeSettingAmount("Relic_BattleJob", relicBattleJob);

        DalamudApi.Log.Information(
            $"[Cosmic] Applied settings to ICE: mode={IPC.CosmicIpc.GetModeName(iceMode)}, " +
            $"stopAfter={stopAfterCurrent}, cosmoCap={stopOnCosmoCredits}/{cosmoCreditsCap}, " +
            $"lunarCap={stopOnLunarCredits}/{lunarCreditsCap}, turninRelic={turninRelic}, " +
            $"farmAll={farmAllRelics}, onlyGrab={onlyGrabMission}");
    }

    // ──────────────────────────────────────────────
    // Session Control
    // ──────────────────────────────────────────────

    private static void StartSession(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;
        if (!ice.IsAvailable) return;

        var isLevelMode = iceMode == IPC.CosmicIpc.ModeLevel;

        // Level Mode requires job selection
        if (isLevelMode && selectedJobIds.Count == 0) return;

        // Record starting levels (only meaningful for Level Mode, but harmless otherwise)
        if (isLevelMode)
        {
            startLevels.Clear();
            foreach (var jobId in selectedJobIds)
            {
                var lvl = JobSwitchManager.GetPlayerJobLevel(jobId);
                if (lvl >= 0) startLevels[jobId] = lvl;
            }
        }

        // Apply all settings to ICE before starting
        ApplyAllSettingsToIce(plugin);

        // Start ICE
        if (ice.Enable())
        {
            sessionActive = true;
            sessionStart = DateTime.UtcNow;
            DalamudApi.Log.Information($"[Cosmic] Started workflow session: mode={IPC.CosmicIpc.GetModeName(iceMode)}.");
        }
        else
        {
            DalamudApi.Log.Warning("[Cosmic] Failed to start ICE.");
        }
    }

    private static void StopSession(Expedition plugin)
    {
        var ice = plugin.Ipc.Cosmic;
        ice.Disable();
        sessionActive = false;
        DalamudApi.Log.Information("[Cosmic] Stopped workflow session.");
    }

    private static void PollIceState(Expedition plugin)
    {
        if (!sessionActive) return;

        var now = DateTime.UtcNow;
        if (now - lastPollTime < PollInterval) return;
        lastPollTime = now;

        var ice = plugin.Ipc.Cosmic;
        if (!ice.IsAvailable) return;

        lastIceState = ice.GetCurrentState();

        // Check if ICE stopped on its own
        if (!ice.GetIsRunning() && sessionActive)
        {
            // In Level Mode, check if all jobs are at target
            if (iceMode == IPC.CosmicIpc.ModeLevel && selectedJobIds.Count > 0)
            {
                var allDone = true;
                foreach (var jobId in selectedJobIds)
                {
                    var lvl = JobSwitchManager.GetPlayerJobLevel(jobId);
                    if (lvl < targetLevel) { allDone = false; break; }
                }

                if (allDone)
                {
                    sessionActive = false;
                    DalamudApi.Log.Information("[Cosmic] All selected jobs reached target level. Session complete.");
                    return;
                }
            }

            // For any mode, if ICE stopped itself (e.g. hit a credit cap), end session
            sessionActive = false;
            DalamudApi.Log.Information("[Cosmic] ICE stopped. Session ended.");
        }
    }
}
