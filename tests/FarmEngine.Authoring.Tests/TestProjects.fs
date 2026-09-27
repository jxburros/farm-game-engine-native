/// Realistic projects and lookups shared by the authoring tests.
module FarmEngine.Authoring.Tests.TestProjects

open System.Collections.Generic
open Xunit
open FarmEngine.Authoring
open FarmEngine.Content
open FarmEngine.Schemas

/// The starter farm with wall-clock fields pinned.
let starter () = DefaultContent.CreateInitialProject(0.0)

let blank () = DefaultContent.CreateBlankProject(0.0)

/// Every "New Project" template, by id.
let templates () : (string * GameProject) list =
    ProjectTemplates.All |> List.ofSeq |> List.map (fun id -> id, Templates.CreateProjectForTemplate(id, 0.0))

let scene (project: GameProject) (sceneId: string) : Scene =
    project.Scenes |> Seq.find (fun s -> s.Id = sceneId)

let farm (project: GameProject) = scene project "scene-farm"

let tile (project: GameProject) (sceneId: string) (x: int) (y: int) : Tile = (scene project sceneId).Tiles.[y].[x]

let apply (edit: Edit) (project: GameProject) : GameProject = Document.run project edit

let errors (project: GameProject) = Problems.collect project |> Problems.errors

/// Problems formatted for assertion messages.
let describe (problems: Problem list) =
    problems |> List.map (fun p -> sprintf "%s %s: %s" p.Code p.Path p.Message) |> String.concat "\n"

let item (id: string) (name: string) : Item =
    Item(Id = id, Name = name, Description = name, Type = ItemTypes.Material, Stackable = true, MaxStack = 99.0, Value = 5.0)

let npc (project: GameProject) (id: string) : Npc = project.Npcs |> Seq.find (fun n -> n.Id = id)

/// A nullable string as text ("" when null), for `Assert.Equal`.
let orEmpty (value: string | null) : string =
    match value with
    | null -> ""
    | s -> s

/// A .NET list from a sequence (the schema records hold `List<T>`).
let listOf (items: seq<'T>) : List<'T> = List<'T>(items)

/// A copy of a .NET list with one more element.
let appended (extra: 'T) (items: List<'T>) : List<'T> =
    let next = List<'T>(items)
    next.Add extra
    next

/// Asserts a `Result` is the given error message.
let expectError (expected: string) (result: Result<'T, string>) =
    match result with
    | Error message -> Assert.Equal(expected, message)
    | Ok _ -> failwithf "expected the error %A" expected
