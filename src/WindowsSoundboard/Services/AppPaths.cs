namespace WindowsSoundboard.Services;

public static class AppPaths
{
    public static string DataFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSoundboard");

    public static string ConfigFile => System.IO.Path.Combine(DataFolder, "config.json");

    public static string DefaultSoundsFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Windows Soundboard");
}
