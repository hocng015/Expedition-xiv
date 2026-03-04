using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Expedition.UI;

/// <summary>
/// Sidebar navigation component for the main window.
/// Replaces the top tab bar with a collapsible group sidebar.
/// </summary>
public static class Sidebar
{
    private const float Width = 200f;

    private static readonly (string Group, string Label, string[] Items)[] Groups =
    {
        ("workflow", "Workflow", new[] { "Browse", "Recipe", "Gathering", "Workflow", "Log" }),
        ("specialized", "Specialized", new[] { "Diadem", "Cosmic", "Fishing" }),
        ("info", "Info & Tools", new[] { "Insights", "Changelog", "Settings" }),
    };

    // FontAwesome icons for sidebar page items
    private static readonly Dictionary<string, FontAwesomeIcon> PageIcons = new()
    {
        ["Browse"]    = FontAwesomeIcon.Search,          // Magnifying glass
        ["Recipe"]    = FontAwesomeIcon.BookOpen,         // Open recipe book
        ["Gathering"] = FontAwesomeIcon.Hammer,           // Pickaxe / gathering tool
        ["Workflow"]  = FontAwesomeIcon.ProjectDiagram,   // Flowchart / linked steps
        ["Log"]       = FontAwesomeIcon.Scroll,           // Scroll / activity log
        ["Diadem"]    = FontAwesomeIcon.Crown,            // Crown (diadem = crown)
        ["Cosmic"]    = FontAwesomeIcon.Globe,            // Globe / celestial
        ["Fishing"]   = FontAwesomeIcon.Fish,             // Fish
        ["Insights"]  = FontAwesomeIcon.Lightbulb,        // Lightbulb / insights
        ["Changelog"] = FontAwesomeIcon.ClipboardList,    // Clipboard / updates
        ["Settings"]  = FontAwesomeIcon.Cog,              // Gear / configuration
    };

    // FontAwesome icons for sidebar group headers
    private static readonly Dictionary<string, FontAwesomeIcon> GroupIcons = new()
    {
        ["workflow"]    = FontAwesomeIcon.Tasks,           // Task list / workflow
        ["specialized"] = FontAwesomeIcon.Star,            // Star / special content
        ["info"]        = FontAwesomeIcon.InfoCircle,      // Info circle
    };

    /// <summary>
    /// Draws the sidebar and returns the selected page name.
    /// </summary>
    public static string Draw(string currentSelection, Configuration config)
    {
        var result = currentSelection;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SidebarBg);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.RoundingLarge);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 8));

        if (ImGui.BeginChild("ExpSidebar", new Vector2(Width, -1), true))
        {
            foreach (var (group, label, items) in Groups)
            {
                // Ensure the group key exists
                if (!config.SidebarGroupExpanded.ContainsKey(group))
                    config.SidebarGroupExpanded[group] = true;

                var expanded = config.SidebarGroupExpanded[group];
                var groupIcon = GroupIcons.GetValueOrDefault(group, (FontAwesomeIcon)0);
                if (Theme.SidebarGroupHeader(label, ref expanded, groupIcon))
                {
                    ImGui.Spacing();
                    foreach (var item in items)
                    {
                        var icon = PageIcons.GetValueOrDefault(item, (FontAwesomeIcon)0);
                        if (Theme.SidebarItem(item, item == currentSelection, icon))
                            result = item;
                    }
                    ImGui.Spacing();
                }

                if (expanded != config.SidebarGroupExpanded[group])
                {
                    config.SidebarGroupExpanded[group] = expanded;
                    config.Save();
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        return result;
    }
}
