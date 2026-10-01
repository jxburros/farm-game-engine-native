namespace FarmEngine.Export

open System
open System.Collections.Generic

/// One leaf of a PE resource tree (`type/name/language` → data).
type PeResource =
    { TypeId: int
      /// Numeric name, or -1 for a string name.
      NameId: int
      Language: int
      /// File offset of the IMAGE_RESOURCE_DATA_ENTRY that points at the data.
      EntryOffset: int
      /// File offset of the data.
      DataOffset: int
      Size: int
      /// Bytes available at `DataOffset` before the next resource structure or the end of
      /// the section: how large a replacement can be.
      Capacity: int }

/// A parsed PE image: just what patching resources needs.
type PeImage =
    { ChecksumOffset: int
      /// File offset of the optional header's Subsystem field (2 = GUI, 3 = console).
      SubsystemOffset: int
      /// Size of the Authenticode signature table (0 when unsigned).
      SignatureSize: int
      Resources: PeResource list }

/// A group icon entry (GRPICONDIRENTRY) and the RT_ICON it names.
type PeIcon =
    { Width: int
      Height: int
      BitCount: int
      Id: int
      Data: byte[] }

/// Reads and patches the resources of a Windows executable in place, from .NET (docs/EXPORT.md
/// "Windows icon and version info"). The player template is built with placeholder
/// RT_GROUP_ICON/RT_ICON and RT_VERSION resources that reserve space (crates/farm-player/build.rs);
/// patching writes new data into those bytes, shrinks each data entry to the new size, updates the
/// icon group, marks a console program as a GUI program, and recomputes the PE checksum. Nothing
/// moves, so no section changes; the Subsystem word and the checksum are the only header fields
/// written.
module PeResources =
    let RT_ICON = 3
    let RT_GROUP_ICON = 14
    let RT_VERSION = 16
    /// `IMAGE_SUBSYSTEM_WINDOWS_GUI`: Windows starts the program without a console window.
    let SUBSYSTEM_GUI = 2
    /// `IMAGE_SUBSYSTEM_WINDOWS_CUI`: a console program (what Rust builds by default).
    let SUBSYSTEM_CONSOLE = 3

    type private Section =
        { VirtualAddress: int64
          VirtualSize: int64
          RawSize: int64
          RawOffset: int64 }

    let private toOffset (sections: Section list) (rva: int64) : int option =
        sections
        |> List.tryFind (fun s -> rva >= s.VirtualAddress && rva < s.VirtualAddress + s.RawSize)
        |> Option.map (fun s -> int (rva - s.VirtualAddress + s.RawOffset))

    exception private BadImage of string

    /// Parses the headers and the resource tree. Errors name what is wrong with the file.
    let read (bytes: byte[]) : Result<PeImage, string> =
        let fail message = raise (BadImage message)
        let parse () =
            if not (Binary.fits bytes 0 64) || bytes[0] <> byte 'M' || bytes[1] <> byte 'Z' then fail "no MZ header."
            let pe = Binary.i32 bytes 0x3C
            if not (Binary.fits bytes pe 24) || Binary.u32 bytes pe <> 0x00004550u then fail "no PE header."
            let sectionCount = Binary.u16 bytes (pe + 6)
            let optionalSize = Binary.u16 bytes (pe + 20)
            let optional = pe + 24
            if not (Binary.fits bytes optional optionalSize) || optionalSize < 96 then fail "optional header is truncated."
            let directories =
                match Binary.u16 bytes optional with
                | 0x10B -> optional + 96
                | 0x20B -> optional + 112
                | _ -> fail "unknown optional header."
            let directoryCount = int (Binary.u32 bytes (directories - 4))
            if directoryCount < 5 || directories + 5 * 8 > optional + optionalSize then fail "data directories are missing."
            let resourceRva = int64 (Binary.u32 bytes (directories + 2 * 8))
            let signatureSize = int (Binary.u32 bytes (directories + 4 * 8 + 4))
            let sectionTable = optional + optionalSize
            if not (Binary.fits bytes sectionTable (sectionCount * 40)) then fail "section table is truncated."
            let sections =
                [ for i in 0 .. sectionCount - 1 ->
                    let at = sectionTable + i * 40
                    { VirtualSize = int64 (Binary.u32 bytes (at + 8))
                      VirtualAddress = int64 (Binary.u32 bytes (at + 12))
                      RawSize = int64 (Binary.u32 bytes (at + 16))
                      RawOffset = int64 (Binary.u32 bytes (at + 20)) } ]
            let image resources =
                { ChecksumOffset = optional + 64
                  SubsystemOffset = optional + 68
                  SignatureSize = signatureSize
                  Resources = resources }
            if resourceRva = 0L then image []
            else
                let section =
                    match sections |> List.tryFind (fun s -> resourceRva >= s.VirtualAddress && resourceRva < s.VirtualAddress + s.RawSize) with
                    | Some section -> section
                    | None -> fail "resource directory is outside the file."
                let root = int (resourceRva - section.VirtualAddress + section.RawOffset)
                // Section end in RVA space: bytes that are both mapped and in the file.
                let sectionEnd =
                    section.VirtualAddress + (if section.VirtualSize > 0L then min section.VirtualSize section.RawSize else section.RawSize)
                let offsetOf (rva: int64) = root + int (rva - resourceRva)
                let structures = List<int64>() // start RVA of every directory structure and data blob
                let leaves = List<PeResource * int64>()
                let rec walk (tableRva: int64) (depth: int) (path: int list) =
                    let table = offsetOf tableRva
                    if depth > 2 || tableRva < resourceRva || tableRva + 16L > sectionEnd then fail "resource directory is malformed."
                    let count = Binary.u16 bytes (table + 12) + Binary.u16 bytes (table + 14)
                    if tableRva + 16L + 8L * int64 count > sectionEnd then fail "resource directory is truncated."
                    structures.Add tableRva
                    for i in 0 .. count - 1 do
                        let entry = table + 16 + i * 8
                        let name = Binary.u32 bytes entry
                        let target = Binary.u32 bytes (entry + 4)
                        let id =
                            if name &&& 0x80000000u <> 0u then
                                let stringRva = resourceRva + int64 (name &&& 0x7FFFFFFFu)
                                if stringRva + 2L > sectionEnd then fail "resource name is outside the section."
                                structures.Add stringRva
                                -1
                            else int (name &&& 0xFFFFu)
                        let childRva = resourceRva + int64 (target &&& 0x7FFFFFFFu)
                        if target &&& 0x80000000u <> 0u then walk childRva (depth + 1) (path @ [ id ])
                        elif depth <> 2 then fail "resource tree is not type/name/language."
                        else
                            if childRva + 16L > sectionEnd then fail "resource data entry is outside the section."
                            structures.Add childRva
                            let entryOffset = offsetOf childRva
                            let dataRva = int64 (Binary.u32 bytes entryOffset)
                            let size = int64 (Binary.u32 bytes (entryOffset + 4))
                            match toOffset sections dataRva with
                            | Some dataOffset when Binary.fits bytes dataOffset (int size) ->
                                structures.Add dataRva
                                let leaf =
                                    { TypeId = path[0]
                                      NameId = path[1]
                                      Language = id
                                      EntryOffset = entryOffset
                                      DataOffset = dataOffset
                                      Size = int size
                                      Capacity = int size }
                                leaves.Add((leaf, dataRva))
                            | _ -> fail "resource data is outside the file."
                walk resourceRva 0 []
                let starts = structures |> Seq.sort |> Array.ofSeq
                // Room for a replacement: up to the next structure, or the end of the section
                // for data inside it (never less than the current data).
                let capacity (leaf: PeResource, start: int64) =
                    let limit = if start < sectionEnd then sectionEnd else start + int64 leaf.Size
                    let next = starts |> Array.tryFind (fun s -> s > start) |> Option.defaultValue limit
                    int (max (int64 leaf.Size) (min next limit - start))
                image [ for leaf, start in leaves -> { leaf with Capacity = capacity (leaf, start) } ]
        try
            Ok(parse ())
        with
        | BadImage message -> Error("Not a usable Windows executable: " + message)
        | :? ArgumentOutOfRangeException
        | :? IndexOutOfRangeException -> Error "Not a usable Windows executable: a header points outside the file."

    /// The PE checksum (`CheckSumMappedFile`): 16-bit one's-complement sum of the file with the
    /// checksum field taken as zero, plus the file length.
    let checksum (bytes: byte[]) (checksumOffset: int) : uint32 =
        let mutable sum = 0UL
        let mutable i = 0
        while i + 1 < bytes.Length do
            if i <> checksumOffset && i <> checksumOffset + 2 then
                sum <- sum + uint64 (Binary.u16 bytes i)
                sum <- (sum &&& 0xFFFFUL) + (sum >>> 16)
            i <- i + 2
        if bytes.Length % 2 = 1 then
            sum <- sum + uint64 bytes[bytes.Length - 1]
            sum <- (sum &&& 0xFFFFUL) + (sum >>> 16)
        sum <- (sum &&& 0xFFFFUL) + (sum >>> 16)
        uint32 (sum + uint64 bytes.Length)

    let private first (typeId: int) (image: PeImage) =
        // Named entries come first in a resource directory, then ids in ascending order.
        image.Resources |> List.tryFind (fun r -> r.TypeId = typeId)

    let private data (bytes: byte[]) (resource: PeResource) = bytes[resource.DataOffset .. resource.DataOffset + resource.Size - 1]

    /// The icons of the first icon group (the one Windows shows for the file).
    let readIcons (bytes: byte[]) : Result<PeIcon list, string> =
        match read bytes with
        | Error e -> Error e
        | Ok image ->
            match first RT_GROUP_ICON image with
            | None -> Error "The executable has no icon group."
            | Some group ->
                let dir = data bytes group
                if dir.Length < 6 || Binary.u16 dir 2 <> 1 || dir.Length < 6 + 14 * Binary.u16 dir 4 then Error "The icon group is malformed."
                else
                    let icons =
                        [ for i in 0 .. Binary.u16 dir 4 - 1 ->
                            let at = 6 + i * 14
                            let id = Binary.u16 dir (at + 12)
                            let icon = image.Resources |> List.tryFind (fun r -> r.TypeId = RT_ICON && r.NameId = id)
                            { Width = (if dir[at] = 0uy then 256 else int dir[at])
                              Height = (if dir[at + 1] = 0uy then 256 else int dir[at + 1])
                              BitCount = Binary.u16 dir (at + 6)
                              Id = id
                              Data = (match icon with Some r -> data bytes r | None -> [||]) } ]
                    Ok icons

    /// The subsystem the executable asks Windows for (`SUBSYSTEM_GUI`, `SUBSYSTEM_CONSOLE`, …).
    let subsystem (bytes: byte[]) : Result<int, string> =
        read bytes |> Result.map (fun image -> Binary.u16 bytes image.SubsystemOffset)

    /// The raw VS_VERSIONINFO of the first version resource.
    let readVersion (bytes: byte[]) : Result<byte[], string> =
        match read bytes with
        | Error e -> Error e
        | Ok image ->
            match first RT_VERSION image with
            | None -> Error "The executable has no version resource."
            | Some version -> Ok(data bytes version)

    let private write (bytes: byte[]) (resource: PeResource) (payload: byte[]) =
        Array.Clear(bytes, resource.DataOffset, resource.Capacity)
        Buffer.BlockCopy(payload, 0, bytes, resource.DataOffset, payload.Length)
        Binary.setU32 bytes (resource.EntryOffset + 4) (uint32 payload.Length)

    /// A copy of `template` with `icons` (square PNGs by edge length) written into the icon
    /// slots of the same size and `version` as the version resource. A console template becomes a
    /// GUI program: double-clicking an exported game opens no console window (whose closing
    /// would kill the game), while the template itself stays usable from a terminal
    /// (`--headless`, `--screenshot`). Fails when the template has no slot for something or the
    /// data is larger than the space reserved for it.
    let patch (icons: (int * byte[]) list) (version: byte[]) (template: byte[]) : Result<byte[], string> =
        match read template with
        | Error e -> Error e
        | Ok image ->
            let bytes = Array.copy template
            let missing what = Error(sprintf "The player template has no %s slot. Use the templates that came with this editor." what)
            let tooLarge what (size: int) (capacity: int) =
                Error(sprintf "The %s is %d bytes, but the player template reserves %d." what size capacity)
            let patchIcons () =
                match first RT_GROUP_ICON image with
                | None -> missing "icon"
                | Some group ->
                    let dir = group.DataOffset
                    let count = if group.Size >= 6 then Binary.u16 bytes (dir + 4) else 0
                    if group.Size < 6 + 14 * count then Error "The player template's icon group is malformed."
                    else
                        let entryFor size =
                            let edge = if size >= 256 then 0 else size
                            [ 0 .. count - 1 ]
                            |> List.map (fun i -> dir + 6 + i * 14)
                            |> List.tryFind (fun at -> int bytes[at] = edge && int bytes[at + 1] = edge)
                        icons
                        |> List.fold
                            (fun result (size, png) ->
                                match result with
                                | Error e -> Error e
                                | Ok() ->
                                    let what = sprintf "%d×%d icon" size size
                                    match entryFor size with
                                    | None -> missing what
                                    | Some at ->
                                        let id = Binary.u16 bytes (at + 12)
                                        match image.Resources |> List.tryFind (fun r -> r.TypeId = RT_ICON && r.NameId = id) with
                                        | None -> missing what
                                        | Some slot when png.Length > slot.Capacity -> tooLarge what png.Length slot.Capacity
                                        | Some slot ->
                                            write bytes slot png
                                            bytes[at + 2] <- 0uy // colours: 0 for true colour
                                            bytes[at + 3] <- 0uy
                                            Binary.setU16 bytes (at + 4) 1
                                            Binary.setU16 bytes (at + 6) 32
                                            Binary.setU32 bytes (at + 8) (uint32 png.Length)
                                            Ok())
                            (Ok())
            let patchVersion () =
                match first RT_VERSION image with
                | None -> missing "version information"
                | Some slot when version.Length > slot.Capacity ->
                    tooLarge "version information (title, company and author text)" version.Length slot.Capacity
                | Some slot ->
                    write bytes slot version
                    Ok()
            match patchIcons () |> Result.bind patchVersion with
            | Error e -> Error e
            | Ok() ->
                if Binary.u16 bytes image.SubsystemOffset = SUBSYSTEM_CONSOLE then
                    Binary.setU16 bytes image.SubsystemOffset SUBSYSTEM_GUI
                Binary.setU32 bytes image.ChecksumOffset (checksum bytes image.ChecksumOffset)
                Ok bytes
