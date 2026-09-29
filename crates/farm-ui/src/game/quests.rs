//! The quest log (web QuestTracker, C# `PlayOverlays.Quests`): active and completed quests with
//! their objectives, progress bars and rewards.

use super::{item_name, GameAction, GameView};
use crate::format::{money, num};
use crate::i18n::Lang;
use crate::icons::Icon;
use crate::layout::Align;
use crate::theme::fade;
use crate::ui::{Ui, WidgetId};
use crate::widgets::ModalSpec;
use farm_render::{FontId, Rect, TextAlign};
use farm_sim::schema::{quest_statuses, Quest, QuestProgress};

struct Objective {
    text: String,
    done: bool,
    amount: u32,
    target: u32,
}

fn objectives(quest: &Quest, progress: Option<&QuestProgress>, completed: bool) -> Vec<Objective> {
    quest
        .objectives
        .iter()
        .map(|objective| {
            let entry = progress.and_then(|p| p.objectives.as_ref()).and_then(|map| map.get(&objective.id));
            let target = [objective.target_item_quantity, objective.target_crop_quantity]
                .into_iter()
                .flatten()
                .find(|value| *value != 0)
                .unwrap_or(1);
            Objective {
                text: objective.description.clone(),
                done: completed || entry.is_some_and(|entry| entry.completed),
                amount: entry.map_or(objective.progress, |entry| entry.progress),
                target,
            }
        })
        .collect()
}

fn rewards_line(view: &GameView<'_>, quest: &Quest, completed: bool, lang: Lang) -> Option<String> {
    let rewards = &quest.rewards;
    let mut parts = Vec::new();
    if let Some(amount) = rewards.money.filter(|amount| *amount > 0) {
        parts.push(money(amount));
    }
    for reward in rewards.items.iter().flatten() {
        parts.push(format!("{}\u{00d7} {}", num(reward.quantity), item_name(view.content, &reward.item_id)));
    }
    let key = if completed { "quests.rewardsClaimed" } else { "quests.rewards" };
    (!parts.is_empty()).then(|| lang.format(key, &[&parts.join(" \u{00b7} ")]))
}

/// Draws one quest card from `top`; returns its height.
fn card(ui: &mut Ui, view: &GameView<'_>, area: Rect, top: f32, quest: &Quest, completed: bool) -> f32 {
    let colors = ui.theme().colors;
    let progress = view.state.quests.get(&quest.id);
    let inner_x = area.x + 12.0;
    let width = area.width - 24.0;
    let text = if completed { colors.muted } else { colors.text };

    // Measure first: the row background goes under everything.
    let title_height = ui.line_height(16.5);
    let description_height = if quest.description.is_empty() {
        0.0
    } else {
        ui.paragraph_height(&quest.description, 12.5, FontId::Regular, width) + 4.0
    };
    let objectives = objectives(quest, progress, completed);
    let count_width = 64.0;
    let objective_width = width - 22.0 - count_width;
    let objective_heights: Vec<f32> = objectives
        .iter()
        .map(|objective| {
            ui.paragraph_height(&objective.text, 12.5, FontId::Regular, objective_width)
                + if objective.done { 4.0 } else { 12.0 }
        })
        .collect();
    let rewards = rewards_line(view, quest, completed, ui.lang());
    let rewards_height =
        rewards.as_ref().map_or(0.0, |line| ui.paragraph_height(line, 12.5, FontId::Regular, width) + 4.0);
    let height =
        10.0 + title_height + description_height + 6.0 + objective_heights.iter().sum::<f32>() + rewards_height + 10.0;
    let rect = Rect::new(area.x, top, area.width, height);
    ui.row_background(rect);

    let mut y = top + 10.0;
    let title_rect = Rect::new(inner_x, y, width, title_height);
    let title_width = ui.label(title_rect, &quest.name, 16.5, FontId::Bold, text, Align::Start);
    if completed {
        let cy = title_rect.y + title_rect.height / 2.0;
        ui.chip(inner_x + title_width + 8.0, cy, "\u{2713} Complete", 11.5, fade(colors.primary, 0.18), colors.primary);
    }
    y += title_height;
    if !quest.description.is_empty() {
        y += ui.paragraph(inner_x, y, width, &quest.description, 12.5, FontId::Regular, colors.muted, TextAlign::Left)
            + 4.0;
    }
    y += 6.0;
    for (objective, objective_height) in objectives.iter().zip(&objective_heights) {
        let line = ui.line_height(12.5);
        let mark_color = if objective.done { colors.primary } else { colors.muted };
        let mark = if objective.done { "\u{25CF}" } else { "\u{25CB}" };
        ui.label(Rect::new(inner_x, y, 16.0, line), mark, 12.5, FontId::Bold, mark_color, Align::Start);
        let text_x = inner_x + 22.0;
        let color = if objective.done { colors.muted } else { colors.text };
        let used =
            ui.paragraph(text_x, y, objective_width, &objective.text, 12.5, FontId::Regular, color, TextAlign::Left);
        if objective.done {
            // Struck through, like the editor.
            let lines = ui.wrap(&objective.text, 12.5, FontId::Regular, objective_width);
            for (index, line_text) in lines.iter().enumerate() {
                let w = ui.measure(line_text, 12.5, FontId::Regular);
                let ly = y + index as f32 * line + line / 2.0 + 1.0;
                ui.list_mut().line((text_x, ly), (text_x + w, ly), colors.muted, 1.0);
            }
        }
        let count = format!("{}/{}", num(objective.amount.min(objective.target)), num(objective.target));
        ui.label(
            Rect::new(inner_x + width - count_width, y, count_width, line),
            &count,
            12.5,
            FontId::Regular,
            colors.muted,
            Align::End,
        );
        if !objective.done {
            let ratio = (f64::from(objective.amount) / f64::from(objective.target)).clamp(0.0, 1.0) as f32;
            ui.progress(Rect::new(text_x, y + used + 3.0, width - 22.0, 6.0), ratio, colors.primary);
        }
        y += objective_height;
    }
    if let Some(line) = rewards {
        ui.paragraph(inner_x, y + 4.0, width, &line, 12.5, FontId::Regular, colors.muted, TextAlign::Left);
    }
    height
}

pub(crate) fn draw(ui: &mut Ui, view: &GameView<'_>, actions: &mut Vec<GameAction>) {
    let status = |quest: &Quest| view.state.quests.get(&quest.id).map(|progress| progress.status.as_str());
    let active: Vec<&Quest> =
        view.content.quests.iter().filter(|q| status(q) == Some(quest_statuses::ACTIVE)).collect();
    let completed: Vec<&Quest> =
        view.content.quests.iter().filter(|q| status(q) == Some(quest_statuses::COMPLETED)).collect();
    let lang = ui.lang();
    let subtitle = lang.format("quests.subtitle", &[&active.len(), &completed.len()]);
    let modal = ui.begin_modal(ModalSpec {
        id: WidgetId::new("quests"),
        icon: Icon::Star,
        title: lang.tr("quests.title"),
        subtitle: Some(&subtitle),
        width: 620.0,
        max_height: 640.0,
        footer: 0.0,
        backdrop: true,
    });
    if modal.close {
        actions.push(GameAction::ClosePanel);
    }
    let area = modal.body;
    let mut y = modal.top;
    if active.is_empty() && completed.is_empty() {
        y += ui.empty_state(area, y, Icon::Star, lang.tr("quests.empty"), lang.tr("quests.emptyDetail"));
    }
    if !active.is_empty() {
        y += ui.section(area, y, lang.tr("quests.active"), None);
        for quest in active {
            y += card(ui, view, area, y, quest, false) + 10.0;
        }
    }
    if !completed.is_empty() {
        y += ui.section(area, y, lang.tr("quests.completed"), None);
        for quest in completed {
            y += card(ui, view, area, y, quest, true) + 10.0;
        }
    }
    ui.end_modal(y);
}
