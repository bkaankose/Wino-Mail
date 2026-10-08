#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wino.Core.ML;

internal static class MagikaAssetLoader
{
    internal const string ModelHash = "FE2D2EB49C5F88A9E0A6C048E15D6FFDF86235519C2AFC535044DE433169EC8C";
    internal const string ConfigurationHash = "AE24C742205358F6FF6DFD5FACB6743FB69743DBBA8373E73DA58FF0CBD695DB";
    internal const string KnowledgeBaseHash = "2788E78D638B1BFF0A0743D6D2EE582BBBBCA6F0C6DC2BE48D62ADA9AF23F760";

    public static string GetDefaultModelDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Models", "Magika", "standard_v3_3");

    public static MagikaModelConfiguration LoadConfiguration(string directory)
    {
        var path = Path.Combine(directory, "config.min.json");
        ValidateHash(path, ConfigurationHash);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var labels = ReadStringArray(root.GetProperty("target_labels_space"));
        var thresholds = ReadDoubleDictionary(root.GetProperty("thresholds"));
        var overwriteMap = ReadStringDictionary(root.GetProperty("overwrite_map"));

        return new MagikaModelConfiguration(
            root.GetProperty("beg_size").GetInt32(),
            root.GetProperty("end_size").GetInt32(),
            root.GetProperty("block_size").GetInt32(),
            root.GetProperty("padding_token").GetInt32(),
            root.GetProperty("min_file_size_for_dl").GetInt32(),
            root.GetProperty("medium_confidence_threshold").GetDouble(),
            labels,
            thresholds,
            overwriteMap);
    }

    public static IReadOnlyDictionary<string, MagikaContentTypeInformation> LoadKnowledgeBase(string directory)
    {
        var path = Path.Combine(directory, "content_types_kb.min.json");
        ValidateHash(path, KnowledgeBaseHash);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new Dictionary<string, MagikaContentTypeInformation>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            result[property.Name] = new MagikaContentTypeInformation(
                ReadNullableString(value, "mime_type"),
                ReadNullableString(value, "group"),
                ReadNullableString(value, "description"),
                ReadStringArray(value.GetProperty("extensions")),
                value.TryGetProperty("is_text", out var isText) && isText.GetBoolean());
        }

        return result;
    }

    public static string ValidateAndGetModelPath(string directory)
    {
        var path = Path.Combine(directory, "model.onnx");
        ValidateHash(path, ModelHash);
        return path;
    }

    private static void ValidateHash(string path, string expectedHash)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("A required Magika model asset is missing.", path);

        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));

        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(string.Format(
                CultureInfo.InvariantCulture,
                "Magika asset integrity validation failed for {0}.",
                Path.GetFileName(path)));
        }
    }

    private static string? ReadNullableString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.GetString();
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
            values.Add(item.GetString() ?? string.Empty);

        return values.ToArray();
    }

    private static Dictionary<string, double> ReadDoubleDictionary(JsonElement element)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.GetDouble();

        return values;
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.GetString() ?? string.Empty;

        return values;
    }
}
