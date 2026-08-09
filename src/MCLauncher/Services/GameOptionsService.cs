using System.Text;

namespace MCLauncher.Services;

/// <summary>
/// Подготовка options.txt перед первым запуском.
/// По умолчанию Minecraft стартует на английском; здесь мы заранее
/// прописываем русскую локаль, чтобы игроку не пришлось лезть в настройки.
/// Уже существующий файл не трогаем — это выбор пользователя.
/// </summary>
public static class GameOptionsService
{
    /// <summary>
    /// Код языка меняется между версиями:
    /// до 1.10 — "ru_RU", с 1.11 — "ru_ru".
    /// </summary>
    public static string LanguageCodeFor(string mcVersion, string lang = "ru")
    {
        var v = VersionService.ParseMcVersion(mcVersion);
        var old = v is not null && v < new Version(1, 11, 0);

        return lang switch
        {
            "ru" => old ? "ru_RU" : "ru_ru",
            "uk" => old ? "uk_UA" : "uk_ua",
            "en" => old ? "en_US" : "en_us",
            _ => old ? "ru_RU" : "ru_ru"
        };
    }

    /// <summary>
    /// Создаёт options.txt с нужным языком, если файла ещё нет.
    /// Возвращает true, если файл был создан.
    /// </summary>
    public static bool EnsureLanguage(string gameDir, string mcVersion, string lang = "ru")
    {
        try
        {
            Directory.CreateDirectory(gameDir);
            var path = Path.Combine(gameDir, "options.txt");
            var code = LanguageCodeFor(mcVersion, lang);

            if (File.Exists(path))
            {
                // Файл есть — ничего не навязываем, игрок мог сменить язык сам
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"lang:{code}");
            // Пропускаем экран «выберите язык» и подсказки новичка
            sb.AppendLine("skipMultiplayerWarning:true");
            sb.AppendLine("tutorialStep:none");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log.Info($"Создан options.txt с языком {code} для {mcVersion}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось записать options.txt: " + ex.Message);
            return false;
        }
    }

    /// <summary>Принудительно меняет язык в существующем options.txt.</summary>
    public static bool SetLanguage(string gameDir, string mcVersion, string lang = "ru")
    {
        try
        {
            var path = Path.Combine(gameDir, "options.txt");
            var code = LanguageCodeFor(mcVersion, lang);

            if (!File.Exists(path)) return EnsureLanguage(gameDir, mcVersion, lang);

            var lines = File.ReadAllLines(path).ToList();
            var found = false;

            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].StartsWith("lang:", StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = $"lang:{code}";
                found = true;
                break;
            }

            if (!found) lines.Insert(0, $"lang:{code}");

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            Log.Info($"Язык игры изменён на {code}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось изменить язык: " + ex.Message);
            return false;
        }
    }

    /// <summary>Текущий язык из options.txt, либо null.</summary>
    public static string? GetLanguage(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "options.txt");
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                    return line[5..].Trim();
            }
        }
        catch { }

        return null;
    }
}
