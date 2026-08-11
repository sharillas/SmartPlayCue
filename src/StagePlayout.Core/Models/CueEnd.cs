namespace StagePlayout.Core.Models;

/// <summary>O que acontece quando o clip chega ao fim.</summary>
public enum CueEnd
{
    /// <summary>Fica parado no último frame (default).</summary>
    HoldLastFrame,

    /// <summary>Fade out (com os segundos configurados) e stop.</summary>
    Stop,

    /// <summary>Loop seamless (nativo do decoder).</summary>
    Loop,

    /// <summary>Avança automaticamente para o cue seguinte.</summary>
    AutoContinue,
}
