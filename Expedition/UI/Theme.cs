using System.Numerics;
using ImGuiNET;

namespace Expedition.UI;

/// <summary>
/// Centralized design system for the Expedition UI.
/// Provides a consistent color palette, spacing, and reusable drawing helpers.
/// </summary>
public static class Theme
{
    // --- Color Palette ---

    // Brand / Accents
    public static readonly Vector4 Accent = new(0.40f, 0.70f, 1.00f, 1.00f);       // Soft blue
    public static readonly Vector4 AccentDim = new(0.30f, 0.50f, 0.75f, 1.00f);     // Muted blue
    public static readonly Vector4 Gold = new(0.92f, 0.80f, 0.35f, 1.00f);          // Warm gold
    public static readonly Vector4 GoldDim = new(0.70f, 0.60f, 0.25f, 1.00f);       // Muted gold

    // Semantic
    public static readonly Vector4 Success = new(0.30f, 0.85f, 0.45f, 1.00f);       // Green
    public static readonly Vector4 SuccessDim = new(0.20f, 0.60f, 0.30f, 1.00f);
    public static readonly Vector4 Warning = new(0.95f, 0.75f, 0.20f, 1.00f);       // Amber
    public static readonly Vector4 WarningDim = new(0.70f, 0.55f, 0.15f, 1.00f);
    public static readonly Vector4 Error = new(0.95f, 0.35f, 0.35f, 1.00f);         // Red
    public static readonly Vector4 ErrorDim = new(0.70f, 0.25f, 0.25f, 1.00f);
    public static readonly Vector4 Critical = new(1.00f, 0.20f, 0.20f, 1.00f);      // Bright red

    // Neutrals
    public static readonly Vector4 TextPrimary = new(1.00f, 1.00f, 1.00f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.70f, 0.70f, 0.70f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.50f, 0.50f, 0.50f, 1.00f);
    public static readonly Vector4 TextDisabled = new(0.35f, 0.35f, 0.35f, 1.00f);

    // Specialty tags
    public static readonly Vector4 Collectable = new(0.40f, 0.80f, 1.00f, 1.00f);  // Cyan
    public static readonly Vector4 Expert = new(0.95f, 0.45f, 0.45f, 1.00f);        // Salmon
    public static readonly Vector4 Specialist = new(0.85f, 0.55f, 0.85f, 1.00f);    // Purple
    public static readonly Vector4 MasterBook = new(0.95f, 0.75f, 0.35f, 1.00f);    // Amber
    public static readonly Vector4 TimedNode = new(0.60f, 0.85f, 1.00f, 1.00f);     // Light blue

    // Phase colors
    public static readonly Vector4 PhaseIdle = new(0.45f, 0.45f, 0.45f, 1.00f);
    public static readonly Vector4 PhaseActive = new(0.40f, 0.70f, 1.00f, 1.00f);
    public static readonly Vector4 PhaseComplete = new(0.30f, 0.85f, 0.45f, 1.00f);
    public static readonly Vector4 PhasePaused = new(0.95f, 0.65f, 0.15f, 1.00f);

    // Backgrounds (used with PushStyleColor)
    public static readonly Vector4 CardBg = new(0.14f, 0.14f, 0.16f, 1.00f);
    public static readonly Vector4 CardBgHover = new(0.18f, 0.18f, 0.22f, 1.00f);
    public static readonly Vector4 SectionBg = new(0.10f, 0.10f, 0.12f, 0.60f);
    public static readonly Vector4 ProgressBg = new(0.15f, 0.15f, 0.18f, 1.00f);

    // --- Spacing Constants ---

    public const float Pad = 8f;
    public const float PadSmall = 4f;
    public const float PadLarge = 16f;
    public const float SectionGap = 12f;
    public const float BadgeHeight = 18f;

    // --- Reusable Drawing Helpers ---

    /// <summary>
    /// Draws a section header with a colored left accent bar.
    /// </summary>
    public static void SectionHeader(string label, Vector4? color = null)
    {
        var c = color ?? Accent;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + 3, pos.Y + ImGui.GetTextLineHeight()),
            ImGui.ColorConvertFloat4ToU32(c));

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10);
        ImGui.TextColored(c, label);
    }

    /// <summary>
    /// Draws a separator with a centered label.
    /// </summary>
    public static void LabeledSeparator(string label)
    {
        ImGui.Spacing();
        var avail = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label).X;
        var lineWidth = (avail - textSize - 20) / 2;

        if (lineWidth > 0)
        {
            var pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            var y = pos.Y + ImGui.GetTextLineHeight() / 2;

            drawList.AddLine(
                new Vector2(pos.X, y),
                new Vector2(pos.X + lineWidth, y),
                ImGui.ColorConvertFloat4ToU32(TextMuted));
            drawList.AddLine(
                new Vector2(pos.X + lineWidth + textSize + 20, y),
                new Vector2(pos.X + avail, y),
                ImGui.ColorConvertFloat4ToU32(TextMuted));
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + lineWidth + 10);
        ImGui.TextColored(TextSecondary, label);
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws a small colored badge/tag.
    /// </summary>
    public static void Badge(string text, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var padding = new Vector2(6, 2);
        var bgColor = new Vector4(color.X, color.Y, color.Z, 0.20f);
        var rounding = 3f;

        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + textSize.X + padding.X * 2, pos.Y + textSize.Y + padding.Y * 2),
            ImGui.ColorConvertFloat4ToU32(bgColor),
            rounding);

        drawList.AddRect(
            pos,
            new Vector2(pos.X + textSize.X + padding.X * 2, pos.Y + textSize.Y + padding.Y * 2),
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.50f)),
            rounding);

        ImGui.SetCursorPos(ImGui.GetCursorPos() + padding);
        ImGui.TextColored(color, text);
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding.X);
    }

    /// <summary>
    /// Inline badge that advances the cursor properly for SameLine usage.
    /// </summary>
    public static void InlineBadge(string text, Vector4 color)
    {
        Badge(text, color);
        ImGui.SameLine(0, PadSmall);
    }

    /// <summary>
    /// Draws a styled progress bar with custom colors.
    /// </summary>
    public static void ProgressBar(float fraction, Vector4 fillColor, string? overlay = null, float height = 0)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        var h = height > 0 ? height : ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        // Background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + h),
            ImGui.ColorConvertFloat4ToU32(ProgressBg),
            3f);

        // Fill
        if (fraction > 0)
        {
            drawList.AddRectFilled(
                pos,
                new Vector2(pos.X + avail * fraction, pos.Y + h),
                ImGui.ColorConvertFloat4ToU32(fillColor),
                3f);
        }

        // Overlay text
        if (overlay != null)
        {
            var textSize = ImGui.CalcTextSize(overlay);
            var textPos = new Vector2(
                pos.X + (avail - textSize.X) / 2,
                pos.Y + (h - textSize.Y) / 2);
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(TextPrimary), overlay);
        }

        ImGui.Dummy(new Vector2(avail, h));
    }

    /// <summary>
    /// Draws a compact status indicator (colored dot + text).
    /// </summary>
    public static void StatusDot(Vector4 color, string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var radius = 4f;
        var center = new Vector2(pos.X + radius, pos.Y + ImGui.GetTextLineHeight() / 2);

        drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(color));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + radius * 2 + 6);
        ImGui.Text(label);
    }

    /// <summary>
    /// Draws key-value pair with muted key and bright value.
    /// </summary>
    public static void KeyValue(string key, string value, Vector4? valueColor = null)
    {
        ImGui.TextColored(TextSecondary, key);
        ImGui.SameLine();
        ImGui.TextColored(valueColor ?? TextPrimary, value);
    }

    /// <summary>
    /// Begins a visual card (subtle background region).
    /// Must pair with EndCard().
    /// </summary>
    public static bool BeginCard(string id, float height = 0)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        var size = new Vector2(-1, height);
        var result = ImGui.BeginChild(id, size, ImGuiChildFlags.Border);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        return result;
    }

    /// <summary>
    /// Ends a card region.
    /// </summary>
    public static void EndCard()
    {
        ImGui.EndChild();
    }

    /// <summary>
    /// Draws a styled button with custom colors.
    /// </summary>
    public static bool ColoredButton(string label, Vector2 size, Vector4 bgColor, Vector4 textColor)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(
            Math.Min(1, bgColor.X + 0.10f),
            Math.Min(1, bgColor.Y + 0.10f),
            Math.Min(1, bgColor.Z + 0.10f),
            bgColor.W));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(
            Math.Max(0, bgColor.X - 0.05f),
            Math.Max(0, bgColor.Y - 0.05f),
            Math.Max(0, bgColor.Z - 0.05f),
            bgColor.W));
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        var result = ImGui.Button(label, size);

        ImGui.PopStyleColor(4);
        return result;
    }

    /// <summary>
    /// Draws a primary action button (blue).
    /// </summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        if (size == default) size = new Vector2(0, 30);
        return ColoredButton(label, size, new Vector4(0.20f, 0.45f, 0.80f, 1.00f), TextPrimary);
    }

    /// <summary>
    /// Draws a danger action button (red).
    /// </summary>
    public static bool DangerButton(string label, Vector2 size = default)
    {
        if (size == default) size = new Vector2(0, 30);
        return ColoredButton(label, size, new Vector4(0.70f, 0.20f, 0.20f, 1.00f), TextPrimary);
    }

    /// <summary>
    /// Draws a secondary/muted action button.
    /// </summary>
    public static bool SecondaryButton(string label, Vector2 size = default)
    {
        if (size == default) size = new Vector2(0, 30);
        return ColoredButton(label, size, new Vector4(0.25f, 0.25f, 0.30f, 1.00f), TextSecondary);
    }

    /// <summary>
    /// Draws a tooltip with a help marker (?).
    /// </summary>
    public static void HelpMarker(string description)
    {
        ImGui.SameLine();
        ImGui.TextColored(TextMuted, "(?)");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(300);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Draws a phase step in a pipeline visualization.
    /// </summary>
    public static void PipelineStep(string label, bool isActive, bool isComplete, bool isFirst = false)
    {
        if (!isFirst)
        {
            ImGui.SameLine(0, 2);
            ImGui.TextColored(isComplete ? PhaseComplete : TextMuted, ">");
            ImGui.SameLine(0, 2);
        }

        var color = isComplete ? PhaseComplete : isActive ? PhaseActive : TextMuted;
        ImGui.TextColored(color, label);
    }

    /// <summary>
    /// Gets the color for a severity level.
    /// </summary>
    public static Vector4 SeverityColor(PlayerState.Severity severity)
    {
        return severity switch
        {
            PlayerState.Severity.Critical => Critical,
            PlayerState.Severity.Error => Error,
            PlayerState.Severity.Warning => Warning,
            _ => TextSecondary,
        };
    }
}
