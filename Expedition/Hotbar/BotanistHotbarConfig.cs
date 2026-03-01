namespace Expedition.Hotbar;

/// <summary>
/// Botanist (BTN) XHB layout.
/// Set 1: Core gathering actions. Set 2: Collectable actions.
/// Action IDs sourced from Lumina Action sheet / GatherBuddy Reborn.
/// </summary>
public static class BotanistHotbarConfig
{
    public static readonly HotbarSlotEntry[] Set1 =
    [
        // L2 D-pad (slots 0-3)
        new(0,  210,   "Triangulate",          1),
        new(1,  215,   "Ageless Words",        1),
        new(2,  4087,  "Bountiful Harvest",   24),
        new(3,  224,   "Blessed Harvest II",  50),

        // L2 Face (slots 4-7)
        new(4,  294,   "Field Mastery III",   50),
        new(5,  25590, "Pioneer's Gift II",   68),
        new(6,  282,   "Twelve's Bounty",     50),
        new(7,  4590,  "The Giving Land",     74),

        // R2 D-pad (slots 8-11)
        new(8,  221,   "Truth of Forests",    46),
        new(9,  26522, "Wise to the World",   80),
        new(10, 21204, "Nophica's Tidings",   78),
        new(11, 21178, "Pioneer's Gift I",    50),

        // R2 Face (slots 12-15)
        new(12, 4095,  "Luck of the Pioneer",  55),
        new(13, 211,   "Arbor Call",             1),
        new(14, 290,   "Arbor Call II",         50),
    ];

    public static readonly HotbarSlotEntry[] Set2 =
    [
        // L2 D-pad (slots 0-3)
        new(0, 815,   "Collect",              50),
        new(1, 22186, "Scour",                50),
        new(2, 22189, "Scrutiny",             58),
        new(3, 22187, "Brazen Woodsman",      71),

        // L2 Face (slots 4-7)
        new(4, 22188, "Meticulous Woodsman",  79),
        new(7, 222,   "Blessed Harvest",      50),
    ];
}
