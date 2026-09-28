#!/usr/bin/env bash
# Rebuilds player-fixture.exe: a tiny Windows program with the same placeholder resources as
# the real player template (crates/farm-player/build.rs), but smaller icon slots so the
# fixture stays small. The export tests patch it and read the result back.
#
# Needs mingw-w64 (x86_64-w64-mingw32-gcc, -windres) and python3:
#   apt-get install gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Same layout as build.rs: a 1x1 PNG padded with zeros per slot, and 200-character strings.
python3 - "$work" <<'PY'
import struct, sys, zlib
work = sys.argv[1]
def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
pixel = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 6, 0, 0, 0))
         + chunk(b'IDAT', zlib.compress(b'\x00' * 5, 9)) + chunk(b'IEND', b''))
slots = [(16, 2048), (32, 4096), (48, 6144), (256, 24576)]
header = struct.pack('<HHH', 0, 1, len(slots))
offset = 6 + 16 * len(slots)
entries, images = b'', b''
for size, capacity in slots:
    edge = 0 if size >= 256 else size
    entries += struct.pack('<BBBBHHII', edge, edge, 0, 0, 1, 32, capacity, offset)
    images += pixel + b'\x00' * (capacity - len(pixel))
    offset += capacity
open(f'{work}/fixture.ico', 'wb').write(header + entries + images)
filler = 'farm-player template' + '_' * 180
keys = ['Comments', 'CompanyName', 'FileDescription', 'FileVersion', 'InternalName',
        'LegalCopyright', 'OriginalFilename', 'ProductName', 'ProductVersion']
values = ''.join(f'            VALUE "{k}", "{filler}"\n' for k in keys)
open(f'{work}/fixture.rc', 'w').write(f'''LANGUAGE 0x09, 0x01
1 ICON "fixture.ico"
1 VERSIONINFO
FILEVERSION 0,0,0,0
PRODUCTVERSION 0,0,0,0
FILEFLAGSMASK 0x3F
FILEFLAGS 0x0
FILEOS 0x40004
FILETYPE 0x1
FILESUBTYPE 0x0
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904B0"
        BEGIN
{values}        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x409, 1200
    END
END
''')
open(f'{work}/main.c', 'w').write('int main(void) { return 0; }\n')
PY

x86_64-w64-mingw32-windres --input "$work/fixture.rc" --include-dir "$work" --output "$work/fixture.o" --output-format=coff
x86_64-w64-mingw32-gcc -Os -s -o "$here/player-fixture.exe" "$work/main.c" "$work/fixture.o"
ls -l "$here/player-fixture.exe"
