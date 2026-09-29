using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace FarmingRpgMaker.App;

/// <summary>
/// Reading the F# schema records from C#: options as nullable values, F# lists from sequences
/// and ordered maps as dictionaries. (Writing goes through the generated <c>WithField</c>
/// extensions of <c>FarmEngine.Authoring.Net.RecordWith</c>.)
/// </summary>
public static class FSharpInterop
{
    /// <summary>The value, or null for <c>None</c>.</summary>
    public static T? OrNull<T>(this FSharpOption<T>? option) where T : class =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : null;

    /// <summary>The value, or null for <c>None</c> (value types).</summary>
    public static T? OrNullable<T>(this FSharpOption<T>? option) where T : struct =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : null;

    /// <summary>The value, or <paramref name="fallback"/> for <c>None</c>.</summary>
    public static T Or<T>(this FSharpOption<T>? option, T fallback) =>
        FSharpOption<T>.get_IsSome(option) ? option!.Value : fallback;

    /// <summary>Whether the option holds a value.</summary>
    public static bool HasValue<T>(this FSharpOption<T>? option) => FSharpOption<T>.get_IsSome(option);

    /// <summary><c>Some value</c>, or <c>None</c> for null.</summary>
    public static FSharpOption<T>? ToOption<T>(this T? value) where T : class =>
        value is null ? null : FSharpOption<T>.Some(value);

    /// <summary><c>Some value</c>, or <c>None</c> for null (value types).</summary>
    public static FSharpOption<T>? ToOption<T>(this T? value) where T : struct =>
        value is { } v ? FSharpOption<T>.Some(v) : null;

    /// <summary>The list, or an empty one for <c>None</c>.</summary>
    public static FSharpList<T> OrEmpty<T>(this FSharpOption<FSharpList<T>>? option) =>
        FSharpOption<FSharpList<T>>.get_IsSome(option) ? option!.Value : FSharpList<T>.Empty;

    /// <summary>Whether an ordered map has <paramref name="key"/>.</summary>
    public static bool ContainsKey<T>(this FSharpList<Tuple<string, T>> pairs, string key) => pairs.Any(pair => pair.Item1 == key);

    /// <summary>The items as an F# list.</summary>
    public static FSharpList<T> ToFSharpList<T>(this IEnumerable<T> items) => ListModule.OfSeq(items);

    /// <summary>An ordered map (<c>(string * T) list</c>) as a dictionary (later keys win).</summary>
    public static Dictionary<string, T> ToDictionary<T>(this FSharpList<Tuple<string, T>> pairs)
    {
        var dictionary = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    /// <summary>The value under <paramref name="key"/> of an ordered map, if any.</summary>
    public static bool TryGet<T>(this FSharpList<Tuple<string, T>> pairs, string key, out T value)
    {
        foreach (var (k, v) in pairs)
        {
            if (k == key)
            {
                value = v;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Key/value pairs as an ordered map, in order.</summary>
    public static FSharpList<Tuple<string, T>> ToOrderedMap<T>(this IEnumerable<KeyValuePair<string, T>> pairs) =>
        ListModule.OfSeq(pairs.Select(pair => Tuple.Create(pair.Key, pair.Value)));
}
