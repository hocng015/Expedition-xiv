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
    private static readonly Vector4 TagSecurity = new(1.00f, 0.45f, 0.45f, 1.00f);

    public static void Draw()
    {
        ImGui.Spacing();

        // --- Cosmic: Fishing Preset Override ---
        DrawVersionHeader("Cosmic", "Fishing Preset Override", "2026-03-04");

        Theme.BeginCardAuto("cl_cosmic_fishing_override");
        ImGui.Indent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Mission-Aware Preset Injection");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Monitors ICE's Cosmic Exploration state and injects optimized AutoHook presets per mission.");
        ImGui.TextColored(Theme.TextSecondary, "Overrides ICE's default fishing presets without modifying ICE — pure IPC-based.");
        ImGui.TextColored(Theme.TextSecondary, "State machine: Idle -> WaitingToInject -> Active -> Idle (polled every 500ms).");
        ImGui.TextColored(Theme.TextSecondary, "Configurable injection delay (default 800ms) to avoid racing ICE's preset load.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Per-Mission Preset Store");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Import AH4_ preset strings for specific mission IDs (highest priority).");
        ImGui.TextColored(Theme.TextSecondary, "Type-level defaults as fallback (VarietyTimeAttack, TimeAttack, LargestSize, etc.).");
        ImGui.TextColored(Theme.TextSecondary, "Persisted as cosmic_fishing_presets.json in plugin config directory.");
        ImGui.TextColored(Theme.TextSecondary, "No overrides = ICE's built-in presets are used (zero behavior change by default).");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Fishing Tab in Cosmic UI");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Master toggle, live monitor status, preset import, and override management.");
        ImGui.TextColored(Theme.TextSecondary, "Per-mission and per-type override lists with individual clear buttons.");
        ImGui.TextColored(Theme.TextSecondary, "Advanced injection delay slider (200-2000ms).");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("CHANGED", TagChanged);
        ImGui.TextColored(Theme.TextPrimary, "AutoHook IPC Expanded");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Added ActivateCustomPreset() for pre-compressed AH4_ strings.");
        ImGui.TextColored(Theme.TextSecondary, "Added SelectPreset() and SwapBaitById() IPC subscribers.");
        ImGui.Unindent(Theme.Pad);

        ImGui.Spacing();
        ImGui.Unindent(Theme.Pad);
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // --- Fishing: Performance Optimization ---
        DrawVersionHeader("Fishing", "Performance Optimization + Multi-Hook", "2026-03-03");

        Theme.BeginCardAuto("cl_fishing_optimization");
        ImGui.Indent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "AutoHook Preset Integration");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Automatically creates and activates an AutoHook preset on session start.");
        ImGui.TextColored(Theme.TextSecondary, "Configures Double Hook and Triple Hook for all bite strengths.");
        ImGui.TextColored(Theme.TextSecondary, "Precision Hook for weak bites, Powerful Hook for strong/legendary.");
        ImGui.TextColored(Theme.TextSecondary, "Presets are cleaned up automatically when the session stops.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagNew);
        ImGui.TextColored(Theme.TextPrimary, "Cordial Support");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Automatically uses Cordials / Hi-Cordials from inventory when GP is low.");
        ImGui.TextColored(Theme.TextSecondary, "Respects 5-minute cooldown, prefers Hi-Cordials by default (configurable).");
        ImGui.TextColored(Theme.TextSecondary, "Inventory counts shown in session stats.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("CHANGED", TagChanged);
        ImGui.TextColored(Theme.TextPrimary, "Tighter Timing");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Update interval: 0.5s -> 0.25s (faster state transitions)");
        ImGui.TextColored(Theme.TextSecondary, "Action delay: 1.5s -> 0.8s (less dead time between actions)");
        ImGui.TextColored(Theme.TextSecondary, "Buff check: 5.0s -> 1.0s (react faster to expired buffs)");
        ImGui.TextColored(Theme.TextSecondary, "Stall timeout: 10s -> 5s (re-cast sooner on missed bite)");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("CHANGED", TagChanged);
        ImGui.TextColored(Theme.TextPrimary, "Smarter GP Management");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Continues fishing without Patience II when GP is too low (Chum-only or naked cast).");
        ImGui.TextColored(Theme.TextSecondary, "Only enters WaitingForGp state when GP is critically low (<100).");
        ImGui.TextColored(Theme.TextSecondary, "Resumes with partial buffs as soon as enough GP is available.");
        ImGui.Unindent(Theme.Pad);

        ImGui.Spacing();
        ImGui.Unindent(Theme.Pad);
        Theme.EndCardAuto();

        ImGui.Spacing();
        ImGui.Spacing();

        // --- v2.0: Session Token Authentication ---
        DrawVersionHeader("v2.0", "Session Token Authentication", "2026-03-02");

        Theme.BeginCardAuto("cl_auth_est");
        ImGui.Indent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagSecurity);
        ImGui.TextColored(Theme.TextPrimary, "Ed25519 Session Tokens");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Replaced legacy key validation with cryptographic session tokens (EST-).");
        ImGui.TextColored(Theme.TextSecondary, "Tokens are signed server-side with Ed25519 and verified locally with an embedded public key.");
        ImGui.TextColored(Theme.TextSecondary, "Machine-bound: tokens are tied to your hardware fingerprint.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("NEW", TagSecurity);
        ImGui.TextColored(Theme.TextPrimary, "Automatic Token Refresh");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Tokens refresh automatically every 4 hours in the background.");
        ImGui.TextColored(Theme.TextSecondary, "24-hour grace period allows offline operation if the server is unreachable.");
        ImGui.TextColored(Theme.TextSecondary, "No manual re-activation needed under normal circumstances.");
        ImGui.Unindent(Theme.Pad);
        ImGui.Spacing();

        Theme.InlineBadge("CHANGED", TagChanged);
        ImGui.TextColored(Theme.TextPrimary, "Legacy Key Migration");
        ImGui.Indent(Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Existing EXP- keys are automatically exchanged for EST- tokens on startup.");
        ImGui.TextColored(Theme.TextSecondary, "30-day migration window for legacy keys.");
        ImGui.Unindent(Theme.Pad);

        ImGui.Spacing();
        ImGui.Unindent(Theme.Pad);
        Theme.EndCardAuto();

        ImGui.Spacing();
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
