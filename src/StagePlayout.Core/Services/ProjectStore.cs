using System.IO;
using System.Text.Json;
using StagePlayout.Core.Models;

namespace StagePlayout.Core.Services;

/// <summary>
/// Guardar/carregar projetos (playlist + opções por cue + presets de output + layers)
/// em JSON. Se o ficheiro principal estiver corrompido, tenta automaticamente o
/// auto-backup (`<projeto>.stageplayout.json.bak`) antes de falhar.
/// </summary>
public static class ProjectStore
{
    public sealed record LayerStateDto(
        string File, double X, double Y, double W, double H, bool Muted, bool BlendAdd);

    private record CueDto(Guid Id, string Name, string FilePath, string End,
                          double FadeInSeconds, double FadeOutSeconds, double Volume,
                          bool IsGroup, bool IsExpanded, Guid? ParentId, bool LoopGroup,
                          string FadeType = "Cross");

    private record ProjectDto(List<CueDto> Cues, string OutputDevice, int OutputRefresh,
                              List<LayerStateDto>? Layers);

    public static void Save(Playlist playlist, string path, List<LayerStateDto>? layers = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        var dtos = playlist.Cues.Select(c => new CueDto(
            c.Id, c.Name, MakePortable(c.FilePath, dir), c.End.ToString(),
            c.FadeInSeconds, c.FadeOutSeconds, c.Volume,
            c.IsGroup, c.IsExpanded, c.ParentId, c.LoopGroup,
            c.FadeType.ToString())).ToList();

        var portableLayers = layers?
            .Select(l => l with { File = MakePortable(l.File, dir) })
            .ToList();

        var project = new ProjectDto(dtos, playlist.OutputDevice, playlist.OutputRefresh, portableLayers);
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Media dentro da pasta do projeto é gravada como caminho RELATIVO —
    /// o show pode ser movido (pen/outro PC) sem quebrar as referências.
    /// </summary>
    private static string MakePortable(string filePath, string projectDir)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(projectDir))
            return filePath;
        try
        {
            var rel = Path.GetRelativePath(projectDir, Path.GetFullPath(filePath));
            return rel.StartsWith("..") ? filePath : rel; // fora da árvore: absoluto
        }
        catch
        {
            return filePath;
        }
    }

    /// <summary>Carrega o projeto; em falha (JSON corrompido), tenta `path + ".bak"`.</summary>
    public static void Load(Playlist playlist, string path,
                            Action<List<LayerStateDto>?>? layers = null)
    {
        Exception? firstError = null;
        try
        {
            LoadCore(playlist, path, layers);
            return;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            firstError = ex;
        }

        var bak = path + ".bak";
        if (File.Exists(bak))
        {
            try
            {
                LoadCore(playlist, bak, layers);
                return;
            }
            catch
            {
                // o backup também falhou — reportar o erro original
            }
        }

        throw firstError ?? new FileNotFoundException("Projeto não encontrado", path);
    }

    private static void LoadCore(Playlist playlist, string path, Action<List<LayerStateDto>?>? layers)
    {
        var json = File.ReadAllText(path);
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        List<CueDto> dtos;
        string outputDevice = "";
        var outputRefresh = 0;
        List<LayerStateDto>? layerStates = null;

        try
        {
            var project = JsonSerializer.Deserialize<ProjectDto>(json);
            if (project is not null)
            {
                dtos = project.Cues ?? new();
                outputDevice = project.OutputDevice ?? "";
                outputRefresh = project.OutputRefresh;
                layerStates = project.Layers;
            }
            else
            {
                dtos = new();
            }
        }
        catch (JsonException)
        {
            // formato antigo (lista simples de cues)
            dtos = JsonSerializer.Deserialize<List<CueDto>>(json) ?? new();
        }

        playlist.Cues.Clear();
        foreach (var d in dtos)
        {
            playlist.Add(new Cue
            {
                Id = d.Id,
                Name = d.Name,
                FilePath = ResolvePath(d.FilePath, projectDir),
                End = Enum.TryParse<CueEnd>(d.End, out var end) ? end : CueEnd.HoldLastFrame,
                FadeInSeconds = d.FadeInSeconds,
                FadeOutSeconds = d.FadeOutSeconds,
                Volume = d.Volume,
                IsGroup = d.IsGroup,
                IsExpanded = d.IsExpanded,
                ParentId = d.ParentId,
                LoopGroup = d.LoopGroup,
                FadeType = Enum.TryParse<FadeType>(d.FadeType, out var ft) ? ft : FadeType.Cross,
            });
        }
        playlist.RefreshChildCounts();
        playlist.OutputDevice = outputDevice;
        playlist.OutputRefresh = outputRefresh;

        if (layerStates is not null)
            layers?.Invoke(layerStates.Select(l => l with { File = ResolvePath(l.File, projectDir) }).ToList());
        else
            layers?.Invoke(null);
    }

    /// <summary>
    /// Resolve caminhos guardados: relativos → contra a pasta do projeto;
    /// absolutos em falta → tenta o ficheiro na pasta do projeto (show movido).
    /// </summary>
    private static string ResolvePath(string stored, string projectDir)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        try
        {
            if (!Path.IsPathRooted(stored))
                return Path.GetFullPath(Path.Combine(projectDir, stored));

            if (!File.Exists(stored) && projectDir.Length > 0)
            {
                var byName = Path.Combine(projectDir, Path.GetFileName(stored));
                if (File.Exists(byName))
                    return byName;
            }
        }
        catch
        {
            // mantém o caminho original
        }
        return stored;
    }
}
