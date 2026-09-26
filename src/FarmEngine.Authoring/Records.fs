namespace FarmEngine.Authoring

open System.Reflection

/// Copy-with helpers over the C# schema records (`FarmEngine.Schemas`).
///
/// C# `record` types expose `with` expressions to C# only; from F# an init-only property can't
/// be set outside an object initializer. Until the schema moves to F# records (phase 3 of
/// docs/LANGUAGES.md) this module does what `with` does: a shallow copy (the record copy
/// constructor is a memberwise clone) followed by the changed properties. Cost is one
/// reflection lookup per property, which is nothing next to an editor click.
module Records =
    let private memberwiseClone : MethodInfo =
        match typeof<obj>.GetMethod("MemberwiseClone", BindingFlags.Instance ||| BindingFlags.NonPublic) with
        | null -> failwith "Object.MemberwiseClone is missing"
        | method -> method

    let private property (recordType: System.Type) (name: string) =
        match recordType.GetProperty(name, BindingFlags.Instance ||| BindingFlags.Public) with
        | null -> invalidArg name (sprintf "%s has no property %s" recordType.Name name)
        | prop -> prop

    /// `record with { name1 = value1; … }` for a C# record.
    let withValues (record: 'T) (changes: (string * objnull) list) : 'T =
        let copy =
            match memberwiseClone.Invoke(record, null) with
            | null -> failwith "MemberwiseClone returned null"
            | copy -> copy
        for (name, value) in changes do
            (property typeof<'T> name).SetValue(copy, value)
        unbox<'T> copy

    /// `record with { name = value }`.
    let withValue (record: 'T) (name: string) (value: objnull) : 'T = withValues record [ (name, value) ]
