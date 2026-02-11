using System.Numerics;
using ImGuiNET;

using Expedition.Scheduling;
using Expedition.Workflow;

namespace Expedition.UI;

/// <summary>
/// Small floating overlay showing current workflow status.
/// Visible while a workflow is running. Shows Eorzean time and health warnings.
/// </summary>
public sealed class OverlayWindow
{
    private readonly WorkflowEngine engine;

    public OverlayWindow(WorkflowEngine engine)
    {
        this.engine = engine;
    }

    public void Draw()
    {
        if (engine.CurrentState == WorkflowState.Idle) return;

        ImGui.SetNextWindowSize(new Vector2(320, 100), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);

        var flags = ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("Expedition Status###ExpeditionOverlay", flags))
        {
            ImGui.End();
            return;
        }

        // Eorzean time display
        if (Expedition.Config.ShowEorzeanTime)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), EorzeanTime.FormatCurrentTime());
            ImGui.SameLine();
        }

        var stateColor = engine.CurrentState switch
        {
            WorkflowState.Completed => new Vector4(0.3f, 1f, 0.3f, 1f),
            WorkflowState.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
            WorkflowState.Paused => new Vector4(1f, 0.7f, 0.2f, 1f),
            _ => new Vector4(1f, 0.9f, 0.4f, 1f),
        };

        ImGui.TextColored(stateColor, $"{engine.CurrentPhase}");
        ImGui.SameLine();
        if (engine.CurrentRecipe != null)
            ImGui.Text($"- {engine.CurrentRecipe.ItemName} x{engine.TargetQuantity}");

        ImGui.TextWrapped(engine.StatusMessage);

        // Health indicators
        if (Expedition.Config.ShowDetailedStatus)
        {
            if (engine.LastDurabilityReport != null && engine.LastDurabilityReport.LowestPercent < 50)
            {
                var durColor = engine.LastDurabilityReport.LowestPercent < 20
                    ? new Vector4(1f, 0.3f, 0.3f, 1f)
                    : new Vector4(1f, 0.9f, 0.4f, 1f);
                ImGui.TextColored(durColor, $"Durability: {engine.LastDurabilityReport.LowestPercent}%");
            }

            if (engine.LastBuffDiagnostic != null)
            {
                ImGui.TextColored(
                    engine.LastBuffDiagnostic.HasFood ? new Vector4(0.3f, 1f, 0.3f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    engine.LastBuffDiagnostic.FoodStatusText);
            }
        }

        // Controls
        if (engine.CurrentState == WorkflowState.Paused)
        {
            if (ImGui.SmallButton("Resume"))
                engine.Resume();
            ImGui.SameLine();
        }

        if (engine.CurrentState != WorkflowState.Completed && engine.CurrentState != WorkflowState.Error)
        {
            if (ImGui.SmallButton("Stop"))
                engine.Cancel();
        }

        ImGui.End();
    }
}
