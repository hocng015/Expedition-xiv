using Expedition.PlayerState;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Expedition.Hotbar;

/// <summary>
/// Generic XHB auto-configuration service.
/// For the currently active job, uses SetAndSaveSlot (updates live display + save data).
/// For non-active jobs, uses WriteSavedSlot (persists to save data only) with careful
/// backup/restore of the live slot to prevent contaminating the active job's display.
/// </summary>
public static class HotbarService
{
    public const uint XhbSet1 = 10;
    public const uint XhbSet2 = 11;
    public const int SlotsPerXhb = 16;

    public readonly record struct ConfigResult(int ActionsSet, int ActionsSkipped, string? Error);

    /// <summary>
    /// Configures XHB Set 1 and Set 2 for a single job.
    /// Actions above the player's job level are skipped.
    /// </summary>
    public static unsafe ConfigResult Configure(uint classJobId, HotbarSlotEntry[] set1, HotbarSlotEntry[] set2)
    {
        var jobLevel = JobSwitchManager.GetPlayerJobLevel(classJobId);
        if (jobLevel < 0)
            return new ConfigResult(0, 0, "Could not read job level. Are you logged in?");

        RaptureHotbarModule* hotbarModule;
        try
        {
            hotbarModule = RaptureHotbarModule.Instance();
            if (hotbarModule == null)
                return new ConfigResult(0, 0, "RaptureHotbarModule is not available.");
        }
        catch (Exception ex)
        {
            return new ConfigResult(0, 0, $"Failed to access hotbar module: {ex.Message}");
        }

        var totalSet = 0;
        var totalSkipped = 0;
        var currentJob = JobSwitchManager.GetCurrentJobId();
        var isActiveJob = classJobId == currentJob;

        ClearCrossHotbar(hotbarModule, XhbSet1, classJobId, isActiveJob);
        ClearCrossHotbar(hotbarModule, XhbSet2, classJobId, isActiveJob);

        PopulateSlots(hotbarModule, XhbSet1, classJobId, isActiveJob, set1, jobLevel, ref totalSet, ref totalSkipped);
        PopulateSlots(hotbarModule, XhbSet2, classJobId, isActiveJob, set2, jobLevel, ref totalSet, ref totalSkipped);

        if (isActiveJob)
        {
            hotbarModule->LoadSavedHotbar(currentJob, XhbSet1);
            hotbarModule->LoadSavedHotbar(currentJob, XhbSet2);
        }

        DalamudApi.Log.Information(
            $"[Hotbar] Configured XHB for ClassJob {classJobId}: {totalSet} actions set, {totalSkipped} skipped (Lv{jobLevel}){(isActiveJob ? " [active]" : "")}");

        return new ConfigResult(totalSet, totalSkipped, null);
    }

    /// <summary>
    /// Applies the same hotbar layout to multiple jobs (e.g. all 8 crafters).
    /// Returns aggregate totals across all jobs.
    /// </summary>
    public static unsafe ConfigResult ConfigureMultiple(uint[] classJobIds, HotbarSlotEntry[] set1, HotbarSlotEntry[] set2)
    {
        RaptureHotbarModule* hotbarModule;
        try
        {
            hotbarModule = RaptureHotbarModule.Instance();
            if (hotbarModule == null)
                return new ConfigResult(0, 0, "RaptureHotbarModule is not available.");
        }
        catch (Exception ex)
        {
            return new ConfigResult(0, 0, $"Failed to access hotbar module: {ex.Message}");
        }

        var totalSet = 0;
        var totalSkipped = 0;
        var currentJob = JobSwitchManager.GetCurrentJobId();

        // Process non-active jobs FIRST, then the active job LAST.
        // Non-active jobs use WriteSavedSlot which requires a live slot pointer as scratch.
        // We backup/restore the live slot around each write to prevent corrupting the display.
        uint activeJobId = 0;
        var activeJobLevel = -1;

        foreach (var classJobId in classJobIds)
        {
            var jobLevel = JobSwitchManager.GetPlayerJobLevel(classJobId);
            if (jobLevel < 0)
            {
                DalamudApi.Log.Warning($"[Hotbar] Could not read level for ClassJob {classJobId}, skipping.");
                continue;
            }

            if (classJobId == currentJob)
            {
                // Defer active job to be processed last
                activeJobId = classJobId;
                activeJobLevel = jobLevel;
                continue;
            }

            ClearCrossHotbar(hotbarModule, XhbSet1, classJobId, false);
            ClearCrossHotbar(hotbarModule, XhbSet2, classJobId, false);

            PopulateSlots(hotbarModule, XhbSet1, classJobId, false, set1, jobLevel, ref totalSet, ref totalSkipped);
            PopulateSlots(hotbarModule, XhbSet2, classJobId, false, set2, jobLevel, ref totalSet, ref totalSkipped);

            DalamudApi.Log.Information(
                $"[Hotbar] Configured XHB for ClassJob {classJobId} (Lv{jobLevel})");
        }

        // Now process the active job using SetAndSaveSlot (live + save in one call).
        if (activeJobId != 0 && activeJobLevel >= 0)
        {
            ClearCrossHotbar(hotbarModule, XhbSet1, activeJobId, true);
            ClearCrossHotbar(hotbarModule, XhbSet2, activeJobId, true);

            PopulateSlots(hotbarModule, XhbSet1, activeJobId, true, set1, activeJobLevel, ref totalSet, ref totalSkipped);
            PopulateSlots(hotbarModule, XhbSet2, activeJobId, true, set2, activeJobLevel, ref totalSet, ref totalSkipped);

            DalamudApi.Log.Information(
                $"[Hotbar] Configured XHB for ClassJob {activeJobId} (Lv{activeJobLevel}) [active]");
        }

        // Reload display for the current job to ensure it's fully refreshed
        hotbarModule->LoadSavedHotbar(currentJob, XhbSet1);
        hotbarModule->LoadSavedHotbar(currentJob, XhbSet2);

        return new ConfigResult(totalSet, totalSkipped, null);
    }

    /// <summary>
    /// Emergency clear: wipes XHB Set 1 and Set 2 for the currently active job.
    /// Removes all slots (including black/corrupted ones) using SetAndSaveSlot + LoadSavedHotbar.
    /// </summary>
    public static unsafe ConfigResult ClearActiveJob()
    {
        RaptureHotbarModule* hotbarModule;
        try
        {
            hotbarModule = RaptureHotbarModule.Instance();
            if (hotbarModule == null)
                return new ConfigResult(0, 0, "RaptureHotbarModule is not available.");
        }
        catch (Exception ex)
        {
            return new ConfigResult(0, 0, $"Failed to access hotbar module: {ex.Message}");
        }

        var currentJob = JobSwitchManager.GetCurrentJobId();
        if (currentJob == 0)
            return new ConfigResult(0, 0, "Could not determine current job. Are you logged in?");

        var cleared = 0;
        for (uint slot = 0; slot < SlotsPerXhb; slot++)
        {
            hotbarModule->SetAndSaveSlot(XhbSet1, slot, RaptureHotbarModule.HotbarSlotType.Empty, 0);
            hotbarModule->SetAndSaveSlot(XhbSet2, slot, RaptureHotbarModule.HotbarSlotType.Empty, 0);
            cleared += 2;
        }

        hotbarModule->LoadSavedHotbar(currentJob, XhbSet1);
        hotbarModule->LoadSavedHotbar(currentJob, XhbSet2);

        DalamudApi.Log.Information($"[Hotbar] Emergency cleared XHB Set 1 & 2 for ClassJob {currentJob} ({cleared} slots)");
        return new ConfigResult(cleared, 0, null);
    }

    /// <summary>
    /// Clears all 16 slots in a cross hotbar.
    /// For non-active jobs: backs up the live slot, sets Empty, writes to saved data, restores live slot.
    /// For the active job: uses SetAndSaveSlot which writes to live + save directly.
    /// </summary>
    private static unsafe void ClearCrossHotbar(RaptureHotbarModule* module, uint hotbarId, uint classJobId, bool isActiveJob)
    {
        for (uint slot = 0; slot < SlotsPerXhb; slot++)
        {
            if (isActiveJob)
            {
                module->SetAndSaveSlot(hotbarId, slot, RaptureHotbarModule.HotbarSlotType.Empty, 0);
            }
            else
            {
                var gameSlot = module->GetSlotById(hotbarId, slot);
                // Backup the live slot so the active job's display isn't corrupted
                var backup = *gameSlot;
                gameSlot->Set(RaptureHotbarModule.HotbarSlotType.Empty, 0);
                module->WriteSavedSlot(classJobId, hotbarId, slot, gameSlot, false, false);
                // Restore the live slot
                *gameSlot = backup;
            }
        }
    }

    /// <summary>
    /// Populates hotbar slots from a config entry array, skipping actions above the player's level.
    /// For non-active jobs: backs up the live slot, sets the action, writes to saved data, restores live slot.
    /// For the active job: uses SetAndSaveSlot which writes to live + save directly.
    /// </summary>
    private static unsafe void PopulateSlots(
        RaptureHotbarModule* module,
        uint hotbarId,
        uint classJobId,
        bool isActiveJob,
        HotbarSlotEntry[] entries,
        int jobLevel,
        ref int totalSet,
        ref int totalSkipped)
    {
        foreach (var entry in entries)
        {
            if (entry.RequiredLevel > jobLevel)
            {
                totalSkipped++;
                continue;
            }

            if (isActiveJob)
            {
                module->SetAndSaveSlot(hotbarId, (uint)entry.SlotIndex, entry.SlotType, entry.ActionId);
            }
            else
            {
                var gameSlot = module->GetSlotById(hotbarId, (uint)entry.SlotIndex);
                // Backup the live slot so the active job's display isn't corrupted
                var backup = *gameSlot;
                gameSlot->Set(entry.SlotType, entry.ActionId);
                module->WriteSavedSlot(classJobId, hotbarId, (uint)entry.SlotIndex, gameSlot, false, false);
                // Restore the live slot
                *gameSlot = backup;
            }

            totalSet++;
        }
    }
}
