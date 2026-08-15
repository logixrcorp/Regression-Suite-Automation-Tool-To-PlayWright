namespace Rsat2Pw.Tests;

internal static class Fixtures
{
    public static string Path(string relative) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, relative);

    public static string FixtureText(string name) =>
        Normalize(File.ReadAllText(Path(System.IO.Path.Combine("fixtures", name))));

    public static string FixturePath(string name) =>
        Path(System.IO.Path.Combine("fixtures", name));

    public static string GoldenText(string name) =>
        Normalize(File.ReadAllText(Path(System.IO.Path.Combine("goldens", name))));

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string Wrap(string name, string nodes) =>
        $"""
         <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
           <Name>{name}</Name><Nodes>{nodes}</Nodes></AxTaskRecording>
         """;

    public static List<Action> LowerXml(string nodes) =>
        Lower.Run(RecordingReader.Parse(Wrap("T", nodes))).Actions;
}
