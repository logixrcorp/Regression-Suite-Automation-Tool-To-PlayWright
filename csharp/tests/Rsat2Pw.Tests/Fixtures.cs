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

    /// <summary>
    /// Wrap nodes in the container a real export uses: the action tree hangs
    /// off <c>RootScope</c>, not off a top-level <c>Nodes</c>.
    /// </summary>
    public static string Wrap(string name, string nodes) =>
        $"""
         <Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
           <Name>{name}</Name><RootScope><Children>{nodes}</Children></RootScope></Recording>
         """;

    public static List<Action> LowerXml(string nodes) =>
        Lower.Run(RecordingReader.Parse(Wrap("T", nodes))).Actions;

    public static TestCase LowerCase(string nodes) =>
        Lower.Run(RecordingReader.Parse(Wrap("T", nodes)));
}
