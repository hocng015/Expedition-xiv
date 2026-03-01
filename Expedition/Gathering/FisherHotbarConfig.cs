using Expedition.Hotbar;

namespace Expedition.Gathering;

/// <summary>
/// Defines the Fisher action layout for cross hotbar (XHB) auto-configuration.
/// Actions are organized into two XHB sets for controller play:
///   Set 1 (hotbarId 10): Core fishing loop
///   Set 2 (hotbarId 11): Utility and collectables
/// </summary>
public static class FisherHotbarConfig
{
    /// <summary>
    /// XHB Set 1 — Core fishing loop.
    /// Bottom-right (R2 Face, slots 12-15) is the primary area for the
    /// core cast/hook loop. Bottom-left (L2 D-pad, slots 0-3) is secondary.
    ///
    /// Visual layout on screen:
    ///   [D-pad 0-3] [Face 4-7]  |SET|  [D-pad 8-11] [Face 12-15]
    ///    bottom-left  inner-left         inner-right   bottom-right
    /// </summary>
    public static readonly HotbarSlotEntry[] Set1 =
    [
        // R2 Face (slots 12-15) — Bottom right: Core cast & hook loop
        new(12, 289,   "Cast",              1),
        new(13, 296,   "Hook",              1),
        new(14, 4179,  "Precision Hookset", 53),
        new(15, 4103,  "Powerful Hookset",  51),

        // R2 D-pad (slots 8-11) — Inner right: Mooch & multi-hook
        new(8,  297,   "Mooch",            25),
        new(9,  268,   "Mooch II",         63),
        new(10, 269,   "Double Hook",      65),
        new(11, 27523, "Triple Hook",      90),

        // L2 D-pad (slots 0-3) — Bottom left: Key buffs
        new(0, 4106,  "Patience II",    60),
        new(1, 4104,  "Chum",           25),
        new(2, 26806, "Prize Catch",    81),
        new(3, 4100,  "Snagging",       36),

        // L2 Face (slots 4-7) — Inner left: Situational utility
        new(4, 4595,  "Surface Slap",    71),
        new(5, 4596,  "Identical Cast",  63),
        new(6, 26804, "Thaliak's Favor", 71),
        new(7, 26805, "Makeshift Bait",  71),
    ];

    /// <summary>
    /// XHB Set 2 — Utility and collectables.
    /// Same outer-edge priority: bottom-left (L2 D-pad) and
    /// bottom-right (R2 Face) get the most-used actions.
    /// </summary>
    public static readonly HotbarSlotEntry[] Set2 =
    [
        // L2 D-pad (slots 0-3) — Bottom left: Core collectable/utility
        new(0, 4101,  "Collector's Glove", 50),
        new(1, 26880, "Big Game Fishing",  80),
        new(2, 4105,  "Fish Eyes",         57),
        new(3, 300,   "Release",            1),

        // L2 Face (slots 4-7) — Inner left: Dawntrail lures
        new(4, 36113, "Baited Breath",    91),
        new(5, 37593, "Spareful Hand",    95),
        new(6, 37594, "Ambitious Lure",   95),
        new(7, 37595, "Modest Lure",      95),

        // R2 Face (slot 12) — Bottom right: Veteran Trade
        new(12, 7906, "Veteran Trade", 15),
    ];
}
