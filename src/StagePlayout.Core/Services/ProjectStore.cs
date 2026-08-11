using System.IO;
using System.Text.Json;
using StagePlayout.Core.Models;

namespace StagePlayout.Core.Services;

/// <summary>
/// Guardar/carregar projetos (playlist + opções por cue) em JSON.
/// </summary>
public static class ProjectStore
{
    private record CueDto(Guid Id, string Name, string FilePath, string End,
                          double FadeInSeconds, double FadeOutSeconds, double Volume,
                          bool IsGroup, bool IsExpanded, Guid? ParentId, bool LoopGroup);

    public static void Save(Playlist playlist, string path)
    {
        var dtos = playlist.Cues.Select(c => new CueDto(
            c.Id, c.Name, c.FilePath, c.End.ToString(),
            c.FadeInSeconds, c.FadeOutSeconds, c.Volume,
            c.IsGroup, c.IsExpanded, c.ParentId, c.LoopGroup)).ToList();

        var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static void Load(Playlist playlist, string path)
    {
        var json = File.ReadAllText(path);
        var dtos = JsonSerializer.Deserialize<List<CueDto>>(json) ?? new();

        playlist.Cues.Clear();
        foreach (var d in dtos)
        {
            playlist.Add(new Cue
            {
                Id = d.Id,
                Name = d.Name,
                FilePath = d.FilePath,
                End = Enum.TryParse<CueEnd>(d.End, out var end) ? end : CueEnd.HoldLastFrame,
                FadeInSeconds = d.FadeInSeconds,
                FadeOutSeconds = d.FadeOutSeconds,
                Volume = d.Volume,
                IsGroup = d.IsGroup,
                IsExpanded = d.IsExpanded,
                ParentId = d.ParentId,
                LoopGroup = d.LoopGroup,
            });
        }
        playlist.RefreshChildCounts();
    }
}
