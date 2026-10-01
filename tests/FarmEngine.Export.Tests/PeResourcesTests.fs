/// The resource patcher against a real PE: player-fixture.exe, built by mingw's gcc and windres
/// from the same placeholder layout as the player template (Fixtures/pe/build.sh).
module FarmEngine.Export.Tests.PeResourcesTests

open System
open Xunit
open FarmEngine.Export
open FarmEngine.Export.Tests.Support

let private template () = IO.File.ReadAllBytes(fixture "player-fixture.exe")

let private ok (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error e -> failwith e

let private icons () = Icons.render (makePng 256 256 false) |> ok

let private version () =
    VersionInfo.build (Exporter.versionResource (Exporter.identity (starter ())) "1.0.0-test")

[<Fact>]
let ``the fixture has the placeholder slots the template promises`` () =
    let image = PeResources.read (template ()) |> ok
    let slots = image.Resources |> List.map (fun r -> r.TypeId, r.NameId, r.Language, r.Size)
    Assert.Equal<(int * int * int * int) list>(
        [ 3, 1, 1033, 2048; 3, 2, 1033, 4096; 3, 3, 1033, 6144; 3, 4, 1033, 24576; 14, 1, 1033, 62; 16, 1, 1033, 4160 ],
        slots
    )
    for resource in image.Resources do
        Assert.True(resource.Capacity >= resource.Size)
    let group = PeResources.readIcons (template ()) |> ok
    Assert.Equal<int list>([ 16; 32; 48; 256 ], group |> List.map (fun i -> i.Width))
    // windres wrote the placeholder version block; our reader understands it.
    let placeholder = PeResources.readVersion (template ()) |> ok |> VersionInfo.parse |> ok
    Assert.Equal(9, placeholder.Strings.Length)
    Assert.All(placeholder.Strings, fun (_, value) -> Assert.StartsWith("farm-player template", value))

[<Fact>]
let ``patching writes icons, version and checksum that read back`` () =
    let original = template ()
    let rendered = icons ()
    let versionBytes = version ()
    let patched = PeResources.patch rendered versionBytes original |> ok
    Assert.Equal(original.Length, patched.Length)
    let image = PeResources.read patched |> ok
    // Icons: each slot now holds exactly the PNG, and the group describes it.
    let read = PeResources.readIcons patched |> ok
    for size, png in rendered do
        let icon = read |> List.find (fun i -> i.Width = size)
        Assert.Equal(size, icon.Height)
        Assert.Equal(32, icon.BitCount)
        Assert.Equal<byte[]>(png, icon.Data)
        Assert.Equal((size, size), pngSize icon.Data)
    // Version: parses back to the same block.
    Assert.Equal<byte[]>(versionBytes, PeResources.readVersion patched |> ok)
    let parsed = VersionInfo.parse versionBytes |> ok
    Assert.Contains(("ProductName", (starter ()).Name), parsed.Strings)
    // Checksum: the stored value is the recomputed one, and it changed with the data.
    let stored = BitConverter.ToUInt32(patched, image.ChecksumOffset)
    Assert.Equal(PeResources.checksum patched image.ChecksumOffset, stored)
    Assert.NotEqual(BitConverter.ToUInt32(original, image.ChecksumOffset), stored)
    // The console template became a GUI program (no console window behind the game).
    Assert.Equal(PeResources.SUBSYSTEM_CONSOLE, PeResources.subsystem original |> ok)
    Assert.Equal(PeResources.SUBSYSTEM_GUI, PeResources.subsystem patched |> ok)
    // Capacities survive: patching the result again works and gives the same bytes.
    Assert.Equal<byte[]>(patched, PeResources.patch rendered versionBytes patched |> ok)
    // Nothing outside the resource data changed.
    let resources = PeResources.read original |> ok
    let inside (offset: int) =
        resources.Resources |> List.exists (fun r -> offset >= r.DataOffset && offset < r.DataOffset + r.Capacity || offset >= r.EntryOffset + 4 && offset < r.EntryOffset + 8)
        || (offset >= image.ChecksumOffset && offset < image.ChecksumOffset + 4)
        || (offset >= image.SubsystemOffset && offset < image.SubsystemOffset + 2)
    for i in 0 .. original.Length - 1 do
        if original[i] <> patched[i] && not (inside i) then failwithf "byte %d changed outside the resources" i

[<Fact>]
let ``data larger than a slot is refused with a clear message`` () =
    let noisy = Icons.render (makePng 256 256 true) |> ok
    match PeResources.patch noisy (version ()) (template ()) with
    | Ok _ -> failwith "an incompressible 256×256 icon cannot fit a 24 KB slot"
    | Error e -> Assert.Matches(@"^The \d+×\d+ icon is \d+ bytes, but the player template reserves \d+\.$", e)
    let longTitle =
        let game = { Exporter.identity (starter ()) with Title = String('x', 3000) }
        VersionInfo.build (Exporter.versionResource game "1.0.0")
    match PeResources.patch (icons ()) longTitle (template ()) with
    | Ok _ -> failwith "a 3000-character title cannot fit the version slot"
    | Error e -> Assert.Contains("version information", e)

[<Fact>]
let ``a template without the slots or not a PE is refused`` () =
    let original = template ()
    let image = PeResources.read original |> ok
    // Point the resource data directory nowhere: the executable has no resources at all.
    let pe = BitConverter.ToInt32(original, 0x3C)
    let bare = Array.copy original
    Array.Clear(bare, pe + 24 + 112 + 16, 8)
    match PeResources.patch (icons ()) (version ()) bare with
    | Ok _ -> failwith "no resources"
    | Error e -> Assert.Contains("no icon slot", e)
    Assert.True(image.Resources.Length > 0)
    match PeResources.patch (icons ()) (version ()) (Text.Encoding.ASCII.GetBytes "#!/bin/sh\necho not windows\n") with
    | Ok _ -> failwith "not a PE"
    | Error e -> Assert.StartsWith("Not a usable Windows executable", e)
    match PeResources.read original[0 .. 1000] with
    | Ok _ -> failwith "truncated"
    | Error e -> Assert.StartsWith("Not a usable Windows executable", e)

[<Fact>]
let ``checksum matches the reference algorithm on the untouched fixture`` () =
    // mingw's linker leaves the checksum at 0; pefile/imagehlp would compute this value.
    let original = template ()
    let image = PeResources.read original |> ok
    let sum = PeResources.checksum original image.ChecksumOffset
    Assert.True(sum > uint32 original.Length)
    // Ignoring the stored field: writing any value there doesn't change the result.
    let changed = Array.copy original
    changed[image.ChecksumOffset] <- 0xAAuy
    Assert.Equal(sum, PeResources.checksum changed image.ChecksumOffset)

/// File offset of the fixture's data directories (PE32+).
let private directories (bytes: byte[]) = BitConverter.ToInt32(bytes, 0x3C) + 24 + 112

[<Fact>]
let ``a signed template loses its signature instead of keeping a broken one`` () =
    // The fixture with a stand-in certificate table appended, as signtool would put it.
    let original = template ()
    let certificate = Array.append [| 16uy; 0uy; 0uy; 0uy; 0uy; 2uy; 2uy; 0uy |] (Array.create 8 0xCCuy)
    let signed = Array.append original certificate
    let security = directories signed + 4 * 8
    BitConverter.GetBytes(uint32 original.Length).CopyTo(signed, security)
    BitConverter.GetBytes(uint32 certificate.Length).CopyTo(signed, security + 4)
    let image = PeResources.read signed |> ok
    Assert.Equal(certificate.Length, image.SignatureSize)
    let patched = PeResources.patch (icons ()) (version ()) signed |> ok
    // The certificate table is cut off and the Security directory cleared.
    Assert.Equal(original.Length, patched.Length)
    Assert.Equal(0UL, BitConverter.ToUInt64(patched, security))
    let result = PeResources.read patched |> ok
    Assert.Equal(0, result.SignatureSize)
    Assert.Equal(PeResources.checksum patched result.ChecksumOffset, BitConverter.ToUInt32(patched, result.ChecksumOffset))
    // Same bytes as patching the unsigned template.
    Assert.Equal<byte[]>(PeResources.patch (icons ()) (version ()) original |> ok, patched)

[<Fact>]
let ``resource data outside the resource section is refused`` () =
    let bytes = template ()
    let image = PeResources.read bytes |> ok
    // Point the version resource's data at the start of .text (RVA 0x1000): patching it would
    // clear code.
    let version = image.Resources |> List.find (fun r -> r.TypeId = PeResources.RT_VERSION)
    BitConverter.GetBytes(0x1000u).CopyTo(bytes, version.EntryOffset)
    match PeResources.read bytes with
    | Ok _ -> failwith "data in .text must be refused"
    | Error e -> Assert.Contains("outside the resource section", e)
    match PeResources.patch (icons ()) (VersionInfo.build (Exporter.versionResource (Exporter.identity (starter ())) "1.0.0")) bytes with
    | Ok _ -> failwith "data in .text must be refused"
    | Error e -> Assert.StartsWith("Not a usable Windows executable", e)

[<Fact>]
let ``a resource table used twice is refused`` () =
    let bytes = template ()
    let rsrc = 0x3800 // the fixture's .rsrc raw offset (Fixtures/pe/build.sh)
    let count = int (BitConverter.ToUInt16(bytes, rsrc + 12)) + int (BitConverter.ToUInt16(bytes, rsrc + 14))
    Assert.True(count >= 2)
    // The second type entry points at the first type's name table.
    Array.blit bytes (rsrc + 16 + 4) bytes (rsrc + 16 + 8 + 4) 4
    match PeResources.read bytes with
    | Ok _ -> failwith "a shared table must be refused"
    | Error e -> Assert.Contains("used twice", e)

[<Fact>]
let ``a truncated resource section never yields a slot past the end of the file`` () =
    let original = template ()
    for length in [ 0x3900; 0x5000; 0x8000; 0xA000; 0xC000; 0xD800 ] do
        let bytes = original[0 .. length - 1]
        match PeResources.read bytes with
        | Error e -> Assert.StartsWith("Not a usable Windows executable", e)
        | Ok image ->
            for r in image.Resources do
                Assert.True(r.DataOffset + r.Capacity <= bytes.Length, sprintf "slot past the end at length %d" length)
            // Patching never throws: it fits, or it says why not.
            PeResources.patch (icons ()) (version ()) bytes |> ignore
