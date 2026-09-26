//! Port of `Graphics.cs` (packages/engine-schemas/src/graphics.ts).

use serde::{Deserialize, Serialize};

/// Rectangles are in source pixels; display size remains independent of art resolution.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ArtFrame {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub asset_id: Option<String>,
    /// int, nonnegative.
    pub x: f64,
    /// int, nonnegative.
    pub y: f64,
    /// int, positive.
    pub width: f64,
    /// int, positive.
    pub height: f64,
    /// int, positive.
    pub ticks: f64,
}

impl Default for ArtFrame {
    fn default() -> Self {
        Self { asset_id: None, x: 0.0, y: 0.0, width: 0.0, height: 0.0, ticks: 6.0 }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AnimationClip {
    /// min length 1.
    pub name: String,
    pub r#loop: bool,
    /// 1..1024 frames.
    pub frames: Vec<ArtFrame>,
}

impl Default for AnimationClip {
    fn default() -> Self {
        Self { name: String::new(), r#loop: true, frames: Vec::new() }
    }
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct VisualRef {
    pub asset_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub animation: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub frame: Option<ArtFrame>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GraphicsSettings {
    pub pixel_art: bool,
}

impl Default for GraphicsSettings {
    fn default() -> Self {
        Self { pixel_art: true }
    }
}
