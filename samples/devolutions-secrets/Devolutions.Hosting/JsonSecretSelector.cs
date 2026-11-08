using System.Globalization;
using System.Text.Json;

namespace Devolutions.Hosting;

internal static class JsonSecretSelector
{
    public static void Validate(string pointer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointer);
        if (!pointer.StartsWith('/'))
        {
            throw new ArgumentException("A secret selector must be a JSON Pointer beginning with '/'.", nameof(pointer));
        }

        for (var i = 0; i < pointer.Length; i++)
        {
            if (pointer[i] == '~' && (++i >= pointer.Length || pointer[i] is not ('0' or '1')))
            {
                throw new ArgumentException("JSON Pointer escapes must be '~0' or '~1'.", nameof(pointer));
            }
        }
    }

    public static string Read(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(key, out var property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array
                && (key == "0" || (key.Length > 0 && key[0] is >= '1' and <= '9'))
                && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                throw new InvalidOperationException("The configured JSON Pointer was not found in the credential entry.");
            }
        }

        if (current.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(current.GetString()))
        {
            throw new InvalidOperationException("The configured credential field must contain a non-empty string.");
        }

        return current.GetString()!;
    }
}
