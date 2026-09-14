using StagePlayout.Core.Models;

namespace StagePlayout.Tests;

public class PlaylistTests
{
    private static Cue MakeCue(string name = "clip") => new() { Name = name, FilePath = $"C:\\media\\{name}.mp4" };

    [Fact]
    public void Add_Renumbers_DisplayIds()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        pl.Add(MakeCue("b"));
        pl.Add(MakeCue("c"));

        Assert.Equal(new[] { 1, 2, 3 }, pl.Cues.Select(c => c.DisplayId));
    }

    [Fact]
    public void Go_Advances_And_Clamps_At_Last()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        pl.Add(MakeCue("b"));

        Assert.Equal("a", pl.Go()!.Name);
        Assert.Equal("b", pl.Go()!.Name);
        Assert.Equal("b", pl.Go()!.Name); // no fim: mantém o último
    }

    [Fact]
    public void Go_Empty_Returns_Null()
    {
        var pl = new Playlist();
        Assert.Null(pl.Go());
    }

    [Fact]
    public void Previous_Stops_At_First()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        pl.Add(MakeCue("b"));
        pl.Select(1);

        Assert.Equal("a", pl.Previous()!.Name);
        Assert.Equal("a", pl.Previous()!.Name);
    }

    [Fact]
    public void PeekNext_Offsets_From_Current()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        pl.Add(MakeCue("b"));
        pl.Add(MakeCue("c"));
        pl.Select(0);

        Assert.Equal("a", pl.PeekNext(0)!.Name); // offset 0 = atual
        Assert.Equal("b", pl.PeekNext(1)!.Name);
        Assert.Equal("c", pl.PeekNext(2)!.Name);
        Assert.Null(pl.PeekNext(3));
    }

    [Fact]
    public void Remove_Adjusts_CurrentIndex()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        pl.Add(MakeCue("b"));
        pl.Add(MakeCue("c"));
        pl.Select(2);

        pl.Remove(pl.Cues[1]);
        Assert.Equal("c", pl.Current!.Name);
        Assert.Equal(1, pl.CurrentIndex);
    }

    [Fact]
    public void Move_Group_Moves_Whole_Block()
    {
        var pl = new Playlist();
        var a = MakeCue("a");
        var b = MakeCue("b");
        var c = MakeCue("c");
        pl.Add(a); pl.Add(b); pl.Add(c);

        var group = pl.GroupSelection("g", new[] { a, b });
        // ordem: g, a, b, c — mover g depois de c
        pl.Move(group, c, insertAfter: true);

        Assert.Equal(new[] { "c", "g", "a", "b" }, pl.Cues.Select(x => x.Name));
    }

    [Fact]
    public void GroupSelection_Chains_AutoContinue()
    {
        var pl = new Playlist();
        var a = MakeCue("a");
        var b = MakeCue("b");
        var c = MakeCue("c");
        pl.Add(a); pl.Add(b); pl.Add(c);

        var group = pl.GroupSelection("g", new[] { a, b });

        Assert.True(group.IsGroup);
        Assert.Equal(CueEnd.AutoContinue, a.End);   // 1.º filho encadeia
        Assert.Equal(CueEnd.HoldLastFrame, b.End);  // último mantém o seu fim
        Assert.Equal(group.Id, a.ParentId);
        Assert.Null(c.ParentId);
    }

    [Fact]
    public void Ungroup_Keeps_Clips_As_TopLevel()
    {
        var pl = new Playlist();
        var a = MakeCue("a");
        var b = MakeCue("b");
        pl.Add(a); pl.Add(b);

        var group = pl.GroupSelection("g", new[] { a, b });
        pl.Ungroup(group);

        Assert.DoesNotContain(pl.Cues, c => c.IsGroup);
        Assert.Null(a.ParentId);
        Assert.Null(b.ParentId);
    }

    [Fact]
    public void CurrentChanged_Fires_On_Select()
    {
        var pl = new Playlist();
        pl.Add(MakeCue("a"));
        var fired = 0;
        pl.CurrentChanged += (_, _) => fired++;

        pl.Select(0);
        pl.Deselect();

        Assert.Equal(2, fired);
    }
}
