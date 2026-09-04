using System.Collections.Generic;
using Questionable.Model.Questing;

namespace Questionable.Data;

internal sealed class FishingData
{
    // Upstream ships this as a lookup of pre-exported AutoHook presets per quest, but every entry is
    // commented out there too (kept only as reference for anyone hand-authoring a preset later) - presets
    // are generated on the fly per-quest via IFishingPresetGenerator instead, so this stays empty.
    public static readonly IReadOnlyDictionary<QuestId, string> FishingPresets = new Dictionary<QuestId, string>();
}
