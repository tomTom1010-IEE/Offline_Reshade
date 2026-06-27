using System.Text;

namespace OfflineReShade.WinUI.Services;

public sealed class CommandLineBuilder
{
    private readonly StringBuilder _builder = new();

    public CommandLineBuilder Add(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return this;

        AddRaw(name);
        AddRaw(Quote(value));
        return this;
    }

    public CommandLineBuilder AddSwitch(string name)
    {
        AddRaw(name);
        return this;
    }

    public override string ToString() => _builder.ToString();

    private void AddRaw(string value)
    {
        if (_builder.Length != 0)
            _builder.Append(' ');
        _builder.Append(value);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
