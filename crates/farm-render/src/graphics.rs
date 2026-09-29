//! Port of `Graphics.cs` (packages/renderer-canvas2d/src/graphics.ts): pure frame selection and
//! the decoration of a world snapshot with the art a project binds. Nothing here changes a
//! gameplay rule; it only picks images and frames.

use crate::num::{cs_max, cs_min};
use crate::snapshot::{SnapshotSprite, WorldSnapshot};
use farm_sim::schema::{
    AnimationClip, ArtFrame, CustomAsset, CustomCropDefinition, GameContent, GameProject, GameState, GraphicsSettings,
    Scene, SpriteSheet, VisualRef,
};
use farm_sim::Presentation;
use std::sync::Arc;

const DIRECTION_ROWS: [(&str, f64); 4] = [("down", 0.0), ("left", 1.0), ("right", 2.0), ("up", 3.0)];

/// A custom asset as the graphics pipeline reads it. The image URL is shared, so every sprite cut
/// from it points at the same string (no copy of a large `data:` URL per sprite per frame).
#[derive(Debug, Clone, PartialEq)]
pub struct ArtAsset {
    pub id: String,
    /// `tile`, `npc`, `item`, `player` or `art`.
    pub asset_type: String,
    pub tile_type: Option<String>,
    pub image_url: Arc<str>,
    pub width: Option<f64>,
    pub height: Option<f64>,
    pub animations: Option<Vec<AnimationClip>>,
    pub sheet: Option<SpriteSheet>,
}

impl From<&CustomAsset> for ArtAsset {
    fn from(asset: &CustomAsset) -> Self {
        Self {
            id: asset.id.clone(),
            asset_type: asset.r#type.clone(),
            tile_type: asset.tile_type.clone(),
            image_url: Arc::from(asset.data_url.as_str()),
            width: asset.width,
            height: asset.height,
            animations: asset.animations.clone(),
            sheet: asset.sheet.clone(),
        }
    }
}

/// Converts a project's custom assets once (keep the result to share URLs across frames).
pub fn art_assets(assets: &[CustomAsset]) -> Vec<ArtAsset> {
    assets.iter().map(ArtAsset::from).collect()
}

/// What an NPC contributes to the art pass: where it is and what it looks like.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NpcArt {
    pub scene_id: String,
    pub visual: Option<VisualRef>,
    pub custom_image: Option<String>,
}

/// An animal instance for the art pass.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AnimalArt {
    pub scene_id: String,
    pub species_id: String,
}

/// A content definition's art binding (node types, machine types, animal species).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DefinitionArt {
    pub id: String,
    pub visual: Option<VisualRef>,
}

/// A custom crop's art binding.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CropArt {
    pub id: String,
    pub visual: Option<VisualRef>,
    /// Legacy image binding (a `data:` URL of a custom asset).
    pub custom_asset: Option<String>,
}

/// The subset of a game the graphics pipeline reads (TS `GraphicsSource`): custom assets, player
/// art, and the definitions that carry [`VisualRef`]s.
///
/// In play, build it once from the game's [`Presentation`] (a cartridge or
/// `Presentation::from_project`) with [`GraphicsSource::from_state`], then refresh the live parts
/// per frame with [`GraphicsSource::set_live_state`]; the asset URLs stay shared across frames.
/// Edit Mode uses [`GraphicsSource::from_project`].
#[derive(Debug, Clone, PartialEq)]
pub struct GraphicsSource {
    pub assets: Vec<ArtAsset>,
    pub player_visual: Option<VisualRef>,
    pub player_custom_image: Option<String>,
    /// `graphics.pixelArt` (true when the game has no graphics settings).
    pub pixel_art: bool,
    pub npcs: Vec<NpcArt>,
    pub animal_species: Vec<DefinitionArt>,
    pub animals: Vec<AnimalArt>,
    pub node_types: Vec<DefinitionArt>,
    pub machine_types: Vec<DefinitionArt>,
    pub custom_crops: Vec<CropArt>,
}

fn definitions<'a>(items: impl IntoIterator<Item = (&'a String, &'a Option<VisualRef>)>) -> Vec<DefinitionArt> {
    items.into_iter().map(|(id, visual)| DefinitionArt { id: id.clone(), visual: visual.clone() }).collect()
}

fn crop_art(crops: Option<&Vec<CustomCropDefinition>>) -> Vec<CropArt> {
    crops
        .into_iter()
        .flatten()
        .map(|crop| CropArt {
            id: crop.id.clone(),
            visual: crop.visual.clone(),
            custom_asset: crop.custom_asset.clone(),
        })
        .collect()
}

fn animal_art(animals: &[farm_sim::schema::AnimalState]) -> Vec<AnimalArt> {
    animals
        .iter()
        .map(|animal| AnimalArt { scene_id: animal.scene_id.clone(), species_id: animal.species_id.clone() })
        .collect()
}

impl GraphicsSource {
    fn base(
        assets: &[CustomAsset],
        player_visual: Option<&VisualRef>,
        player_custom_image: Option<&String>,
        graphics: Option<&GraphicsSettings>,
        custom_crops: Option<&Vec<CustomCropDefinition>>,
    ) -> Self {
        Self {
            assets: art_assets(assets),
            player_visual: player_visual.cloned(),
            player_custom_image: player_custom_image.cloned(),
            pixel_art: graphics.is_none_or(|graphics| graphics.pixel_art),
            npcs: Vec::new(),
            animal_species: Vec::new(),
            animals: Vec::new(),
            node_types: Vec::new(),
            machine_types: Vec::new(),
            custom_crops: crop_art(custom_crops),
        }
    }

    /// The presentation parts only (assets, player art, pixel-art mode, custom crops); NPCs,
    /// animals and definitions come from [`GraphicsSource::set_live_state`].
    pub fn from_presentation(presentation: &Presentation) -> Self {
        Self::base(
            &presentation.custom_assets,
            presentation.player_visual.as_ref(),
            presentation.player_custom_image.as_ref(),
            presentation.graphics.as_ref(),
            presentation.custom_crops.as_ref(),
        )
    }

    /// Play mode (game-shell `draw()`): presentation art plus live NPC and animal positions from
    /// the state and the merged content definitions.
    pub fn from_state(presentation: &Presentation, content: &GameContent, state: &GameState) -> Self {
        let mut source = Self::from_presentation(presentation);
        source.set_live_state(content, state);
        source
    }

    /// Everything straight from the project (Edit Mode, where the project is the live state).
    pub fn from_project(project: &GameProject) -> Self {
        let mut source = Self::base(
            &project.custom_assets,
            project.player_visual.as_ref(),
            project.player_custom_image.as_ref(),
            project.graphics.as_ref(),
            project.custom_crops.as_ref(),
        );
        source.npcs = project
            .npcs
            .iter()
            .map(|npc| NpcArt {
                scene_id: npc.scene_id.clone(),
                visual: npc.visual.clone(),
                custom_image: npc.custom_image.clone(),
            })
            .collect();
        source.animal_species = definitions(project.animal_species.iter().map(|d| (&d.id, &d.visual)));
        source.animals = animal_art(&project.animals);
        source.node_types = definitions(project.node_types.iter().map(|d| (&d.id, &d.visual)));
        source.machine_types = definitions(project.machine_types.iter().map(|d| (&d.id, &d.visual)));
        source
    }

    /// Replaces the live parts (NPC scenes, animals, merged definitions) from `content` and
    /// `state`, keeping the assets.
    pub fn set_live_state(&mut self, content: &GameContent, state: &GameState) {
        self.npcs = content
            .npcs
            .iter()
            .map(|npc| NpcArt {
                scene_id: state.npcs.get(&npc.id).map_or(&npc.scene_id, |live| &live.scene_id).clone(),
                visual: npc.visual.clone(),
                custom_image: npc.custom_image.clone(),
            })
            .collect();
        self.node_types = definitions(content.node_types.iter().map(|d| (&d.id, &d.visual)));
        self.machine_types = definitions(content.machine_types.iter().map(|d| (&d.id, &d.visual)));
        self.animal_species = definitions(content.animal_species.iter().map(|d| (&d.id, &d.visual)));
        self.animals = animal_art(&state.animals);
    }

    fn visual_of<'a>(list: &'a [DefinitionArt], id: Option<&str>) -> Option<&'a VisualRef> {
        let id = id?;
        list.iter().find(|definition| definition.id == id)?.visual.as_ref()
    }
}

/// JS `%` (the C# `JsMod` helper): NaN for a zero divisor.
fn js_mod(x: f64, y: f64) -> f64 {
    if y == 0.0 {
        f64::NAN
    } else {
        x % y
    }
}

/// Pure frame selection, shared by previews and exported games (`Graphics.ResolveVisual`).
///
/// `direction` and `moving` pick direction-specific walk/idle clips and sheet rows; creator
/// definitions (NPCs, items, tiles…) use the defaults `"down"` and `true`.
pub fn resolve_visual(
    assets: &[ArtAsset],
    visual: Option<&VisualRef>,
    tick: f64,
    direction: &str,
    moving: bool,
) -> Option<SnapshotSprite> {
    let visual = visual?;
    let asset = assets.iter().find(|asset| asset.id == visual.asset_id)?;
    let mut source = asset;
    let mut frame: Option<ArtFrame> = visual.frame.clone();
    let preferred: Vec<String> = match &visual.animation {
        Some(animation) => vec![animation.clone()],
        None if moving => {
            vec![format!("walk-{direction}"), "walk".to_owned(), format!("idle-{direction}"), "idle".to_owned()]
        }
        None => vec![format!("idle-{direction}"), "idle".to_owned()],
    };
    let clips = asset.animations.as_deref().unwrap_or_default();
    let clip = preferred.iter().find_map(|name| clips.iter().find(|clip| clip.name == *name)).or_else(|| clips.first());
    if let (None, Some(clip)) = (&frame, clip) {
        if !clip.frames.is_empty() {
            let duration: f64 = clip.frames.iter().map(|f| f.ticks).sum();
            let mut time = if clip.r#loop {
                js_mod(cs_max(0.0, tick), duration)
            } else {
                cs_min(cs_max(0.0, tick), duration - 1.0)
            };
            let mut chosen = clip.frames.last();
            for candidate in &clip.frames {
                if time < candidate.ticks {
                    chosen = Some(candidate);
                    break;
                }
                time -= candidate.ticks;
            }
            frame = chosen.cloned();
        }
    }

    if let Some(frame_asset_id) = frame.as_ref().and_then(|f| f.asset_id.as_ref()) {
        source = assets.iter().find(|asset| asset.id == *frame_asset_id)?;
    }

    if source.image_url.is_empty() {
        return None;
    }
    let image_url = Arc::clone(&source.image_url);

    if let Some(frame) = frame {
        return Some(SnapshotSprite {
            image_url,
            frame_width: frame.width,
            frame_height: frame.height,
            frame: 0.0,
            row: 0.0,
            source_x: Some(frame.x),
            source_y: Some(frame.y),
        });
    }

    if let Some(sheet) = &asset.sheet {
        let frame = if moving && sheet.ticks_per_frame > 0.0 && sheet.frames > 0.0 {
            js_mod((tick / sheet.ticks_per_frame).floor(), sheet.frames)
        } else {
            0.0
        };
        let row = if sheet.directional {
            DIRECTION_ROWS.iter().find(|(name, _)| *name == direction).map_or(0.0, |(_, row)| *row)
        } else {
            0.0
        };
        return Some(SnapshotSprite {
            image_url,
            frame_width: sheet.frame_width,
            frame_height: sheet.frame_height,
            frame,
            row,
            source_x: None,
            source_y: None,
        });
    }

    // Zero sizes mean a full image; the actual dimensions become available at draw time.
    Some(SnapshotSprite {
        image_url,
        frame_width: asset.width.unwrap_or(0.0),
        frame_height: asset.height.unwrap_or(0.0),
        frame: 0.0,
        row: 0.0,
        source_x: None,
        source_y: None,
    })
}

/// Decorates a simulation snapshot with the project's art, in place (`Graphics.ApplyGraphics`):
/// pixel-art mode, player art, NPC and animal sprites (matched by order within the scene),
/// per-layer tile art, crop growth frames, node, machine and item art. Nothing else changes.
pub fn apply_graphics(snapshot: &mut WorldSnapshot, source: &GraphicsSource, scene: &Scene, tick: f64, moving: bool) {
    let assets = &source.assets;
    let legacy = |url: Option<&str>| -> Option<VisualRef> {
        let url = url.filter(|url| !url.is_empty())?;
        let asset = assets.iter().find(|asset| &*asset.image_url == url)?;
        Some(VisualRef { asset_id: asset.id.clone(), ..VisualRef::default() })
    };
    let art = |visual: Option<&VisualRef>| resolve_visual(assets, visual, tick, "down", true);

    snapshot.pixel_art = Some(source.pixel_art);
    snapshot.player.image_url = source.player_custom_image.as_deref().map(Arc::from);
    let player_visual = source.player_visual.clone().or_else(|| legacy(source.player_custom_image.as_deref()));
    snapshot.player.sprite = resolve_visual(assets, player_visual.as_ref(), tick, &snapshot.player.direction, moving);

    let npcs: Vec<&NpcArt> = source.npcs.iter().filter(|npc| npc.scene_id == scene.id).collect();
    for (target, npc) in snapshot.npcs.iter_mut().zip(&npcs) {
        let visual = npc.visual.clone().or_else(|| legacy(npc.custom_image.as_deref()));
        target.sprite = art(visual.as_ref());
    }
    let animals = source.animals.iter().filter(|animal| animal.scene_id == scene.id);
    for (target, animal) in snapshot.npcs.iter_mut().skip(npcs.len()).zip(animals) {
        target.sprite = art(GraphicsSource::visual_of(&source.animal_species, Some(&animal.species_id)));
    }

    for (y, row) in scene.tiles.iter().enumerate() {
        let Some(targets) = snapshot.tiles.get_mut(y) else { continue };
        for (tile, target) in row.iter().zip(targets.iter_mut()) {
            let layer = |tile_type: Option<&str>, visual: Option<&VisualRef>| {
                let fallback = tile_type.and_then(|tile_type| {
                    assets
                        .iter()
                        .find(|asset| asset.asset_type == "tile" && asset.tile_type.as_deref() == Some(tile_type))
                });
                let fallback = fallback.map(|asset| VisualRef { asset_id: asset.id.clone(), ..VisualRef::default() });
                art(visual.or(fallback.as_ref()))
            };
            let visuals = tile.visuals.as_ref();
            target.art_layers = Some(vec![
                layer(Some(&tile.background), visuals.and_then(|v| v.background.as_ref())),
                layer(tile.overlay.as_deref(), visuals.and_then(|v| v.overlay.as_ref())),
                layer(tile.object.as_deref(), visuals.and_then(|v| v.object.as_ref())),
            ]);

            if let (Some(target_crop), Some(crop)) = (target.crop.as_mut(), tile.crop.as_ref()) {
                let definition = source.custom_crops.iter().find(|c| c.id == crop.r#type);
                let visual = definition
                    .and_then(|d| d.visual.clone())
                    .or_else(|| legacy(definition.and_then(|d| d.custom_asset.as_deref())));
                let asset = visual.as_ref().and_then(|v| assets.iter().find(|asset| asset.id == v.asset_id));
                let growth = asset
                    .and_then(|asset| asset.animations.as_ref())
                    .and_then(|clips| clips.iter().find(|clip| clip.name == "growth"));
                let visual = match (growth, visual) {
                    (Some(growth), Some(visual)) if !growth.frames.is_empty() => {
                        let index = (crop.stage as usize).min(growth.frames.len() - 1);
                        Some(VisualRef { frame: Some(growth.frames[index].clone()), ..visual })
                    }
                    (_, visual) => visual,
                };
                target_crop.sprite = art(visual.as_ref());
            }

            if let Some(node) = target.node.as_mut() {
                let type_id = tile.node.as_ref().map(|n| n.type_id.as_str());
                node.sprite = art(GraphicsSource::visual_of(&source.node_types, type_id));
            }
            if let Some(machine) = target.machine.as_mut() {
                let type_id = tile.machine.as_ref().map(|m| m.type_id.as_str());
                machine.sprite = art(GraphicsSource::visual_of(&source.machine_types, type_id));
            }
            if let Some(item) = target.item.as_mut() {
                let visual = tile
                    .item
                    .as_ref()
                    .and_then(|item| item.visual.clone().or_else(|| legacy(item.custom_image.as_deref())));
                item.sprite = art(visual.as_ref());
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(asset_id: Option<&str>, x: f64, ticks: f64) -> ArtFrame {
        ArtFrame { asset_id: asset_id.map(str::to_owned), x, y: 0.0, width: 16.0, height: 16.0, ticks }
    }

    fn assets() -> Vec<ArtAsset> {
        art_assets(&[
            CustomAsset {
                id: "a".into(),
                name: "Sheet".into(),
                r#type: "art".into(),
                data_url: "data:image/png;base64,a".into(),
                width: Some(32.0),
                height: Some(16.0),
                animations: Some(vec![AnimationClip {
                    name: "idle".into(),
                    r#loop: true,
                    frames: vec![frame(None, 0.0, 2.0), frame(Some("b"), 0.0, 3.0)],
                }]),
                ..CustomAsset::default()
            },
            CustomAsset {
                id: "b".into(),
                name: "Frame".into(),
                r#type: "art".into(),
                data_url: "data:image/png;base64,b".into(),
                width: Some(16.0),
                height: Some(16.0),
                ..CustomAsset::default()
            },
        ])
    }

    fn visual(id: &str) -> VisualRef {
        VisualRef { asset_id: id.into(), ..VisualRef::default() }
    }

    fn url_at(assets: &[ArtAsset], tick: f64) -> Option<String> {
        resolve_visual(assets, Some(&visual("a")), tick, "down", true).map(|s| s.image_url.to_string())
    }

    #[test]
    fn selects_exact_frame_boundaries_and_resolves_separate_image_frames() {
        let assets = assets();
        let a = Some("data:image/png;base64,a".to_owned());
        let b = Some("data:image/png;base64,b".to_owned());
        assert_eq!(url_at(&assets, 1.0), a);
        assert_eq!(url_at(&assets, 2.0), b);
        assert_eq!(url_at(&assets, 4.0), b);
        assert_eq!(url_at(&assets, 5.0), a);
        assert_eq!(url_at(&assets, -3.0), a);
        let mut once = assets.clone();
        once[0].animations.as_mut().unwrap()[0].r#loop = false;
        assert_eq!(url_at(&once, 100.0), b);
        assert!(resolve_visual(&assets, Some(&visual("missing")), 0.0, "down", true).is_none());
        assert!(resolve_visual(&assets, None, 0.0, "down", true).is_none());
        // Frames share the asset's URL allocation.
        let sprite = resolve_visual(&assets, Some(&visual("a")), 1.0, "down", true).unwrap();
        assert!(Arc::ptr_eq(&sprite.image_url, &assets[0].image_url));
    }

    #[test]
    fn prefers_direction_specific_player_clips_over_generic_clips() {
        let mut assets = assets();
        assets[0].animations.as_mut().unwrap().push(AnimationClip {
            name: "walk-left".into(),
            r#loop: true,
            frames: vec![frame(None, 16.0, 1.0)],
        });
        let walking = resolve_visual(&assets, Some(&visual("a")), 0.0, "left", true).unwrap();
        assert_eq!(walking.source_x, Some(16.0));
        let idle = resolve_visual(&assets, Some(&visual("a")), 0.0, "left", false).unwrap();
        assert_eq!(idle.source_x, Some(0.0));
        let named = VisualRef { animation: Some("walk-left".into()), ..visual("a") };
        assert_eq!(resolve_visual(&assets, Some(&named), 0.0, "down", false).unwrap().source_x, Some(16.0));
        let fixed = VisualRef { frame: Some(frame(None, 7.0, 1.0)), ..visual("a") };
        assert_eq!(resolve_visual(&assets, Some(&fixed), 0.0, "left", true).unwrap().source_x, Some(7.0));
    }

    #[test]
    fn keeps_legacy_directional_sheets_working() {
        let mut sheet = assets().remove(0);
        sheet.animations = Some(Vec::new());
        sheet.sheet = Some(SpriteSheet {
            frame_width: 8.0,
            frame_height: 4.0,
            frames: 4.0,
            directional: true,
            ticks_per_frame: 2.0,
            ..SpriteSheet::default()
        });
        let moving = resolve_visual(std::slice::from_ref(&sheet), Some(&visual("a")), 5.0, "up", true).unwrap();
        assert_eq!((moving.frame, moving.row), (2.0, 3.0));
        let idle = resolve_visual(std::slice::from_ref(&sheet), Some(&visual("a")), 5.0, "up", false).unwrap();
        assert_eq!((idle.frame, idle.row), (0.0, 3.0));
        // Plain images keep their authored size (0 = full image at draw time).
        let plain = resolve_visual(&assets()[1..], Some(&visual("b")), 0.0, "down", true).unwrap();
        assert_eq!((plain.frame_width, plain.frame_height, plain.source_x), (16.0, 16.0, None));
    }

    #[test]
    fn empty_image_urls_resolve_to_nothing() {
        let mut assets = assets();
        assets[1].image_url = Arc::from("");
        assert!(resolve_visual(&assets, Some(&visual("b")), 0.0, "down", true).is_none());
        assert!(js_mod(5.0, 0.0).is_nan());
    }
}
