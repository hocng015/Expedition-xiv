namespace Expedition.Hotbar;

/// <summary>
/// Miner (MIN) XHB layout.
/// Set 1: Core gathering actions. Set 2: Collectable actions.
/// Action IDs sourced from Lumina Action sheet / GatherBuddy Reborn.
/// </summary>
public static class MinerHotbarConfig
{
    public static readonly HotbarSlotEntry[] Set1 =
    [
        // L2 D-pad (slots 0-3)
        new(0,  227,   "Prospect",               1),
        new(1,  232,   "Solid Reason",            1),
        new(2,  4073,  "Bountiful Yield",        24),
        new(3,  241,   "King's Yield II",        50),

        // L2 Face (slots 4-7)
        new(4,  295,   "Sharp Vision III",       46),
        new(5,  25589, "Mountaineer's Gift II",  68),
        new(6,  280,   "Twelve's Bounty",        50),
        new(7,  4589,  "The Giving Land",        74),

        // R2 D-pad (slots 8-11)
        new(8,  238,   "Truth of Mountains",     46),
        new(9,  26521, "Wise to the World",      80),
        new(10, 21203, "Nald'thal's Tidings",    78),
        new(11, 21177, "Mountaineer's Gift I",   50),

        // R2 Face (slots 12-15)
        new(12, 4081,  "Luck of the Mountaineer", 55),
        new(13, 228,   "Lay of the Land",          1),
        new(14, 291,   "Lay of the Land II",      50),
    ];

    public static readonly HotbarSlotEntry[] Set2 =
    [
        // L2 D-pad (slots 0-3)
        new(0, 240,   "Collect",               50),
        new(1, 22182, "Scour",                 50),
        new(2, 22185, "Scrutiny",              58),
        new(3, 22183, "Brazen Prospector",     71),

        // L2 Face (slots 4-7)
        new(4, 22184, "Meticulous Prospector", 79),
        new(7, 239,   "King's Yield",          50),
    ];
}
