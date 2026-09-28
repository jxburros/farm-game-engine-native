//! Test fixtures for the play screens: the starter farm, a UI and a way to click widgets.

use super::{GameAction, GameUi, GameView, ItemArt};
use crate::input::{NavAction, UiInput};
use crate::settings::Bindings;
use crate::ui::{Ui, WidgetId};
use farm_render::{BuiltinArt, ImageStore};
use farm_runtime::host::{calendar_view, HostedMinigame, MinigameView};
use farm_runtime::panels::{self, PanelState};
use farm_sim::schema::{GamePanel, GameProject, GameState};
use farm_sim::{overlay, EngineContext};

pub(crate) struct Fixture {
    pub ctx: EngineContext,
    pub state: GameState,
    pub panels: Vec<GamePanel>,
    pub minigame: Option<MinigameView>,
    pub game_ui: GameUi,
    pub ui: Ui,
    pub images: ImageStore,
    pub size: (f32, f32),
    pub scale: f32,
    pub text_scale: f32,
    /// The last frame's draw list.
    pub last: farm_render::DrawList,
}

pub(crate) fn starter_project() -> GameProject {
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../../../../fixtures/golden/content/starter-farm.json")).unwrap();
    serde_json::from_value(fixture["project"].clone()).unwrap()
}

impl Fixture {
    pub fn starter() -> Self {
        let project = starter_project();
        let ctx = EngineContext::new(farm_sim::create_content_from_project(&project));
        let state = farm_sim::create_game_state(&project, Some("ui-test"));
        Self {
            ctx,
            state,
            panels: Vec::new(),
            minigame: None,
            game_ui: GameUi::new(),
            ui: Ui::default(),
            images: ImageStore::default(),
            size: (1280.0, 800.0),
            scale: 1.0,
            text_scale: 1.0,
            last: farm_render::DrawList::new(),
        }
    }

    /// Mounts the state's minigame the way the player does.
    pub fn mount_minigame(&mut self) {
        let active = self.state.minigame.as_ref().expect("a minigame is open");
        let definition = self.ctx.content.minigames.iter().find(|def| def.id == active.minigame_id);
        self.minigame = Some(HostedMinigame::mount(definition, 0.5).unwrap().view());
    }

    /// One frame; returns the actions.
    pub fn frame(&mut self, input: UiInput) -> Vec<GameAction> {
        let overlay = overlay::overlay_view(&self.ctx, &self.state);
        let calendar = calendar_view(&self.ctx.content, &self.state);
        let modal = self.game_ui.panel.is_some();
        let panel_views = panels::render(&self.panels, &PanelState::from_game_state(&self.state, modal));
        let bindings = Bindings::default();
        let view = GameView {
            state: &self.state,
            content: &self.ctx.content,
            overlay: &overlay,
            calendar: &calendar,
            panels: &panel_views,
            minigame: self.minigame.as_ref(),
            art: ItemArt { assets: &[], pixel_art: true, builtin: Some(BuiltinArt::embedded()) },
            bindings: &bindings,
            show_made_with: true,
            keys_active: true,
        };
        self.ui.begin_frame(input, self.size, self.scale, self.text_scale, 0.0);
        let actions = self.game_ui.draw(&mut self.ui, &view, &mut self.images);
        self.game_ui.draw_toasts(&mut self.ui);
        self.last = self.ui.end_frame();
        actions
    }

    pub fn idle(&mut self) -> Vec<GameAction> {
        self.frame(UiInput::default())
    }

    /// Clicks a widget drawn last frame (press, then release); returns the release frame's actions.
    pub fn click(&mut self, id: WidgetId) -> Vec<GameAction> {
        let rect = self.ui.last_rect(id).unwrap_or_else(|| panic!("widget {id:?} was not drawn"));
        let at = Some(((rect.x + rect.width / 2.0) * self.scale, (rect.y + rect.height / 2.0) * self.scale));
        let mut actions =
            self.frame(UiInput { pointer: at, pointer_pressed: true, pointer_down: true, ..UiInput::default() });
        actions.extend(self.frame(UiInput { pointer: at, pointer_released: true, ..UiInput::default() }));
        actions
    }

    /// Scrolls (the wheel over the screen center) until `id` is on screen.
    pub fn scroll_to(&mut self, id: WidgetId) {
        let center = Some((self.size.0 / 2.0, self.size.1 / 2.0));
        for direction in [10.0, -1.0] {
            for _ in 0..40 {
                if self.ui.last_rect(id).is_some_and(|rect| rect.height > 8.0) {
                    return;
                }
                self.frame(UiInput { pointer: center, wheel: direction, ..UiInput::default() });
            }
        }
        panic!("widget {id:?} never scrolled into view");
    }

    pub fn nav(&mut self, actions: &[NavAction]) -> Vec<GameAction> {
        self.frame(UiInput { nav: actions.to_vec(), ..UiInput::default() })
    }

    pub fn keys(&mut self, keys: &[&str]) -> Vec<GameAction> {
        self.frame(UiInput { keys_pressed: keys.iter().map(|key| (*key).to_owned()).collect(), ..UiInput::default() })
    }

    /// The text of every text command drawn last frame.
    pub fn texts(&self) -> Vec<String> {
        self.last
            .commands
            .iter()
            .filter_map(|command| match command {
                farm_render::DrawCmd::Text { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }
}
