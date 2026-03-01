using Expedition.Hotbar;
using Expedition.PlayerState;

namespace Expedition.Gathering;

/// <summary>
/// Writes Fisher actions to cross hotbar slots.
/// Delegates to the shared <see cref="HotbarService"/>.
/// </summary>
public static class FisherHotbarService
{
    /// <summary>
    /// Populates XHB Set 1 and Set 2 with Fisher actions from <see cref="FisherHotbarConfig"/>.
    /// Actions above the player's current Fisher level are skipped (slot left empty).
    /// </summary>
    public static HotbarService.ConfigResult ConfigureHotbar()
    {
        return HotbarService.Configure(JobSwitchManager.FSH, FisherHotbarConfig.Set1, FisherHotbarConfig.Set2);
    }
}
