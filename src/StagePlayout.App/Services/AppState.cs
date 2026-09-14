using System.IO;
using System.Text.Json;

namespace StagePlayout.App.Services;

/// <summary>
/// Estado persistente da app: posição/tamanho da janela + último projeto aberto.
/// Guardado em %APPDATA%\SmartPlayCue\appstate.json.
/// </summary>
public class AppState
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1260;
    public double Height { get; set; } = 680;
    public bool Maximized { get; set; }
    public string LastProjectPath { get; set; } = "";

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartPlayCue", "appstate.json");

    public static AppState Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppState();
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path)) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // estado é conveniência — nunca falhar
        }
    }
}
