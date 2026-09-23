using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Animals &amp; ranching (M4c). Interacting with an animal feeds it (if you
/// carry its feed), collects a ready product, or pets it. The nightly pass
/// ages animals, rolls products (fed + adult + interval) and adjusts mood.
///
/// v1 simplifications (documented): animals are editor-placed (no in-game
/// purchase flow yet) and stay where placed; barns/coops reuse the existing
/// interior-scene pattern rather than being special buildings.
/// (Port of engine-core/src/animals.ts.)
/// </summary>
public static class Animals
{
    public static AnimalSpeciesDefinition? SpeciesById(EngineContext ctx, string speciesId) =>
        ctx.Content.AnimalSpecies.Find(species => species.Id == speciesId);

    public static AnimalState? AnimalAt(GameState state, string sceneId, double x, double y) =>
        state.Animals.Find(animal => animal.SceneId == sceneId && animal.X == x && animal.Y == y);

    /// <summary>TS <c>updateAnimal(state, id, patch)</c>: the patch is a <c>with</c> transform.</summary>
    private static GameState UpdateAnimal(GameState state, string animalId, Func<AnimalState, AnimalState> patch) =>
        state with
        {
            Animals = state.Animals.Select(animal => animal.Id == animalId ? patch(animal) : animal).ToList(),
        };

    /// <summary>Feed → collect → pet, in that priority.</summary>
    public static EngineStep HandleAnimalInteraction(EngineContext ctx, GameState state, AnimalState animal)
    {
        var species = SpeciesById(ctx, animal.SpeciesId);
        if (species is null) return EngineStep.Of(state);

        // 1. Feed (needs the species' feed item in inventory)
        if (!animal.FedToday && !string.IsNullOrEmpty(species.FeedItemId))
        {
            var feedItemId = species.FeedItemId;
            var feedSlot = state.Player.Inventory.Find(slot => slot.Item.Id == feedItemId);
            if (feedSlot is not null)
            {
                var fed = UpdateAnimal(state, animal.Id, a => a with { FedToday = true, Mood = Math.Min(100, animal.Mood + 5) });
                return EngineStep.Of(
                    fed with { Player = fed.Player with { Inventory = Inventory.RemoveItem(fed.Player.Inventory, feedItemId, 1) } },
                    Effect.Message(MessageLevels.Success, $"Fed {animal.Name}"));
            }
        }

        // 2. Collect a ready product
        if (animal.ProductReady)
        {
            var product = ctx.Content.Items.Find(i => i.Id == species.ProductItemId);
            if (product is not null)
            {
                var added = Inventory.AddItem(state.Player.Inventory, product, 1, state.Player.MaxInventorySize);
                if (!added.Added)
                {
                    return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Inventory is full!"));
                }
                var collected = UpdateAnimal(
                    state with { Player = state.Player with { Inventory = added.Inventory } },
                    animal.Id,
                    a => a with { ProductReady = false, DaysSinceProduct = 0 });
                return EngineStep.Of(collected, Effect.Message(MessageLevels.Success, $"Collected {product.Name} from {animal.Name}"));
            }
        }

        // 3. Pet
        if (!animal.PettedToday)
        {
            var petted = UpdateAnimal(state, animal.Id, a => a with { PettedToday = true, Mood = Math.Min(100, animal.Mood + 8) });
            return EngineStep.Of(petted, Effect.Message(MessageLevels.Success, $"{animal.Name} looks happy! ♥"));
        }

        return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"{animal.Name} is content."));
    }

    /// <summary>Nightly pass for animals: age, mood, product rolls.</summary>
    public static GameState AdvanceAnimalsNightly(EngineContext ctx, GameState state)
    {
        if (state.Animals.Count == 0) return state;

        var animals = state.Animals.Select(animal =>
        {
            var species = SpeciesById(ctx, animal.SpeciesId);
            if (species is null) return animal;

            var needsFeed = !string.IsNullOrEmpty(species.FeedItemId);
            var wasCaredFor = !needsFeed || animal.FedToday;
            var moodDelta = (wasCaredFor ? 4 : -15) + (animal.PettedToday ? 4 : 0);
            var ageDays = animal.AgeDays + 1;
            var isAdult = ageDays >= species.DaysToAdult;

            var daysSinceProduct = animal.DaysSinceProduct;
            var productReady = animal.ProductReady;
            if (isAdult && wasCaredFor && !productReady)
            {
                daysSinceProduct += 1;
                if (daysSinceProduct >= species.ProductIntervalDays && animal.Mood >= 30)
                {
                    productReady = true;
                }
            }

            return animal with
            {
                AgeDays = ageDays,
                Mood = Math.Max(0, Math.Min(100, animal.Mood + moodDelta)),
                FedToday = false,
                PettedToday = false,
                DaysSinceProduct = daysSinceProduct,
                ProductReady = productReady,
            };
        }).ToList();

        return state with { Animals = animals };
    }
}
