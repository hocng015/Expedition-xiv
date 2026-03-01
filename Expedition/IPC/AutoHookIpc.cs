using System.Linq;

namespace Expedition.IPC;

/// <summary>
/// Checks whether the AutoHook plugin is installed and loaded.
/// AutoHook runs passively in the background — no IPC calls needed,
/// just availability detection via the installed plugins list.
/// </summary>
public sealed class AutoHookIpc
{
    public bool IsAvailable { get; private set; }

    public AutoHookIpc()
    {
        CheckAvailability();
    }

    public void CheckAvailability()
    {
        try
        {
            var installed = DalamudApi.PluginInterface.InstalledPlugins
                .FirstOrDefault(p => p.InternalName == "AutoHook" && p.IsLoaded);
            IsAvailable = installed != null;
        }
        catch
        {
            IsAvailable = false;
        }
    }
}
