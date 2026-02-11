using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;

using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.Inventory;
using Expedition.IPC;
using Expedition.RecipeResolver;
using Expedition.UI;
using Expedition.Workflow;

namespace Expedition;

/// <summary>
/// Main plugin entry point. Orchestrates gather-to-craft workflows
/// by coordinating GatherBuddy Reborn (gathering) and Artisan (crafting).
/// </summary>
public sealed class Expedition : IDalamudPlugin
{
    public const string PluginName = "Expedition";
    private const string CommandName = "/expedition";
    private const string CommandAlias = "/exp";

    public static Configuration Config { get; private set; } = null!;
    public static Expedition Instance { get; private set; } = null!;

    public IpcManager Ipc { get; }
    public RecipeResolverService RecipeResolver { get; }
    public InventoryManager InventoryManager { get; }
    public GatheringOrchestrator GatheringOrchestrator { get; }
    public CraftingOrchestrator CraftingOrchestrator { get; }
    public WorkflowEngine WorkflowEngine { get; }

    private readonly MainWindow mainWindow;
    private readonly OverlayWindow overlayWindow;

    public Expedition(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        DalamudApi.Initialize(pluginInterface);

        Config = DalamudApi.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Ipc = new IpcManager();
        RecipeResolver = new RecipeResolverService();
        InventoryManager = new InventoryManager();
        GatheringOrchestrator = new GatheringOrchestrator(Ipc);
        CraftingOrchestrator = new CraftingOrchestrator(Ipc);
        WorkflowEngine = new WorkflowEngine(
            RecipeResolver,
            InventoryManager,
            GatheringOrchestrator,
            CraftingOrchestrator,
            Config);

        mainWindow = new MainWindow(this);
        overlayWindow = new OverlayWindow(WorkflowEngine);

        DalamudApi.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Expedition recipe automation window.\n" +
                          "/expedition craft <item name> [quantity] — Start a full gather+craft workflow.\n" +
                          "/expedition stop — Stop the current workflow.\n" +
                          "/expedition status — Show current workflow status.",
        });

        DalamudApi.CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /expedition.",
        });

        DalamudApi.PluginInterface.UiBuilder.Draw += DrawUI;
        DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUI;
        DalamudApi.PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUI;
        DalamudApi.Framework.Update += OnFrameworkUpdate;

        DalamudApi.Log.Information("Expedition loaded.");
    }

    public void Dispose()
    {
        DalamudApi.Framework.Update -= OnFrameworkUpdate;
        DalamudApi.PluginInterface.UiBuilder.Draw -= DrawUI;
        DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUI;
        DalamudApi.PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUI;

        DalamudApi.CommandManager.RemoveHandler(CommandName);
        DalamudApi.CommandManager.RemoveHandler(CommandAlias);

        WorkflowEngine.Dispose();
        Ipc.Dispose();

        DalamudApi.Log.Information("Expedition unloaded.");
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            mainWindow.Toggle();
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "craft":
                HandleCraftCommand(parts.Length > 1 ? parts[1] : string.Empty);
                break;

            case "stop":
                WorkflowEngine.Cancel();
                DalamudApi.ChatGui.Print("[Expedition] Workflow stopped.");
                break;

            case "status":
                PrintStatus();
                break;

            case "config":
            case "settings":
                mainWindow.OpenSettings();
                break;

            default:
                mainWindow.Toggle();
                break;
        }
    }

    private void HandleCraftCommand(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            DalamudApi.ChatGui.PrintError("[Expedition] Usage: /expedition craft <item name> [quantity]");
            return;
        }

        // Try to parse trailing quantity
        var quantity = 1;
        var itemName = args;
        var lastSpace = args.LastIndexOf(' ');
        if (lastSpace > 0 && int.TryParse(args[(lastSpace + 1)..], out var parsedQty) && parsedQty > 0)
        {
            quantity = parsedQty;
            itemName = args[..lastSpace].Trim();
        }

        var recipe = RecipeResolver.FindRecipeByName(itemName);
        if (recipe == null)
        {
            DalamudApi.ChatGui.PrintError($"[Expedition] Could not find a recipe for \"{itemName}\".");
            return;
        }

        DalamudApi.ChatGui.Print($"[Expedition] Starting workflow: {recipe.ItemName} x{quantity}");
        WorkflowEngine.Start(recipe, quantity);
    }

    private void PrintStatus()
    {
        var state = WorkflowEngine.CurrentState;
        var phase = WorkflowEngine.CurrentPhase;
        DalamudApi.ChatGui.Print($"[Expedition] State: {state} | Phase: {phase}");

        if (WorkflowEngine.CurrentRecipe != null)
            DalamudApi.ChatGui.Print($"[Expedition] Target: {WorkflowEngine.CurrentRecipe.ItemName} x{WorkflowEngine.TargetQuantity}");

        if (!string.IsNullOrEmpty(WorkflowEngine.StatusMessage))
            DalamudApi.ChatGui.Print($"[Expedition] {WorkflowEngine.StatusMessage}");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        WorkflowEngine.Update();
    }

    private void DrawUI()
    {
        mainWindow.Draw();
        if (Config.ShowOverlay)
            overlayWindow.Draw();
    }

    private void OnOpenConfigUI()
    {
        mainWindow.OpenSettings();
    }

    private void OnOpenMainUI()
    {
        mainWindow.IsOpen = true;
    }
}
