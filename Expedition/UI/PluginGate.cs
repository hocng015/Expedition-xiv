using System.Numerics;
using Dalamud.Bindings.ImGui;

using Expedition.IPC;

namespace Expedition.UI;

/// <summary>
/// Draws a blocking gate when a required optional plugin is not installed.
/// Shows the feature name, which plugin is needed, what it does, and a refresh button.
/// </summary>
public static class PluginGate
{
    public static void Draw(string featureName, string pluginName, string description, string? repoUrl, IpcManager ipc)
    {
        var avail = ImGui.GetContentRegionAvail();

        // Center the content vertically
        var totalHeight = 200f;
        var startY = Math.Max(0, (avail.Y - totalHeight) / 2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + startY);

        // Center horizontally
        var contentWidth = Math.Min(450f, avail.X - 40f);
        var startX = (avail.X - contentWidth) / 2;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + startX);
        ImGui.BeginGroup();

        // Feature title
        var title = $"{featureName}";
        var titleSize = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (contentWidth - titleSize.X) / 2);
        ImGui.TextColored(Theme.TextPrimary, title);
        ImGui.Spacing();

        // Plugin required message
        var reqText = $"Requires: {pluginName}";
        var reqSize = ImGui.CalcTextSize(reqText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (contentWidth - reqSize.X) / 2);
        ImGui.TextColored(Theme.Warning, reqText);
        ImGui.Spacing();
        ImGui.Spacing();

        // Description
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
        ImGui.TextColored(Theme.TextMuted, description);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Spacing();

        // Repo URL if provided
        if (!string.IsNullOrEmpty(repoUrl))
        {
            var repoLabel = $"Add this repo to Dalamud:";
            ImGui.TextColored(Theme.TextMuted, repoLabel);
            ImGui.TextColored(Theme.Accent, repoUrl);
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, $"Install {pluginName} from Dalamud's plugin installer.");
            ImGui.Spacing();
        }

        ImGui.Spacing();

        // Refresh button
        var btnLabel = "Refresh Plugin Status";
        var btnWidth = ImGui.CalcTextSize(btnLabel).X + 32;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (contentWidth - btnWidth) / 2);
        if (ImGui.Button(btnLabel, new Vector2(btnWidth, 28)))
        {
            ipc.RefreshAvailability();
        }

        ImGui.EndGroup();
    }
}
