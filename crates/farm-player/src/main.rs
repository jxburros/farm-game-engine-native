use farm_player::script::{run_frames, InputScript};
use farm_player::{run_cartridge, Player, PlayerOptions, ReplayFile};
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
    screenshot: Option<PathBuf>,
    size: Option<(u32, u32)>,
    frames: Option<u32>,
    script: Option<PathBuf>,
    exit_after_frames: Option<u64>,
    fullscreen: Option<bool>,
}

fn usage() -> &'static str {
    "Usage:\n\
     \x20 farm-player [--cart game.cart] [--fullscreen | --windowed] [--exit-after-frames N]\n\
     \x20     Play the game in a window (game.cart beside the executable by default).\n\
     \x20 farm-player --headless [--cart game.cart] [--replay replay.json] [--load save] [--save save]\n\
     \x20     Replay without a window and print a JSON report. A save path ending in .json writes a\n\
     \x20     JSON save; any other name writes a binary save. --load reads either.\n\
     \x20 farm-player --screenshot out.png [--cart game.cart] [--size 1280x800] [--frames 60] [--script inputs.json]\n\
     \x20     Render frames without a window (60 per second of game time) and write the last one."
}

fn parse_size(text: &str) -> Result<(u32, u32), String> {
    let (width, height) =
        text.split_once(['x', 'X']).ok_or_else(|| format!("--size wants WIDTHxHEIGHT, not {text}"))?;
    let parse = |value: &str| value.trim().parse::<u32>().ok().filter(|v| (16..=8192).contains(v));
    match (parse(width), parse(height)) {
        (Some(width), Some(height)) => Ok((width, height)),
        _ => Err(format!("--size wants WIDTHxHEIGHT between 16 and 8192, not {text}")),
    }
}

fn parse_args() -> Result<Options, String> {
    let mut options = Options::default();
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        let mut value = |name: &str| args.next().ok_or_else(|| format!("{name} needs a value.\n{}", usage()));
        match arg.as_str() {
            "--headless" => options.headless = true,
            "--cart" => options.cart = Some(PathBuf::from(value("--cart")?)),
            "--replay" => options.replay = Some(PathBuf::from(value("--replay")?)),
            "--load" => options.load = Some(PathBuf::from(value("--load")?)),
            "--save" => options.save = Some(PathBuf::from(value("--save")?)),
            "--screenshot" => options.screenshot = Some(PathBuf::from(value("--screenshot")?)),
            "--script" => options.script = Some(PathBuf::from(value("--script")?)),
            "--size" => options.size = Some(parse_size(&value("--size")?)?),
            "--frames" => {
                let text = value("--frames")?;
                options.frames = Some(text.parse().map_err(|_| format!("--frames wants a number, not {text}"))?);
            }
            "--exit-after-frames" => {
                let text = value("--exit-after-frames")?;
                options.exit_after_frames =
                    Some(text.parse().map_err(|_| format!("--exit-after-frames wants a number, not {text}"))?);
            }
            "--fullscreen" => options.fullscreen = Some(true),
            "--windowed" => options.fullscreen = Some(false),
            "--help" | "-h" => return Err(usage().to_owned()),
            _ => return Err(format!("Unknown option {arg}.\n{}", usage())),
        }
    }
    Ok(options)
}

fn cart_path(options: &Options) -> Result<PathBuf, String> {
    match &options.cart {
        Some(path) => Ok(path.clone()),
        None => Ok(env::current_exe()
            .map_err(|error| format!("Find player executable: {error}"))?
            .parent()
            .ok_or("Player executable has no folder.")?
            .join("game.cart")),
    }
}

fn read(path: &PathBuf) -> Result<Vec<u8>, String> {
    fs::read(path).map_err(|error| format!("Read {}: {error}", path.display()))
}

fn headless(options: Options) -> Result<(), String> {
    let cart = read(&cart_path(&options)?)?;
    let replay: ReplayFile = match &options.replay {
        Some(path) => {
            let text = fs::read_to_string(path).map_err(|error| format!("Read {}: {error}", path.display()))?;
            serde_json::from_str(&text).map_err(|error| format!("Replay {}: {error}", path.display()))?
        }
        None => ReplayFile::default(),
    };
    let incoming_save = match &options.load {
        Some(path) => Some(read(path)?),
        None => None,
    };
    let result = run_cartridge(&cart, &replay, incoming_save.as_deref())?;
    if let Some(path) = options.save {
        let json = path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("json"));
        let bytes = if json { result.save_json.into_bytes() } else { result.save_binary };
        fs::write(&path, bytes).map_err(|error| format!("Write {}: {error}", path.display()))?;
    }
    println!("{}", serde_json::to_string(&result.report).map_err(|error| error.to_string())?);
    Ok(())
}

/// Renders frames without a window (in-memory saves and settings, a fixed clock).
fn screenshot(options: Options, out: PathBuf) -> Result<(), String> {
    let cart = read(&cart_path(&options)?)?;
    let script = match &options.script {
        Some(path) => {
            InputScript::parse(&fs::read_to_string(path).map_err(|error| format!("Read {}: {error}", path.display()))?)?
        }
        None => InputScript::default(),
    };
    let (width, height) = options.size.unwrap_or((1280, 800));
    let mut player =
        Player::from_cartridge_bytes(&cart, PlayerOptions::standalone()).map_err(|error| error.to_string())?;
    let pixmap = run_frames(&mut player, options.frames.unwrap_or(60), width, height, &script)
        .map_err(|error| error.to_string())?;
    let png = farm_render::encode_png(&pixmap);
    fs::write(&out, png).map_err(|error| format!("Write {}: {error}", out.display()))?;
    println!("Wrote {} ({width}×{height})", out.display());
    Ok(())
}

#[cfg(feature = "desktop")]
fn windowed(options: Options) -> Result<(), String> {
    let desktop = farm_player::desktop::DesktopOptions {
        exit_after_frames: options.exit_after_frames,
        fullscreen: options.fullscreen,
    };
    // An exported game has no console: a missing cartridge is shown in a window too.
    let cart = match cart_path(&options).and_then(|path| read(&path)) {
        Ok(cart) => cart,
        Err(error) => {
            if desktop.exit_after_frames.is_none() {
                farm_player::desktop::show_error(&format!("The game could not be loaded: {error}"), desktop);
            }
            return Err(error);
        }
    };
    farm_player::desktop::run(&cart, desktop)
}

#[cfg(not(feature = "desktop"))]
fn windowed(_: Options) -> Result<(), String> {
    Err(format!("This player was built without the desktop feature; use --headless or --screenshot.\n{}", usage()))
}

fn run(options: Options) -> Result<(), String> {
    if options.headless {
        return headless(options);
    }
    if let Some(out) = options.screenshot.clone() {
        return screenshot(options, out);
    }
    windowed(options)
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
