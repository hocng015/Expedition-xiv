using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// Manages AutoHook plugin integration via IPC.
/// Handles availability detection and preset management for multi-hook (Double/Triple Hook).
///
/// AutoHook preset format: JSON -> GZip -> Base64 -> "AH4_" prefix.
/// Presets control hookset selection, Double/Triple Hook, auto-cast buffs, and cordials.
/// </summary>
public sealed class AutoHookIpc
{
    private const string ExportPrefix = "AH4_";

    public bool IsAvailable { get; private set; }
    public bool PresetActive { get; private set; }

    // IPC subscribers — AutoHook exposes both anonymous (temporary) and import (permanent) preset endpoints
    private readonly ICallGateSubscriber<bool, object>? setPluginState;
    private readonly ICallGateSubscriber<string, object>? createAnonymousPreset;
    private readonly ICallGateSubscriber<string, object>? importAndSelectPreset;
    private readonly ICallGateSubscriber<object>? deleteAnonymousPresets;
    private readonly ICallGateSubscriber<string, object>? setPreset;
    private readonly ICallGateSubscriber<uint, Task<bool>>? swapBaitById;

    public AutoHookIpc()
    {
        try
        {
            var pi = DalamudApi.PluginInterface;
            setPluginState = pi.GetIpcSubscriber<bool, object>("AutoHook.SetPluginState");
            createAnonymousPreset = pi.GetIpcSubscriber<string, object>("AutoHook.CreateAndSelectAnonymousPreset");
            importAndSelectPreset = pi.GetIpcSubscriber<string, object>("AutoHook.ImportAndSelectPreset");
            deleteAnonymousPresets = pi.GetIpcSubscriber<object>("AutoHook.DeleteAllAnonymousPresets");
            setPreset = pi.GetIpcSubscriber<string, object>("AutoHook.SetPreset");
            swapBaitById = pi.GetIpcSubscriber<uint, Task<bool>>("AutoHook.SwapBaitById");
            DalamudApi.Log.Information("[AutoHook] IPC subscribers initialized.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] Failed to initialize IPC: {ex.Message}");
        }

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

    /// <summary>
    /// Enables or disables AutoHook plugin state.
    /// </summary>
    public void SetPluginEnabled(bool enabled)
    {
        if (!IsAvailable) return;
        try
        {
            setPluginState?.InvokeAction(enabled);
            DalamudApi.Log.Information($"[AutoHook] Plugin state set to: {enabled}");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] SetPluginState failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates and activates an anonymous AutoHook preset optimized for Expedition fishing.
    /// Tries CreateAndSelectAnonymousPreset first (temporary), falls back to ImportAndSelectPreset.
    /// Configures: Precision/Powerful hooksets, Double Hook, Triple Hook, auto-cast Patience II + Chum.
    /// </summary>
    public bool ActivateExpeditionPreset()
    {
        if (!IsAvailable)
        {
            DalamudApi.Log.Warning("[AutoHook] Cannot activate preset: AutoHook not available.");
            return false;
        }

        try
        {
            var presetJson = BuildExpeditionPresetJson();
            DalamudApi.Log.Debug($"[AutoHook] Preset JSON length: {presetJson.Length}");

            var compressed = CompressPreset(presetJson);
            DalamudApi.Log.Debug($"[AutoHook] Compressed preset length: {compressed.Length}, prefix: {compressed[..Math.Min(10, compressed.Length)]}");

            // Try anonymous preset first (temporary, cleaned up on stop)
            var success = false;
            try
            {
                createAnonymousPreset?.InvokeAction(compressed);
                success = true;
                DalamudApi.Log.Information("[AutoHook] Expedition preset activated via CreateAndSelectAnonymousPreset.");
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Warning($"[AutoHook] CreateAndSelectAnonymousPreset failed: {ex.Message}");

                // Fallback to ImportAndSelectPreset
                try
                {
                    importAndSelectPreset?.InvokeAction(compressed);
                    success = true;
                    DalamudApi.Log.Information("[AutoHook] Expedition preset activated via ImportAndSelectPreset (fallback).");
                }
                catch (Exception ex2)
                {
                    DalamudApi.Log.Warning($"[AutoHook] ImportAndSelectPreset also failed: {ex2.Message}");
                }
            }

            PresetActive = success;
            return success;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] Failed to build/compress preset: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes all anonymous presets (cleanup on session stop).
    /// </summary>
    public void CleanupPresets()
    {
        if (!IsAvailable) return;

        try
        {
            deleteAnonymousPresets?.InvokeAction();
            PresetActive = false;
            DalamudApi.Log.Information("[AutoHook] Anonymous presets cleaned up.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Activates a pre-compressed AH4_ preset string (from the Cosmic fishing preset store).
    /// Unlike ActivateExpeditionPreset() which builds a generic preset from code, this takes
    /// an already-compressed AH4_ string and pushes it directly to AutoHook.
    /// </summary>
    public bool ActivateCustomPreset(string ah4CompressedString)
    {
        if (!IsAvailable)
        {
            DalamudApi.Log.Warning("[AutoHook] Cannot activate custom preset: AutoHook not available.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(ah4CompressedString) || !ah4CompressedString.StartsWith(ExportPrefix))
        {
            DalamudApi.Log.Warning($"[AutoHook] Invalid preset string (must start with '{ExportPrefix}').");
            return false;
        }

        try
        {
            // Try anonymous preset first (temporary, cleaned up on stop)
            try
            {
                createAnonymousPreset?.InvokeAction(ah4CompressedString);
                DalamudApi.Log.Information("[AutoHook] Custom preset activated via CreateAndSelectAnonymousPreset.");
                return true;
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Warning($"[AutoHook] CreateAndSelectAnonymousPreset failed: {ex.Message}");

                // Fallback to ImportAndSelectPreset
                try
                {
                    importAndSelectPreset?.InvokeAction(ah4CompressedString);
                    DalamudApi.Log.Information("[AutoHook] Custom preset activated via ImportAndSelectPreset (fallback).");
                    return true;
                }
                catch (Exception ex2)
                {
                    DalamudApi.Log.Warning($"[AutoHook] ImportAndSelectPreset also failed: {ex2.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] Failed to activate custom preset: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Activates multiple AH4_ presets sequentially with a small delay between each.
    /// Cleans up existing anonymous presets first.
    /// Returns the number of presets successfully activated.
    /// </summary>
    public int ActivateCustomPresets(List<string> ah4Strings)
    {
        if (!IsAvailable || ah4Strings.Count == 0) return 0;

        var count = 0;
        foreach (var preset in ah4Strings)
        {
            if (ActivateCustomPreset(preset))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Selects a named preset that already exists in AutoHook.
    /// </summary>
    public void SelectPreset(string presetName)
    {
        if (!IsAvailable) return;
        try
        {
            setPreset?.InvokeAction(presetName);
            DalamudApi.Log.Information($"[AutoHook] Selected preset: {presetName}");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] SetPreset failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Swaps the equipped bait to the specified bait item ID via AutoHook IPC.
    /// Returns a task that resolves to true if the swap was successful.
    /// </summary>
    public async Task<bool> SwapBait(uint baitId)
    {
        if (!IsAvailable) return false;
        try
        {
            if (swapBaitById == null) return false;
            var result = await swapBaitById.InvokeFunc(baitId);
            DalamudApi.Log.Debug($"[AutoHook] SwapBaitById({baitId}): {result}");
            return result;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[AutoHook] SwapBaitById failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Builds the Expedition AutoHook preset JSON.
    /// Uses a catch-all bait config (BaitFish.Id = -1) to apply to any bait.
    /// Configures:
    /// - Precision Hook for weak bites, Powerful Hook for strong/legendary bites (with Patience)
    /// - Double Hook enabled for all bite strengths
    /// - Triple Hook enabled for all bite strengths
    /// - Auto-cast: Cast Line, Patience II (560 GP), Chum (100 GP)
    ///
    /// Matches AutoHook's Newtonsoft.Json serialization: NullValueHandling.Ignore (omit nulls).
    /// </summary>
    private static string BuildExpeditionPresetJson()
    {
        // Build JSON manually to match AutoHook's Newtonsoft.Json property names exactly.
        // AutoHook uses [JsonProperty("...")] annotations -- property names must match.
        // AutoHook serializer uses NullValueHandling.Ignore — null fields are OMITTED, not "null".

        var uniqueId = Guid.NewGuid().ToString();
        var baitUniqueId = Guid.NewGuid().ToString();

        // Hook type IDs (matching AHHookType enum)
        const int Precision = 4179;
        const int Powerful = 4103;
        const int DoubleHookType = 27523;
        const int TripleHookType = 27524;

        var normalHook = BuildHooksetBlock(0, Precision, Powerful, DoubleHookType, TripleHookType);
        var intuitionHook = BuildHooksetBlock(762, Precision, Powerful, DoubleHookType, TripleHookType);

        return $@"{{
  ""UniqueId"": ""{uniqueId}"",
  ""PresetName"": ""Expedition"",
  ""ListOfBaits"": [{{
    ""UniqueId"": ""{baitUniqueId}"",
    ""Enabled"": true,
    ""BaitFish"": {{ ""Id"": -1 }},
    ""NormalHook"": {normalHook},
    ""IntuitionHook"": {intuitionHook}
  }}],
  ""ListOfMooch"": [],
  ""ListOfFish"": [],
  ""AutoCastsCfg"": {{
    ""EnableAll"": true,
    ""DontCancelMooch"": true,
    ""TurnCollectOffWithoutAnimCancel"": false,
    ""CastLine"": {{ ""Enabled"": true }},
    ""CastPatience"": {{
      ""Enabled"": true,
      ""Id"": 4106,
      ""GpThreshold"": 560,
      ""GpThresholdAbove"": true
    }},
    ""CastChum"": {{
      ""Enabled"": true,
      ""GpThreshold"": 100,
      ""GpThresholdAbove"": true
    }}
  }}
}}";
    }

    private static string BuildBiteConfig(int hookType)
    {
        return $@"{{
      ""HooksetEnabled"": true,
      ""EnableHooksetSwap"": false,
      ""HookTimerEnabled"": false,
      ""MinHookTimer"": 0.0,
      ""MaxHookTimer"": 0.0,
      ""ChumTimerEnabled"": false,
      ""ChumMinHookTimer"": 0.0,
      ""ChumMaxHookTimer"": 0.0,
      ""OnlyWhenActiveSlap"": false,
      ""OnlyWhenNotActiveSlap"": false,
      ""OnlyWhenActiveIdentical"": false,
      ""OnlyWhenNotActiveIdentical"": false,
      ""PrizeCatchReq"": false,
      ""PrizeCatchNotReq"": false,
      ""HooksetType"": {hookType}
    }}";
    }

    private static string BuildHooksetBlock(int requiredStatus, int precision, int powerful, int doubleHook, int tripleHook)
    {
        return $@"{{
      ""RequiredStatus"": {requiredStatus},
      ""PatienceWeak"": {BuildBiteConfig(precision)},
      ""PatienceStrong"": {BuildBiteConfig(powerful)},
      ""PatienceLegendary"": {BuildBiteConfig(powerful)},
      ""UseDoubleHook"": true,
      ""LetFishEscapeDoubleHook"": false,
      ""DoubleWeak"": {BuildBiteConfig(doubleHook)},
      ""DoubleStrong"": {BuildBiteConfig(doubleHook)},
      ""DoubleLegendary"": {BuildBiteConfig(doubleHook)},
      ""UseTripleHook"": true,
      ""LetFishEscapeTripleHook"": false,
      ""TripleWeak"": {BuildBiteConfig(tripleHook)},
      ""TripleStrong"": {BuildBiteConfig(tripleHook)},
      ""TripleLegendary"": {BuildBiteConfig(tripleHook)},
      ""TimeoutMax"": 0.0,
      ""ChumTimeoutMax"": 0.0,
      ""StopAfterCaught"": false,
      ""StopAfterResetCount"": false,
      ""StopAfterCaughtLimit"": 1,
      ""StopFishingStep"": 0,
      ""UseCustomStatusHook"": false
    }}";
    }

    /// <summary>
    /// Compresses a JSON string into AutoHook's AH4_ format (GZip + Base64 + prefix).
    /// Matches AutoHookExporter.ExportPreset() exactly.
    /// </summary>
    private static string CompressPreset(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gs = new GZipStream(ms, CompressionMode.Compress))
        {
            gs.Write(bytes, 0, bytes.Length);
        }
        return ExportPrefix + Convert.ToBase64String(ms.ToArray());
    }
}
