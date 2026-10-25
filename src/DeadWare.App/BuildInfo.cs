using System.Reflection;
namespace DeadWare.App;
internal static class BuildInfo
{
    public static string Version {get;}=typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ??typeof(App).Assembly.GetName().Version?.ToString()??"unknown";
}
