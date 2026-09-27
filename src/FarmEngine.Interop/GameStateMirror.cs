using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>
/// A managed copy of a <see cref="RustSession"/>'s live state for the C# views (HUD, overlays,
/// renderer), kept current from <see cref="RustSession.StateChanges"/> without a whole-state
/// round trip per frame.
/// <para>
/// Structural sharing: a section whose JSON did not change keeps the very object it had, so
/// views that detect changes by reference (the dialogue and minigame overlays) or by record
/// equality over lists (the shop and panel signatures) behave exactly as they do over the C#
/// engine's immutable states. Top-level sections come from Rust only when they changed; the
/// <c>player</c> section is also split by property here, so walking (new x/y) keeps the
/// inventory, skills and quest lists as they were.
/// </para>
/// <para>Not thread-safe: one session, one thread.</para>
/// </summary>
public sealed class GameStateMirror
{
    private readonly ObjectMirror _root;

    public GameStateMirror()
    {
        _root = new ObjectMirror(typeof(GameState), new Dictionary<string, ObjectMirror>
        {
            ["player"] = new ObjectMirror(typeof(PlayerState), null),
        });
        State = new GameState();
    }

    /// <summary>The mirrored state. Its reference changes only when some section changed.</summary>
    public GameState State { get; private set; }

    /// <summary>
    /// Applies one <see cref="RustSession.StateChanges"/> object: listed sections are replaced
    /// (keeping unchanged parts), the others stay as they were. Returns true when
    /// <see cref="State"/> is a new object.
    /// </summary>
    public bool Apply(ReadOnlySpan<byte> changes)
    {
        var next = (GameState)_root.Update(changes, partial: true)!;
        if (ReferenceEquals(next, State))
        {
            return false;
        }

        State = next;
        return true;
    }

    /// <summary>
    /// One object type split by property: each property's last raw JSON and value, reused while
    /// the JSON stays byte-identical.
    /// </summary>
    private sealed class ObjectMirror
    {
        private readonly JsonTypeInfo _info;
        private readonly Dictionary<string, JsonPropertyInfo> _properties = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, ObjectMirror>? _children;
        private readonly Dictionary<string, (byte[] Raw, object? Value)> _last = new(StringComparer.Ordinal);
        private object? _value;

        public ObjectMirror(Type type, IReadOnlyDictionary<string, ObjectMirror>? children)
        {
            _info = JsonDefaults.Options.GetTypeInfo(type);
            _children = children;
            if (_info.Kind != JsonTypeInfoKind.Object || _info.CreateObject is null)
            {
                throw new InvalidOperationException($"{type.Name} cannot be mirrored by property: it is not a plain JSON object.");
            }

            foreach (var property in _info.Properties)
            {
                // Reading a property's value on its own must equal reading it inside the object.
                if (property.IsExtensionData || property.CustomConverter is not null || property.NumberHandling is not null || property.Set is null)
                {
                    throw new InvalidOperationException($"{type.Name}.{property.Name} cannot be mirrored on its own.");
                }

                _properties[property.Name] = property;
            }
        }

        /// <summary>
        /// Reads <paramref name="json"/> (an object). <paramref name="partial"/>: absent
        /// properties keep their last value; otherwise they fall back to the type's defaults, as
        /// deserializing would. Returns the previous object when nothing changed.
        /// </summary>
        public object? Update(ReadOnlySpan<byte> json, bool partial)
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                // Not an object (null): no structure to share.
                _last.Clear();
                _value = JsonSerializer.Deserialize(json, _info);
                return _value;
            }

            var changed = _value is null;
            var present = partial ? null : new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                var raw = json[start..(int)reader.BytesConsumed];
                if (!_properties.TryGetValue(name, out var property))
                {
                    // Unknown members are ignored, as the serializer does.
                    continue;
                }

                present?.Add(name);
                if (_last.TryGetValue(name, out var last) && raw.SequenceEqual(last.Raw))
                {
                    continue;
                }

                var value = _children is not null && _children.TryGetValue(name, out var child)
                    ? child.Update(raw, partial: false)
                    : JsonSerializer.Deserialize(raw, property.PropertyType, JsonDefaults.Options);
                _last[name] = (raw.ToArray(), value);
                changed = true;
            }

            if (present is not null)
            {
                foreach (var name in _last.Keys.Where(name => !present.Contains(name)).ToList())
                {
                    _last.Remove(name);
                    changed = true;
                }
            }

            if (!changed)
            {
                return _value;
            }

            var result = _info.CreateObject!();
            foreach (var (name, entry) in _last)
            {
                _properties[name].Set!(result, entry.Value);
            }

            _value = result;
            return result;
        }
    }
}
