using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/social.ts.
// NPC relationships & gifting (M4d).

public sealed record GiftTastes
{
    public List<string> Loved { get; init; } = [];
    public List<string> Liked { get; init; } = [];
    public List<string> Disliked { get; init; } = [];
    public List<string> Hated { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Per-NPC social state (friendship points etc.).</summary>
public sealed record NpcSocialState
{
    public double Friendship { get; init; } = 0;
    /// <summary>int.</summary>
    public double GiftsToday { get; init; } = 0;
    /// <summary>int.</summary>
    public double? LastGiftDay { get; init; }
}

/// <summary>TS <c>GiftReaction</c> (<c>keyof typeof GIFT_FRIENDSHIP_DELTAS</c>).</summary>
public static class GiftReactions
{
    public const string Loved = "loved";
    public const string Liked = "liked";
    public const string Neutral = "neutral";
    public const string Disliked = "disliked";
    public const string Hated = "hated";

    public static readonly IReadOnlyList<string> All = [Loved, Liked, Neutral, Disliked, Hated];
}

public static class SocialSchema
{
    public const double FriendshipPerHeart = 125;
    public const double MaxFriendship = 1250;

    /// <summary>TS <c>GIFT_FRIENDSHIP_DELTAS</c>, keyed by <see cref="GiftReactions"/> (declaration order kept).</summary>
    public static readonly IReadOnlyDictionary<string, double> GiftFriendshipDeltas =
        new ReadOnlyDictionary<string, double>(new OrderedDictionary<string, double>
        {
            [GiftReactions.Loved] = 80,
            [GiftReactions.Liked] = 45,
            [GiftReactions.Neutral] = 20,
            [GiftReactions.Disliked] = -20,
            [GiftReactions.Hated] = -40,
        });
}
