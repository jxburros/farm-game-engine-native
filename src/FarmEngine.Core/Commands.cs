using System.Text.Json.Serialization;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Commands are the ONLY way player intent mutates game state (port of
/// commands.ts). Determinism contract: same seed + same command log ⇒ same state.
/// JSON shape is identical to the TS union (<c>{"type":"move","dir":"up"}</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetMoveIntentCommand), "setMoveIntent")]
[JsonDerivedType(typeof(MoveCommand), "move")]
[JsonDerivedType(typeof(UseToolCommand), "useTool")]
[JsonDerivedType(typeof(InteractCommand), "interact")]
[JsonDerivedType(typeof(ChooseDialogueOptionCommand), "chooseDialogueOption")]
[JsonDerivedType(typeof(CloseDialogueCommand), "closeDialogue")]
[JsonDerivedType(typeof(SleepCommand), "sleep")]
[JsonDerivedType(typeof(OpenShopCommand), "openShop")]
[JsonDerivedType(typeof(CloseShopCommand), "closeShop")]
[JsonDerivedType(typeof(BuyItemCommand), "buyItem")]
[JsonDerivedType(typeof(SellItemCommand), "sellItem")]
[JsonDerivedType(typeof(RepairToolCommand), "repairTool")]
[JsonDerivedType(typeof(CraftCommand), "craft")]
[JsonDerivedType(typeof(PlaceMachineCommand), "placeMachine")]
[JsonDerivedType(typeof(MachineLoadCommand), "machineLoad")]
[JsonDerivedType(typeof(GiveGiftCommand), "giveGift")]
[JsonDerivedType(typeof(DescendMineCommand), "descendMine")]
[JsonDerivedType(typeof(ExitMineCommand), "exitMine")]
[JsonDerivedType(typeof(PerformActionCommand), "performAction")]
[JsonDerivedType(typeof(UseItemCommand), "useItem")]
[JsonDerivedType(typeof(StartMinigameCommand), "startMinigame")]
[JsonDerivedType(typeof(ResolveMinigameCommand), "resolveMinigame")]
[JsonDerivedType(typeof(CancelMinigameCommand), "cancelMinigame")]
[JsonDerivedType(typeof(PluginMutationCommand), "pluginMutation")]
public abstract record Command
{
    [JsonIgnore] public abstract string Type { get; }
}

/// <summary>Free movement (v4): set the held movement intent (each axis −1/0/1).</summary>
public sealed record SetMoveIntentCommand(double Dx, double Dy) : Command { [JsonIgnore] public override string Type => "setMoveIntent"; }
/// <summary>Discrete one-tile step — scripted movement / legacy primitive.</summary>
public sealed record MoveCommand(string Dir) : Command { [JsonIgnore] public override string Type => "move"; }
public sealed record UseToolCommand(string Tool) : Command { [JsonIgnore] public override string Type => "useTool"; }
public sealed record InteractCommand : Command { [JsonIgnore] public override string Type => "interact"; }
public sealed record ChooseDialogueOptionCommand(double Index) : Command { [JsonIgnore] public override string Type => "chooseDialogueOption"; }
public sealed record CloseDialogueCommand : Command { [JsonIgnore] public override string Type => "closeDialogue"; }
public sealed record SleepCommand : Command { [JsonIgnore] public override string Type => "sleep"; }
public sealed record OpenShopCommand(string ShopId) : Command { [JsonIgnore] public override string Type => "openShop"; }
public sealed record CloseShopCommand : Command { [JsonIgnore] public override string Type => "closeShop"; }
public sealed record BuyItemCommand(string ItemId, double Quantity) : Command { [JsonIgnore] public override string Type => "buyItem"; }
public sealed record SellItemCommand(string ItemId, double Quantity) : Command { [JsonIgnore] public override string Type => "sellItem"; }
public sealed record RepairToolCommand(string ItemId) : Command { [JsonIgnore] public override string Type => "repairTool"; }
public sealed record CraftCommand(string RecipeId) : Command { [JsonIgnore] public override string Type => "craft"; }
public sealed record PlaceMachineCommand(string MachineTypeId) : Command { [JsonIgnore] public override string Type => "placeMachine"; }
public sealed record MachineLoadCommand(string RecipeId) : Command { [JsonIgnore] public override string Type => "machineLoad"; }
public sealed record GiveGiftCommand(string ItemId) : Command { [JsonIgnore] public override string Type => "giveGift"; }
public sealed record DescendMineCommand(double Floor) : Command { [JsonIgnore] public override string Type => "descendMine"; }
public sealed record ExitMineCommand : Command { [JsonIgnore] public override string Type => "exitMine"; }
/// <summary>Run a creator-defined action (extensibility layer).</summary>
public sealed record PerformActionCommand(string ActionId) : Command { [JsonIgnore] public override string Type => "performAction"; }
/// <summary>Use an inventory item (runs its bound action).</summary>
public sealed record UseItemCommand(string ItemId) : Command { [JsonIgnore] public override string Type => "useItem"; }
public sealed record StartMinigameCommand(string MinigameId) : Command { [JsonIgnore] public override string Type => "startMinigame"; }
/// <summary>Resolve the open minigame with a score in [0, 1].</summary>
public sealed record ResolveMinigameCommand(double Score) : Command { [JsonIgnore] public override string Type => "resolveMinigame"; }
public sealed record CancelMinigameCommand : Command { [JsonIgnore] public override string Type => "cancelMinigame"; }
/// <summary>A validated mutation returned by a sandboxed plugin hook (M5).</summary>
public sealed record PluginMutationCommand(string PluginId, PluginMutation Mutation) : Command { [JsonIgnore] public override string Type => "pluginMutation"; }
