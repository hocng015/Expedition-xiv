using System.Reflection;
using Dalamud.Plugin;

namespace Expedition.IPC;

/// <summary>
/// Uses .NET reflection to manipulate GatherBuddy Reborn's auto-gather lists
/// at runtime. This allows Expedition to programmatically add items to GBR's
/// gather queue without requiring manual user configuration.
///
/// GBR's IPC only exposes basic AutoGather on/off and item identification.
/// The auto-gather list system (which controls what items AutoGather walks to
/// and gathers) has no IPC. This class uses the same reflection pattern that
/// GBR itself uses to import from Artisan.
/// </summary>
public sealed class GatherBuddyListManager
{
    private const string GbrPluginName = "GatherbuddyReborn";
    private const string ExpeditionListName = "[Expedition]";
    private const BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private object? gbrPluginInstance;
    private object? listsManager;
    private object? gameData;
    private Type? autoGatherListType;
    private MethodInfo? addItemMethod;
    private MethodInfo? addListMethod;
    private MethodInfo? deleteListMethod;
    private MethodInfo? saveMethod;
    private MethodInfo? setActiveItemsMethod;
    private PropertyInfo? gatherablesProp;
    private PropertyInfo? fishesProp;
    private PropertyInfo? listsProp;
    private PropertyInfo? listNameProp;
    private PropertyInfo? listEnabledProp;
    private PropertyInfo? listDescriptionProp;
    private MethodInfo? gatherablesTryGetValue;
    private MethodInfo? fishesTryGetValue;

    public bool IsInitialized { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Item IDs that were skipped during the last SetGatherList call because
    /// they couldn't be found in GBR's Gatherables/Fishes dictionaries.
    /// The orchestrator uses this to fall back to command-only gathering for these items.
    /// </summary>
    public HashSet<uint> SkippedItemIds { get; } = new();

    /// <summary>
    /// Initializes reflection handles to GBR's internal types.
    /// Must be called after GBR is loaded.
    /// </summary>
    public bool Initialize()
    {
        try
        {
            // Step 1: Get the GBR plugin instance via Dalamud's plugin manager
            if (!TryGetDalamudPlugin(GbrPluginName, out gbrPluginInstance) || gbrPluginInstance == null)
            {
                LastError = "Could not find GatherBuddyReborn plugin instance.";
                DalamudApi.Log.Warning(LastError);
                return false;
            }

            var gbrType = gbrPluginInstance.GetType();

            // Step 2: Get GameData (try static property first, then instance)
            gameData = FindMember(gbrType, null, "GameData");
            gameData ??= FindMember(gbrType, gbrPluginInstance, "GameData");
            if (gameData == null)
            {
                LastError = "Could not find GatherBuddy.GameData.";
                DalamudApi.Log.Warning(LastError);
                LogTypeStructure(gbrType, "GBR plugin (for GameData)");
                return false;
            }

            // Step 3: Get Gatherables and Fishes dictionaries from GameData
            var gameDataType = gameData.GetType();
            gatherablesProp = gameDataType.GetProperty("Gatherables");
            fishesProp = gameDataType.GetProperty("Fishes");
            if (gatherablesProp == null)
            {
                LastError = "Could not find GameData.Gatherables property.";
                DalamudApi.Log.Warning(LastError);
                return false;
            }

            var gatherablesDict = gatherablesProp.GetValue(gameData);
            if (gatherablesDict != null)
            {
                gatherablesTryGetValue = gatherablesDict.GetType().GetMethod("TryGetValue");
            }

            var fishesDict = fishesProp?.GetValue(gameData);
            if (fishesDict != null)
            {
                fishesTryGetValue = fishesDict.GetType().GetMethod("TryGetValue");
            }

            // Step 4: Get AutoGatherListsManager (try field first, then property)
            listsManager = FindMember(gbrType, gbrPluginInstance,
                "AutoGatherListsManager", "_autoGatherListsManager", "ListsManager");
            if (listsManager == null)
            {
                // Broader search: look for any field/property whose type name contains "ListsManager"
                listsManager = FindMemberByTypeName(gbrType, gbrPluginInstance, "ListsManager");
            }
            if (listsManager == null)
            {
                LastError = "Could not find AutoGatherListsManager on GBR plugin instance.";
                DalamudApi.Log.Warning(LastError);
                LogTypeStructure(gbrType, "GBR plugin");
                return false;
            }

            var listsManagerType = listsManager.GetType();
            addListMethod = listsManagerType.GetMethod("AddList", AllFlags);
            deleteListMethod = listsManagerType.GetMethod("DeleteList", AllFlags);
            saveMethod = listsManagerType.GetMethod("Save", AllFlags);
            setActiveItemsMethod = listsManagerType.GetMethod("SetActiveItems", AllFlags);
            listsProp = listsManagerType.GetProperty("Lists", AllFlags);

            // Step 5: Get AutoGatherList type and its methods
            autoGatherListType = listsManagerType.Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "AutoGatherList" && !t.IsInterface);
            if (autoGatherListType == null)
            {
                LastError = "Could not find AutoGatherList type.";
                DalamudApi.Log.Warning(LastError);
                return false;
            }

            addItemMethod = autoGatherListType.GetMethod("Add", AllFlags);
            listNameProp = autoGatherListType.GetProperty("Name", AllFlags);
            listEnabledProp = autoGatherListType.GetProperty("Enabled", AllFlags);
            listDescriptionProp = autoGatherListType.GetProperty("Description", AllFlags);

            if (addItemMethod == null || addListMethod == null)
            {
                LastError = "Could not find required methods on AutoGatherList/Manager.";
                DalamudApi.Log.Warning(LastError);
                return false;
            }

            IsInitialized = true;
            DalamudApi.Log.Information("GatherBuddyListManager initialized successfully via reflection.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Reflection initialization failed: {ex.Message}";
            DalamudApi.Log.Error(ex, LastError);
            return false;
        }
    }

    /// <summary>
    /// Creates or updates the Expedition auto-gather list in GBR with the given items.
    /// Removes any existing Expedition list first.
    /// </summary>
    /// <param name="items">List of (itemId, quantity) pairs to gather.</param>
    /// <returns>True if the list was created successfully.</returns>
    public bool SetGatherList(IReadOnlyList<(uint ItemId, uint Quantity)> items)
    {
        if (!IsInitialized)
        {
            if (!Initialize())
                return false;
        }

        try
        {
            // Remove any existing Expedition list first
            RemoveExpeditionList();
            SkippedItemIds.Clear();

            if (items.Count == 0)
                return true;

            // Create a new AutoGatherList
            var list = Activator.CreateInstance(autoGatherListType!);
            if (list == null)
            {
                LastError = "Failed to create AutoGatherList instance.";
                return false;
            }

            listNameProp!.SetValue(list, ExpeditionListName);
            listDescriptionProp?.SetValue(list, "Managed by Expedition — do not edit manually.");
            listEnabledProp!.SetValue(list, true);

            var gatherablesDict = gatherablesProp!.GetValue(gameData);
            var fishesDict = fishesProp?.GetValue(gameData);
            var addedCount = 0;

            foreach (var (itemId, quantity) in items)
            {
                var gatherable = LookupGatherable(gatherablesDict, fishesDict, itemId);
                if (gatherable == null)
                {
                    DalamudApi.Log.Warning($"Item {itemId} not found in GBR gatherables/fishes — will use command-only gathering.");
                    SkippedItemIds.Add(itemId);
                    continue;
                }

                // Check if the item has gathering locations
                var locationsProp = gatherable.GetType().GetProperty("Locations");
                if (locationsProp != null)
                {
                    var locations = locationsProp.GetValue(gatherable);
                    if (locations is System.Collections.IEnumerable enumerable)
                    {
                        var hasAny = false;
                        foreach (var _ in enumerable) { hasAny = true; break; }
                        if (!hasAny)
                        {
                            DalamudApi.Log.Warning($"Item {itemId} has no gathering locations in GBR — will use command-only gathering.");
                            SkippedItemIds.Add(itemId);
                            continue;
                        }
                    }
                }

                var result = addItemMethod!.Invoke(list, new[] { gatherable, quantity });
                if (result is true)
                    addedCount++;
                else
                {
                    DalamudApi.Log.Warning($"Failed to add item {itemId} to GBR list — will use command-only gathering.");
                    SkippedItemIds.Add(itemId);
                }
            }

            if (addedCount == 0)
            {
                LastError = "No items could be added to the GBR gather list.";
                DalamudApi.Log.Warning(LastError);
                return false;
            }

            // Register the list with GBR's manager
            // AddList signature: AddList(AutoGatherList list, FileSystem<AutoGatherList>.Folder? folder = null)
            var addListParams = addListMethod!.GetParameters();
            var args = new object?[addListParams.Length];
            args[0] = list;
            for (var i = 1; i < args.Length; i++)
                args[i] = null; // optional folder parameter
            addListMethod.Invoke(listsManager, args);

            DalamudApi.Log.Information($"Created GBR auto-gather list '{ExpeditionListName}' with {addedCount} items.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to set gather list: {ex.Message}";
            DalamudApi.Log.Error(ex, LastError);
            return false;
        }
    }

    /// <summary>
    /// Removes the Expedition-managed auto-gather list from GBR.
    /// </summary>
    public void RemoveExpeditionList()
    {
        if (!IsInitialized || listsManager == null || listsProp == null || deleteListMethod == null)
            return;

        try
        {
            var lists = listsProp.GetValue(listsManager);
            if (lists is not System.Collections.IEnumerable enumerable)
                return;

            // Find lists named [Expedition] and delete them
            var toDelete = new List<object>();
            foreach (var listObj in enumerable)
            {
                var name = listNameProp?.GetValue(listObj) as string;
                if (name == ExpeditionListName)
                    toDelete.Add(listObj);
            }

            foreach (var listObj in toDelete)
            {
                deleteListMethod.Invoke(listsManager, new[] { listObj });
                DalamudApi.Log.Information($"Removed GBR auto-gather list '{ExpeditionListName}'.");
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "Failed to remove Expedition GBR list.");
        }
    }

    /// <summary>
    /// Looks up an IGatherable by item ID from GBR's GameData.
    /// Tries Gatherables first, then Fishes.
    /// </summary>
    private object? LookupGatherable(object? gatherablesDict, object? fishesDict, uint itemId)
    {
        if (gatherablesDict != null && gatherablesTryGetValue != null)
        {
            var args = new object?[] { itemId, null };
            var found = gatherablesTryGetValue.Invoke(gatherablesDict, args);
            if (found is true && args[1] != null)
                return args[1];
        }

        if (fishesDict != null && fishesTryGetValue != null)
        {
            var args = new object?[] { itemId, null };
            var found = fishesTryGetValue.Invoke(fishesDict, args);
            if (found is true && args[1] != null)
                return args[1];
        }

        return null;
    }

    /// <summary>
    /// Known field/property names Dalamud has used across versions to store the
    /// IDalamudPlugin instance on LocalPlugin/LocalDevPlugin.
    /// </summary>
    private static readonly string[] InstanceFieldNames =
        { "instance", "_instance", "Instance", "pluginObject", "DalamudPlugin" };

    /// <summary>
    /// Gets a Dalamud plugin instance by its internal name using reflection.
    /// Tries multiple strategies to handle different Dalamud SDK versions.
    /// </summary>
    private static bool TryGetDalamudPlugin(string internalName, out object? instance)
    {
        instance = null;
        try
        {
            // Strategy 1: Via Dalamud's internal Service<PluginManager>
            if (TryGetPluginViaServiceManager(internalName, out instance))
                return true;

            // Strategy 2: Via DalamudPluginInterface.InstalledPlugins
            if (TryGetPluginViaInterface(internalName, out instance))
                return true;

            // Strategy 3: Scan loaded assemblies for the plugin type directly
            if (TryGetPluginViaAssemblyScan(internalName, out instance))
                return true;

            DalamudApi.Log.Warning($"All strategies to find plugin '{internalName}' exhausted.");
            return false;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, $"Failed to get plugin instance for '{internalName}'");
            return false;
        }
    }

    /// <summary>
    /// Extracts the plugin instance from a Dalamud LocalPlugin/LocalDevPlugin object
    /// by trying all known field and property names across Dalamud versions.
    /// </summary>
    private static object? ExtractPluginInstance(object pluginWrapper)
    {
        var wrapperType = pluginWrapper.GetType();

        // Try each known field name, walking up the inheritance chain
        foreach (var fieldName in InstanceFieldNames)
        {
            var type = wrapperType;
            while (type != null)
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    var value = field.GetValue(pluginWrapper);
                    if (value != null)
                    {
                        DalamudApi.Log.Debug($"Found plugin instance via field '{fieldName}' on {type.Name}");
                        return value;
                    }
                }

                var prop = type.GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                {
                    try
                    {
                        var value = prop.GetValue(pluginWrapper);
                        if (value != null)
                        {
                            DalamudApi.Log.Debug($"Found plugin instance via property '{fieldName}' on {type.Name}");
                            return value;
                        }
                    }
                    catch { /* property getter may throw */ }
                }

                type = type.BaseType;
            }
        }

        // Last resort: check all fields for an IDalamudPlugin assignable value
        var currentType = wrapperType;
        while (currentType != null)
        {
            foreach (var field in currentType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(IDalamudPlugin).IsAssignableFrom(field.FieldType))
                {
                    var value = field.GetValue(pluginWrapper);
                    if (value != null)
                    {
                        DalamudApi.Log.Debug($"Found plugin instance via IDalamudPlugin-typed field '{field.Name}' on {currentType.Name}");
                        return value;
                    }
                }
            }
            currentType = currentType.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Strategy 1: Find the plugin via Dalamud.Service&lt;PluginManager&gt;.
    /// </summary>
    private static bool TryGetPluginViaServiceManager(string internalName, out object? instance)
    {
        instance = null;
        try
        {
            var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;

            var serviceType = dalamudAssembly.GetTypes()
                .FirstOrDefault(t => t.IsGenericTypeDefinition && t.Name.StartsWith("Service`"));
            if (serviceType == null) return false;

            var pluginManagerType = dalamudAssembly.GetTypes()
                .FirstOrDefault(t => t.Name == "PluginManager");
            if (pluginManagerType == null) return false;

            var genericService = serviceType.MakeGenericType(pluginManagerType);
            var getMethod = genericService.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            if (getMethod == null) return false;

            var pluginManager = getMethod.Invoke(null, null);
            if (pluginManager == null) return false;

            var installedProp = pluginManager.GetType().GetProperty("InstalledPlugins", AllFlags);
            if (installedProp == null) return false;

            var plugins = installedProp.GetValue(pluginManager) as System.Collections.IEnumerable;
            if (plugins == null) return false;

            foreach (var plugin in plugins)
            {
                var nameProp = plugin.GetType().GetProperty("InternalName", AllFlags);
                var name = nameProp?.GetValue(plugin) as string;
                if (name != internalName) continue;

                instance = ExtractPluginInstance(plugin);
                if (instance != null) return true;

                // Log all available fields/properties for debugging
                LogPluginWrapperStructure(plugin);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Debug($"Service manager strategy failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Strategy 2: Find the plugin via DalamudPluginInterface.InstalledPlugins.
    /// </summary>
    private static bool TryGetPluginViaInterface(string internalName, out object? instance)
    {
        instance = null;
        try
        {
            var installedPlugins = DalamudApi.PluginInterface.InstalledPlugins;
            foreach (var plugin in installedPlugins)
            {
                if (plugin.InternalName != internalName) continue;

                instance = ExtractPluginInstance(plugin);
                if (instance != null) return true;

                LogPluginWrapperStructure(plugin);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Debug($"Interface strategy failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Strategy 3: Scan loaded assemblies for a type that matches the plugin's
    /// entry point. GatherBuddyReborn's main class implements IDalamudPlugin.
    /// </summary>
    private static bool TryGetPluginViaAssemblyScan(string internalName, out object? instance)
    {
        instance = null;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Look for assemblies that might belong to the target plugin
                if (!assembly.FullName?.Contains("GatherBuddy", StringComparison.OrdinalIgnoreCase) ?? true)
                    continue;

                foreach (var type in assembly.GetTypes())
                {
                    if (!typeof(IDalamudPlugin).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
                        continue;

                    // Check for a static Instance property (common pattern)
                    var instanceProp = type.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (instanceProp != null)
                    {
                        instance = instanceProp.GetValue(null);
                        if (instance != null)
                        {
                            DalamudApi.Log.Information($"Found {internalName} via assembly scan: {type.FullName}");
                            return true;
                        }
                    }

                    // Check for a static field pattern
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (field.FieldType == type || typeof(IDalamudPlugin).IsAssignableFrom(field.FieldType))
                        {
                            instance = field.GetValue(null);
                            if (instance != null)
                            {
                                DalamudApi.Log.Information($"Found {internalName} via static field '{field.Name}' on {type.FullName}");
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Debug($"Assembly scan strategy failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Logs the structure of a plugin wrapper object for debugging field name mismatches.
    /// </summary>
    private static void LogPluginWrapperStructure(object pluginWrapper)
    {
        var type = pluginWrapper.GetType();
        LogTypeStructure(type, type.Name);
    }

    /// <summary>
    /// Logs all fields and properties on a type for debugging.
    /// </summary>
    private static void LogTypeStructure(Type type, string label)
    {
        var fields = new List<string>();
        var props = new List<string>();

        var currentType = type;
        while (currentType != null)
        {
            foreach (var f in currentType.GetFields(AllFlags))
                fields.Add($"{currentType.Name}.{f.Name} ({f.FieldType.Name})");
            foreach (var p in currentType.GetProperties(AllFlags))
                props.Add($"{currentType.Name}.{p.Name} ({p.PropertyType.Name})");
            currentType = currentType.BaseType;
        }

        DalamudApi.Log.Warning(
            $"Type structure for '{label}':\n" +
            $"  Fields: {string.Join(", ", fields)}\n" +
            $"  Properties: {string.Join(", ", props)}");
    }

    /// <summary>
    /// Searches for a field or property by name(s) on a type. Returns its value.
    /// Pass null for instance to search static members only.
    /// </summary>
    private static object? FindMember(Type type, object? instance, params string[] names)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic;
        flags |= instance != null ? BindingFlags.Instance : BindingFlags.Static;
        // Also include both just in case
        flags |= BindingFlags.Instance | BindingFlags.Static;

        foreach (var name in names)
        {
            var currentType = type;
            while (currentType != null)
            {
                var prop = currentType.GetProperty(name, flags);
                if (prop != null && prop.CanRead)
                {
                    try
                    {
                        var val = prop.GetValue(prop.GetMethod!.IsStatic ? null : instance);
                        if (val != null) return val;
                    }
                    catch { /* may throw if instance is wrong */ }
                }

                var field = currentType.GetField(name, flags);
                if (field != null)
                {
                    try
                    {
                        var val = field.GetValue(field.IsStatic ? null : instance);
                        if (val != null) return val;
                    }
                    catch { /* may throw */ }
                }

                currentType = currentType.BaseType;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a field or property whose type name contains the given substring.
    /// </summary>
    private static object? FindMemberByTypeName(Type type, object? instance, string typeNameFragment)
    {
        var currentType = type;
        while (currentType != null)
        {
            foreach (var field in currentType.GetFields(AllFlags))
            {
                if (field.FieldType.Name.Contains(typeNameFragment, StringComparison.OrdinalIgnoreCase))
                {
                    var val = field.GetValue(field.IsStatic ? null : instance);
                    if (val != null)
                    {
                        DalamudApi.Log.Debug($"Found member by type name: {field.Name} ({field.FieldType.Name})");
                        return val;
                    }
                }
            }

            foreach (var prop in currentType.GetProperties(AllFlags))
            {
                if (prop.PropertyType.Name.Contains(typeNameFragment, StringComparison.OrdinalIgnoreCase) && prop.CanRead)
                {
                    try
                    {
                        var val = prop.GetValue(prop.GetMethod!.IsStatic ? null : instance);
                        if (val != null)
                        {
                            DalamudApi.Log.Debug($"Found member by type name: {prop.Name} ({prop.PropertyType.Name})");
                            return val;
                        }
                    }
                    catch { /* may throw */ }
                }
            }

            currentType = currentType.BaseType;
        }

        return null;
    }
}
