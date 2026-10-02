//! Every sentence the engine shows the player comes from the message catalog
//! (`farm_sim::messages`), so players can translate it. This lint fails when engine code writes
//! a message as a string literal or a `format!` instead: `Effect::message` is only for text a
//! creator wrote (event and plugin messages), which is passed through as a variable.

use std::fs;
use std::path::Path;

fn sources(dir: &Path, out: &mut Vec<(String, String)>) {
    for entry in fs::read_dir(dir).expect("src") {
        let path = entry.expect("entry").path();
        if path.is_dir() {
            sources(&path, out);
        } else if path.extension().is_some_and(|ext| ext == "rs") {
            let text = fs::read_to_string(&path).expect("read");
            // Unit tests may spell out the English they expect.
            let code = text.split("#[cfg(test)]").next().unwrap_or_default().to_owned();
            out.push((path.display().to_string(), code));
        }
    }
}

/// The argument list of each call that starts at `needle`, whitespace collapsed.
fn calls(code: &str, needle: &str) -> Vec<String> {
    let mut found = Vec::new();
    for (start, _) in code.match_indices(needle) {
        let rest = &code[start + needle.len()..];
        let mut depth = 1;
        let mut end = rest.len();
        for (index, ch) in rest.char_indices() {
            match ch {
                '(' => depth += 1,
                ')' => {
                    depth -= 1;
                    if depth == 0 {
                        end = index;
                        break;
                    }
                }
                _ => {}
            }
        }
        found.push(rest[..end].split_whitespace().collect::<Vec<_>>().join(" "));
    }
    found
}

#[test]
fn engine_messages_come_from_the_catalog() {
    let mut files = Vec::new();
    sources(&Path::new(env!("CARGO_MANIFEST_DIR")).join("src"), &mut files);
    let mut offenders = Vec::new();
    for (path, code) in &files {
        if path.ends_with("effects.rs") || path.ends_with("messages.rs") {
            continue;
        }
        for args in calls(code, "Effect::message(") {
            let text = args.split_once(',').map_or("", |(_, text)| text.trim());
            if text.starts_with('"') || text.starts_with("format!") || text.contains(".to_owned()") {
                offenders.push(format!("{path}: Effect::message({args})"));
            }
        }
        if code.contains("Effect::Message {") && code.contains("text: format!") {
            offenders.push(format!("{path}: builds Effect::Message from format!"));
        }
    }
    assert!(
        offenders.is_empty(),
        "engine text bypasses the message catalog (add a template to farm_sim::messages and use Effect::say):\n{}",
        offenders.join("\n")
    );
}
