using System.Text.Json;

namespace CiFail.Core.Ai;

/// <summary>Shared helper for reading an embedding vector out of a JSON number array.</summary>
public static class EmbeddingJson
{
    /// <summary>
    /// Convert a JSON array element of numbers into a float[]; returns null if the element isn't
    /// a non-empty array.
    /// </summary>
    public static float[]? ToFloats(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array)
            return null;

        var vector = new float[arr.GetArrayLength()];
        var i = 0;
        foreach (var n in arr.EnumerateArray())
            vector[i++] = n.GetSingle();
        return vector.Length > 0 ? vector : null;
    }
}
