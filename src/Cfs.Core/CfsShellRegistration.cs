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

    public static string BuildCreateHereCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" create-here \"%V\"";
    }

    public static string BuildCreateInFolderCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" create-here \"%1\"";
    }

    public static string BuildCloseCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" close \"%1\"";
    }

    public static string BuildExtractCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" extract \"%1\"";
    }

    public static string BuildCommitCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" commit \"%1\"";
    }

    public static string BuildDiscardCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" discard \"%1\"";
    }

    public static string BuildStatusCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" status \"%1\"";
    }

    public static string BuildRecoverCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" recover \"%1\"";
    }

    public static string BuildRecoveryStatusCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" recovery-status \"%1\"";
    }

    public static string BuildDiscardRecoveryCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" discard-recovery \"%1\"";
    }

    public static string BuildOpenReadOnlyCommand(string commandClientExecutablePath)
    {
        _ = BuildOpenCommand(commandClientExecutablePath);
        return $"\"{Path.GetFullPath(commandClientExecutablePath)}\" open-readonly \"%1\"";
    }
}
