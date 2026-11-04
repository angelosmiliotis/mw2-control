using System.Buffers.Binary;
using System.Globalization;

namespace MW2Control.Core;

/// <summary>Value drift does not establish who wrote it; diagnostics must not accuse another tool.</summary>
internal static class ConflictDiagnostics
{
    internal static string Tuning(TuningFeature feature, byte[] observed, byte[] expected,
        string expectedDescription, string? operation, bool explicitRetryAllowed = false) => Describe(
            feature == TuningFeature.Fov ? "cg_fov (FOV)" : "com_maxfps (FPS cap)",
            FormatTuning(feature, observed), FormatTuning(feature, expected), expectedDescription, operation, explicitRetryAllowed);

    internal static string Advanced(AdvancedDvar feature, byte[] observed, byte[] expected,
        string expectedDescription, string? operation, bool explicitRetryAllowed = false) => Describe(feature switch
        {
            AdvancedDvar.StreamEnabled => "snd_enableStream (Mute streamed sounds)",
            AdvancedDvar.Fullbright => "r_fullbright (Fullbright)",
            AdvancedDvar.Fog => "r_fog (No fog)",
            AdvancedDvar.Glow => "r_glow (No glow)",
            AdvancedDvar.SpecularMap => "r_specularMap (Specular mode)",
            AdvancedDvar.Detail => "r_detail (Detail overlays)",
            AdvancedDvar.DetailMap => "r_detailMap (Detail-map mode)",
        AdvancedDvar.PingText => "cg_scoreboardPingText (Numeric ping)",
        AdvancedDvar.PingGraph => "cg_scoreboardPingGraph (Ping bars)",
            _ => feature.ToString()
        }, FormatAdvanced(observed), FormatAdvanced(expected), expectedDescription, operation, explicitRetryAllowed);

    private static string Describe(string feature, string observed, string expected, string expectedDescription, string? operation, bool explicitRetryAllowed) =>
        $"{feature}: observed {observed}; {expectedDescription} {expected}. " +
        (operation is null ? "Read-only inspection detected a value change. " : $"{operation} refused to overwrite the changed value. ") +
        "The game itself may reset values during respawn, map load or settings changes; another writer can also change them. " +
        "Automatic recovery waits for validated targets or recognized reset values in the same session. " +
        (explicitRetryAllowed
            ? "Current bytes exactly match the captured original. Apply or Keep applied may reapply. Restore can clear ownership without writing this value. "
            : "If the conflict persists, restart the game and Apply in the new session. ") +
        "Values may reset again; persistence is not guaranteed.";

    private static string FormatTuning(TuningFeature feature, byte[] raw)
    {
        var bits = BinaryPrimitives.ReadInt32LittleEndian(raw);
        return feature == TuningFeature.Fov
            ? BitConverter.Int32BitsToSingle(bits).ToString("G9", CultureInfo.InvariantCulture)
            : bits.ToString(CultureInfo.InvariantCulture) + (bits == 0 ? " (uncapped)" : "");
    }

    private static string FormatAdvanced(byte[] raw) => (raw.Length == 1 ? raw[0] : BinaryPrimitives.ReadInt32LittleEndian(raw))
        .ToString(CultureInfo.InvariantCulture);
}
