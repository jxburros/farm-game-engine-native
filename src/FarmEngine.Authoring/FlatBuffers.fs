namespace FarmEngine.Authoring

open System

/// A FlatBuffers builder in plain F# (the authoring core compiles under Fable, so it cannot use
/// the Google.FlatBuffers package). It follows the reference builder's layout step for step:
/// back-to-front writing, the same alignment and padding, vtable trimming and deduplication and
/// the same `Finish` with a file identifier, so it writes byte for byte what flatc's C# code writes
/// for the same calls. Only what `schemas/cart.fbs` needs is here: strings, byte and offset
/// vectors, tables with offset, uint and bool fields.
type FlatBufferBuilder(initialSize: int) =
    let mutable buffer : byte[] = Array.zeroCreate (max 1 initialSize)
    let mutable space = buffer.Length
    let mutable minAlign = 1
    let mutable vtable : int[] = [||]
    let mutable vtableSize = -1
    let mutable objectStart = 0
    let vtables = ResizeArray<int>()
    let mutable vectorElements = 0
    let mutable nested = false

    /// Bytes written so far (offsets count from the end of the buffer).
    member _.Offset = buffer.Length - space

    member private _.Grow() =
        let oldLength = buffer.Length
        let grown = Array.zeroCreate<byte> (oldLength * 2)
        Array.blit buffer 0 grown (grown.Length - oldLength) oldLength
        buffer <- grown
        space <- space + (grown.Length - oldLength)

    member private _.Pad(size: int) =
        for _ in 1 .. size do
            space <- space - 1
            buffer.[space] <- 0uy

    /// Makes room for `size` bytes after `additionalBytes` more, aligned to `size`.
    member this.Prep(size: int, additionalBytes: int) =
        if size > minAlign then minAlign <- size
        let alignSize = ((~~~(buffer.Length - space + additionalBytes)) + 1) &&& (size - 1)
        while space < alignSize + size + additionalBytes do
            this.Grow()
        if alignSize > 0 then this.Pad alignSize

    member private _.PutByte(value: byte) =
        space <- space - 1
        buffer.[space] <- value

    member private _.PutInt32At(position: int, value: int) =
        buffer.[position] <- byte (value &&& 0xFF)
        buffer.[position + 1] <- byte ((value >>> 8) &&& 0xFF)
        buffer.[position + 2] <- byte ((value >>> 16) &&& 0xFF)
        buffer.[position + 3] <- byte ((value >>> 24) &&& 0xFF)

    member private this.PutInt32(value: int) =
        space <- space - 4
        this.PutInt32At(space, value)

    member private _.PutInt16(value: int) =
        space <- space - 2
        buffer.[space] <- byte (value &&& 0xFF)
        buffer.[space + 1] <- byte ((value >>> 8) &&& 0xFF)

    member private _.GetInt16(position: int) : int =
        let value = int buffer.[position] ||| (int buffer.[position + 1] <<< 8)
        if value >= 0x8000 then value - 0x10000 else value

    member this.AddByte(value: byte) =
        this.Prep(1, 0)
        this.PutByte value

    member this.AddBool(value: bool) = this.AddByte(if value then 1uy else 0uy)

    member this.AddUInt32(value: uint32) =
        this.Prep(4, 0)
        this.PutInt32(int value)

    member this.AddInt32(value: int) =
        this.Prep(4, 0)
        this.PutInt32 value

    member private this.AddInt16(value: int) =
        this.Prep(2, 0)
        this.PutInt16 value

    /// An offset to something already written, relative to where it is stored.
    member this.AddOffset(offset: int) =
        this.Prep(4, 0)
        if offset > this.Offset then invalidArg "offset" "FlatBuffers: offset points forward"
        this.PutInt32(this.Offset - offset + 4)

    member this.StartVector(elementSize: int, count: int, alignment: int) =
        if nested then invalidOp "FlatBuffers: a vector cannot start inside a table"
        vectorElements <- count
        this.Prep(4, elementSize * count)
        this.Prep(alignment, elementSize * count)

    member this.EndVector() : int =
        this.PutInt32 vectorElements
        this.Offset

    member this.CreateString(text: string) : int =
        if nested then invalidOp "FlatBuffers: a string cannot start inside a table"
        this.AddByte 0uy
        let bytes = Bytes.utf8 text
        this.StartVector(1, bytes.Length, 1)
        space <- space - bytes.Length
        Array.blit bytes 0 buffer space bytes.Length
        this.EndVector()

    /// `[ubyte]`, written element by element like flatc's `Create…Vector`.
    member this.CreateByteVector(data: byte[]) : int =
        this.StartVector(1, data.Length, 1)
        for i in data.Length - 1 .. -1 .. 0 do
            this.AddByte data.[i]
        this.EndVector()

    /// A vector of offsets (strings or tables).
    member this.CreateOffsetVector(offsets: int[]) : int =
        this.StartVector(4, offsets.Length, 4)
        for i in offsets.Length - 1 .. -1 .. 0 do
            this.AddOffset offsets.[i]
        this.EndVector()

    member this.StartTable(fields: int) =
        if nested then invalidOp "FlatBuffers: tables cannot nest while building"
        if vtable.Length < fields then vtable <- Array.zeroCreate fields
        vtableSize <- fields
        objectStart <- this.Offset
        nested <- true

    member private this.Slot(field: int) = vtable.[field] <- this.Offset

    /// An offset field, left out when it is 0 (absent).
    member this.AddOffsetField(field: int, offset: int) =
        if offset <> 0 then
            this.AddOffset offset
            this.Slot field

    /// A uint field, left out when it equals the schema default.
    member this.AddUInt32Field(field: int, value: uint32, defaultValue: uint32) =
        if value <> defaultValue then
            this.AddUInt32 value
            this.Slot field

    /// A bool field, left out when it equals the schema default.
    member this.AddBoolField(field: int, value: bool, defaultValue: bool) =
        if value <> defaultValue then
            this.AddBool value
            this.Slot field

    member this.EndTable() : int =
        if not nested then invalidOp "FlatBuffers: EndTable without StartTable"
        this.AddInt32 0
        let vtableLocation = this.Offset
        let mutable i = vtableSize - 1
        while i >= 0 && vtable.[i] = 0 do
            i <- i - 1
        let trimmedSize = i + 1
        while i >= 0 do
            let offset = if vtable.[i] <> 0 then vtableLocation - vtable.[i] else 0
            this.AddInt16 offset
            vtable.[i] <- 0
            i <- i - 1
        this.AddInt16(vtableLocation - objectStart)
        this.AddInt16((trimmedSize + 2) * 2)
        let mutable existing = 0
        let mutable index = 0
        while existing = 0 && index < vtables.Count do
            let vt1 = buffer.Length - vtables.[index]
            let vt2 = space
            let length = this.GetInt16 vt1
            if length = this.GetInt16 vt2 then
                let mutable same = true
                let mutable j = 2
                while same && j < length do
                    if this.GetInt16(vt1 + j) <> this.GetInt16(vt2 + j) then same <- false
                    j <- j + 2
                if same then existing <- vtables.[index]
            index <- index + 1
        if existing <> 0 then
            space <- buffer.Length - vtableLocation
            this.PutInt32At(space, existing - vtableLocation)
        else
            vtables.Add this.Offset
            this.PutInt32At(buffer.Length - vtableLocation, this.Offset - vtableLocation)
        vtableSize <- -1
        nested <- false
        vtableLocation

    /// Finishes the buffer with the root table and a four-character file identifier.
    member this.Finish(rootTable: int, fileIdentifier: string) =
        if fileIdentifier.Length <> 4 then invalidArg "fileIdentifier" "FlatBuffers: file identifiers have 4 characters"
        this.Prep(minAlign, 4 + 4)
        for i in 3 .. -1 .. 0 do
            this.AddByte(byte fileIdentifier.[i])
        this.Prep(minAlign, 4)
        this.AddOffset rootTable

    /// The finished bytes.
    member _.ToArray() : byte[] = Array.sub buffer space (buffer.Length - space)
