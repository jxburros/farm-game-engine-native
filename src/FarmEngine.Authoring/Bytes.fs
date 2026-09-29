namespace FarmEngine.Authoring

open System

/// Byte helpers the cartridge compiler needs, in plain F# so the authoring core compiles under
/// Fable (no System.Security.Cryptography there): UTF-8, base64, hex and SHA-256.
module Bytes =
    /// UTF-8 bytes of a string (lone surrogates become U+FFFD, like .NET and `TextEncoder`).
    let utf8 (text: string) : byte[] =
        let out = ResizeArray<byte>(text.Length)
        let mutable i = 0
        while i < text.Length do
            let c = int text.[i]
            let codePoint =
                if c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length && int text.[i + 1] >= 0xDC00 && int text.[i + 1] <= 0xDFFF then
                    let low = int text.[i + 1]
                    i <- i + 1
                    0x10000 + ((c - 0xD800) <<< 10) + (low - 0xDC00)
                elif c >= 0xD800 && c <= 0xDFFF then 0xFFFD
                else c
            if codePoint < 0x80 then
                out.Add(byte codePoint)
            elif codePoint < 0x800 then
                out.Add(byte (0xC0 ||| (codePoint >>> 6)))
                out.Add(byte (0x80 ||| (codePoint &&& 0x3F)))
            elif codePoint < 0x10000 then
                out.Add(byte (0xE0 ||| (codePoint >>> 12)))
                out.Add(byte (0x80 ||| ((codePoint >>> 6) &&& 0x3F)))
                out.Add(byte (0x80 ||| (codePoint &&& 0x3F)))
            else
                out.Add(byte (0xF0 ||| (codePoint >>> 18)))
                out.Add(byte (0x80 ||| ((codePoint >>> 12) &&& 0x3F)))
                out.Add(byte (0x80 ||| ((codePoint >>> 6) &&& 0x3F)))
                out.Add(byte (0x80 ||| (codePoint &&& 0x3F)))
            i <- i + 1
        out.ToArray()

    /// The string a UTF-8 byte range encodes (malformed sequences become U+FFFD, like .NET and
    /// `TextDecoder`).
    let utf8Text (bytes: byte[]) (start: int) (length: int) : string =
        let out = System.Text.StringBuilder(length)
        let stop = start + length
        let mutable i = start
        let continuation (k: int) = k < stop && (int bytes.[k] &&& 0xC0) = 0x80
        while i < stop do
            let b = int bytes.[i]
            let append (codePoint: int) (size: int) =
                if codePoint >= 0x10000 then
                    let v = codePoint - 0x10000
                    out.Append(char (0xD800 + (v >>> 10))).Append(char (0xDC00 + (v &&& 0x3FF))) |> ignore
                else
                    out.Append(char codePoint) |> ignore
                i <- i + size
            if b < 0x80 then append b 1
            elif b >= 0xC2 && b < 0xE0 && continuation (i + 1) then
                append (((b &&& 0x1F) <<< 6) ||| (int bytes.[i + 1] &&& 0x3F)) 2
            elif b >= 0xE0 && b < 0xF0 && continuation (i + 1) && continuation (i + 2) then
                let cp = ((b &&& 0x0F) <<< 12) ||| ((int bytes.[i + 1] &&& 0x3F) <<< 6) ||| (int bytes.[i + 2] &&& 0x3F)
                if cp >= 0x800 && (cp < 0xD800 || cp > 0xDFFF) then append cp 3 else append 0xFFFD 1
            elif b >= 0xF0 && b < 0xF5 && continuation (i + 1) && continuation (i + 2) && continuation (i + 3) then
                let cp =
                    ((b &&& 0x07) <<< 18) ||| ((int bytes.[i + 1] &&& 0x3F) <<< 12)
                    ||| ((int bytes.[i + 2] &&& 0x3F) <<< 6) ||| (int bytes.[i + 3] &&& 0x3F)
                if cp >= 0x10000 && cp <= 0x10FFFF then append cp 4 else append 0xFFFD 1
            else
                append 0xFFFD 1
        out.ToString()

    let private base64Value (c: char) : int =
        if c >= 'A' && c <= 'Z' then int c - int 'A'
        elif c >= 'a' && c <= 'z' then int c - int 'a' + 26
        elif c >= '0' && c <= '9' then int c - int '0' + 52
        elif c = '+' then 62
        elif c = '/' then 63
        else -1

    /// Strict base64 (`Convert.FromBase64String`): white space is ignored, padding must be right,
    /// anything else is `None`.
    let fromBase64 (text: string) : byte[] option =
        let chars = text |> Seq.filter (fun c -> not (c = ' ' || c = '\t' || c = '\r' || c = '\n')) |> Array.ofSeq
        let padding =
            if chars.Length >= 2 && chars.[chars.Length - 1] = '=' && chars.[chars.Length - 2] = '=' then 2
            elif chars.Length >= 1 && chars.[chars.Length - 1] = '=' then 1
            else 0
        let body = chars.Length - padding
        if chars.Length % 4 <> 0 || Array.exists (fun c -> base64Value c < 0) chars.[.. body - 1] then
            None
        else
            let out = ResizeArray<byte>(chars.Length / 4 * 3)
            let mutable i = 0
            while i < chars.Length do
                let value k = if i + k < body then base64Value chars.[i + k] else 0
                let n = (value 0 <<< 18) ||| (value 1 <<< 12) ||| (value 2 <<< 6) ||| value 3
                out.Add(byte (n >>> 16))
                if i + 2 < body then out.Add(byte ((n >>> 8) &&& 0xFF))
                if i + 3 < body then out.Add(byte (n &&& 0xFF))
                i <- i + 4
            Some(out.ToArray())

    /// Lowercase hex.
    let toHex (bytes: byte[]) : string =
        let digits = "0123456789abcdef"
        let chars = Array.zeroCreate<char> (bytes.Length * 2)
        for i in 0 .. bytes.Length - 1 do
            chars.[2 * i] <- digits.[int bytes.[i] >>> 4]
            chars.[2 * i + 1] <- digits.[int bytes.[i] &&& 0xF]
        String(chars)

    let private k : uint32[] =
        [| 0x428a2f98u; 0x71374491u; 0xb5c0fbcfu; 0xe9b5dba5u; 0x3956c25bu; 0x59f111f1u; 0x923f82a4u; 0xab1c5ed5u
           0xd807aa98u; 0x12835b01u; 0x243185beu; 0x550c7dc3u; 0x72be5d74u; 0x80deb1feu; 0x9bdc06a7u; 0xc19bf174u
           0xe49b69c1u; 0xefbe4786u; 0x0fc19dc6u; 0x240ca1ccu; 0x2de92c6fu; 0x4a7484aau; 0x5cb0a9dcu; 0x76f988dau
           0x983e5152u; 0xa831c66du; 0xb00327c8u; 0xbf597fc7u; 0xc6e00bf3u; 0xd5a79147u; 0x06ca6351u; 0x14292967u
           0x27b70a85u; 0x2e1b2138u; 0x4d2c6dfcu; 0x53380d13u; 0x650a7354u; 0x766a0abbu; 0x81c2c92eu; 0x92722c85u
           0xa2bfe8a1u; 0xa81a664bu; 0xc24b8b70u; 0xc76c51a3u; 0xd192e819u; 0xd6990624u; 0xf40e3585u; 0x106aa070u
           0x19a4c116u; 0x1e376c08u; 0x2748774cu; 0x34b0bcb5u; 0x391c0cb3u; 0x4ed8aa4au; 0x5b9cca4fu; 0x682e6ff3u
           0x748f82eeu; 0x78a5636fu; 0x84c87814u; 0x8cc70208u; 0x90befffau; 0xa4506cebu; 0xbef9a3f7u; 0xc67178f2u |]

    let private rotr (x: uint32) (n: int) = (x >>> n) ||| (x <<< (32 - n))

    /// SHA-256 (FIPS 180-4).
    let sha256 (data: byte[]) : byte[] =
        let bitLength = uint64 data.Length * 8UL
        let paddedLength = ((data.Length + 9 + 63) / 64) * 64
        let message = Array.zeroCreate<byte> paddedLength
        Array.blit data 0 message 0 data.Length
        message.[data.Length] <- 0x80uy
        for i in 0 .. 7 do
            message.[paddedLength - 1 - i] <- byte ((bitLength >>> (8 * i)) &&& 0xFFUL)
        let h = [| 0x6a09e667u; 0xbb67ae85u; 0x3c6ef372u; 0xa54ff53au; 0x510e527fu; 0x9b05688cu; 0x1f83d9abu; 0x5be0cd19u |]
        let w = Array.zeroCreate<uint32> 64
        for block in 0 .. paddedLength / 64 - 1 do
            let offset = block * 64
            for t in 0 .. 15 do
                let at = offset + 4 * t
                w.[t] <- (uint32 message.[at] <<< 24) ||| (uint32 message.[at + 1] <<< 16) ||| (uint32 message.[at + 2] <<< 8) ||| uint32 message.[at + 3]
            for t in 16 .. 63 do
                let s0 = rotr w.[t - 15] 7 ^^^ rotr w.[t - 15] 18 ^^^ (w.[t - 15] >>> 3)
                let s1 = rotr w.[t - 2] 17 ^^^ rotr w.[t - 2] 19 ^^^ (w.[t - 2] >>> 10)
                w.[t] <- w.[t - 16] + s0 + w.[t - 7] + s1
            let mutable a = h.[0]
            let mutable b = h.[1]
            let mutable c = h.[2]
            let mutable d = h.[3]
            let mutable e = h.[4]
            let mutable f = h.[5]
            let mutable g = h.[6]
            let mutable hh = h.[7]
            for t in 0 .. 63 do
                let s1 = rotr e 6 ^^^ rotr e 11 ^^^ rotr e 25
                let ch = (e &&& f) ^^^ (~~~e &&& g)
                let temp1 = hh + s1 + ch + k.[t] + w.[t]
                let s0 = rotr a 2 ^^^ rotr a 13 ^^^ rotr a 22
                let maj = (a &&& b) ^^^ (a &&& c) ^^^ (b &&& c)
                let temp2 = s0 + maj
                hh <- g
                g <- f
                f <- e
                e <- d + temp1
                d <- c
                c <- b
                b <- a
                a <- temp1 + temp2
            h.[0] <- h.[0] + a
            h.[1] <- h.[1] + b
            h.[2] <- h.[2] + c
            h.[3] <- h.[3] + d
            h.[4] <- h.[4] + e
            h.[5] <- h.[5] + f
            h.[6] <- h.[6] + g
            h.[7] <- h.[7] + hh
        [| for word in h do
               yield byte (word >>> 24)
               yield byte ((word >>> 16) &&& 0xFFu)
               yield byte ((word >>> 8) &&& 0xFFu)
               yield byte (word &&& 0xFFu) |]
