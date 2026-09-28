use farm_render::graphics::resolve_visual;
use farm_sim::schema::{AnimationClip, ArtFrame, CustomAsset, SpriteSheet, VisualRef};

fn assets() -> Vec<CustomAsset> {
    vec![
        CustomAsset {
            id: "sheet".into(),
            data_url: "first".into(),
            animations: Some(vec![AnimationClip {
                name: "idle".into(),
                r#loop: true,
                frames: vec![
                    ArtFrame { ticks: 2.0, width: 16.0, height: 16.0, ..Default::default() },
                    ArtFrame {
                        asset_id: Some("other".into()),
                        ticks: 3.0,
                        width: 16.0,
                        height: 16.0,
                        ..Default::default()
                    },
                ],
            }]),
            ..Default::default()
        },
        CustomAsset { id: "other".into(), data_url: "second".into(), ..Default::default() },
    ]
}

#[test]
fn frame_boundaries_cross_image_frames_and_non_looping_clips() {
    let mut assets = assets();
    let visual = VisualRef { asset_id: "sheet".into(), ..Default::default() };
    for (tick, url) in
        [(-1.0, "first"), (0.0, "first"), (1.0, "first"), (2.0, "second"), (4.0, "second"), (5.0, "first")]
    {
        assert_eq!(resolve_visual(&assets, Some(&visual), tick, "down", false).unwrap().image_url, url);
    }
    assets[0].animations.as_mut().unwrap()[0].r#loop = false;
    assert_eq!(resolve_visual(&assets, Some(&visual), 100.0, "down", false).unwrap().image_url, "second");
    assets.pop();
    assert!(resolve_visual(&assets, Some(&visual), 2.0, "down", false).is_none());
}

#[test]
fn directional_clips_explicit_frames_and_legacy_sheets() {
    let mut assets = assets();
    assets[0].animations.as_mut().unwrap().push(AnimationClip {
        name: "walk-left".into(),
        frames: vec![ArtFrame { x: 32.0, ..Default::default() }],
        ..Default::default()
    });
    let mut visual = VisualRef { asset_id: "sheet".into(), ..Default::default() };
    assert_eq!(resolve_visual(&assets, Some(&visual), 0.0, "left", true).unwrap().source_x, Some(32.0));
    assert_eq!(resolve_visual(&assets, Some(&visual), 0.0, "left", false).unwrap().source_x, Some(0.0));
    visual.frame = Some(ArtFrame { x: 48.0, ..Default::default() });
    assert_eq!(resolve_visual(&assets, Some(&visual), 0.0, "left", true).unwrap().source_x, Some(48.0));
    visual.frame = None;
    assets[0].animations = None;
    assets[0].sheet = Some(SpriteSheet {
        frame_width: 8.0,
        frame_height: 4.0,
        frames: 4.0,
        ticks_per_frame: 2.0,
        directional: true,
        ..Default::default()
    });
    let moving = resolve_visual(&assets, Some(&visual), 5.0, "up", true).unwrap();
    assert_eq!((moving.frame, moving.row), (2.0, 3.0));
    let idle = resolve_visual(&assets, Some(&visual), 5.0, "up", false).unwrap();
    assert_eq!((idle.frame, idle.row), (0.0, 3.0));
    assert!(resolve_visual(&assets, None, 0.0, "up", true).is_none());
    assert!(resolve_visual(
        &assets,
        Some(&VisualRef { asset_id: "missing".into(), ..Default::default() }),
        0.0,
        "up",
        true
    )
    .is_none());
}
