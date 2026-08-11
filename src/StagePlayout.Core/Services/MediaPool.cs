using StagePlayout.Core.Models;

namespace StagePlayout.Core.Services;

/// <summary>
/// Gere quais os cues com decoder ativo.
/// Princípio retirado do vMix/PlayDeck: apenas o cue atual e os N seguintes
/// têm decoder aberto e primeiro frame pronto (pre-roll); os restantes são
/// apenas metadata + thumbnail. Mantém RAM/GPU estáveis com 100+ clips.
/// </summary>
public class MediaPool
{
    /// <summary>Quantos cues à frente do atual ficam pré-carregados.</summary>
    public int PreloadAhead { get; set; } = 2;

    // Próxima iteração: object -> IVideoSource (Flyleaf) com texturas D3D11 em pool.
    private readonly Dictionary<Guid, object> _preloaded = new();

    public int PreloadedCount => _preloaded.Count;

    public void UpdateWindow(Playlist playlist)
    {
        var keep = new HashSet<Guid>();

        for (var i = 0; i <= PreloadAhead; i++)
        {
            var cue = playlist.PeekNext(i);
            if (cue is null) continue;

            keep.Add(cue.Id);
            if (!_preloaded.ContainsKey(cue.Id))
            {
                // TODO: abrir decoder, decodificar o 1.º frame, pausar (pre-roll)
                _preloaded[cue.Id] = new object();
            }
        }

        foreach (var id in _preloaded.Keys.ToList())
        {
            if (keep.Contains(id)) continue;
            // TODO: dispose do decoder e libertar texturas GPU
            _preloaded.Remove(id);
        }
    }
}
