namespace MCLauncher.Services;

public class SkinInfo
{
    public string Id = "";
    public string Name = "";
    public string Url = "";
    public string PreviewUrl = "";
    public string Source = "";
    public bool Slim;
    public byte[]? Data;
}

public class SkinItem
{
    public string Name = "";
    public string FilePath = "";
    public string PreviewPath = "";
    public bool IsLocal = true;
}
