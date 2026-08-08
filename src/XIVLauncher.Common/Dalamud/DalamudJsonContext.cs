using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Dalamud;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DalamudVersionInfo))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class DalamudJsonContext : JsonSerializerContext
{
}
