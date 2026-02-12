using System.Numerics;
using Dalamud.Bindings.ImGui;

using Expedition.Crafting;
using Expedition.Gathering;
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

    public OverlayWindow(WorkflowEngine engine, Expedition plugin)
    {
        this.engine = engine;
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (engine.CurrentState == WorkflowState.Idle) return;

        ImGui.SetNextWindowSize(new Vector2(340, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
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

        DrawHeader();
        DrawProgress();
        DrawHealthRow();
        DrawControls();

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
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - timeWidth + 10);
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
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40);
                ImGui.TextColored(Theme.TextMuted, $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}");
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

                Theme.ProgressBar(fraction, Theme.Accent, $"Gathering: {completed}/{total}", 14);
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

                Theme.ProgressBar(fraction, Theme.Gold, $"Crafting: {completed}/{total}", 14);
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

        if (state == WorkflowState.Paused)
        {
            if (Theme.PrimaryButton("Resume", new Vector2(70, 22)))
                engine.Resume();
            ImGui.SameLine(0, Theme.PadSmall);
        }

        if (Theme.DangerButton("Stop", new Vector2(50, 22)))
            engine.Cancel();
    }
}
