namespace Cfs.Core;

public static class CfsShellRegistration
{
    public const string BrokerExecutableName = "Cfs.Broker.exe";

    public static string BuildOpenCommand(string brokerExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutablePath);
        if (brokerExecutablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("A Windows executable path cannot contain quotes or line breaks.", nameof(brokerExecutablePath));
        var fullPath = Path.GetFullPath(brokerExecutablePath);
        if (!string.Equals(Path.GetFileName(fullPath), BrokerExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The CFS shell handler must be {BrokerExecutableName}; Cfs.App and Cfs.Cli are not valid handlers.", nameof(brokerExecutablePath));
        return $"\"{fullPath}\" open \"%1\"";
    }

    public static string BuildCompressCommand(string brokerExecutablePath)
    {
        _ = BuildOpenCommand(brokerExecutablePath);
        return $"\"{Path.GetFullPath(brokerExecutablePath)}\" compress \"%1\"";
    }

    public static string BuildCloseCommand(string brokerExecutablePath)
    {
        _ = BuildOpenCommand(brokerExecutablePath);
        return $"\"{Path.GetFullPath(brokerExecutablePath)}\" close \"%1\"";
    }
}
