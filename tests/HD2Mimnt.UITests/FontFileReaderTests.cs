using System;
using System.IO;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UITests;

public class FontFileReaderTests
{
    private static string LooseFontPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Monocraft.ttf");

    [Fact]
    public void ReadsFamilyNameFromLooseFontFile()
    {
        Assert.True(File.Exists(LooseFontPath));
        Assert.Equal("Monocraft", FontFileReader.TryReadFamilyName(LooseFontPath));
    }

    [Fact]
    public void ReturnsNullForNonFontContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a font file");
            Assert.Null(FontFileReader.TryReadFamilyName(path));
            Assert.Null(FontFileReader.TryReadFamilyName(Path.Combine(Path.GetTempPath(), "missing-font.ttf")));
        }
        finally { File.Delete(path); }
    }
}
