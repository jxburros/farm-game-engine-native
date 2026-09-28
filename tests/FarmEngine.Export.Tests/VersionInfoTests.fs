module FarmEngine.Export.Tests.VersionInfoTests

open Xunit
open FarmEngine.Export

[<Theory>]
[<InlineData("1.2.3", 1, 2, 3, 0)>]
[<InlineData("1.2", 1, 2, 0, 0)>]
[<InlineData("2026.9.28.7", 2026, 9, 28, 7)>]
[<InlineData("1.2.3-beta.4", 1, 2, 3, 0)>]
[<InlineData("0.3.0+build5", 0, 3, 0, 0)>]
[<InlineData("v1", 0, 0, 0, 0)>]
[<InlineData("99999.1", 65535, 1, 0, 0)>]
[<InlineData("", 0, 0, 0, 0)>]
let ``version numbers come from the leading numeric parts`` (version: string, a: int, b: int, c: int, d: int) =
    Assert.Equal((uint16 a, uint16 b, uint16 c, uint16 d), VersionInfo.parseNumbers version)

[<Fact>]
let ``a built block parses back with every string and number`` () =
    let info =
        { Numbers = (1us, 2us, 3us, 4us)
          Strings =
            [ "CompanyName", "Willow & Co"
              "FileDescription", "Willow Creek Farm"
              "ProductVersion", "1.2.3-beta"
              "LegalCopyright", "© Jane Doe"
              "Comments", "" ] }
    let bytes = VersionInfo.build info
    Assert.Equal(0, bytes.Length % 2)
    Assert.Equal(bytes.Length, int bytes[0] ||| (int bytes[1] <<< 8))
    match VersionInfo.parse bytes with
    | Error e -> failwith e
    | Ok parsed ->
        Assert.Equal(info.Numbers, parsed.Numbers)
        // Empty strings are left out.
        Assert.Equal<(string * string) list>(info.Strings |> List.filter (fun (_, v) -> v <> ""), parsed.Strings)

[<Fact>]
let ``damaged blocks are reported, not thrown`` () =
    let bytes = VersionInfo.build { Numbers = (1us, 0us, 0us, 0us); Strings = [ "ProductName", "Farm" ] }
    Assert.True(Result.isError (VersionInfo.parse bytes[0..20]))
    let wrongKey = Array.copy bytes
    wrongKey[6] <- byte 'X'
    Assert.True(Result.isError (VersionInfo.parse wrongKey))
