using Dalamud.Game.Text;

namespace Expedition.IPC;

/// <summary>
/// Helper for sending chat commands to interact with plugins
/// that don't expose full IPC (e.g., GBR gather commands).
/// </summary>
public static class ChatIpc
{
    /// <summary>
    /// Sends a chat command. Used to invoke GBR gather commands
    /// since the IPC doesn't have a "gather this specific item" method.
    /// </summary>
    public static void SendCommand(string command)
    {
        DalamudApi.Log.Debug($"Sending command: {command}");
        DalamudApi.CommandManager.ProcessCommand(command);
    }

    /// <summary>
    /// Tells GatherBuddy Reborn to gather a specific item by name.
    /// Uses the /gather chat command.
    /// </summary>
    public static void GatherItem(string itemName)
    {
        SendCommand($"/gather {itemName}");
    }

    /// <summary>
    /// Tells GatherBuddy Reborn to gather a specific BTN item.
    /// </summary>
    public static void GatherBotanist(string itemName)
    {
        SendCommand($"/gatherbtn {itemName}");
    }

    /// <summary>
    /// Tells GatherBuddy Reborn to gather a specific MIN item.
    /// </summary>
    public static void GatherMiner(string itemName)
    {
        SendCommand($"/gathermin {itemName}");
    }

    /// <summary>
    /// Tells GatherBuddy Reborn to start collectable gathering.
    /// </summary>
    public static void StartCollectableGathering()
    {
        SendCommand("/gbc collect");
    }

    /// <summary>
    /// Tells GatherBuddy Reborn to stop collectable gathering.
    /// </summary>
    public static void StopCollectableGathering()
    {
        SendCommand("/gbc collectstop");
    }
}
