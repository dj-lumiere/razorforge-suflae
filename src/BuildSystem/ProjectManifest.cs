namespace Builder;

public sealed class PackageInfo
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public List<string>? Authors { get; set; }
    public string? License { get; set; }
    public string? Description { get; set; }
    public string? Repository { get; set; }
    public string? RazorForgeVersion { get; set; }
}

public sealed class TargetInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "executable";
    public string Entry { get; set; } = "";
    public string? LibType { get; set; }
}

public sealed class ProjectManifest
{
    public PackageInfo Package { get; set; } = new();
    public List<TargetInfo> Targets { get; set; } = [];
    public string ManifestDirectory { get; set; } = "";
}
