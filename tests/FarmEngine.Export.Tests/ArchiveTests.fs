module FarmEngine.Export.Tests.ArchiveTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open Xunit
open FarmEngine.Export

let private files =
    [ { Path = "licenses/THIRD-PARTY.txt"; Data = Text.Encoding.UTF8.GetBytes "MIT\n"; Executable = false }
      { Path = "Game"; Data = Array.init 5000 (fun i -> byte (i % 7)); Executable = true }
      { Path = "game.cart"; Data = Array.init 300 byte; Executable = false }
      // Incompressible data is stored rather than deflated.
      { Path = "noise.bin"; Data = (let r = Random(3) in Array.init 4096 (fun _ -> byte (r.Next 256))); Executable = false } ]

[<Fact>]
let ``zip entries are sorted, readable and carry a fixed timestamp`` () =
    let bytes = Archives.zip "Game" files
    use archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
    let names = [ for entry in archive.Entries -> entry.FullName ]
    Assert.Equal<string list>([ "Game/Game"; "Game/game.cart"; "Game/licenses/THIRD-PARTY.txt"; "Game/noise.bin" ], names)
    for entry in archive.Entries do
        let original = files |> List.find (fun f -> "Game/" + f.Path = entry.FullName)
        use stream = entry.Open()
        use copy = new MemoryStream()
        stream.CopyTo copy
        Assert.Equal<byte[]>(original.Data, copy.ToArray())
        Assert.Equal(DateTime(1980, 1, 1), entry.LastWriteTime.DateTime)
        Assert.Equal(0, entry.ExternalAttributes)

[<Fact>]
let ``archives are byte-identical for the same files in any order`` () =
    Assert.Equal<byte[]>(Archives.zip "Game" files, Archives.zip "Game" (List.rev files))
    Assert.Equal<byte[]>(Archives.tarGz "Game" files, Archives.tarGz "Game" (List.rev files))

[<Fact>]
let ``tar has folders, modes, root owner and the execute bit on the player`` () =
    let tar = Archives.tar "Game" files
    use reader = new TarReader(new MemoryStream(tar))
    let entries =
        [ let mutable entry = reader.GetNextEntry()
          while not (isNull entry) do
              yield entry.Name, entry.EntryType, entry.Mode, entry.ModificationTime, entry.Uid, entry.Gid
              entry <- reader.GetNextEntry() ]
    let names = entries |> List.map (fun (name, _, _, _, _, _) -> name)
    Assert.Equal<string list>(
        [ "Game/"; "Game/Game"; "Game/game.cart"; "Game/licenses/"; "Game/licenses/THIRD-PARTY.txt"; "Game/noise.bin" ],
        names
    )
    let execute = UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute
    for name, kind, mode, time, uid, gid in entries do
        Assert.Equal(Archives.fixedTime, time)
        Assert.Equal(0, uid)
        Assert.Equal(0, gid)
        let executable = kind = TarEntryType.Directory || name = "Game/Game"
        Assert.Equal(executable, mode &&& execute = execute)
        Assert.True(mode.HasFlag UnixFileMode.UserRead && mode.HasFlag UnixFileMode.OtherRead)
        Assert.False(mode.HasFlag UnixFileMode.OtherWrite)

[<Fact>]
let ``gzip wraps the tar with a fixed header and a valid trailer`` () =
    let tar = Archives.tar "Game" files
    let gz = Archives.gzip tar
    Assert.Equal<byte[]>([| 0x1fuy; 0x8buy; 8uy; 0uy; 0uy; 0uy; 0uy; 0uy; 2uy; 0xffuy |], gz[0..9])
    use decompressed = new MemoryStream()
    (use stream = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)
     stream.CopyTo decompressed)
    Assert.Equal<byte[]>(tar, decompressed.ToArray())
