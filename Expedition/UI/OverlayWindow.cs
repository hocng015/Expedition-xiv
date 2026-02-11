using System.Numerics;
using ImGuiNET;

using Expedition.Workflow;

namespace Expedition.UI;

/// <summary>
/// Small floating overlay showing current workflow status.
/// Visible while a workflow is running.
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

        ImGui.SetNextWindowSize(new Vector2(300, 80), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);

        var flags = ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("Expedition Status###ExpeditionOverlay", flags))
        {
            ImGui.End();
            return;
        }

        var stateColor = engine.CurrentState switch
        {
            WorkflowState.Completed => new Vector4(0.3f, 1f, 0.3f, 1f),
            WorkflowState.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
            _ => new Vector4(1f, 0.9f, 0.4f, 1f),
        };

        ImGui.TextColored(stateColor, $"{engine.CurrentPhase}");
        ImGui.SameLine();
        if (engine.CurrentRecipe != null)
            ImGui.Text($"- {engine.CurrentRecipe.ItemName} x{engine.TargetQuantity}");

        ImGui.TextWrapped(engine.StatusMessage);

        if (engine.CurrentState != WorkflowState.Completed && engine.CurrentState != WorkflowState.Error)
        {
            if (ImGui.SmallButton("Stop"))
                engine.Cancel();
        }

        ImGui.End();
    }
}
