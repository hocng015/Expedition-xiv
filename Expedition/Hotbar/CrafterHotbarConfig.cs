using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Expedition.Hotbar;

/// <summary>
/// Shared crafter (DOH) XHB layout for all 8 crafting jobs.
/// All entries use CraftAction slot type.
///
/// Optimized for the Lv.100 collectable/endgame rotation flow:
///   Opener → Quality buffs + touches → Byregot's finisher → Durability restore → Synthesis completion
///
/// Set 1: Every action used in the standard Lv.100 rotation (Reflect → Byregot's → Groundwork).
///   R2 Face (12-15): Highest frequency — Prep Touch, Groundwork, Innovation, Great Strides
///   R2 D-pad (8-11): Completion — Veneration, Careful Synthesis, Byregot's, Waste Not
///   L2 Face (4-7): Dawntrail essentials — Trained Perfection, Immaculate Mend, Reflect, Prudent Touch
///   L2 D-pad (0-3): Setup — Waste Not II, Manipulation (legacy), Basic Touch (combo), Standard Touch (combo)
///
/// Set 2: Situational, expert, conditional, and legacy actions.
/// </summary>
public static class CrafterHotbarConfig
{
    private const RaptureHotbarModule.HotbarSlotType CA = RaptureHotbarModule.HotbarSlotType.CraftAction;

    public static readonly HotbarSlotEntry[] Set1 =
    [
        // R2 Face (slots 12-15) — Highest frequency actions
        new(12, 100299, "Preparatory Touch",  71, CA),  // 4-5x per craft, most pressed
        new(13, 100403, "Groundwork",         72, CA),  // 4-5x per craft, main progress
        new(14, 19004,  "Innovation",         26, CA),  // 2x per craft, quality buff
        new(15, 260,    "Great Strides",      21, CA),  // 1-2x, doubles next touch

        // R2 D-pad (slots 8-11) — Completion phase
        new(8,  19297,  "Veneration",         15, CA),  // Progress buff
        new(9,  100203, "Careful Synthesis",  62, CA),  // Final progress action
        new(10, 100339, "Byregot's Blessing", 50, CA),  // Quality finisher
        new(11, 4631,   "Waste Not",          15, CA),  // Durability management

        // L2 Face (slots 4-7) — Dawntrail essentials + opener
        new(4,  100445, "Trained Perfection", 100, CA), // Free durability, used every craft
        new(5,  100443, "Immaculate Mend",     98, CA), // Full durability restore, used every craft
        new(6,  100387, "Reflect",             69, CA), // Standard opener
        new(7,  100227, "Prudent Touch",       66, CA), // Efficient quality filler

        // L2 D-pad (slots 0-3) — Setup & combos
        new(0,  4639,   "Waste Not II",       47, CA),  // Extended durability management
        new(1,  100003, "Master's Mend",       7, CA),  // Low-level durability (pre-Immaculate)
        new(2,  100002, "Basic Touch",         5, CA),  // Combo starter for Refined Touch
        new(3,  100004, "Standard Touch",     18, CA),  // Combo piece
    ];

    public static readonly HotbarSlotEntry[] Set2 =
    [
        // R2 Face (slots 12-15) — Alternative openers & synthesis
        new(12, 100379, "Muscle Memory",      54, CA),  // Alternative opener (progress-first)
        new(13, 100001, "Basic Synthesis",     1, CA),  // Low-level fallback
        new(14, 100411, "Advanced Touch",     84, CA),  // Combo finisher (Basic → Standard → Advanced)
        new(15, 100451, "Refined Touch",      92, CA),  // Combo from Basic Touch (extra IQ stack)

        // R2 D-pad (slots 8-11) — Expert & specialist
        new(8,  100419, "Heart and Soul",     86, CA),  // Specialist only
        new(9,  100441, "Quick Innovation",   96, CA),  // Specialist only
        new(10, 100235, "Focused Synthesis",  67, CA),  // Observe combo
        new(11, 100243, "Focused Touch",      68, CA),  // Observe combo

        // L2 Face (slots 4-7) — Conditional & utility
        new(4,  100128, "Precise Touch",      53, CA),  // Good/Excellent condition
        new(5,  100371, "Tricks of the Trade", 13, CA), // Good/Excellent CP recovery
        new(6,  100283, "Trained Eye",        80, CA),  // Sub-Lv.90 recipes (1-button max quality)
        new(7,  100323, "Delicate Synthesis", 76, CA),  // Progress + quality combo

        // L2 D-pad (slots 0-3) — Legacy & niche
        new(0,  100010, "Observe",            13, CA),  // Setup for Focused actions
        new(1,  4574,   "Manipulation",       65, CA),  // Legacy durability (pre-Immaculate)
        new(2,  100395, "Prudent Synthesis",  88, CA),  // Rarely used
        new(3,  100363, "Rapid Synthesis",     9, CA),  // Unreliable (60% success)
    ];
}
