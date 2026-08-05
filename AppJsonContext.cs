using System.Text.Json.Serialization;

namespace RetroGameCard;

// game card
[JsonSerializable(typeof(Card))]
[JsonSerializable(typeof(RetroArchConfig))]
[JsonSerializable(typeof(MisterFpgaConfig))]

// launcher config
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(MainSettings))]
[JsonSerializable(typeof(RetroArchSettings))]

internal partial class AppJsonContext : JsonSerializerContext
{
}