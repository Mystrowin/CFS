namespace Cfs.Core;

public static class CfsShellRegistration
{
    public const string CommandClientExecutableName = "Cfs.CommandClient.exe";

    public static string BuildOpenCommand(string commandClientExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandClientExecutablePath);
        if (commandClientExecutablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("A Windows executable path cannot contain quotes or line breaks.", nameof(commandClientExecutablePath));
        var fullPath = Path.GetFullPath(commandClientExecutablePath);
        if (!string.Equals(Path.GetFileName(fullPath), CommandClientExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The CFS shell handler must be {CommandClientExecutableName}; Cfs.Broker, Cfs.App, and Cfs.Cli are not valid shell handlers.", nameof(commandClientExecutablePath));
        return $"\"{fullPath}\" open \"%1\"";
    }

    public static string BuildCompressCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" compress \"%1\"";
    }

    public static string BuildCloseCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" close \"%1\"";
    }
}
