using System.Text.Json;
using StagePlayout.Core.Models;
using StagePlayout.Core.Services;

namespace StagePlayout.Tests;

public class ProjectStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"spt_test_{Guid.NewGuid():N}.stageplayout.json");

    private static Cue MakeCue(string name, string file) => new() { Name = name, FilePath = file };

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var pl = new Playlist();
        var a = MakeCue("intro", @"C:\media\intro.mp4");
        a.FadeInSeconds = 1;
        a.FadeOutSeconds = 2;
        var b = MakeCue("body", @"C:\media\body.mov");
        pl.Add(a); pl.Add(b);
        pl.GroupSelection("grupo", new[] { a, b });
        a.End = CueEnd.Loop; // o fim é definido depois do agrupamento
        a.FadeType = FadeType.Dip;
        pl.OutputDevice = @"\\.\DISPLAY2";
        pl.OutputRefresh = 50;

        var path = TempFile();
        try
        {
            ProjectStore.Save(pl, path);

            var loaded = new Playlist();
            ProjectStore.Load(loaded, path);

            Assert.Equal(pl.Cues.Count, loaded.Cues.Count);
            // ordem: grupo, intro, body (o grupo é inserido antes do 1.º membro)
            var group = loaded.Cues.Single(c => c.IsGroup);
            var intro = loaded.Cues.Single(c => c.Name == "intro");
            var body = loaded.Cues.Single(c => c.Name == "body");
            Assert.Equal("grupo", group.Name);
            Assert.Equal(@"C:\media\intro.mp4", intro.FilePath);
            Assert.Equal(CueEnd.Loop, intro.End);
            Assert.Equal(FadeType.Dip, intro.FadeType);
            Assert.Equal(1, intro.FadeInSeconds);
            Assert.Equal(2, intro.FadeOutSeconds);
            Assert.Equal(@"C:\media\body.mov", body.FilePath);
            Assert.Equal(pl.Cues.First(c => c.IsGroup).Id, group.Id);
            Assert.Equal(group.Id, intro.ParentId); // ligação de grupo restaurada
            Assert.Equal(group.Id, body.ParentId);
            Assert.Equal(@"\\.\DISPLAY2", loaded.OutputDevice);
            Assert.Equal(50, loaded.OutputRefresh);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyFormat_ListOfCues()
    {
        // formato antigo (v1.x): lista simples de cues, sem wrapper de projeto
        var legacy = JsonSerializer.Serialize(new[]
        {
            new { Id = Guid.NewGuid(), Name = "old.mp4", FilePath = @"C:\media\old.mp4", End = "Stop",
                 FadeInSeconds = 0.5, FadeOutSeconds = 0.5, Volume = 1.0,
                 IsGroup = false, IsExpanded = true, ParentId = (Guid?)null, LoopGroup = false },
        });
        var path = TempFile();
        File.WriteAllText(path, legacy);
        try
        {
            var loaded = new Playlist();
            ProjectStore.Load(loaded, path);

            Assert.Single(loaded.Cues);
            Assert.Equal("old.mp4", loaded.Cues[0].Name);
            Assert.Equal(CueEnd.Stop, loaded.Cues[0].End);
            Assert.Equal("", loaded.OutputDevice);
            Assert.Equal(0, loaded.OutputRefresh);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_Missing_File_Throws()
    {
        var loaded = new Playlist();
        Assert.ThrowsAny<Exception>(() => ProjectStore.Load(loaded, @"C:\nao_existe.stageplayout.json"));
    }

    [Fact]
    public void Save_Load_Roundtrip_Layers()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a", @"C:\media\a.mp4"));

        var layers = new List<ProjectStore.LayerStateDto>
        {
            new(@"C:\media\logo.mov", 0.68, 0.66, 0.28, 0.28, Muted: true, BlendAdd: false),
            new(@"C:\media\glow.mov", 0.1, 0.1, 0.5, 0.5, Muted: false, BlendAdd: true),
        };

        var path = TempFile();
        try
        {
            ProjectStore.Save(pl, path, layers);

            var loaded = new Playlist();
            List<ProjectStore.LayerStateDto>? got = null;
            ProjectStore.Load(loaded, path, l => got = l);

            Assert.NotNull(got);
            Assert.Equal(2, got!.Count);
            Assert.Equal(@"C:\media\logo.mov", got[0].File);
            Assert.Equal(0.68, got[0].X, 3);
            Assert.Equal(0.28, got[0].W, 3);
            Assert.True(got[0].Muted);
            Assert.False(got[0].BlendAdd);
            Assert.True(got[1].BlendAdd);
            Assert.False(got[1].Muted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_Corrupt_Falls_Back_To_Bak()
    {
        var path = TempFile();
        var bak = path + ".bak";
        try
        {
            // backup válido
            ProjectStore.Save(CreatePlaylist(), bak);

            // principal corrompido
            File.WriteAllText(path, "{ isto nao e json valido");

            var loaded = new Playlist();
            ProjectStore.Load(loaded, path);

            Assert.Single(loaded.Cues);
            Assert.Equal("clip", loaded.Cues[0].Name);
        }
        finally
        {
            File.Delete(path);
            File.Delete(bak);
        }
    }

    [Fact]
    public void Save_Makes_Media_Paths_Portable_Inside_Project_Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spt_proj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var pl = new Playlist();
            var cue = MakeCue("clip", Path.Combine(dir, "media", "clip.mp4"));
            Directory.CreateDirectory(Path.Combine(dir, "media"));
            File.WriteAllText(cue.FilePath, "x");
            pl.Add(cue);
            // ficheiro fora da árvore do projeto: mantém absoluto
            var outside = Path.Combine(Path.GetTempPath(), $"spt_ext_{Guid.NewGuid():N}.mp4");
            File.WriteAllText(outside, "x");
            pl.Add(MakeCue("ext", outside));

            var path = Path.Combine(dir, "show.stageplayout.json");
            ProjectStore.Save(pl, path);

            // o JSON deve guardar o caminho RELATIVO para media dentro da árvore
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var cueDtos = doc.RootElement.GetProperty("Cues").EnumerateArray().ToList();
            var storedClip = cueDtos.Single(c => c.GetProperty("Name").GetString() == "clip")
                                   .GetProperty("FilePath").GetString();
            Assert.Equal("media" + Path.DirectorySeparatorChar + "clip.mp4", storedClip);

            var loaded = new Playlist();
            ProjectStore.Load(loaded, path);
            var clip = loaded.Cues.Single(c => c.Name == "clip");
            Assert.Equal(cue.FilePath, clip.FilePath, ignoreCase: true); // resolvido de volta
            var ext = loaded.Cues.Single(c => c.Name == "ext");
            Assert.Equal(outside, ext.FilePath, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static Playlist CreatePlaylist()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("clip", @"C:\media\clip.mp4"));
        return pl;
    }
}
