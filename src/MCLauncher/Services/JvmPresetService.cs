namespace MCLauncher.Services;

/// <summary>Готовый набор аргументов JVM.</summary>
public sealed class JvmPreset
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Arguments { get; init; }
    public string Recommendation { get; init; } = "";
}

/// <summary>
/// Пресеты аргументов запуска.
/// Флаги Aikar заметно сглаживают лаги в модпаках за счёт настройки G1GC —
/// это стандарт де-факто в сообществе.
/// </summary>
public static class JvmPresetService
{
    public const string Default = "Стандарт";
    public const string Custom = "Свои аргументы";

    public static readonly JvmPreset[] Presets =
    {
        new()
        {
            Name = Default,
            Description = "Базовые настройки лаунчера. Подходит для ванильной игры.",
            Arguments = "",
            Recommendation = "Ваниль и лёгкие сборки"
        },
        new()
        {
            Name = "Aikar's Flags",
            Description = "Тонкая настройка сборщика мусора G1. Убирает микрофризы в модпаках.",
            Arguments =
                "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 " +
                "-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch " +
                "-XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M " +
                "-XX:G1ReservePercent=20 -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 " +
                "-XX:InitiatingHeapOccupancyPercent=15 -XX:G1MixedGCLiveThresholdPercent=90 " +
                "-XX:G1RSetUpdatingPauseTimePercent=5 -XX:SurvivorRatio=32 " +
                "-XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1",
            Recommendation = "Модпаки от 100 модов, 6+ ГБ памяти"
        },
        new()
        {
            Name = "Aikar (много памяти)",
            Description = "Вариант Aikar для 12 ГБ и больше — крупные регионы кучи.",
            Arguments =
                "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 " +
                "-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch " +
                "-XX:G1NewSizePercent=40 -XX:G1MaxNewSizePercent=50 -XX:G1HeapRegionSize=16M " +
                "-XX:G1ReservePercent=15 -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 " +
                "-XX:InitiatingHeapOccupancyPercent=20 -XX:G1MixedGCLiveThresholdPercent=90 " +
                "-XX:G1RSetUpdatingPauseTimePercent=5 -XX:SurvivorRatio=32 " +
                "-XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1",
            Recommendation = "Тяжёлые сборки, 12+ ГБ памяти"
        },
        new()
        {
            Name = "ZGC (плавность)",
            Description = "Сборщик с паузами меньше миллисекунды. Нужна Java 17+ и много ядер.",
            Arguments =
                "-XX:+UnlockExperimentalVMOptions -XX:+UseZGC -XX:+ZGenerational " +
                "-XX:+AlwaysPreTouch -XX:+DisableExplicitGC -XX:+PerfDisableSharedMem",
            Recommendation = "Запись видео без микрофризов, 8+ ядер"
        },
        new()
        {
            Name = "Слабый ПК",
            Description = "Экономит память, жертвуя частотой кадров.",
            Arguments =
                "-XX:+UseSerialGC -XX:TieredStopAtLevel=1 -XX:+UseCompressedOops " +
                "-Dsun.rmi.dgc.server.gcInterval=2147483646",
            Recommendation = "2–4 ГБ памяти, старое железо"
        },
        new()
        {
            Name = "Запись видео",
            Description = "Ровный FPS важнее пикового: длинные паузы GC исключены.",
            Arguments =
                "-XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:+ParallelRefProcEnabled " +
                "-XX:+UnlockExperimentalVMOptions -XX:+AlwaysPreTouch -XX:+DisableExplicitGC " +
                "-XX:G1NewSizePercent=30 -XX:G1HeapRegionSize=16M -XX:+PerfDisableSharedMem " +
                "-Dsun.java2d.opengl=true",
            Recommendation = "Стримы и запись роликов"
        },
        new()
        {
            Name = Custom,
            Description = "Аргументы задаются вручную в поле ниже.",
            Arguments = "",
            Recommendation = "Для опытных"
        }
    };

    public static JvmPreset Get(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];

    /// <summary>Итоговые аргументы: пресет + пользовательские.</summary>
    public static string Resolve(string presetName, string customArgs)
    {
        var preset = Get(presetName);

        if (string.Equals(preset.Name, Custom, StringComparison.OrdinalIgnoreCase))
            return customArgs;

        return string.IsNullOrWhiteSpace(customArgs)
            ? preset.Arguments
            : preset.Arguments + " " + customArgs;
    }

    /// <summary>Предупреждает, если пресет не подходит к железу или Java.</summary>
    public static string? Validate(string presetName, int memoryMb, int javaMajor)
    {
        var preset = Get(presetName);

        if (preset.Name.Contains("много памяти") && memoryMb < 10240)
            return "Этот пресет рассчитан на 12 ГБ и больше. Сейчас выделено " +
                   $"{memoryMb} МБ — используйте обычные Aikar's Flags.";

        if (preset.Name.StartsWith("ZGC") && javaMajor is > 0 and < 17)
            return $"ZGC требует Java 17 или новее, у вас Java {javaMajor}.";

        if (preset.Name.StartsWith("ZGC") && memoryMb < 4096)
            return "ZGC заметно выигрывает от 8 ГБ и больше. При малой памяти лучше G1.";

        if (preset.Name == "Слабый ПК" && memoryMb > 8192)
            return "Для такого объёма памяти лучше подойдут Aikar's Flags.";

        return null;
    }
}
