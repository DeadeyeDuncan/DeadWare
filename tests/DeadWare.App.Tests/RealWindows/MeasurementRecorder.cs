using System.IO;
using System.Text;
using System.Text.Json;
using DeadWare.App;

namespace DeadWare.App.Tests;

/// <summary>
/// Writes a real-Windows measurement to <c>&lt;repo&gt;/artifacts/measurements/&lt;name&gt;.json</c>, which CI uploads as the
/// DeadWare-measurements artifact, and adds it to the job summary when <c>GITHUB_STEP_SUMMARY</c> is set.
/// </summary>
internal static class MeasurementRecorder
{
    private static readonly JsonSerializerOptions Indented=new() {WriteIndented=true};
    /// <summary>The app's own elevation check, fixed for the test process.</summary>
    public static bool Elevated {get;}=Collector.IsElevated;

    public static void Write(string name,object values)
    {
        string folder=Path.Combine(RepositoryRoot(),"artifacts","measurements");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,$"{name}.json"),JsonSerializer.Serialize(values,Indented));
        if(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is {Length:>0} summary)File.AppendAllText(summary,SummaryTable(name,values));
    }

    /// <summary>A <c>### name</c> heading and one table row per top-level value, nested values as compact JSON.</summary>
    private static string SummaryTable(string name,object values)
    {
        var text=new StringBuilder().Append("### ").AppendLine(name).AppendLine().AppendLine("| Measurement | Value |").AppendLine("| --- | --- |");
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(values));
        foreach(var property in json.RootElement.EnumerateObject())
            text.Append("| ").Append(property.Name).Append(" | ").Append(property.Value.GetRawText().Replace("|","\\|")).AppendLine(" |");
        return text.AppendLine().ToString();
    }

    private static string RepositoryRoot()
    {
        for(var folder=new DirectoryInfo(AppContext.BaseDirectory);folder is not null;folder=folder.Parent)
            if(File.Exists(Path.Combine(folder.FullName,"DeadWare.slnx")))return folder.FullName;
        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds DeadWare.slnx.");
    }
}
