using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Expedition.Activation;
using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.RecipeResolver;
using Expedition.Scheduling;
using Expedition.Workflow;

namespace Expedition.UI;

/// <summary>
/// Compact floating overlay showing current workflow status at a glance.
/// Visible while a workflow is running. Shows phase, progress, and health.
/// </summary>
public sealed class OverlayWindow
{
    private readonly WorkflowEngine engine;
    private readonly Expedition plugin;

    /// <summary>How long to keep the overlay visible after completion/error before auto-dismissing (seconds).</summary>
    private const float DismissDelay = 5f;

    /// <summary>Scale factor applied to text and UI elements in the overlay.</summary>
    private const float Scale = 1.35f;

    private DateTime? terminalStateTime;

    public OverlayWindow(WorkflowEngine engine, Expedition plugin)
    {
        this.engine = engine;
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (!ActivationService.IsActivated) return;
        if (engine.CurrentState == WorkflowState.Idle) return;

        // Auto-dismiss overlay after workflow ends (Completed/Error)
        var isTerminal = engine.CurrentState is WorkflowState.Completed or WorkflowState.Error;
        if (isTerminal)
        {
            terminalStateTime ??= DateTime.Now;
            if ((DateTime.Now - terminalStateTime.Value).TotalSeconds > DismissDelay)
                return;
        }
        else
        {
            terminalStateTime = null;
        }

        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.10f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.30f, 0.60f));

        var flags = ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoCollapse;

        if (!ImGui.Begin("Expedition###ExpeditionOverlay", flags))
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
            ImGui.End();
            return;
        }

        // Scale up all text and elements
        ImGui.SetWindowFontScale(Scale);

        DrawHeader();
        DrawMaterials();
        DrawProgress();
        DrawHealthRow();
        DrawControls();

        ImGui.SetWindowFontScale(1.0f);

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.End();
    }

    private void DrawHeader()
    {
        // Top line: Phase + Eorzean time
        var stateColor = engine.CurrentState switch
        {
            WorkflowState.Completed => Theme.Success,
            WorkflowState.Error => Theme.Error,
            WorkflowState.Paused => Theme.PhasePaused,
            _ => Theme.Accent,
        };

        var phaseLabel = engine.CurrentState switch
        {
            WorkflowState.Completed => "Complete",
            WorkflowState.Error => "Error",
            WorkflowState.Paused => "Paused",
            _ => engine.CurrentPhase.ToString(),
        };

        Theme.StatusDot(stateColor, phaseLabel);

        if (Expedition.Config.ShowEorzeanTime)
        {
            var timeText = EorzeanTime.FormatCurrentTime();
            var timeWidth = ImGui.CalcTextSize(timeText).X;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - timeWidth + 14);
            ImGui.TextColored(Theme.TextMuted, timeText);
        }

        // Target item
        if (engine.CurrentRecipe != null)
        {
            ImGui.TextColored(Theme.Gold, engine.CurrentRecipe.ItemName);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextSecondary, $"x{engine.TargetQuantity}");

            // Elapsed time
            if (engine.StartTime.HasValue)
            {
                var elapsed = DateTime.Now - engine.StartTime.Value;
                var timeStr = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                var timeWidth = ImGui.CalcTextSize(timeStr).X;
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - timeWidth + 14);
                ImGui.TextColored(Theme.TextMuted, timeStr);
            }
        }

        // Status message (truncated for overlay)
        if (!string.IsNullOrEmpty(engine.StatusMessage))
        {
            var msg = engine.StatusMessage;
            if (msg.Length > 80) msg = msg[..77] + "...";
            ImGui.TextColored(Theme.TextSecondary, msg);
        }

        ImGui.Spacing();
    }

    private void DrawMaterials()
    {
        var resolved = engine.ResolvedRecipe;
        if (resolved == null) return;

        var otherMats = resolved.OtherMaterials;
        if (otherMats == null || otherMats.Count == 0) return;

        var vendorItems = otherMats.Where(m => m.IsVendorItem && m.VendorInfo != null).ToList();
        var dropItems = otherMats.Where(m => m.IsMobDrop && m.MobDrops != null && m.MobDrops.Count > 0 && (!m.IsVendorItem || m.VendorInfo == null)).ToList();

        if (vendorItems.Count == 0 && dropItems.Count == 0) return;

        ImGui.Separator();
        ImGui.Spacing();

        // Vendor items
        foreach (var mat in vendorItems)
        {
            var v = mat.VendorInfo!;
            var remaining = mat.QuantityRemaining;
            var qtyColor = remaining == 0 ? Theme.Success : Theme.TextPrimary;

            ImGui.TextColored(qtyColor, $"x{mat.QuantityNeeded}");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Gold, mat.ItemName);

            ImGui.SameLine();
            var loc = string.IsNullOrEmpty(v.ZoneName) ? v.NpcName : $"{v.NpcName}, {v.ZoneName}";
            ImGui.TextColored(Theme.TextMuted, $"— {loc}");

            if (v.HasMapCoords)
            {
                ImGui.SameLine();
                if (ImGui.Button($"Map##ov{mat.ItemId}", new Vector2(50 * Scale, 24 * Scale)))
                    OpenMapPin(v.TerritoryTypeId, v.MapId, v.MapX, v.MapY);
            }
        }

        // Mob drop items
        foreach (var mat in dropItems)
        {
            var remaining = mat.QuantityRemaining;
            var qtyColor = remaining == 0 ? Theme.Success : Theme.TextPrimary;
            var topMob = mat.MobDrops![0];

            ImGui.TextColored(qtyColor, $"x{mat.QuantityNeeded}");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, mat.ItemName);

            ImGui.SameLine();
            var zone = string.IsNullOrEmpty(topMob.ZoneName) ? "" : $", {topMob.ZoneName}";
            var lvl = string.IsNullOrEmpty(topMob.Level) ? "" : $" Lv.{topMob.Level}";
            ImGui.TextColored(Theme.TextMuted, $"— {topMob.MobName}{lvl}{zone}");

            if (topMob.HasMapCoords)
            {
                ImGui.SameLine();
                if (ImGui.Button($"Map##ov{mat.ItemId}", new Vector2(50 * Scale, 24 * Scale)))
                    OpenMapPin(topMob.TerritoryTypeId, topMob.MapId, topMob.MapX, topMob.MapY);
            }
        }

        ImGui.Spacing();
    }

    private static void OpenMapPin(uint territoryTypeId, uint mapId, float mapX, float mapY)
    {
        try
        {
            var payload = new MapLinkPayload(territoryTypeId, mapId, mapX, mapY);
            DalamudApi.GameGui.OpenMapWithMapLink(payload);
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, $"Failed to open map pin at ({mapX:F1}, {mapY:F1})");
        }
    }

    private void DrawProgress()
    {
        // Show a compact progress bar based on current phase
        if (engine.CurrentPhase == WorkflowPhase.Gathering)
        {
            var orch = plugin.GatheringOrchestrator;
            if (orch.Tasks.Count > 0)
            {
                var completed = orch.Tasks.Count(t => t.Status == GatheringTaskStatus.Completed);
                var total = orch.Tasks.Count;
                var fraction = total > 0 ? (float)completed / total : 0;

                var current = orch.CurrentTask;
                if (current != null && current.QuantityNeeded > 0)
                {
                    // Blend in partial progress of current item
                    var itemFraction = (float)current.QuantityGathered / current.QuantityNeeded;
                    fraction = (completed + itemFraction) / total;
                }

                Theme.ProgressBar(fraction, Theme.Accent, $"Gathering: {completed}/{total}", 20);
                ImGui.Spacing();
            }
        }
        else if (engine.CurrentPhase == WorkflowPhase.Crafting)
        {
            var orch = plugin.CraftingOrchestrator;
            if (orch.Tasks.Count > 0)
            {
                var completed = orch.Tasks.Count(t => t.Status == CraftingTaskStatus.Completed);
                var total = orch.Tasks.Count;
                var fraction = total > 0 ? (float)completed / total : 0;

                Theme.ProgressBar(fraction, Theme.Gold, $"Crafting: {completed}/{total}", 20);
                ImGui.Spacing();
            }
        }
    }

    private void DrawHealthRow()
    {
        if (!Expedition.Config.ShowDetailedStatus) return;

        var showDur = engine.LastDurabilityReport != null && engine.LastDurabilityReport.LowestPercent < 50;
        var showFood = engine.LastBuffDiagnostic != null;

        if (!showDur && !showFood) return;

        if (showDur)
        {
            var dur = engine.LastDurabilityReport!;
            var durColor = dur.LowestPercent < 20 ? Theme.Error : Theme.Warning;
            ImGui.TextColored(durColor, $"Dur: {dur.LowestPercent}%");
            if (showFood) ImGui.SameLine(0, Theme.PadLarge);
        }

        if (showFood)
        {
            var buff = engine.LastBuffDiagnostic!;
            var foodColor = buff.HasFood
                ? (buff.FoodExpiringSoon ? Theme.Warning : Theme.Success)
                : Theme.TextMuted;
            ImGui.TextColored(foodColor, buff.HasFood ? $"Food: {buff.FoodRemainingSeconds:F0}s" : "No food");
        }

        ImGui.Spacing();
    }

    private void DrawControls()
    {
        var state = engine.CurrentState;
        if (state == WorkflowState.Completed || state == WorkflowState.Error) return;

        ImGui.Separator();
        ImGui.Spacing();

        var btnH = 30 * Scale;

        if (state == WorkflowState.Paused)
        {
            if (Theme.PrimaryButton("Resume", new Vector2(100 * Scale, btnH)))
                engine.Resume();
            ImGui.SameLine(0, Theme.Pad);
        }

        if (Theme.DangerButton("Stop", new Vector2(70 * Scale, btnH)))
            engine.Cancel();
    }
}
