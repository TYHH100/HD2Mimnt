using System.Text.Json;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>上游 <c>ias_body_types</c>/<c>ias_stock_recipes</c> 的只读数据表。</summary>
public static class IasBodyCatalog
{
    public static readonly IReadOnlyDictionary<string, string> NativeBodyTypes = Load("ias_body_types.json", "native");
    public static readonly IReadOnlyDictionary<string, string> NativeSourceBodyTypes = Load("ias_body_types.json", "source");
    public static readonly string StockRecipeMetadataSha256 = LoadScalar("ias_stock_recipes.json", "metadata_sha256");
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string Stocky, string Slim)>> StockRecipes = LoadRecipes();

    private static IReadOnlyDictionary<string, string> Load(string file, string property)
    {
        using var document = JsonDocument.Parse(Read(file));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty(property).EnumerateObject())
            result[item.Name] = item.Value.GetString()!;
        return result;
    }

    private static string LoadScalar(string file, string property)
    {
        using var document = JsonDocument.Parse(Read(file));
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string, string)>> LoadRecipes()
    {
        using var document = JsonDocument.Parse(Read("ias_stock_recipes.json"));
        var result = new Dictionary<string, IReadOnlyDictionary<string, (string, string)>>(StringComparer.Ordinal);
        foreach (var archive in document.RootElement.GetProperty("recipes").EnumerateObject())
        {
            var looks = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var look in archive.Value.EnumerateObject())
                looks[look.Name] = (look.Value[0].GetString()!, look.Value[1].GetString()!);
            result[archive.Name] = looks;
        }
        return result;
    }

    private static byte[] Read(string file)
    {
        using var stream = typeof(IasBodyCatalog).Assembly.GetManifestResourceStream(
            "HD2Mimnt.Infrastructure.ModelConversion.Data." + file)
            ?? throw new InvalidOperationException("缺少内置数据：" + file);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
