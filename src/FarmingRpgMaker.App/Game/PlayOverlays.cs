using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Builders for the play-mode panels — ports of the web components DialogueBox, ShopDialog,
/// CraftingDialog, PlayerInventory and QuestTracker. Every action is an engine command run
/// through <see cref="PlaySession.RunCommand"/>; the panels only read state.
/// </summary>
internal static class PlayOverlays
{
    // ─── DialogueBox ────────────────────────────────────────────────────

    /// <summary>
    /// The dialogue card for <c>state.dialogue</c> (null when none/unknown). Options are the
    /// VISIBLE ones (friendship/item/flag gates) — the engine indexes the same list.
    /// </summary>
    public static Control? Dialogue(PlaySession session)
    {
        var dialogueState = session.State.Dialogue;
        if (dialogueState is null)
        {
            return null;
        }

        var dialogue = DialogueSystem.FindDialogue(session.Context, dialogueState.NpcId, dialogueState.DialogueId);
        if (dialogue is null)
        {
            return null;
        }

        var npc = session.Content.Npcs.FirstOrDefault(n => n.Id == dialogueState.NpcId);
        var portrait = new Border
        {
            Child = new PathIcon { Data = Ui.Icon("IconAccount").Data, Width = 28, Height = 28, Foreground = Brushes.White },
        }.WithClasses("portrait");
        portrait.VerticalAlignment = VerticalAlignment.Top;

        var name = Ui.Text(npc?.Name ?? dialogueState.NpcId, "h2");
        name.Name = "DialogueNpcName";
        var text = Ui.Wrapped(dialogue.Text);
        text.Name = "DialogueText";
        text.FontSize = 14.5;
        text.LineHeight = 22;
        text.Margin = new Thickness(0, 4, 0, 0);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(portrait);
        var textStack = Ui.VStack(0, name, text);
        textStack.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(textStack, 1);
        header.Children.Add(textStack);

        var options = new StackPanel { Name = "DialogueOptions", Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        var visible = Social.VisibleDialogueOptions(session.Context, session.State, dialogue);
        for (var i = 0; i < visible.Count; i++)
        {
            var index = i;
            var label = Ui.HStack(10, Keycap((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), Ui.Wrapped(visible[i].Text));
            options.Children.Add(Ui.Button(label, () => session.RunCommand(new ChooseDialogueOptionCommand(index)), "option"));
        }

        if (visible.Count == 0)
        {
            options.Children.Add(Ui.Button(Ui.HStack(10, Keycap("Esc"), Ui.Text("Goodbye")), () => session.RunCommand(new CloseDialogueCommand()), "option"));
        }

        var card = new Border
        {
            Name = "DialogueBox",
            Child = Ui.VStack(0, header, options),
            MaxWidth = 680,
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        }.WithClasses("dialogue");

        return new Border { Child = card }.WithClasses("backdrop");
    }

    /// <summary>A small keycap label ("1", "Esc").</summary>
    public static Border Keycap(string key) => new Border { Child = Ui.Text(key), VerticalAlignment = VerticalAlignment.Center }.WithClasses("key");

    // ─── ShopDialog ─────────────────────────────────────────────────────

    public enum ShopTab
    {
        Buy,
        Sell,
        Repair,
    }

    /// <summary>
    /// In-game shop (web ShopDialog): buy stock (season-filtered, daily limits), sell
    /// inventory, repair tools. All actions go through engine commands.
    /// </summary>
    public static Control? Shop(PlaySession session, ShopTab tab, Action<ShopTab> selectTab)
    {
        var shopSession = session.State.Shop;
        if (shopSession is null)
        {
            return null;
        }

        var shop = Economy.FindShop(session.Context, shopSession.ShopId);
        if (shop is null)
        {
            return null;
        }

        var state = session.State;
        var player = state.Player;
        var items = session.Content.Items;
        var body = new StackPanel { Spacing = 8 };

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
        void AddTab(ShopTab value, string label)
        {
            var button = new Avalonia.Controls.Primitives.ToggleButton { Content = label, IsChecked = tab == value, Name = $"ShopTab{value}" };
            button.Classes.Add("tool");
            button.Click += (_, _) => selectTab(value);
            tabs.Children.Add(button);
        }

        AddTab(ShopTab.Buy, "Buy");
        if (shop.BuysItems)
        {
            AddTab(ShopTab.Sell, "Sell");
        }

        if (shop.RepairsTools)
        {
            AddTab(ShopTab.Repair, "Repair");
        }

        body.Children.Add(tabs);

        if (tab == ShopTab.Sell && shop.BuysItems)
        {
            var sellable = player.Inventory.Where(slot => slot.Item.Type != "quest").ToList();
            if (sellable.Count == 0)
            {
                body.Children.Add(Ui.Empty("IconPackage", "Nothing to sell.", "Harvest crops or gather resources first."));
            }

            foreach (var slot in sellable)
            {
                var unit = Math.Floor(slot.Item.Value * shop.SellPriceMultiplier);
                var title = Ui.HStack(6, Ui.Text(slot.Item.Name, "h3"));
                ((TextBlock)title.Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
                if (slot.Item.Stackable)
                {
                    title.Children.Add(Ui.Text($"×{Ui.Num(slot.Quantity)}", "muted", "small"));
                }

                var info = Ui.VStack(2, title, Ui.Text($"{Ui.Money(unit)} each", "muted", "small"));
                var actions = new List<Control>
                {
                    Ui.Button("Sell 1", () => session.RunCommand(new SellItemCommand(slot.Item.Id, 1)), "accent", "small"),
                };
                if (slot.Quantity > 1)
                {
                    actions.Add(Ui.Button("Sell all", () => session.RunCommand(new SellItemCommand(slot.Item.Id, slot.Quantity)), "tool", "small"));
                }

                body.Children.Add(Ui.Row(info, [.. actions]));
            }
        }
        else if (tab == ShopTab.Repair && shop.RepairsTools)
        {
            var damaged = player.Inventory.Where(slot => slot.Item.Type == "tool"
                && slot.Item.Durability is not null && slot.Item.MaxDurability is not null
                && slot.Item.Durability < slot.Item.MaxDurability).ToList();
            if (damaged.Count == 0)
            {
                body.Children.Add(Ui.Empty("IconHammer", "All your tools are in good shape.", "Come back when something breaks."));
            }

            foreach (var slot in damaged)
            {
                var missing = slot.Item.MaxDurability!.Value - slot.Item.Durability!.Value;
                var cost = Math.Ceiling(missing * shop.RepairCostPerPoint);
                var broken = Tools.IsToolBroken(slot.Item);
                var detail = Ui.Text($"{Ui.Num(slot.Item.Durability.Value)}/{Ui.Num(slot.Item.MaxDurability.Value)} durability{(broken ? " — BROKEN" : "")}", "small");
                detail.Classes.Add(broken ? "error" : "muted");
                var repair = Ui.Button($"Repair {Ui.Money(cost)}", () => session.RunCommand(new RepairToolCommand(slot.Item.Id)), "accent", "small");
                repair.IsEnabled = player.Money >= cost;
                body.Children.Add(Ui.Row(Ui.VStack(2, Ui.Text(slot.Item.Name, "h3"), detail), repair));
            }
        }
        else
        {
            var stock = shop.Stock
                .Select(entry => (Entry: entry, Item: items.FirstOrDefault(i => i.Id == entry.ItemId)))
                .Where(row => row.Item is not null)
                .Where(row => row.Entry.Seasons is not { Count: > 0 } seasons || seasons.Contains(state.Clock.Season))
                .ToList();
            if (stock.Count == 0)
            {
                body.Children.Add(Ui.Empty("IconStore", "Nothing in stock this season.", "Check back when the season turns."));
            }

            foreach (var (entry, item) in stock)
            {
                var price = entry.Price ?? item!.Value;
                var remaining = entry.DailyLimit is not null ? Economy.RemainingDailyStock(state, shop.Id, entry.ItemId, entry.DailyLimit) : double.PositiveInfinity;
                var detail = item!.Description;
                if (entry.DailyLimit is { } limit)
                {
                    detail = $"{detail}{(detail.Length > 0 ? " · " : "")}{Ui.Num(Math.Max(0, remaining))}/{Ui.Num(limit)} left today";
                }

                var info = Ui.VStack(2, Ui.Text(item.Name, "h3"), Ui.Text(detail, "muted", "small"));
                ((TextBlock)info.Children[0]).Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
                ((TextBlock)info.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
                var priceText = Ui.Text(Ui.Money(price), "h3");
                priceText.Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
                var buy = Ui.Button("Buy", () => session.RunCommand(new BuyItemCommand(entry.ItemId, 1)), "accent", "small");
                buy.Name = $"Buy_{entry.ItemId}";
                buy.IsEnabled = player.Money >= price && remaining >= 1;
                var actions = new List<Control> { priceText, buy };
                if (item.Stackable)
                {
                    var five = Ui.Button("×5", () => session.RunCommand(new BuyItemCommand(entry.ItemId, 5)), "tool", "small");
                    five.IsEnabled = player.Money >= price * 5 && remaining >= 5;
                    actions.Add(five);
                }

                body.Children.Add(Ui.Row(info, [.. actions]));
            }
        }

        var card = Ui.ModalCard("ShopDialog", "IconStore", shop.Name, $"Your money: {Ui.Money(player.Money)}", body, () => session.RunCommand(new CloseShopCommand()), 600, closeLabel: "Leave shop (Esc)");
        return new Border { Child = card }.WithClasses("backdrop");
    }

    // ─── CraftingDialog ─────────────────────────────────────────────────

    /// <summary>
    /// Crafting (web CraftingDialog): load recipes into the faced machine, hand recipes by
    /// category with availability from <see cref="Crafting.CraftableStatus"/>, and machine
    /// placement.
    /// </summary>
    public static Control Crafting(PlaySession session, Action onClose)
    {
        var ctx = session.Context;
        var state = session.State;
        var content = session.Content;
        var inventory = state.Player.Inventory;
        double Held(string itemId) => inventory.Where(slot => slot.Item.Id == itemId).Sum(slot => slot.Quantity);
        string ItemName(string itemId) => content.Items.FirstOrDefault(i => i.Id == itemId)?.Name ?? itemId;
        string IngredientLine(RecipeDefinition recipe) =>
            string.Join(", ", recipe.Inputs.Select(input => $"{Ui.Num(input.Quantity)}x {ItemName(input.ItemId)} ({Ui.Num(Held(input.ItemId))})"));

        // What machine is the player facing (for load actions)?
        var scene = session.CurrentScene;
        var facing = WorldMovement.FacingTarget(state);
        var facingTile = scene is not null && facing.Y >= 0 && facing.Y < scene.Tiles.Count && facing.X >= 0 && facing.X < scene.Tiles[(int)facing.Y].Count
            ? scene.Tiles[(int)facing.Y][(int)facing.X]
            : null;
        var facingMachine = facingTile?.Machine;
        var facingType = facingMachine is not null ? content.MachineTypes.FirstOrDefault(t => t.Id == facingMachine.TypeId) : null;

        var body = new StackPanel { Spacing = 8 };
        if (facingMachine is not null)
        {
            body.Children.Add(Ui.Text($"LOAD INTO {(facingType?.Name ?? "machine").ToUpperInvariant()}", "section"));
            var machineRecipes = content.Recipes.Where(recipe => recipe.MachineTypeId == facingMachine.TypeId).ToList();
            if (facingMachine.Processing is not null)
            {
                body.Children.Add(Ui.Text("Working… come back later.", "muted", "small"));
            }
            else if (machineRecipes.Count == 0)
            {
                body.Children.Add(Ui.Text("No recipes for this machine.", "muted", "small"));
            }
            else
            {
                foreach (var recipe in machineRecipes)
                {
                    var load = Ui.Button("Load", () => session.RunCommand(new MachineLoadCommand(recipe.Id)), "accent", "small");
                    load.IsEnabled = FarmEngine.Core.Crafting.HasIngredients(state, recipe);
                    body.Children.Add(Ui.Row(Ui.VStack(2, Ui.Text(recipe.Name, "h3"), Ui.Text($"{IngredientLine(recipe)} · {Ui.Num(recipe.ProcessingMinutes)} min", "muted", "small")), load));
                }
            }
        }

        body.Children.Add(Ui.Text("HAND CRAFTING", "section"));
        var handRecipes = content.Recipes.Where(recipe => string.IsNullOrEmpty(recipe.MachineTypeId)).ToList();
        if (handRecipes.Count == 0)
        {
            body.Children.Add(Ui.Text("No hand recipes known.", "muted", "small"));
        }

        foreach (var category in handRecipes.Select(r => r.Category).Distinct().Order(StringComparer.Ordinal))
        {
            var header = Ui.Text(category.ToUpperInvariant(), "category");
            header.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(header);
            foreach (var recipe in handRecipes.Where(r => r.Category == category))
            {
                var status = FarmEngine.Core.Crafting.CraftableStatus(ctx, state, recipe);
                var info = Ui.VStack(2, Ui.Text(recipe.Name, "h3"), Ui.Wrapped(IngredientLine(recipe), "muted", "small"));
                if (!status.Craftable && status.Message is not null)
                {
                    var blocked = Ui.Wrapped(status.Message, "small");
                    blocked.Classes.Add(status.Reason == CraftBlockReasons.Station ? "error" : "muted");
                    info.Children.Add(blocked);
                }

                var craft = Ui.Button("Craft", () => session.RunCommand(new CraftCommand(recipe.Id)), "accent", "small");
                craft.Name = $"Craft_{recipe.Id}";
                craft.IsEnabled = status.Craftable;
                if (!status.Craftable && status.Message is not null)
                {
                    ToolTip.SetTip(craft, status.Message);
                }

                body.Children.Add(Ui.Row(info, craft));
            }
        }

        var placeable = content.MachineTypes.Where(type => !string.IsNullOrEmpty(type.ItemId) && inventory.Any(slot => slot.Item.Id == type.ItemId)).ToList();
        if (placeable.Count > 0)
        {
            body.Children.Add(Ui.Text("PLACE MACHINE (on the tile you face)", "section"));
            foreach (var type in placeable)
            {
                var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = new SolidColorBrush(ToColor(type.Color)) };
                var info = Ui.HStack(10, swatch, Ui.VStack(2, Ui.Text(type.Name, "h3"), Ui.Text(type.Description, "muted", "small")));
                body.Children.Add(Ui.Row(info, Ui.Button("Place", () => session.RunCommand(new PlaceMachineCommand(type.Id)), "accent", "small")));
            }
        }

        var card = Ui.ModalCard("CraftingDialog", "IconHammer", "Crafting", facingType is not null ? $"Facing: {facingType.Name}" : "Hand crafting & machine placement", body, onClose, 560, closeLabel: "Close (X)");
        return new Border { Child = card }.WithClasses("backdrop");
    }

    // ─── PlayerInventory ────────────────────────────────────────────────

    /// <summary>Inventory (web PlayerInventory): items with Use (bound action) and Gift (faced NPC).</summary>
    public static Control Inventory(PlaySession session, Action onClose)
    {
        var player = session.State.Player;
        Control body;
        if (player.Inventory.Count == 0)
        {
            body = Ui.Empty("IconPackage", "Your inventory is empty", "Explore the world to find items!");
        }
        else
        {
            var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var slot in player.Inventory)
            {
                var item = slot.Item;
                var art = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.Parse("#4DF9C718")),
                    BorderBrush = (IBrush?)Application.Current?.FindResource("FarmBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Child = ItemArt(session, item),
                };
                var meta = Ui.HStack(8);
                if (item.Stackable)
                {
                    meta.Children.Add(new Border { Child = Ui.Text($"×{Ui.Num(slot.Quantity)}") }.WithClasses("qty-chip"));
                }

                meta.Children.Add(Ui.Text(Ui.Money(item.Value), "muted", "small"));
                if (item.Type == "tool" && item.Durability is { } durability && item.MaxDurability is { } max)
                {
                    meta.Children.Add(Ui.Text($"{Ui.Num(durability)}/{Ui.Num(max)}", "muted", "small"));
                }

                var buttons = Ui.HStack(4);
                if (!string.IsNullOrEmpty(item.UseActionId))
                {
                    buttons.Children.Add(Ui.Button("Use", () =>
                    {
                        onClose();
                        session.RunCommand(new UseItemCommand(item.Id));
                    }, "secondary", "small"));
                }

                if (item.Type != "tool")
                {
                    var gift = Ui.Button("Gift", () => session.RunCommand(new GiveGiftCommand(item.Id)), "tool", "small");
                    ToolTip.SetTip(gift, "Give to the NPC you are facing");
                    buttons.Children.Add(gift);
                }

                var name = Ui.Text(item.Name, "h3");
                name.Foreground = (IBrush?)Application.Current?.FindResource("FarmForegroundBrush");
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var description = Ui.Text(item.Description, "muted", "small");
                description.TextTrimming = TextTrimming.CharacterEllipsis;
                var info = Ui.VStack(3, name, description, meta);
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                rowGrid.Children.Add(art);
                info.Margin = new Thickness(10, 0, 6, 0);
                Grid.SetColumn(info, 1);
                rowGrid.Children.Add(info);
                buttons.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(buttons, 2);
                rowGrid.Children.Add(buttons);
                grid.Children.Add(new Border { Child = rowGrid, Margin = new Thickness(0, 0, 6, 6) }.WithClasses("row"));
            }

            body = grid;
        }

        var total = player.Inventory.Sum(slot => slot.Item.Value * slot.Quantity);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(Ui.Text($"Total value: {Ui.Money(total)}", "muted"));
        var close = Ui.Button("Close", onClose, "accent");
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);

        var card = Ui.ModalCard("InventoryPanel", "IconPackage", "Inventory", $"{player.Inventory.Count} / {Ui.Num(player.MaxInventorySize)} slots used", body, onClose, 640, footer, "Close (I)");
        return new Border { Child = card }.WithClasses("backdrop");
    }

    /// <summary>First frame of an item's custom art, or a package glyph.</summary>
    private static Control ItemArt(PlaySession session, Item item)
    {
        var definition = session.Content.Items.FirstOrDefault(i => i.Id == item.Id) ?? item;
        var sprite = FarmEngine.Rendering.Graphics.ResolveVisual(session.Project.CustomAssets ?? [], definition.Visual ?? item.Visual, 0);
        if (sprite is not null && SpriteImage.Create(sprite, session.Project.Graphics?.PixelArt != false) is { } image)
        {
            image.Width = 32;
            image.Height = 32;
            return image;
        }

        var glyph = Ui.Icon("IconPackage", 22);
        glyph.Foreground = new SolidColorBrush(Color.Parse("#8A6A1F"));
        return glyph;
    }

    // ─── QuestTracker ───────────────────────────────────────────────────

    public static Control Quests(PlaySession session, Action onClose)
    {
        var state = session.State;
        var quests = session.Content.Quests;
        QuestProgress? Progress(Quest quest) => state.Quests.TryGetValue(quest.Id, out var p) ? p : null;
        var active = quests.Where(q => Progress(q)?.Status == "active").ToList();
        var completed = quests.Where(q => Progress(q)?.Status == "completed").ToList();

        var body = new StackPanel { Spacing = 10 };
        if (active.Count == 0 && completed.Count == 0)
        {
            body.Children.Add(Ui.Empty("IconStar", "No quests yet", "Talk to NPCs to discover new quests!"));
        }

        if (active.Count > 0)
        {
            body.Children.Add(Ui.Text("ACTIVE QUESTS", "section"));
            foreach (var quest in active)
            {
                body.Children.Add(QuestCard(quest, Progress(quest), completed: false, session.Content.Items));
            }
        }

        if (completed.Count > 0)
        {
            body.Children.Add(Ui.Text("COMPLETED QUESTS", "section"));
            foreach (var quest in completed)
            {
                body.Children.Add(QuestCard(quest, Progress(quest), completed: true, session.Content.Items));
            }
        }

        var card = Ui.ModalCard("QuestLog", "IconStar", "Quest Log", $"{active.Count} active • {completed.Count} completed", body, onClose, 620, closeLabel: "Close (J)");
        return new Border { Child = card }.WithClasses("backdrop");
    }

    private static Border QuestCard(Quest quest, QuestProgress? progress, bool completed, List<Item> items)
    {
        var stack = new StackPanel { Spacing = 6 };
        var title = Ui.HStack(8, Ui.Text(quest.Name, "h2"));
        if (completed)
        {
            title.Children.Add(new Border { Child = Ui.Text("✓ Complete") }.WithClasses("qty-chip"));
        }

        stack.Children.Add(title);
        if (!string.IsNullOrEmpty(quest.Description))
        {
            stack.Children.Add(Ui.Wrapped(quest.Description, "muted", "small"));
        }

        foreach (var objective in quest.Objectives)
        {
            var objectiveProgress = progress?.Objectives.TryGetValue(objective.Id, out var p) == true ? p : null;
            var done = completed || objectiveProgress?.Completed == true;
            var amount = objectiveProgress?.Progress ?? objective.Progress;
            var target = objective.TargetItemQuantity is { } a && a != 0 ? a : objective.TargetCropQuantity is { } b && b != 0 ? b : 1;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var mark = Ui.Text(done ? "●" : "○", done ? "hud-value" : "muted");
            if (done)
            {
                mark.Classes.Add("primary");
            }

            line.Children.Add(mark);
            var text = Ui.Wrapped(objective.Description, "small");
            text.Margin = new Thickness(8, 0);
            if (done)
            {
                text.TextDecorations = TextDecorations.Strikethrough;
                text.Classes.Add("muted");
            }

            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            var count = Ui.Text($"{Ui.Num(Math.Min(amount, target))}/{Ui.Num(target)}", "muted", "small");
            Grid.SetColumn(count, 2);
            line.Children.Add(count);
            stack.Children.Add(line);
            if (!done)
            {
                stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(amount / target * 100, 0, 100), Height = 6, Margin = new Thickness(22, 0, 0, 2) });
            }
        }

        if (quest.Rewards is { } rewards && (rewards.Money is > 0 || rewards.Items is { Count: > 0 }))
        {
            var parts = new List<string>();
            if (rewards.Money is > 0)
            {
                parts.Add(Ui.Money(rewards.Money.Value));
            }

            parts.AddRange((rewards.Items ?? []).Select(reward => $"{Ui.Num(reward.Quantity)}× {items.FirstOrDefault(i => i.Id == reward.ItemId)?.Name ?? reward.ItemId}"));
            stack.Children.Add(Ui.Text($"{(completed ? "Rewards claimed" : "Rewards")}: {string.Join(" · ", parts)}", "muted", "small"));
        }

        var border = new Border { Child = stack, Opacity = completed ? 0.75 : 1 }.WithClasses("row");
        return border;
    }

    public static Color ToColor(string css)
    {
        var sk = FarmEngine.Rendering.CssColor.Parse(css);
        return Color.FromArgb(sk.Alpha, sk.Red, sk.Green, sk.Blue);
    }
}
