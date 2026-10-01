/// farmc (src/FarmEngine.Cli) through its entry point: exit codes and messages.
module FarmEngine.Export.Tests.CliTests

open System
open System.IO
open FarmEngine.Authoring
open FarmEngine.Cli
open FarmEngine.Export.Tests.Support
open Xunit

/// The exit code and what farmc wrote to stdout and stderr.
let private farmc (args: string list) : int * string * string =
    use out = new StringWriter()
    use err = new StringWriter()
    let code = Program.run (Array.ofList args) out err
    code, out.ToString(), err.ToString()

[<Fact>]
let ``help and version print and exit 0`` () =
    for help in [ [ "--help" ]; [ "-h" ]; [ "help" ]; [ "compile"; "--help" ]; [ "export"; "x.json"; "-h" ] ] do
        let code, out, err = farmc help
        Assert.Equal(0, code)
        Assert.Contains("farmc compile <project.json> --out <game.cart>", out)
        Assert.Equal("", err)
    let code, out, _ = farmc [ "--version" ]
    Assert.Equal(0, code)
    Assert.StartsWith("farmc ", out)
    Assert.DoesNotContain("+", out)

[<Fact>]
let ``usage errors exit 2 and say what is wrong`` () =
    let cases =
        [ [], ""
          [ "build" ], "Unknown command build."
          [ "--verbose" ], "Unknown option --verbose."
          [ "compile"; "p.json" ], "compile needs --out."
          [ "compile"; "--out"; "x.cart" ], "compile needs a project file."
          [ "compile"; "a.json"; "b.json"; "--out"; "x.cart" ], "compile takes one project file."
          [ "compile"; "p.json"; "--out"; "x.cart"; "--target"; "web" ], "compile takes no --target"
          [ "compile"; "p.json"; "--frobnicate" ], "Unknown option --frobnicate."
          [ "export"; "p.json"; "--out" ], "--out needs a value."
          // A flag is never taken as the value of the one before it.
          [ "export"; "p.json"; "--out"; "--no-archive" ], "--out needs a value."
          [ "export"; "p.json"; "--target"; "--out"; "dist" ], "--target needs a value." ]
    for args, message in cases do
        let code, out, err = farmc args
        Assert.True((code = 2), sprintf "%A exited %d" args code)
        Assert.Equal("", out)
        Assert.Contains(message, err)
        Assert.Contains("Usage:", err)

[<Fact>]
let ``options parse in any order`` () =
    match Program.parseArguments [ "--out"; "dist"; "--no-archive"; "game.json"; "--target"; "web"; "--target"; "linux-x64" ] Program.emptyArguments with
    | Ok parsed ->
        Assert.Equal<string list>([ "game.json" ], parsed.Positional)
        Assert.Equal<string list>([ "web"; "linux-x64" ], parsed.Targets)
        Assert.Equal(Some "dist", parsed.Out)
        Assert.False parsed.Archives
    | Error message -> failwith message

[<Fact>]
let ``a bad path or a refused project exits 1 with the message, never a stack trace`` () =
    use temp = new TempDir()
    for path in [ ""; temp.Sub "missing.json"; temp.Path ] do
        let code, _, err = farmc [ "compile"; path; "--out"; temp.Sub "x.cart" ]
        Assert.True((code = 1), sprintf "%A exited %d: %s" path code err)
        Assert.DoesNotContain("   at ", err)
        Assert.True(err.Trim().Length > 0)
    let broken = temp.Sub "broken.json"
    File.WriteAllText(broken, "{ \"schemaVersion\": 9, ")
    let code, _, err = farmc [ "compile"; broken; "--out"; temp.Sub "x.cart" ]
    Assert.Equal(1, code)
    Assert.Contains("not valid JSON", err)
    // Deep nesting is a parse error, not a crashed process.
    let deep = temp.Sub "deep.json"
    File.WriteAllText(deep, "{\"a\":" + String('[', 100_000) + String(']', 100_000) + "}")
    let code, _, err = farmc [ "export"; deep; "--out"; temp.Sub "dist" ]
    Assert.Equal(1, code)
    Assert.Contains("Too deeply nested", err)

[<Fact>]
let ``compile writes the cartridge with options in any order`` () =
    use temp = new TempDir()
    let project = temp.Sub "game.json"
    File.WriteAllText(project, ProjectLoad.toText (starter ()))
    let cart = temp.Sub "out/game.cart"
    let code, out, err = farmc [ "compile"; "--out"; cart; project ]
    Assert.True((code = 0), err)
    Assert.Contains("Compiled", out)
    Assert.True(CartridgeReader.hasIdentifier (File.ReadAllBytes cart))
