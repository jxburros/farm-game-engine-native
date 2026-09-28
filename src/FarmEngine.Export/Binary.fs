namespace FarmEngine.Export

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography

/// Little-endian reads and writes over byte arrays, and the checksums export needs.
module internal Binary =
    let u16 (bytes: byte[]) (offset: int) : int = int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(bytes, offset, 2)))
    let u32 (bytes: byte[]) (offset: int) : uint32 = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, offset, 4))
    let i32 (bytes: byte[]) (offset: int) : int = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, offset, 4))
    let setU16 (bytes: byte[]) (offset: int) (value: int) = BinaryPrimitives.WriteUInt16LittleEndian(Span(bytes, offset, 2), uint16 value)
    let setU32 (bytes: byte[]) (offset: int) (value: uint32) = BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, offset, 4), value)

    /// `count` bytes at `offset` exist in `bytes`.
    let fits (bytes: byte[]) (offset: int) (count: int) = offset >= 0 && count >= 0 && int64 offset + int64 count <= int64 bytes.Length

    /// A growable little-endian buffer.
    type Writer() =
        let stream = new MemoryStream()
        member _.Position = int stream.Position
        member _.Byte(value: byte) = stream.WriteByte value
        member _.Bytes(value: byte[]) = stream.Write(value, 0, value.Length)
        member this.U16(value: int) =
            this.Byte(byte (value &&& 0xFF))
            this.Byte(byte ((value >>> 8) &&& 0xFF))
        member this.U32(value: uint32) =
            let buffer = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteUInt32LittleEndian(Span buffer, value)
            this.Bytes buffer
        member this.Align(boundary: int) =
            while this.Position % boundary <> 0 do
                this.Byte 0uy
        /// Overwrites a 16-bit value already written.
        member _.PatchU16(offset: int, value: int) =
            let saved = stream.Position
            stream.Position <- int64 offset
            stream.WriteByte(byte (value &&& 0xFF))
            stream.WriteByte(byte ((value >>> 8) &&& 0xFF))
            stream.Position <- saved
        member _.ToArray() = stream.ToArray()
        interface IDisposable with
            member _.Dispose() = stream.Dispose()

    let private crcTable =
        Array.init 256 (fun n ->
            let mutable c = uint32 n
            for _ in 0..7 do
                c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
            c)

    /// CRC-32 (IEEE 802.3), as ZIP and gzip use it.
    let crc32 (data: byte[]) : uint32 =
        let mutable crc = 0xFFFFFFFFu
        for b in data do
            crc <- crcTable[int ((crc ^^^ uint32 b) &&& 0xFFu)] ^^^ (crc >>> 8)
        crc ^^^ 0xFFFFFFFFu

    /// Lowercase hex SHA-256.
    let sha256 (data: byte[]) : string = Convert.ToHexString(SHA256.HashData data).ToLowerInvariant()

    let sha256File (path: string) : string =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()
