//! Windows builds of the player carry placeholder icon and version resources. Export Game
//! overwrites them in place from .NET, without a resource compiler on the creator's machine
//! (docs/EXPORT.md "Windows icon and version info"). Other targets need nothing.
//!
//! The placeholders reserve room: each icon slot holds a 1×1 PNG followed by zeros, sized so
//! that any PNG of that size fits, and every version string is padded. Export writes the new
//! data into the same bytes and shrinks the resource entry to the real size.

use std::env;
use std::fmt::Write as _;
use std::fs;
use std::path::PathBuf;

/// Icon sizes Export writes, in the order of the icon group.
const ICON_SIZES: [u32; 4] = [16, 32, 48, 256];

/// Characters reserved for each version string.
const VERSION_FIELD_CHARS: usize = 200;

/// Version strings Export writes (StringFileInfo keys).
const VERSION_FIELDS: [&str; 9] = [
    "Comments",
    "CompanyName",
    "FileDescription",
    "FileVersion",
    "InternalName",
    "LegalCopyright",
    "OriginalFilename",
    "ProductName",
    "ProductVersion",
];

/// A valid 1×1 transparent RGBA PNG.
const PIXEL_PNG: [u8; 68] = [
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00,
    0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0b, 0x49,
    0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0x60, 0x00, 0x02, 0x00, 0x00, 0x05, 0x00, 0x01, 0xe9, 0xfa, 0xdc, 0xd8, 0x00,
    0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
];

/// Room for any 8-bit RGBA PNG of `size`×`size`: raw pixels, one filter byte per row, and
/// generous headroom for zlib and PNG chunk overhead.
fn icon_capacity(size: u32) -> usize {
    (size * size * 4 + size) as usize + 1024
}

/// An .ico whose images are the padded placeholder PNGs.
fn placeholder_ico() -> Vec<u8> {
    let count = ICON_SIZES.len();
    let mut ico = Vec::new();
    ico.extend_from_slice(&0u16.to_le_bytes());
    ico.extend_from_slice(&1u16.to_le_bytes());
    ico.extend_from_slice(&(count as u16).to_le_bytes());
    let mut offset = 6 + 16 * count;
    for size in ICON_SIZES {
        let dimension = if size >= 256 { 0 } else { size as u8 };
        let capacity = icon_capacity(size);
        ico.extend_from_slice(&[dimension, dimension, 0, 0]);
        ico.extend_from_slice(&1u16.to_le_bytes());
        ico.extend_from_slice(&32u16.to_le_bytes());
        ico.extend_from_slice(&(capacity as u32).to_le_bytes());
        ico.extend_from_slice(&(offset as u32).to_le_bytes());
        offset += capacity;
    }
    for size in ICON_SIZES {
        let mut image = PIXEL_PNG.to_vec();
        image.resize(icon_capacity(size), 0);
        ico.extend_from_slice(&image);
    }
    ico
}

/// The resource script: icon group 1 and a VERSIONINFO whose strings reserve space.
fn resource_script() -> String {
    let filler = "farm-player template".to_owned() + &"_".repeat(VERSION_FIELD_CHARS - 20);
    let mut values = String::new();
    for key in VERSION_FIELDS {
        let _ = writeln!(values, "            VALUE \"{key}\", \"{filler}\"");
    }
    format!(
        "LANGUAGE 0x09, 0x01\n\
         1 ICON \"farm-player.ico\"\n\
         1 VERSIONINFO\n\
         FILEVERSION 0,0,0,0\n\
         PRODUCTVERSION 0,0,0,0\n\
         FILEFLAGSMASK 0x3F\n\
         FILEFLAGS 0x0\n\
         FILEOS 0x40004\n\
         FILETYPE 0x1\n\
         FILESUBTYPE 0x0\n\
         BEGIN\n\
         \x20   BLOCK \"StringFileInfo\"\n\
         \x20   BEGIN\n\
         \x20       BLOCK \"040904B0\"\n\
         \x20       BEGIN\n\
         {values}\
         \x20       END\n\
         \x20   END\n\
         \x20   BLOCK \"VarFileInfo\"\n\
         \x20   BEGIN\n\
         \x20       VALUE \"Translation\", 0x409, 1200\n\
         \x20   END\n\
         END\n"
    )
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let out_dir = PathBuf::from(env::var("OUT_DIR").expect("cargo sets OUT_DIR"));
    fs::write(out_dir.join("farm-player.ico"), placeholder_ico()).expect("write placeholder icon");
    let script = out_dir.join("farm-player.rc");
    fs::write(&script, resource_script()).expect("write resource script");
    // Export needs these slots: a Windows player without them is not a usable template.
    if let Err(error) = embed_resource::compile(&script, embed_resource::NONE).manifest_required() {
        panic!(
            "farm-player needs a Windows resource compiler (rc.exe or windres) for its icon and version slots: {error}"
        );
    }
}
