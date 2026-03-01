using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Expedition.UI;

public static class ChangelogTab
{
    private static readonly Vector4 VersionColor = new(0.40f, 0.70f, 1.00f, 1.00f);
    private static readonly Vector4 DateColor = new(0.50f, 0.50f, 0.50f, 1.00f);
    private static readonly Vector4 TagNew = new(0.30f, 0.85f, 0.45f, 1.00f);
    private static readonly Vector4 TagChanged = new(0.95f, 0.75f, 0.20f, 1.00f);
    private static readonly Vector4 TagDep = new(0.85f, 0.55f, 0.85f, 1.00f);

    public static void Draw()
    {
        ImGui.Spacing();

        // --- ICE: Relic Auto Job Rotation ---
        DrawVersionHeader("ICE", "Relic Mode: Auto Job Rotation", "2026-02-28");

        Theme.BeginCardAuto("cl_ice_relic_rotation");
        ImGui.Indent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Farm All Relics (Auto Rotation)");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Automatically rotates between enabled relic jobs when grinding XP.");
        ImGui.TextColored(Theme.TextSecondary, "When a job's relic is fully maxed (stage 17, all 500/500), it skips to the next job.");
        ImGui.TextColored(Theme.TextSecondary, "When ALL enabled jobs are done, ICE stops automatically.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Tier-Aware Rotation (Increased Data Acquisition)");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Prioritizes finishing one job's tier first to unlock the character-wide XP buff,");
        ImGui.TextColored(Theme.TextSecondary, "then rotates to other jobs in the same tier so they benefit from the bonus.");
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextMuted, "Tier boundaries: Novice (0-8), Intermediate (9-13), Advanced (14-17)");
        ImGui.TextColored(Theme.TextMuted, "Buff: +50% (1-2 tools done) / +100% (3-4) / +150% (5-11)");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Smart Job Selection Algorithm");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "1. Turnin-ready jobs are handled first (don't waste capped XP)");
        ImGui.TextColored(Theme.TextSecondary, "2. Then picks the lowest tier job (maximize buff benefit)");
        ImGui.TextColored(Theme.TextSecondary, "3. Within the same tier, picks highest progress (unlock buff sooner)");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextMuted, "Files changed:");
        ImGui.Spacing();

        Theme.InlineBadge("DEP", TagDep);
        ImGui.TextColored(Theme.TextSecondary, "CosmicHelper.cs");
        ImGui.Indent(Theme.PadLarge);
        ImGui.BulletText("Added GetRelicTier() helper (Novice/Intermediate/Advanced boundaries)");
        ImGui.Unindent(Theme.PadLarge);

        Theme.InlineBadge("DEP", TagDep);
        ImGui.TextColored(Theme.TextSecondary, "Task_CheckState.cs");
        ImGui.Indent(Theme.PadLarge);
        ImGui.BulletText("Added IsJobFullyMaxed(), IsJobReadyForTurnin() classification");
        ImGui.BulletText("Added RelicJobRotation() + PickNextRelicJob() rotation algorithm");
        ImGui.BulletText("Added AreAllEnabledRelicJobsMaxed() for multi-job stop checks");
        ImGui.BulletText("Wired rotation into CheckStateV2() before stop checks");
        ImGui.BulletText("Updated StopOnceRelicFinished to check all enabled jobs when FarmAllRelics is on");
        ImGui.Unindent(Theme.PadLarge);

        Theme.InlineBadge("DEP", TagDep);
        ImGui.TextColored(Theme.TextSecondary, "IceCosmicExplorationIPC.cs");
        ImGui.Indent(Theme.PadLarge);
        ImGui.BulletText("Added \"FarmAllRelics\" case to ChangeSetting() IPC");
        ImGui.Unindent(Theme.PadLarge);

        Theme.InlineBadge("CHANGED", TagChanged);
        ImGui.TextColored(Theme.TextSecondary, "modeSelect_Standard.cs");
        ImGui.Indent(Theme.PadLarge);
        ImGui.BulletText("Added \"Farm All Relics (Auto Rotation)\" checkbox in Relic Grind Settings");
        ImGui.BulletText("Tooltip explains tier-aware rotation and buff optimization");
        ImGui.Unindent(Theme.PadLarge);

        ImGui.Spacing();
        ImGui.TextColored(Theme.TextMuted, "Config fields used (already existed): FarmAllRelics, RelicJobs");

        ImGui.Spacing();
        ImGui.Unindent(Theme.Pad);
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();
    }

    private static void DrawVersionHeader(string plugin, string title, string date)
    {
        Theme.SectionHeader($"{plugin}: {title}", VersionColor);
        ImGui.SameLine();
        ImGui.TextColored(DateColor, $"({date})");
        ImGui.Spacing();
    }
}
