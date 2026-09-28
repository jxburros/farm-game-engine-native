module FarmEngine.Export.Tests.TemplateTests

open System.IO
open Xunit
open FarmEngine.Export
open FarmEngine.Export.Tests.Support

let private error (result: Result<'T, string>) =
    match result with
    | Ok _ -> failwith "expected an error"
    | Error e -> e

[<Fact>]
let ``a matching template is found`` () =
    use dir = new TempDir()
    fakeTemplates dir.Path "1.0.0" |> ignore
    match Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0" with
    | Error e -> failwith e
    | Ok template ->
        Assert.Equal("1.0.0", template.Version)
        Assert.Equal(Path.Combine(dir.Path, "linux-x64", "farm-player"), template.Executable)

[<Fact>]
let ``a template for another editor version is refused`` () =
    use dir = new TempDir()
    fakeTemplates dir.Path "0.9.0" |> ignore
    let message = error (Templates.find dir.Path ExportTarget.WindowsX64 "1.0.0")
    Assert.Contains("version 0.9.0", message)
    Assert.Contains("this editor is version 1.0.0", message)

[<Fact>]
let ``missing, damaged or mislabelled templates are refused`` () =
    use dir = new TempDir()
    Assert.Contains("No Linux x64 player template", error (Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0"))
    let folder = writeTemplate dir.Path ExportTarget.LinuxX64 "1.0.0" fakeLinuxPlayer
    File.AppendAllText(Path.Combine(folder, "farm-player"), "tampered")
    Assert.Contains("damaged", error (Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0"))
    File.WriteAllText(Path.Combine(folder, "template.json"), "{ not json")
    Assert.Contains("not valid JSON", error (Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0"))
    File.WriteAllText(Path.Combine(folder, "template.json"), """{ "target": "windows-x64", "version": "1.0.0", "sha256": "00" }""")
    Assert.Contains("is for windows-x64", error (Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0"))
    writeTemplate dir.Path ExportTarget.LinuxX64 "1.0.0" fakeLinuxPlayer |> ignore
    File.Delete(Path.Combine(folder, "THIRD-PARTY.txt"))
    Assert.Contains("has no THIRD-PARTY.txt", error (Templates.find dir.Path ExportTarget.LinuxX64 "1.0.0"))

[<Fact>]
let ``the editor version drops build metadata`` () =
    Assert.DoesNotContain("+", Templates.editorVersion)
    Assert.Equal(Templates.editorVersion, GameExporter.EditorVersion)
