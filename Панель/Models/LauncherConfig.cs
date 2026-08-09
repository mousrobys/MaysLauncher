using Newtonsoft.Json;

namespace LauncherPanel.Models;

public class NewsItem
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("content")]
    public string Content { get; set; } = "";

    [JsonProperty("date")]
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

    [JsonProperty("important")]
    public bool Important { get; set; } = false;
}

public class SponsorServer
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("address")]
    public string Address { get; set; } = "";

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("site")]
    public string Site { get; set; } = "";

    [JsonProperty("requiredVersion")]
    public string RequiredVersion { get; set; } = "";

    [JsonProperty("featured")]
    public bool Featured { get; set; } = true;
}

public class LauncherConfig
{
    [JsonProperty("news")]
    public List<NewsItem> News { get; set; } = new();

    [JsonProperty("sponsorServers")]
    public List<SponsorServer> SponsorServers { get; set; } = new();

    [JsonIgnore]
    public string? RemoteSha { get; set; }
}
