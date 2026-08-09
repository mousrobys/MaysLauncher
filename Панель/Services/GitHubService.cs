using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LauncherPanel.Models;
using Newtonsoft.Json;

namespace LauncherPanel.Services;

public class GitHubService
{
    private readonly HttpClient _http;
    private string _token = "";
    private string _owner = "mousrobys";
    private string _repo = "MaysLauncher";

    private const string ConfigPath = "launcher-config.json";
    private const string ConfigFolder = "билды exe";

    public GitHubService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "MaysLauncher-Panel");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void SetToken(string token)
    {
        _token = token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void SetRepository(string owner, string repo)
    {
        _owner = owner;
        _repo = repo;
    }

    public string Owner => _owner;
    public string Repo => _repo;

    private string ConfigFile => Path.Combine(ConfigFolder, ConfigPath);

    public LauncherConfig LoadLocalConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                return JsonConvert.DeserializeObject<LauncherConfig>(json) ?? new LauncherConfig();
            }
        }
        catch { }
        return new LauncherConfig();
    }

    public void SaveLocalConfig(LauncherConfig config)
    {
        Directory.CreateDirectory(ConfigFolder);
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(ConfigFile, json);
    }

    public async Task<LauncherConfig?> GetRemoteConfigAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/{ConfigPath}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            var base64 = doc.RootElement.GetProperty("content").GetString();
            var sha = doc.RootElement.GetProperty("sha").GetString();

            if (base64 == null) return null;

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace("\n", "")));
            var config = JsonConvert.DeserializeObject<LauncherConfig>(json);
            if (config != null)
                config.RemoteSha = sha;
            return config;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> PushConfigAsync(LauncherConfig config, string message)
    {
        try
        {
            var existing = await GetRemoteConfigAsync();
            var sha = existing?.RemoteSha;

            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var body = new
            {
                message = message,
                content = base64,
                sha = sha
            };

            var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/{ConfigPath}";
            var requestBody = System.Text.Json.JsonSerializer.Serialize(body);
            var content_req = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _http.PutAsync(url, content_req);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetReleasesAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return $"Ошибка: {ex.Message}";
        }
    }

    public async Task<bool> CreateReleaseAsync(string tagName, string name, string body, string? exePath = null)
    {
        try
        {
            var releaseBody = new
            {
                tag_name = tagName,
                name = name,
                body = body,
                draft = false,
                prerelease = false
            };

            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases";
            var requestBody = System.Text.Json.JsonSerializer.Serialize(releaseBody);
            var content_req = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(url, content_req);
            if (!response.IsSuccessStatusCode) return false;

            if (exePath != null && File.Exists(exePath))
            {
                var uploadUrlDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var uploadUrl = uploadUrlDoc.RootElement.GetProperty("upload_url").GetString();
                if (uploadUrl != null)
                {
                    await UploadAssetAsync(uploadUrl, exePath);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> UploadAssetAsync(string uploadUrl, string filePath)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var url = uploadUrl.Replace("{?name,label}", $"?name={Uri.EscapeDataString(fileName)}");

            using var fileStream = File.OpenRead(filePath);
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await _http.PostAsync(url, streamContent);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}";
            var response = await _http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}


