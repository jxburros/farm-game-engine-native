//! Authored animation selection and snapshot decoration (renderer-canvas2d/graphics.ts).
use crate::snapshot::*;
use farm_sim::schema::*;

/// Exact frame-duration boundaries, cross-image frames and legacy directional sheets.
pub fn resolve_visual(
    assets: &[CustomAsset],
    visual: Option<&VisualRef>,
    tick: f64,
    direction: &str,
    moving: bool,
) -> Option<SnapshotSprite> {
    let visual = visual?;
    let asset = assets.iter().find(|a| a.id == visual.asset_id)?;
    let clips = asset.animations.as_deref().unwrap_or(&[]);
    let preferred = if let Some(animation) = &visual.animation {
        vec![animation.clone()]
    } else if moving {
        vec![format!("walk-{direction}"), "walk".to_owned(), format!("idle-{direction}"), "idle".to_owned()]
    } else {
        vec![format!("idle-{direction}"), "idle".to_owned()]
    };
    let clip = preferred.iter().find_map(|name| clips.iter().find(|c| c.name == *name)).or_else(|| clips.first());
    let mut frame = visual.frame.as_ref();
    if frame.is_none() {
        if let Some(clip) = clip.filter(|c| !c.frames.is_empty()) {
            let duration: f64 = clip.frames.iter().map(|f| f.ticks).sum();
            let mut time = if clip.r#loop { tick.max(0.0) % duration } else { tick.max(0.0).min(duration - 1.0) };
            frame = clip.frames.last();
            for candidate in &clip.frames {
                if time < candidate.ticks {
                    frame = Some(candidate);
                    break;
                }
                time -= candidate.ticks;
            }
        }
    }
    let source = if let Some(id) = frame.and_then(|f| f.asset_id.as_ref()) {
        assets.iter().find(|a| a.id == *id)?
    } else {
        asset
    };
    if source.data_url.is_empty() {
        return None;
    }
    let mut result = SnapshotSprite { image_url: source.data_url.clone(), ..Default::default() };
    if let Some(frame) = frame {
        result.frame_width = frame.width;
        result.frame_height = frame.height;
        result.source_x = Some(frame.x);
        result.source_y = Some(frame.y);
    } else if let Some(sheet) = &asset.sheet {
        result.frame_width = sheet.frame_width;
        result.frame_height = sheet.frame_height;
        result.frame = if moving && sheet.ticks_per_frame > 0.0 && sheet.frames > 0.0 {
            (tick / sheet.ticks_per_frame).floor() % sheet.frames
        } else {
            0.0
        };
        result.row = if sheet.directional {
            match direction {
                "left" => 1.0,
                "right" => 2.0,
                "up" => 3.0,
                _ => 0.0,
            }
        } else {
            0.0
        };
    } else {
        result.frame_width = asset.width.unwrap_or(0.0);
        result.frame_height = asset.height.unwrap_or(0.0);
    }
    Some(result)
}

fn legacy(assets: &[CustomAsset], url: Option<&str>) -> Option<VisualRef> {
    let url = url.filter(|u| !u.is_empty())?;
    assets.iter().find(|a| a.data_url == url).map(|a| VisualRef { asset_id: a.id.clone(), ..Default::default() })
}

/// Project art plus live merged definitions. `None` state means an editor preview.
pub fn apply_graphics(
    snapshot: &mut WorldSnapshot,
    project: &GameProject,
    content: &GameContent,
    state: Option<&GameState>,
    scene: &Scene,
    tick: f64,
    moving: bool,
) {
    let assets = &project.custom_assets;
    let art = |visual: Option<&VisualRef>| resolve_visual(assets, visual, tick, "down", true);
    snapshot.pixel_art = Some(project.graphics.as_ref().is_none_or(|g| g.pixel_art));
    snapshot.player.entity.image_url = project.player_custom_image.clone();
    snapshot.player.entity.sprite = resolve_visual(
        assets,
        project.player_visual.as_ref().or(legacy(assets, project.player_custom_image.as_deref()).as_ref()),
        tick,
        &snapshot.player.entity.direction,
        moving,
    );
    let definitions = if state.is_some() { &content.npcs } else { &project.npcs };
    let npcs: Vec<_> = definitions
        .iter()
        .filter(|n| {
            let live = state.and_then(|s| s.npcs.get(&n.id));
            live.map_or(n.scene_id.as_str(), |n| n.scene_id.as_str()) == scene.id
        })
        .collect();
    for (i, npc) in npcs.iter().enumerate() {
        if let Some(target) = snapshot.npcs.get_mut(i) {
            target.sprite = art(npc.visual.as_ref().or(legacy(assets, npc.custom_image.as_deref()).as_ref()));
        }
    }
    let animals = state.map_or(&project.animals, |s| &s.animals);
    let species = if state.is_some() { &content.animal_species } else { &project.animal_species };
    for (i, animal) in animals.iter().filter(|a| a.scene_id == scene.id).enumerate() {
        if let Some(target) = snapshot.npcs.get_mut(npcs.len() + i) {
            target.sprite = art(species.iter().find(|s| s.id == animal.species_id).and_then(|s| s.visual.as_ref()));
        }
    }
    let nodes = if state.is_some() { &content.node_types } else { &project.node_types };
    let machines = if state.is_some() { &content.machine_types } else { &project.machine_types };
    for (y, row) in scene.tiles.iter().enumerate() {
        for (x, tile) in row.iter().enumerate() {
            let Some(target) = snapshot.tiles.get_mut(y).and_then(|row| row.get_mut(x)) else {
                continue;
            };
            let layer = |kind: Option<&str>, visual: Option<&VisualRef>| {
                let fallback = kind
                    .and_then(|kind| assets.iter().find(|a| a.r#type == "tile" && a.tile_type.as_deref() == Some(kind)))
                    .map(|a| VisualRef { asset_id: a.id.clone(), ..Default::default() });
                art(visual.or(fallback.as_ref()))
            };
            target.art_layers = Some(vec![
                layer(Some(&tile.background), tile.visuals.as_ref().and_then(|v| v.background.as_ref())),
                layer(tile.overlay.as_deref(), tile.visuals.as_ref().and_then(|v| v.overlay.as_ref())),
                layer(tile.object.as_deref(), tile.visuals.as_ref().and_then(|v| v.object.as_ref())),
            ]);
            if let (Some(target), Some(crop)) = (&mut target.crop, &tile.crop) {
                let definition = project.custom_crops.as_deref().unwrap_or(&[]).iter().find(|c| c.id == crop.r#type);
                let mut visual =
                    definition.and_then(|c| c.visual.clone().or_else(|| legacy(assets, c.custom_asset.as_deref())));
                if let Some(visual) = &mut visual {
                    let growth = assets
                        .iter()
                        .find(|a| a.id == visual.asset_id)
                        .and_then(|a| a.animations.as_ref())
                        .and_then(|clips| clips.iter().find(|c| c.name == "growth" && !c.frames.is_empty()));
                    if let Some(growth) = growth {
                        visual.frame =
                            Some(growth.frames[(crop.stage.max(0.0) as usize).min(growth.frames.len() - 1)].clone());
                    }
                }
                target.sprite = art(visual.as_ref());
            }
            if let Some(target) = &mut target.node {
                target.sprite = art(tile
                    .node
                    .as_ref()
                    .and_then(|n| nodes.iter().find(|d| d.id == n.type_id))
                    .and_then(|d| d.visual.as_ref()));
            }
            if let Some(target) = &mut target.machine {
                target.sprite = art(tile
                    .machine
                    .as_ref()
                    .and_then(|m| machines.iter().find(|d| d.id == m.type_id))
                    .and_then(|d| d.visual.as_ref()));
            }
            if let (Some(target), Some(item)) = (&mut target.item, &tile.item) {
                target.sprite = art(item.visual.as_ref().or(legacy(assets, item.custom_image.as_deref()).as_ref()));
            }
        }
    }
}
