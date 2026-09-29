//! The ordering buffer between plugin dispatch and the simulation (port of
//! `PluginMutationQueue` / TS `PluginMutationQueue`).

use crate::PluginDispatchResult;
use farm_sim::schema::PluginMutation;
use farm_sim::Command;

/// A drained mutation with the plugin that produced it (TS `QueuedPluginMutation`).
#[derive(Debug, Clone, PartialEq)]
pub struct QueuedPluginMutation {
    /// The plugin that returned the mutation (`packId:pluginId`).
    pub plugin_id: String,
    /// The validated mutation.
    pub mutation: PluginMutation,
}

impl QueuedPluginMutation {
    /// The command that replays this mutation through the engine.
    pub fn into_command(self) -> Command {
        Command::PluginMutation { plugin_id: self.plugin_id, mutation: self.mutation }
    }
}

/// Dispatch happens while the host handles a step's hook events. Applying the results the
/// moment they arrive would put them at arbitrary positions in the command stream. Hosts
/// enqueue results as they arrive and drain the queue at ONE fixed point in the frame (before
/// the frame's commands and ticks), so mutations enter the command log at a well-defined
/// position, in arrival order. Replaying that command log is then fully deterministic: the
/// log, not live plugin behavior, is the replay artifact.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PluginMutationQueue {
    queue: Vec<QueuedPluginMutation>,
}

impl PluginMutationQueue {
    /// An empty queue.
    pub fn new() -> Self {
        Self::default()
    }

    /// Queue every mutation of `results`, in order.
    pub fn enqueue<'a>(&mut self, results: impl IntoIterator<Item = &'a PluginDispatchResult>) {
        for result in results {
            for mutation in &result.mutations {
                self.queue
                    .push(QueuedPluginMutation { plugin_id: result.plugin_id.clone(), mutation: mutation.clone() });
            }
        }
    }

    /// Remove and return everything queued, in arrival order.
    pub fn drain(&mut self) -> Vec<QueuedPluginMutation> {
        std::mem::take(&mut self.queue)
    }

    /// Number of queued mutations.
    pub fn len(&self) -> usize {
        self.queue.len()
    }

    /// Whether nothing is queued.
    pub fn is_empty(&self) -> bool {
        self.queue.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn result(plugin_id: &str, texts: &[&str]) -> PluginDispatchResult {
        PluginDispatchResult {
            plugin_id: plugin_id.to_owned(),
            mutations: texts.iter().map(|text| PluginMutation::Message { text: (*text).to_owned() }).collect(),
            errors: Vec::new(),
        }
    }

    /// plugins.test.ts: "drains everything in arrival order and empties itself".
    #[test]
    fn drains_everything_in_arrival_order_and_empties_itself() {
        let mut queue = PluginMutationQueue::new();
        queue.enqueue(&[result("a", &["one"]), result("b", &["two", "three"])]);
        queue.enqueue(&[result("a", &["four"])]);
        assert_eq!(queue.len(), 4);

        let drained: Vec<(String, String)> = queue
            .drain()
            .into_iter()
            .map(|queued| match queued.mutation {
                PluginMutation::Message { text } => (queued.plugin_id, text),
                other => panic!("unexpected {other:?}"),
            })
            .collect();
        let expected = [("a", "one"), ("b", "two"), ("b", "three"), ("a", "four")];
        assert_eq!(drained, expected.map(|(id, text)| (id.to_owned(), text.to_owned())));
        assert_eq!(queue.len(), 0);
        assert!(queue.is_empty());
        assert!(queue.drain().is_empty());
    }

    #[test]
    fn drained_mutations_become_plugin_mutation_commands() {
        let queued = QueuedPluginMutation {
            plugin_id: "gifts:daily".to_owned(),
            mutation: PluginMutation::GiveMoney { amount: 20 },
        };
        let command = queued.into_command();
        assert_eq!(
            farm_sim::stable_json::stringify(&command),
            r#"{"mutation":{"amount":20,"type":"giveMoney"},"pluginId":"gifts:daily","type":"pluginMutation"}"#
        );
    }
}
