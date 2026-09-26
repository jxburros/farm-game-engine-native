//! Animals (port of `Animals.cs` / animals.ts).

use crate::engine_types::{Effects, EngineContext};
use crate::schema::{AnimalSpeciesDefinition, AnimalState, GameState};

pub fn species_by_id<'a>(ctx: &'a EngineContext, species_id: &str) -> Option<&'a AnimalSpeciesDefinition> {
    let _ = (ctx, species_id);
    todo!("port Animals.SpeciesById")
}

pub fn animal_at<'a>(state: &'a GameState, scene_id: &str, x: f64, y: f64) -> Option<&'a AnimalState> {
    let _ = (state, scene_id, x, y);
    todo!("port Animals.AnimalAt")
}

/// `animal` is a copy of the animal being interacted with (the C# passes the record).
pub fn handle_animal_interaction(ctx: &EngineContext, state: &mut GameState, animal: &AnimalState) -> Effects {
    let _ = (ctx, state, animal);
    todo!("port Animals.HandleAnimalInteraction")
}

/// C# returned a new state; here the animals advance in place.
pub fn advance_animals_nightly(ctx: &EngineContext, state: &mut GameState) {
    let _ = (ctx, state);
    todo!("port Animals.AdvanceAnimalsNightly")
}
