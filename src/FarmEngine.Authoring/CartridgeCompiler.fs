namespace FarmEngine.Authoring

open System
open System.Text.Json
open FarmEngine.Cart
open FarmEngine.Json
open FarmEngine.Schemas
open Google.FlatBuffers

/// First version of the deterministic F# cartridge compiler. Game content is still stored
/// as compatibility JSON inside FlatBuffers until the indexed content tables are ported.
[<AbstractClass; Sealed>]
type CartridgeCompiler =
    static member Compile(project: GameProject) : byte[] =
        let problems = Problems.collect project |> Problems.errors
        if not problems.IsEmpty then
            raise (InvalidOperationException(problems |> List.map (fun p -> p.Path + ": " + p.Message) |> String.concat "\n"))

        let settings =
            match project.Export with
            | null -> Defaults.newExportSettings project
            | export -> export
        let title = match settings.Title with null -> project.Name | value -> value
        let version = match settings.Version with null -> project.Version | value -> value
        let executable = match settings.ExecutableName with null -> Defaults.slugId title Seq.empty "game" | value -> value
        let builder = FlatBufferBuilder(4096)
        let stringOffset (value: string) = builder.CreateString value
        let optionalStringOffset (value: string | null) =
            match value with
            | null -> StringOffset(0)
            | text -> stringOffset text

        // FlatBuffers writes back-to-front. Create all referenced values before each table.
        let titleOffset = stringOffset title
        let versionOffset = stringOffset version
        let gameIdOffset = stringOffset settings.GameId
        let authorOffset = optionalStringOffset settings.Author
        let companyOffset = optionalStringOffset settings.Company
        let executableOffset = stringOffset executable
        let scaleOffset = stringOffset settings.PixelScale
        let creditsOffset = optionalStringOffset settings.Credits
        let info =
            GameInfo.CreateGameInfo(builder, titleOffset, versionOffset, gameIdOffset,
                                    authorOffset, companyOffset, executableOffset,
                                    uint32 settings.Window.Width, uint32 settings.Window.Height,
                                    settings.Window.Fullscreen, scaleOffset, creditsOffset)
        // Embed the effective settings even when the source project has not saved them yet:
        // the Rust save target must agree with GameInfo on its persistent identity.
        let cartridgeProject =
            match project.Export with
            | null -> Records.withValue project "Export" (box settings)
            | _ -> project
        let projectBytes = JsonSerializer.SerializeToUtf8Bytes<GameProject>(cartridgeProject, JsonDefaults.Options)
        let content = ContentCompiler.compile project
        let contentBytes = JsonSerializer.SerializeToUtf8Bytes<GameContent>(content, JsonDefaults.Options)
        let projectOffset = Cartridge.CreateProjectJsonVector(builder, projectBytes)
        let contentOffset = Cartridge.CreateContentJsonVector(builder, contentBytes)
        let cart = Cartridge.CreateCartridge(builder, 1u, uint32 project.SchemaVersion, info, projectOffset, contentOffset)
        Cartridge.FinishCartridgeBuffer(builder, cart)
        builder.SizedByteArray()
