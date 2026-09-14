namespace StagePlayout.Core.Models;

/// <summary>Tipo de transição quando um cue entra no ar.</summary>
public enum FadeType
{
    /// <summary>Dissolve sobreposto (A→B em simultâneo).</summary>
    Cross,

    /// <summary>Fade através de preto (outgoing desce primeiro, incoming entra depois).</summary>
    Dip,
}
