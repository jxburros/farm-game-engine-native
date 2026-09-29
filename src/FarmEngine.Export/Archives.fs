namespace FarmEngine.Export

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression

/// One file of an exported game. `Path` is relative to the game folder and uses `/`;
/// `Executable` marks the player binary (the Unix execute bit in `.tar.gz` archives).
type PackageFile =
    { Path: string
      Data: byte[]
      Executable: bool }

/// Reproducible archives: entries sorted by path, one fixed timestamp, no owner names, and
/// no field that depends on the machine or the clock. The same files give the same bytes.
module Archives =
    /// 1980-01-01 00:00:00 UTC, the earliest time a ZIP entry can hold. Tar entries use it too.
    let fixedTime = DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

    let private deflate (data: byte[]) : byte[] =
        use output = new MemoryStream()
        let compress () =
            use deflater = new DeflateStream(output, CompressionLevel.SmallestSize, true)
            deflater.Write(data, 0, data.Length)
        compress ()
        output.ToArray()

    let private sorted (files: PackageFile list) = files |> List.sortWith (fun a b -> String.CompareOrdinal(a.Path, b.Path))

    /// `root/path`, or just `path` when there is no root folder (`zipFlat`).
    let private entryName (root: string) (path: string) = if root = "" then path else root + "/" + path

    /// A ZIP archive of `files` under the folder `root`. Entries are deflated unless that
    /// makes them larger. DOS time 1980-01-01, "made by" MS-DOS, no extra fields.
    let zip (root: string) (files: PackageFile list) : byte[] =
        let files = sorted files
        if files.Length > 0xFFFF then invalidOp "Too many files for a ZIP archive."
        use output = new Binary.Writer()
        let central = ResizeArray<byte[]>()
        let dosTime, dosDate = 0, ((1980 - 1980) <<< 9) ||| (1 <<< 5) ||| 1
        let utf8Names = 0x0800
        for file in files do
            let name = Text.Encoding.UTF8.GetBytes(entryName root file.Path)
            let crc = Binary.crc32 file.Data
            let deflated = deflate file.Data
            let stored = deflated.Length >= file.Data.Length
            let method, body = if stored then 0, file.Data else 8, deflated
            let offset = output.Position
            if int64 offset + int64 body.Length > int64 UInt32.MaxValue then invalidOp "The game is too large for a ZIP archive (4 GB)."
            output.U32 0x04034b50u
            output.U16 20
            output.U16 utf8Names
            output.U16 method
            output.U16 dosTime
            output.U16 dosDate
            output.U32 crc
            output.U32(uint32 body.Length)
            output.U32(uint32 file.Data.Length)
            output.U16 name.Length
            output.U16 0
            output.Bytes name
            output.Bytes body
            use header = new Binary.Writer()
            header.U32 0x02014b50u
            header.U16 20
            header.U16 20
            header.U16 utf8Names
            header.U16 method
            header.U16 dosTime
            header.U16 dosDate
            header.U32 crc
            header.U32(uint32 body.Length)
            header.U32(uint32 file.Data.Length)
            header.U16 name.Length
            header.U16 0
            header.U16 0
            header.U16 0
            header.U16 0
            header.U32 0u
            header.U32(uint32 offset)
            header.Bytes name
            central.Add(header.ToArray())
        let directoryOffset = output.Position
        for header in central do
            output.Bytes header
        let directorySize = output.Position - directoryOffset
        output.U32 0x06054b50u
        output.U16 0
        output.U16 0
        output.U16 files.Length
        output.U16 files.Length
        output.U32(uint32 directorySize)
        output.U32(uint32 directoryOffset)
        output.U16 0
        output.ToArray()

    /// The folders a set of files needs, as tar directory names (`root/`, `root/licenses/`).
    let private directories (root: string) (files: PackageFile list) =
        let folders = Collections.Generic.SortedSet<string>(StringComparer.Ordinal)
        folders.Add(root + "/") |> ignore
        for file in files do
            let parts = file.Path.Split('/')
            for depth in 1 .. parts.Length - 1 do
                folders.Add(entryName root (String.Join("/", parts[0 .. depth - 1])) + "/") |> ignore
        List.ofSeq folders

    let private executableMode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead

    /// 0755 for executables and folders, 0644 for everything else.
    let modeOf (file: PackageFile) = if file.Executable then executableMode else fileMode

    /// An uncompressed POSIX (ustar) tar of `files` under `root`, with folder entries.
    let tar (root: string) (files: PackageFile list) : byte[] =
        use output = new MemoryStream()
        let write () =
            use writer = new TarWriter(output, TarEntryFormat.Ustar, true)
            let entries =
                [ for folder in directories root files -> folder, None
                  for file in files -> entryName root file.Path, Some file ]
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            for name, file in entries do
                let entry =
                    match file with
                    | None -> UstarTarEntry(TarEntryType.Directory, name, Mode = executableMode)
                    | Some file -> UstarTarEntry(TarEntryType.RegularFile, name, Mode = modeOf file, DataStream = new MemoryStream(file.Data, false))
                entry.ModificationTime <- fixedTime
                entry.Uid <- 0
                entry.Gid <- 0
                entry.UserName <- ""
                entry.GroupName <- ""
                writer.WriteEntry entry
                match entry.DataStream with
                | null -> ()
                | stream -> stream.Dispose()
        write ()
        output.ToArray()

    /// gzip with a fixed header (no name, time 0, OS "unknown"), so the host doesn't show.
    let gzip (data: byte[]) : byte[] =
        use output = new Binary.Writer()
        output.Bytes [| 0x1fuy; 0x8buy; 8uy; 0uy; 0uy; 0uy; 0uy; 0uy; 2uy; 0xffuy |]
        output.Bytes(deflate data)
        output.U32(Binary.crc32 data)
        output.U32(uint32 (int64 data.Length &&& 0xFFFFFFFFL))
        output.ToArray()

    /// `tar` then `gzip`.
    let tarGz (root: string) (files: PackageFile list) : byte[] = gzip (tar root files)

    /// A ZIP archive of `files` at its root (web demos: itch.io serves `index.html` from the
    /// root of the upload).
    let zipFlat (files: PackageFile list) : byte[] = zip "" files
