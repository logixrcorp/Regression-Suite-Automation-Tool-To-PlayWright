using System.Reflection;
using System.Text;

namespace Rsat2Pw;

public static class RuntimeAsset
{
    private const string ResourceName = "Rsat2Pw.d365.ts";

    private static string? cached;

    public static string D365Ts => cached ??= Load();

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' is missing from the assembly");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
