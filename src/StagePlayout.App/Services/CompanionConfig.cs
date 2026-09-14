using System.IO;
using System.Text.Json;

namespace StagePlayout.App.Services;

/// <summary>
/// Configuração OSC/Companion via companion.json (pasta do executável).
/// Criado com os defaults no primeiro arranque.
///
/// O feedback (tempo restante HH/MM/SS) é enviado para FeedbackHost:FeedbackPort —
/// tipicamente o IP da máquina onde corre o Bitfocus Companion (se o Companion
/// correr noutra máquina, mudar FeedbackHost para o IP dessa máquina).
/// </summary>
public class CompanionConfig
{
    /// <summary>Porta UDP onde a app ouve comandos OSC (Companion envia para aqui).</summary>
    public int ListenPort { get; set; } = 8010;

    /// <summary>IP para onde a app envia o feedback (tempo restante).</summary>
    public string FeedbackHost { get; set; } = "127.0.0.1";

    /// <summary>Porta onde o módulo Companion escuta o feedback.</summary>
    public int FeedbackPort { get; set; } = 8011;

    public static CompanionConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "companion.json");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(
                    new CompanionConfig(), new JsonSerializerOptions { WriteIndented = true }));
                return new CompanionConfig();
            }

            return JsonSerializer.Deserialize<CompanionConfig>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new CompanionConfig();
        }
    }
}
