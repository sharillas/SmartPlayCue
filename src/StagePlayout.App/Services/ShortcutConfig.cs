using System.IO;
using System.Text.Json;

namespace StagePlayout.App.Services;

/// <summary>
/// Atalhos de teclado configuráveis via shortcuts.json (pasta do executável).
/// Valores = nomes de teclas WPF (ex.: "Space", "S", "F1", "Left"...).
/// O ficheiro é criado com os defaults no primeiro arranque — editar e reiniciar a app.
/// </summary>
public class ShortcutConfig
{
    public string Go { get; set; } = "Space";
    public string Next { get; set; } = "Right";
    public string Previous { get; set; } = "Left";
    public string Stop { get; set; } = "S";
    public string Pause { get; set; } = "P";

    public static ShortcutConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "shortcuts.json");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(
                    new ShortcutConfig(), new JsonSerializerOptions { WriteIndented = true }));
                return new ShortcutConfig();
            }

            return JsonSerializer.Deserialize<ShortcutConfig>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new ShortcutConfig();
        }
    }
}
