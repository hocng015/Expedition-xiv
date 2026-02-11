using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// IPC subscriber for Artisan crafting plugin.
/// Wraps all known Artisan IPC endpoints for crafting automation.
/// </summary>
public sealed class ArtisanIpc : IDisposable
{
    private readonly ICallGateSubscriber<bool> getEnduranceStatus;
    private readonly ICallGateSubscriber<bool, object?> setEnduranceStatus;
    private readonly ICallGateSubscriber<bool> isListRunning;
    private readonly ICallGateSubscriber<bool> isListPaused;
    private readonly ICallGateSubscriber<bool, object?> setListPause;
    private readonly ICallGateSubscriber<bool> getStopRequest;
    private readonly ICallGateSubscriber<bool, object?> setStopRequest;
    private readonly ICallGateSubscriber<ushort, int, object?> craftItem;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<uint, string, bool, object?> changeSolver;
    private readonly ICallGateSubscriber<uint, object?> setTempSolverBackToNormal;

    public bool IsAvailable { get; private set; }

    public ArtisanIpc()
    {
        var pi = DalamudApi.PluginInterface;

        getEnduranceStatus = pi.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
        setEnduranceStatus = pi.GetIpcSubscriber<bool, object?>("Artisan.SetEnduranceStatus");
        isListRunning = pi.GetIpcSubscriber<bool>("Artisan.IsListRunning");
        isListPaused = pi.GetIpcSubscriber<bool>("Artisan.IsListPaused");
        setListPause = pi.GetIpcSubscriber<bool, object?>("Artisan.SetListPause");
        getStopRequest = pi.GetIpcSubscriber<bool>("Artisan.GetStopRequest");
        setStopRequest = pi.GetIpcSubscriber<bool, object?>("Artisan.SetStopRequest");
        craftItem = pi.GetIpcSubscriber<ushort, int, object?>("Artisan.CraftItem");
        isBusy = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        changeSolver = pi.GetIpcSubscriber<uint, string, bool, object?>("Artisan.ChangeSolver");
        setTempSolverBackToNormal = pi.GetIpcSubscriber<uint, object?>("Artisan.SetTempSolverBackToNormal");

        CheckAvailability();
    }

    public void Dispose() { }

    public void CheckAvailability()
    {
        try
        {
            isBusy.InvokeFunc();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Returns whether Artisan endurance mode is active.
    /// </summary>
    public bool GetEnduranceStatus()
    {
        try { return getEnduranceStatus.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Enables or disables Artisan endurance mode.
    /// </summary>
    public void SetEnduranceStatus(bool enabled)
    {
        try { setEnduranceStatus.InvokeAction(enabled); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to set Artisan endurance status"); }
    }

    /// <summary>
    /// Returns whether an Artisan crafting list is running.
    /// </summary>
    public bool GetIsListRunning()
    {
        try { return isListRunning.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Returns whether an Artisan crafting list is paused.
    /// </summary>
    public bool GetIsListPaused()
    {
        try { return isListPaused.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Pauses or resumes the current Artisan crafting list.
    /// </summary>
    public void SetListPause(bool paused)
    {
        try { setListPause.InvokeAction(paused); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to set Artisan list pause"); }
    }

    /// <summary>
    /// Returns whether a stop has been requested.
    /// </summary>
    public bool GetStopRequest()
    {
        try { return getStopRequest.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Requests Artisan to stop or resume crafting.
    /// </summary>
    public void SetStopRequest(bool stop)
    {
        try { setStopRequest.InvokeAction(stop); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to set Artisan stop request"); }
    }

    /// <summary>
    /// Instructs Artisan to craft a specific recipe a given number of times.
    /// This selects the recipe and starts endurance mode.
    /// </summary>
    public void CraftItem(ushort recipeId, int amount)
    {
        try { craftItem.InvokeAction(recipeId, amount); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, $"Failed to start Artisan craft: recipe={recipeId}, amount={amount}"); }
    }

    /// <summary>
    /// Returns true if Artisan is currently doing anything (endurance, list, crafting).
    /// </summary>
    public bool GetIsBusy()
    {
        try { return isBusy.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Changes the solver used for a specific recipe.
    /// </summary>
    public void ChangeSolver(uint recipeId, string solverName, bool temporary = true)
    {
        try { changeSolver.InvokeAction(recipeId, solverName, temporary); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to change Artisan solver"); }
    }

    /// <summary>
    /// Resets a temporarily changed solver back to the default for a recipe.
    /// </summary>
    public void ResetSolver(uint recipeId)
    {
        try { setTempSolverBackToNormal.InvokeAction(recipeId); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to reset Artisan solver"); }
    }
}
