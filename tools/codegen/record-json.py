#!/usr/bin/env python3
"""Regenerates the codec and JSON-key tables in src/FarmEngine.Authoring.Net/RecordJson.fs from
the decoders and encoders in src/FarmEngine.Authoring/SchemaJson.fs. Run after SchemaJson.fs
changes: python3 tools/codegen/record-json.py"""
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[2]
schema_json = (root / "src/FarmEngine.Authoring/SchemaJson.fs").read_text()
target = root / "src/FarmEngine.Authoring.Net/RecordJson.fs"

decoders = re.findall(r"(?:let rec|and|let) decode(\w+) \(path: Path\) \(json: Json\) : (\w+) =", schema_json)
encoders = set(re.findall(r"(?:let rec|and|let) encode(\w+) \(value: (\w+)\) : Json", schema_json))
types = [t for name, t in decoders if (name, t) in encoders]

keys = []
for chunk in re.split(r"\n    (?:let rec|and|let) ", schema_json):
    m = re.match(r"decode(\w+) \(path: Path\) \(json: Json\) : (\w+) =", chunk)
    if m:
        pairs = re.findall(r'\| "([^"]+)" -> v(\w+) <-', chunk)
        if pairs:
            keys.append((m.group(2), pairs))

lines = [
    "    static let codecs : Dictionary<Type, (obj -> Json) * (Path -> Json -> obj)> =",
    "        let table = Dictionary<Type, (obj -> Json) * (Path -> Json -> obj)>()",
    "        let add (encode: 'T -> Json) (decode: Path -> Json -> 'T) =",
    "            table.[typeof<'T>] <- ((fun (value: obj) -> encode (unbox<'T> value)), (fun path json -> box (decode path json) |> nonNull))",
]
lines += [f"        add SchemaJson.encode{t} SchemaJson.decode{t}" for t in types]
lines += [
    "        table",
    "",
    "    static let keys : Dictionary<Type, Dictionary<string, string>> =",
    "        let table = Dictionary<Type, Dictionary<string, string>>()",
    "        let add (t: Type) (pairs: (string * string) list) =",
    "            let fields = Dictionary<string, string>()",
    "            for field, key in pairs do fields.[field] <- key",
    "            table.[t] <- fields",
]
for t, pairs in keys:
    lines.append("        add typeof<%s> [ %s ]" % (t, "; ".join('"%s", "%s"' % (field, key) for key, field in pairs)))
lines.append("        table")

text = target.read_text()
begin = text.index("    // <generated")
begin = text.index("\n", begin) + 1
end = text.index("    // </generated>")
target.write_text(text[:begin] + "\n".join(lines) + "\n" + text[end:])
print(f"{len(types)} codecs, {len(keys)} key tables")
