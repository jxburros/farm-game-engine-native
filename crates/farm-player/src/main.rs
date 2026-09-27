use farm_player::{run_cartridge, ReplayFile};
use std::env;
use std::fs;
use std::path::PathBuf;

#[derive(Debug, Default)]
struct Options {
    cart: Option<PathBuf>,
    replay: Option<PathBuf>,
    load: Option<PathBuf>,
    save: Option<PathBuf>,
    headless: bool,
}

fn usage() -> &'static str {
    "Usage: farm-player --headless [--cart game.cart] [--replay replay.json] [--load save.json] [--save save.json]"
}

fn parse_args() -> Result<Options, String> {
    let mut options = Options::default();
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--headless" => options.headless = true,
            "--cart" | "--replay" | "--load" | "--save" => {
                let value = args.next().ok_or_else(|| format!("{arg} needs a path. {}", usage()))?;
                let path = PathBuf::from(value);
                match arg.as_str() {
                    "--cart" => options.cart = Some(path),
                    "--replay" => options.replay = Some(path),
                    "--load" => options.load = Some(path),
                    _ => options.save = Some(path),
                }
            }
            "--help" | "-h" => return Err(usage().to_owned()),
            _ => return Err(format!("Unknown option {arg}. {}", usage())),
        }
    }
    if !options.headless {
        return Err(format!("The graphical shell is not available yet. {}", usage()));
    }
    Ok(options)
}

fn run(options: Options) -> Result<(), String> {
    let cart_path = match options.cart {
        Some(path) => path,
        None => env::current_exe()
            .map_err(|error| format!("Find player executable: {error}"))?
            .parent()
            .ok_or("Player executable has no folder.")?
            .join("game.cart"),
    };
    let cart = fs::read(&cart_path).map_err(|error| format!("Read {}: {error}", cart_path.display()))?;
    let replay: ReplayFile = match options.replay {
        Some(path) => {
            let text = fs::read_to_string(&path).map_err(|error| format!("Read {}: {error}", path.display()))?;
            serde_json::from_str(&text).map_err(|error| format!("Replay {}: {error}", path.display()))?
        }
        None => ReplayFile::default(),
    };
    let incoming_save = match options.load {
        Some(path) => Some(fs::read_to_string(&path).map_err(|error| format!("Read {}: {error}", path.display()))?),
        None => None,
    };
    let result = run_cartridge(&cart, &replay, incoming_save.as_deref())?;
    if let Some(path) = options.save {
        fs::write(&path, result.save_json).map_err(|error| format!("Write {}: {error}", path.display()))?;
    }
    println!("{}", serde_json::to_string(&result.report).map_err(|error| error.to_string())?);
    Ok(())
}

fn main() {
    if matches!(env::args().nth(1).as_deref(), Some("--help" | "-h")) {
        println!("{}", usage());
        return;
    }
    let outcome = parse_args().and_then(run);
    if let Err(error) = outcome {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
