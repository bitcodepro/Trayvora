using System.IO.Compression;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class PortableUpdateTests
{
    [Fact]
    public void UpdateReplacesProgramFilesAndKeepsData()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-update-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName;
            File.WriteAllText(Path.Combine(app, "Trayvora.exe"), "old");
            Directory.CreateDirectory(Path.Combine(app, "settings", "discussions"));
            File.WriteAllText(Path.Combine(app, "settings", "discussions", "a.json"), "mine");
            Directory.CreateDirectory(Path.Combine(app, "models"));
            File.WriteAllText(Path.Combine(app, "models", "ggml-base.bin"), "model");

            var package = Directory.CreateDirectory(Path.Combine(root.FullName, "package", "Trayvora")).FullName;
            File.WriteAllText(Path.Combine(package, "Trayvora.exe"), "new");
            File.WriteAllText(Path.Combine(package, "Trayvora.dll"), "new dll");
            Directory.CreateDirectory(Path.Combine(package, "settings"));
            File.WriteAllText(Path.Combine(package, "settings", "hotkeys.json"), "theirs");
            Directory.CreateDirectory(Path.Combine(package, "models"));
            File.WriteAllText(Path.Combine(package, "models", "ggml-base.bin"), "model2");
            var zip = Path.Combine(root.FullName, "new.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root.FullName, "package"), zip);

            var staged = PortableUpdate.Stage(zip, app);
            Assert.True(File.Exists(Path.Combine(staged, "Trayvora.exe")));
            PortableUpdate.CopyProgram(staged, app);

            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Trayvora.exe")));
            Assert.Equal("new dll", File.ReadAllText(Path.Combine(app, "Trayvora.dll")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "settings", "discussions", "a.json")));
            Assert.False(File.Exists(Path.Combine(app, "settings", "hotkeys.json")));
            Assert.Equal("model", File.ReadAllText(Path.Combine(app, "models", "ggml-base.bin")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ZipWithoutTrayvoraIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-update-");
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root.FullName, "package")).FullName;
            File.WriteAllText(Path.Combine(package, "readme.txt"), "x");
            var zip = Path.Combine(root.FullName, "other.zip");
            ZipFile.CreateFromDirectory(package, zip);
            Assert.Throws<InvalidDataException>(() => PortableUpdate.Stage(zip, Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
