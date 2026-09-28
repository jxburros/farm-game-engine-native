namespace FarmEngine.Export

open System
open System.Text

/// A Windows `VS_VERSIONINFO` resource: what Explorer shows under Properties → Details and
/// Task Manager shows as the process name (`FileDescription`).
type VersionResource =
    { /// Four numbers (major, minor, patch, build) for both the file and product version.
      Numbers: uint16 * uint16 * uint16 * uint16
      /// StringFileInfo values in the order they are written (US English, Unicode).
      Strings: (string * string) list }

/// Builds and reads `VS_VERSIONINFO` blocks (the layout `rc.exe` writes for a VERSIONINFO
/// statement). Every block is a header (length, value length, type, UTF-16 key), padding to
/// four bytes, its value, then its children, each starting on a four-byte boundary.
module VersionInfo =
    /// "1.2.3-beta.4" → (1, 2, 3, 0): the leading numeric parts, at most four, each clamped
    /// to 0–65535. Anything after them (a pre-release label) only appears in the strings.
    let parseNumbers (version: string) : uint16 * uint16 * uint16 * uint16 =
        let parts =
            version.Split([| '.'; '-'; '+' |])
            |> Seq.takeWhile (fun p -> p.Length > 0 && p |> Seq.forall Char.IsAsciiDigit)
            |> Seq.truncate 4
            |> Seq.map (fun p -> if p.Length > 5 then 65535us else uint16 (min 65535 (int p)))
            |> Array.ofSeq
        let at i = if i < parts.Length then parts[i] else 0us
        at 0, at 1, at 2, at 3

    let private fixedFileInfoSize = 52

    let private block (w: Binary.Writer) (key: string) (valueType: int) (value: Binary.Writer -> int) (children: Binary.Writer -> unit) =
        w.Align 4
        let start = w.Position
        w.U16 0
        w.U16 0
        w.U16 valueType
        w.Bytes(Encoding.Unicode.GetBytes(key + "\000"))
        w.Align 4
        let valueLength = value w
        children w
        w.PatchU16(start, w.Position - start)
        w.PatchU16(start + 2, valueLength)

    let private noValue (_: Binary.Writer) = 0
    let private noChildren (_: Binary.Writer) = ()

    /// The resource bytes for `info`. Empty strings are left out.
    let build (info: VersionResource) : byte[] =
        let major, minor, patch, build = info.Numbers
        let ms = (uint32 major <<< 16) ||| uint32 minor
        let ls = (uint32 patch <<< 16) ||| uint32 build
        use w = new Binary.Writer()
        let fixedInfo (w: Binary.Writer) =
            for value in [ 0xFEEF04BDu; 0x00010000u; ms; ls; ms; ls; 0x3Fu; 0u; 0x00040004u; 1u; 0u; 0u; 0u ] do
                w.U32 value
            fixedFileInfoSize
        let text (value: string) (w: Binary.Writer) =
            w.Bytes(Encoding.Unicode.GetBytes(value + "\000"))
            value.Length + 1
        let strings (w: Binary.Writer) =
            for key, value in info.Strings do
                if value.Length > 0 then block w key 1 (text value) noChildren
        let translation (w: Binary.Writer) =
            w.U16 0x0409
            w.U16 1200
            4
        block w "VS_VERSION_INFO" 0 fixedInfo (fun w ->
            block w "StringFileInfo" 1 noValue (fun w -> block w "040904B0" 1 noValue strings)
            block w "VarFileInfo" 1 noValue (fun w -> block w "Translation" 0 translation noChildren))
        w.ToArray()

    type private Node =
        { Key: string
          ValueType: int
          Value: byte[]
          Children: Node list }

    let private align4 (offset: int) = (offset + 3) &&& ~~~3

    /// Parses one block at `offset` that must end by `limit`. `wValueLength` counts UTF-16
    /// units for text values (type 1) and bytes for binary ones (type 0).
    let rec private parseNode (bytes: byte[]) (offset: int) (limit: int) : Result<Node, string> =
        if not (Binary.fits bytes offset 6) || offset + 6 > limit then Error "Version block header is truncated."
        else
            let length = Binary.u16 bytes offset
            let valueLength = Binary.u16 bytes (offset + 2)
            let valueType = Binary.u16 bytes (offset + 4)
            let finish = offset + length
            if length < 6 || finish > limit then Error "Version block length is out of range."
            else
                let mutable keyEnd = offset + 6
                while keyEnd + 1 < finish && (bytes[keyEnd] <> 0uy || bytes[keyEnd + 1] <> 0uy) do
                    keyEnd <- keyEnd + 2
                if keyEnd + 1 >= finish then Error "Version block key is not terminated."
                else
                    let key = Encoding.Unicode.GetString(bytes, offset + 6, keyEnd - offset - 6)
                    let valueStart = align4 (keyEnd + 2)
                    let valueBytes = if valueType = 1 then valueLength * 2 else valueLength
                    if valueStart + valueBytes > finish then Error(sprintf "Version value of %s overruns its block." key)
                    else
                        let value = bytes[valueStart .. valueStart + valueBytes - 1]
                        let rec children (position: int) (acc: Node list) =
                            let position = align4 position
                            if position >= finish then Ok(List.rev acc)
                            else
                                match parseNode bytes position finish with
                                | Ok child -> children (position + Binary.u16 bytes position) (child :: acc)
                                | Error e -> Error e
                        children (valueStart + valueBytes) []
                        |> Result.map (fun kids -> { Key = key; ValueType = valueType; Value = value; Children = kids })

    /// Reads a `VS_VERSIONINFO` block back (tests and diagnostics).
    let parse (bytes: byte[]) : Result<VersionResource, string> =
        match parseNode bytes 0 bytes.Length with
        | Error e -> Error e
        | Ok root when root.Key <> "VS_VERSION_INFO" -> Error "Not a VS_VERSION_INFO block."
        | Ok root when root.Value.Length <> fixedFileInfoSize || Binary.u32 root.Value 0 <> 0xFEEF04BDu ->
            Error "VS_FIXEDFILEINFO is missing."
        | Ok root ->
            let ms = Binary.u32 root.Value 8
            let ls = Binary.u32 root.Value 12
            let strings =
                root.Children
                |> List.filter (fun n -> n.Key = "StringFileInfo")
                |> List.collect (fun n -> n.Children)
                |> List.collect (fun table -> table.Children)
                |> List.map (fun s ->
                    let text = Encoding.Unicode.GetString(s.Value)
                    s.Key, text.TrimEnd('\000'))
            Ok
                { Numbers = uint16 (ms >>> 16), uint16 (ms &&& 0xFFFFu), uint16 (ls >>> 16), uint16 (ls &&& 0xFFFFu)
                  Strings = strings }
