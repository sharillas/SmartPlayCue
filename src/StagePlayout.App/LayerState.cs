using StagePlayout.App.Video;

namespace StagePlayout.App;

/// <summary>Estado interno de uma layer (ficheiro, geometria, mute, blend, decoder).</summary>
public sealed class LayerState
{
    public FFDecoder? Decoder;
    public string? File;
    public bool Visible;
    public bool Muted = true; // layers começam mudas (logos/lower-thirds)
    public bool BlendAdd;     // false = alpha normal, true = aditivo (Add)
    public double X, Y, W, H;
}
