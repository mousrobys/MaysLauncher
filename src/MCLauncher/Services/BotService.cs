using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MCLauncher.Services;

/// <summary>
/// Запуск бота-помощника на mineflayer.
///
/// Бот подключается к открытому в LAN миру или к серверу и управляется
/// командами из лаунчера — удобно для записи роликов в одиночку.
/// Node.js и mineflayer ставятся автоматически в папку лаунчера.
/// </summary>
public sealed class BotService
{
    private const string NodeIndexUrl = "https://nodejs.org/dist/index.json";

    private readonly HttpClient _http;
    private Process? _process;

    public BotService(HttpClient http) => _http = http;

    public event Action<string>? Output;
    public event Action<bool>? RunningChanged;

    public bool IsRunning
    {
        get
        {
            try { return _process is not null && !_process.HasExited; }
            catch { return false; }
        }
    }

    private static string BotRoot => Path.Combine(LauncherPaths.Root, "bot");
    private static string NodeRoot => Path.Combine(LauncherPaths.RuntimeDir, "node");
    private static string ScriptPath => Path.Combine(BotRoot, "bot.js");

    private static string? FindNodeExe()
    {
        try
        {
            // Сначала свой портативный
            if (Directory.Exists(NodeRoot))
            {
                var local = Directory.GetFiles(NodeRoot, "node.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (local is not null) return local;
            }

            // Затем системный
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return null;
    }

    public static bool IsNodeInstalled() => FindNodeExe() is not null;
    public static bool IsMineflayerInstalled() =>
        Directory.Exists(Path.Combine(BotRoot, "node_modules", "mineflayer"));

    // =====================================================================
    //  УСТАНОВКА ОКРУЖЕНИЯ
    // =====================================================================

    public async Task EnsureEnvironmentAsync(
        Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BotRoot);

        var node = FindNodeExe();

        if (node is null)
        {
            Output?.Invoke("Node.js не найден — скачиваю портативную версию...");
            node = await DownloadNodeAsync(progress, ct).ConfigureAwait(false);
        }
        else
        {
            Output?.Invoke("Node.js найден: " + node);
        }

        if (!IsMineflayerInstalled())
        {
            Output?.Invoke("Устанавливаю mineflayer (это займёт минуту)...");
            await InstallMineflayerAsync(node, ct).ConfigureAwait(false);
        }

        WriteBotScript();
        Output?.Invoke("Окружение бота готово.");
    }

    private async Task<string> DownloadNodeAsync(
        Action<DownloadProgress>? progress, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(NodeIndexUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        string? version = null;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            // lts — либо false, либо строка с кодовым именем
            if (el.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String)
            {
                version = el.GetProperty("version").GetString();
                break;
            }
        }

        version ??= doc.RootElement[0].GetProperty("version").GetString()
                    ?? throw new InvalidOperationException("Не удалось определить версию Node.js.");

        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var url = $"https://nodejs.org/dist/{version}/node-{version}-win-{arch}.zip";

        Directory.CreateDirectory(LauncherPaths.CacheDir);
        var zipPath = Path.Combine(LauncherPaths.CacheDir, $"node-{version}.zip");

        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long done = 0;
            int read;

            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                progress?.Invoke(new DownloadProgress
                {
                    Stage = $"Загрузка Node.js {version}",
                    CurrentFile = $"node-{version}-win-{arch}.zip",
                    BytesDone = done, BytesTotal = total
                });
            }
        }

        if (Directory.Exists(NodeRoot)) Directory.Delete(NodeRoot, true);
        Directory.CreateDirectory(NodeRoot);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, NodeRoot, true), ct).ConfigureAwait(false);
        try { File.Delete(zipPath); } catch { }

        var exe = FindNodeExe()
                  ?? throw new InvalidOperationException("node.exe не найден после распаковки.");

        Output?.Invoke($"Node.js {version} установлен.");
        return exe;
    }

    private async Task InstallMineflayerAsync(string nodeExe, CancellationToken ct)
    {
        // package.json, чтобы npm не ругался
        var pkg = Path.Combine(BotRoot, "package.json");
        if (!File.Exists(pkg))
        {
            await File.WriteAllTextAsync(pkg, """
                {
                  "name": "mays-bot",
                  "version": "1.0.0",
                  "private": true,
                  "type": "commonjs"
                }
                """, ct).ConfigureAwait(false);
        }

        var nodeDir = Path.GetDirectoryName(nodeExe)!;
        var npmCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");

        ProcessStartInfo psi;

        if (File.Exists(npmCli))
        {
            psi = new ProcessStartInfo(nodeExe);
            psi.ArgumentList.Add(npmCli);
        }
        else
        {
            var npmCmd = Path.Combine(nodeDir, "npm.cmd");
            psi = File.Exists(npmCmd)
                ? new ProcessStartInfo(npmCmd)
                : new ProcessStartInfo("npm.cmd");
        }

        foreach (var a in new[] { "install", "mineflayer@latest", "mineflayer-pathfinder@latest",
                     "--no-audit", "--no-fund", "--loglevel", "error" })
            psi.ArgumentList.Add(a);

        psi.WorkingDirectory = BotRoot;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke("npm: " + e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke("npm: " + e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var finished = await Task.Run(() => proc.WaitForExit(300_000), ct).ConfigureAwait(false);
        if (!finished)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException("npm install не завершился за 5 минут.");
        }

        if (!IsMineflayerInstalled())
            throw new InvalidOperationException(
                "mineflayer не установился. Проверьте подключение к интернету.");
    }

    /// <summary>Пишет скрипт бота. Команды приходят построчно через stdin.</summary>
    private static void WriteBotScript()
    {
        Directory.CreateDirectory(BotRoot);

        const string js = """
            const mineflayer = require('mineflayer');

            const host = process.argv[2] || 'localhost';
            const port = parseInt(process.argv[3] || '25565', 10);
            const username = process.argv[4] || 'MaysBot';
            const version = process.argv[5] && process.argv[5] !== 'auto' ? process.argv[5] : false;

            function log(msg) { console.log(msg); }

            log(`[bot] подключаюсь к ${host}:${port} как ${username}`);

            const bot = mineflayer.createBot({
                host, port, username,
                version,
                auth: 'offline',
                hideErrors: false
            });

            let following = null;
            let ready = false;
            const pending = [];

            bot.on('login', () => log('[bot] вошёл в игру'));

            bot.on('spawn', () => {
                ready = true;
                log('[bot] появился в мире, готов к командам');
                log(`[pos] ${Math.round(bot.entity.position.x)} ${Math.round(bot.entity.position.y)} ${Math.round(bot.entity.position.z)}`);

                // Выполняем то, что прислали до входа в мир
                while (pending.length) handle(pending.shift());
            });

            bot.on('health', () => {
                log(`[stat] здоровье ${Math.round(bot.health)} еда ${Math.round(bot.food)}`);
            });

            bot.on('chat', (user, message) => {
                if (user === bot.username) return;
                log(`[chat] <${user}> ${message}`);
            });

            bot.on('kicked', (reason) => {
                let text = reason;
                try {
                    const o = typeof reason === 'string' ? JSON.parse(reason) : reason;
                    text = o.text || o.translate || JSON.stringify(o);
                } catch (e) { /* причина пришла обычным текстом */ }

                log('[bot] сервер отклонил подключение: ' + text);

                if (String(text).includes('multiplayer.disconnect.not_whitelisted'))
                    log('[подсказка] бот не в белом списке сервера');
                else if (String(text).toLowerCase().includes('outdated') || String(text).includes('version'))
                    log('[подсказка] не совпала версия — укажите версию мира вручную');
                else if (String(text).toLowerCase().includes('premium') || String(text).includes('authenticat'))
                    log('[подсказка] сервер требует лицензию, бот заходит в оффлайн-режиме');
            });

            bot.on('error', (err) => {
                const msg = err && err.message ? err.message : String(err);
                log('[error] ' + msg);

                if (msg.includes('ECONNREFUSED'))
                    log('[подсказка] порт закрыт. В игре: Esc - Открыть для сети, затем впишите новый порт');
                else if (msg.includes('ETIMEDOUT') || msg.includes('EHOSTUNREACH'))
                    log('[подсказка] адрес недоступен. Для своего мира используйте localhost');
                else if (msg.includes('ENOTFOUND'))
                    log('[подсказка] не найден такой адрес');
                else if (msg.includes('unsupported protocol') || msg.includes('Unsupported protocol'))
                    log('[подсказка] mineflayer не знает эту версию Minecraft');
            });

            bot.on('end', (reason) => {
                log('[bot] отключился' + (reason ? ': ' + reason : ''));
                process.exit(0);
            });

            // Если за 25 секунд не вошли - объясняем, что не так
            setTimeout(() => {
                if (!ready) {
                    log('[bot] за 25 секунд подключиться не удалось');
                    log('[подсказка] проверьте: мир открыт для сети, порт совпадает, версия указана верно');
                }
            }, 25000);

            // Следование за игроком
            setInterval(() => {
                if (!ready || !following || !bot.entity) return;
                const target = bot.players[following]?.entity;
                if (!target) return;
                bot.lookAt(target.position.offset(0, 1.6, 0));
                const dist = bot.entity.position.distanceTo(target.position);
                bot.setControlState('forward', dist > 3);
            }, 200);

            // Команды из лаунчера
            process.stdin.setEncoding('utf8');
            let buffer = '';

            process.stdin.on('data', (chunk) => {
                buffer += chunk;
                let idx;
                while ((idx = buffer.indexOf('\n')) >= 0) {
                    const line = buffer.slice(0, idx).trim();
                    buffer = buffer.slice(idx + 1);
                    if (!line) continue;

                    // До спавна bot.entity ещё не существует — копим команды
                    if (!ready && line.toLowerCase() !== 'quit') {
                        pending.push(line);
                        log('[bot] команда принята, выполню после входа в мир');
                        continue;
                    }

                    handle(line);
                }
            });

            function handle(line) {
                const space = line.indexOf(' ');
                const cmd = (space < 0 ? line : line.slice(0, space)).toLowerCase();
                const arg = space < 0 ? '' : line.slice(space + 1).trim();

                try {
                    switch (cmd) {
                        case 'say':
                            bot.chat(arg);
                            break;
                        case 'follow':
                            following = arg || null;
                            log(following ? `[bot] следую за ${following}` : '[bot] стою на месте');
                            if (!following) bot.setControlState('forward', false);
                            break;
                        case 'stop':
                            following = null;
                            ['forward','back','left','right','jump','sprint','sneak']
                                .forEach(c => bot.setControlState(c, false));
                            log('[bot] остановлен');
                            break;
                        case 'come': {
                            const p = bot.players[arg]?.entity;
                            if (!p) { log('[bot] игрок не найден: ' + arg); break; }
                            following = arg;
                            log('[bot] иду к ' + arg);
                            break;
                        }
                        case 'jump':
                            bot.setControlState('jump', true);
                            setTimeout(() => bot.setControlState('jump', false), 400);
                            break;
                        case 'look': {
                            const p = bot.players[arg]?.entity;
                            if (p) bot.lookAt(p.position.offset(0, 1.6, 0));
                            break;
                        }
                        case 'drop':
                            bot.tossStack(bot.inventory.items()[0]).catch(e => log('[error] ' + e.message));
                            break;
                        case 'pos':
                            if (!bot.entity) { log('[bot] ещё не в мире'); break; }
                            log(`[pos] ${Math.round(bot.entity.position.x)} ${Math.round(bot.entity.position.y)} ${Math.round(bot.entity.position.z)}`);
                            break;
                        case 'players':
                            log('[players] ' + (bot.players ? Object.keys(bot.players).join(', ') : 'нет данных'));
                            break;
                        case 'inv':
                            if (!bot.inventory) { log('[bot] инвентарь недоступен'); break; }
                            log('[inv] ' + (bot.inventory.items().map(i => `${i.name} x${i.count}`).join(', ') || 'пусто'));
                            break;
                        case 'quit':
                            log('[bot] отключаюсь');
                            if (typeof bot.quit === 'function') bot.quit();
                            else if (bot._client) bot._client.end();
                            setTimeout(() => process.exit(0), 800);
                            break;
                        default:
                            log('[bot] неизвестная команда: ' + cmd);
                    }
                } catch (e) {
                    log('[error] ' + e.message);
                }
            }
            """;

        File.WriteAllText(ScriptPath, js, new UTF8Encoding(false));
    }

    // =====================================================================
    //  ЗАПУСК
    // =====================================================================

    /// <summary>
    /// Версии Minecraft, которые понимает mineflayer.
    /// Новые релизы (26.x) протокол ещё не поддерживает — предупреждаем заранее,
    /// чтобы пользователь не гадал, почему бот молча не заходит.
    /// </summary>
    public static readonly string[] SupportedVersions =
    {
        "1.21.11", "1.21.9", "1.21.8", "1.21.6", "1.21.5", "1.21.4", "1.21.3", "1.21.1", "1.21",
        "1.20.6", "1.20.5", "1.20.4", "1.20.3", "1.20.2", "1.20.1", "1.20",
        "1.19.4", "1.19.3", "1.19.2", "1.19.1", "1.19",
        "1.18.2", "1.18.1", "1.18",
        "1.17.1", "1.17",
        "1.16.5", "1.16.4", "1.16.3", "1.16.1"
    };

    public static bool IsVersionSupported(string version) =>
        string.IsNullOrWhiteSpace(version) ||
        SupportedVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    /// <summary>Ближайшая поддерживаемая версия — на случай слишком свежей игры.</summary>
    public static string? SuggestVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (IsVersionSupported(version)) return version;

        var target = VersionService.ParseMcVersion(version);
        if (target is null) return SupportedVersions[0];

        // Подбираем самую свежую из поддерживаемых, не выше запрошенной
        var lower = SupportedVersions
            .Select(v => (Raw: v, Parsed: VersionService.ParseMcVersion(v)))
            .Where(x => x.Parsed is not null && x.Parsed <= target)
            .OrderByDescending(x => x.Parsed)
            .FirstOrDefault();

        return lower.Raw ?? SupportedVersions[0];
    }
    public async Task StartAsync(string host, int port, string username, string mcVersion,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Бот уже запущен.");

        await EnsureEnvironmentAsync(null, ct).ConfigureAwait(false);

        var node = FindNodeExe() ?? throw new InvalidOperationException("Node.js не найден.");

        // Проверяем версию заранее — иначе бот просто молча не подключится
        if (!string.IsNullOrWhiteSpace(mcVersion) && !IsVersionSupported(mcVersion))
        {
            var suggested = SuggestVersion(mcVersion);
            Output?.Invoke($"[внимание] mineflayer не поддерживает Minecraft {mcVersion}.");
            Output?.Invoke($"[внимание] пробую подключиться как {suggested} — если не выйдет, " +
                           "откройте мир на версии 1.21.11 или ниже.");
            mcVersion = suggested ?? "";
        }

        var psi = new ProcessStartInfo(node)
        {
            WorkingDirectory = BotRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add(username);
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(mcVersion) ? "auto" : mcVersion);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke("[err] " + e.Data); };
        _process.Exited += (_, _) => RunningChanged?.Invoke(false);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        RunningChanged?.Invoke(true);
        Log.Info($"Бот запущен: {username}@{host}:{port}");
    }

    public void Send(string command)
    {
        if (!IsRunning || _process is null) return;

        try { _process.StandardInput.WriteLine(command); _process.StandardInput.Flush(); }
        catch (Exception ex) { Output?.Invoke("[error] не удалось отправить команду: " + ex.Message); }
    }

    public void Stop()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                Send("quit");
                // Даём боту корректно попрощаться с сервером
                if (!_process.WaitForExit(4000)) _process.Kill(true);
            }
        }
        catch { }
        finally
        {
            _process = null;
            RunningChanged?.Invoke(false);
            Log.Info("Бот остановлен.");
        }
    }
}
