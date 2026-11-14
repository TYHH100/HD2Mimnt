using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class Hd2ClOptionImageTests
{
    [Fact]
    public void CopyOptionImages_CopiesDeclaredAndFallbackIconsAndRewritesPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2cl-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(root, "components/0/group.png");
            Write(root, "components/0/hair/Black1/icon.png");
            Write(root, "components/0/hair/Black2/icon.jpg");
            Write(root, "components/0/stockings/icon.png");
            var groups = new[]
            {
                new Hd2ClOptionGroup("头发换色", null, "components/0/group.png", true, 0,
                [
                    new Hd2ClOptionChoice("Black1", "components/0/hair/Black1/icon.png", "components/0/hair/Black1"),
                    new Hd2ClOptionChoice("Black2", null, "components/0/hair/Black2"),
                    new Hd2ClOptionChoice("Plain", null, "components/0/hair/Plain")
                ]),
                new Hd2ClOptionGroup("丝袜换色", null, null, true, 0,
                [
                    new Hd2ClOptionChoice("黑色丝袜", null, "components/0/stockings")
                ])
            };
            var output = Path.Combine(root, "out");

            var result = Hd2ClArtifactBuilder.CopyOptionImages(root, output, groups, CancellationToken.None);

            var group = result[0];
            Assert.Equal("Material/1/icon.png", group.Image);
            Assert.Equal(["Material/1/1/icon.png", "Material/1/2/icon.jpg", null],
                group.Choices.Select(choice => choice.Image));
            Assert.True(File.Exists(Path.Combine(output, "Material", "1", "1", "icon.png")));
            Assert.True(File.Exists(Path.Combine(output, "Material", "1", "2", "icon.jpg")));
            Assert.False(Directory.Exists(Path.Combine(output, "Material", "1", "3")));

            // 单分支组在清单里是普通开关，缩略图从唯一分支目录补。
            Assert.Equal("Material/2/icon.png", result[1].Image);

            // 清单里的图片路径必须与复制后的成品内位置一致，否则游戏面板取不到缩略图。
            var json = IasLua.WriteJson(IasModelPackager.OptionManifest(result), ensureAscii: false,
                sortKeys: false, indent: 2);
            Assert.Contains("Material/1/2/icon.jpg", json);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }
}
