using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

using Expedition.Fishing;
using Expedition.IPC;

namespace Expedition.UI;

/// <summary>
/// Draws the Fishing tab for the Freestyle Catch feature.
/// Follows CosmicTab pattern: static class with Draw(Expedition plugin).
/// </summary>
public static class FishingTab
{
    // Teal accent for fishing theme
    private static readonly Vector4 TealAccent = new(0.20f, 0.75f, 0.70f, 1.00f);
    private static readonly Vector4 TealDim = new(0.10f, 0.40f, 0.38f, 1.00f);

    public static void Draw(Expedition plugin)
    {
        var session = plugin.FishingSession;
        var autoHook = plugin.Ipc.AutoHook;
        var vnavmesh = plugin.Ipc.Vnavmesh;

        DrawHeader(session);
        ImGui.Spacing();
        DrawSessionCard(session);
        ImGui.Spacing();
        DrawSettingsCard();
        ImGui.Spacing();
        DrawRequirementsCard(autoHook, vnavmesh);
    }

    // ──────────────────────────────────────────────
    // Header Banner
    // ──────────────────────────────────────────────

    private static void DrawHeader(FishingSession session)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        const float headerH = 48f;

        // Banner background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + headerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.06f, 0.14f, 0.14f, 1.00f)),
            4f);

        // Teal accent line
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + 2),
            ImGui.ColorConvertFloat4ToU32(TealAccent),
            4f);

        // Title
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 6f),
            ImGui.ColorConvertFloat4ToU32(TealAccent), "Freestyle Catch  —  Automated Fishing");

        // Status line
        var statusText = session.IsActive
            ? $"Session: {session.GetDurationString()}  |  {session.TotalCatches} catches  |  {session.GetCatchRate():F1}/hr"
            : "Idle  |  AutoHook handles hooksets & re-cast";

        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 26f),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), statusText);

        // Start/Stop button
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - 140, pos.Y + 10f));
        if (session.IsActive)
        {
            if (Theme.DangerButton("Stop##fish", new Vector2(120, 28)))
                session.Stop();
        }
        else
        {
            if (Theme.PrimaryButton("Start##fish", new Vector2(120, 28)))
                session.Start();
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + headerH));
        ImGui.Dummy(new Vector2(avail, 0));
    }

    // ──────────────────────────────────────────────
    // Session Card
    // ──────────────────────────────────────────────

    private static void DrawSessionCard(FishingSession session)
    {
        Theme.BeginCardAuto("FishingSession");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Session", TealAccent);
            ImGui.Spacing();
            ImGui.Spacing();

            // State indicator
            var stateColor = session.State switch
            {
                FishingState.Fishing => Theme.Success,
                FishingState.PreFishing or FishingState.NavigatingToSpot or FishingState.ValidatingPrereqs => Theme.Warning,
                FishingState.WaitingForGp => Theme.Accent,
                FishingState.Error => Theme.Error,
                FishingState.Stopped => Theme.TextMuted,
                _ => Theme.TextSecondary,
            };
            Theme.StatusDot(stateColor, session.StatusMessage.Length > 0 ? session.StatusMessage : "Idle");

            ImGui.Spacing();

            // Stats
            var duration = session.StartTime.HasValue ? session.GetDurationString() : "--";
            var catches = session.TotalCatches > 0 ? session.TotalCatches.ToString() : "--";
            var rate = session.TotalCatches > 0 ? $"{session.GetCatchRate():F1}/hr" : "--";

            var player = DalamudApi.ObjectTable.LocalPlayer;
            var gpText = player != null ? $"{player.CurrentGp}/{player.MaxGp}" : "--";

            Theme.KeyValue("Duration:", $"  {duration}");
            Theme.KeyValue("Catches:", $"  {catches}");
            Theme.KeyValue("Rate:", $"  {rate}");
            Theme.KeyValue("GP:", $"  {gpText}");

            // Target spot
            if (session.TargetSpot != null)
            {
                ImGui.Spacing();
                Theme.KeyValue("Spot:", $"  {session.TargetSpot.Name} ({session.TargetSpot.Distance:F0}y)", TealAccent);
            }

            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }

    // ──────────────────────────────────────────────
    // Settings Card
    // ──────────────────────────────────────────────

    private static void DrawSettingsCard()
    {
        var config = Expedition.Config;

        Theme.BeginCardAuto("FishingSettings");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Settings", TealAccent);
            ImGui.Spacing();
            ImGui.Spacing();

            var patienceII = config.FishingUsePatienceII;
            if (ImGui.Checkbox("Patience II (560 GP)", ref patienceII))
            {
                config.FishingUsePatienceII = patienceII;
                config.Save();
            }

            var chum = config.FishingUseChum;
            if (ImGui.Checkbox("Chum (100 GP)", ref chum))
            {
                config.FishingUseChum = chum;
                config.Save();
            }

            var thaliaks = config.FishingUseThaliaksFavor;
            if (ImGui.Checkbox("Thaliak's Favor (GP recovery)", ref thaliaks))
            {
                config.FishingUseThaliaksFavor = thaliaks;
                config.Save();
            }

            var cordials = config.FishingUseCordials;
            if (ImGui.Checkbox("Cordials (Phase 2)", ref cordials))
            {
                config.FishingUseCordials = cordials;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cordial item usage not yet implemented. Coming in Phase 2.");

            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }

    // ──────────────────────────────────────────────
    // Requirements Card
    // ──────────────────────────────────────────────

    private static void DrawRequirementsCard(AutoHookIpc autoHook, VnavmeshIpc vnavmesh)
    {
        Theme.BeginCardAuto("FishingReqs");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Requirements", TealAccent);
            ImGui.Spacing();
            ImGui.Spacing();

            // AutoHook
            Theme.StatusDot(
                autoHook.IsAvailable ? Theme.Success : Theme.Error,
                autoHook.IsAvailable ? "AutoHook: Ready" : "AutoHook: Not Found");

            // vnavmesh
            Theme.StatusDot(
                vnavmesh.IsAvailable ? Theme.Success : Theme.Warning,
                vnavmesh.IsAvailable ? "vnavmesh: Ready" : "vnavmesh: Not Found (navigation disabled)");

            // Job check
            var player = DalamudApi.ObjectTable.LocalPlayer;
            var isFisher = player != null && player.ClassJob.RowId == 18;
            Theme.StatusDot(
                isFisher ? Theme.Success : Theme.Error,
                isFisher ? "Job: Fisher" : "Job: Not Fisher (switch to FSH)");

            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }
}
