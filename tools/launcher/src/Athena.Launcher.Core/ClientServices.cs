using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using GRF;

namespace Athena.Net.Launcher.Core;

public interface IClientDataSource : IDisposable
{
    bool TryRead(string relativePath, out byte[] data, out string source);
}

public interface IClientDataSourceFactory
{
    IClientDataSource Open(RagnarokInstallation installation);
}

public sealed class GrfClientDataSourceFactory : IClientDataSourceFactory
{
    public IClientDataSource Open(RagnarokInstallation installation) => new GrfClientDataSource(installation);
}

public sealed class GrfClientDataSource : IClientDataSource
{
    private readonly string _root;
    private readonly GrfCollection _collection;

    public GrfClientDataSource(RagnarokInstallation installation)
    {
        _root = installation.DirectoryPath;
        _collection = new GrfCollection(installation.DataIniPath);
    }

    public bool TryRead(string relativePath, out byte[] data, out string source)
    {
        var diskPath = Path.Combine(_root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(diskPath))
        {
            data = File.ReadAllBytes(diskPath);
            source = diskPath;
            return true;
        }

        var grfPath = relativePath.Replace('/', '\\');
        if (_collection.FindEntry(grfPath, out var entry))
        {
            data = entry.GetUncompressedData();
            source = $"{Path.GetFileName(_root)}\\data.ini::{grfPath}";
            return true;
        }

        data = [];
        source = string.Empty;
        return false;
    }

    public void Dispose() { }
}

public sealed class RagnarokClientConfigurationReader : IRagnarokClientConfigurationReader
{
    private static readonly string[] CandidatePaths = ["data\\sclientinfo.xml", "data\\clientinfo.xml"];
    private readonly IClientDataSourceFactory _factory;
    private readonly ILauncherLog _log;

    public RagnarokClientConfigurationReader(IClientDataSourceFactory factory, ILauncherLog log)
    {
        _factory = factory;
        _log = log;
    }

    public Task<RagnarokLoginEndpoint> ReadAsync(RagnarokInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = _factory.Open(installation);
        foreach (var path in CandidatePaths)
        {
            if (!source.TryRead(path, out var bytes, out var selectedSource)) continue;
            var endpoint = Parse(bytes, selectedSource);
            _log.Information("client.configuration.selected", "Client service configuration selected.", new Dictionary<string, object?> { ["source"] = selectedSource, ["host"] = endpoint.Host, ["port"] = endpoint.Port });
            return Task.FromResult(endpoint);
        }

        throw new InvalidOperationException("Neither data\\sclientinfo.xml nor data\\clientinfo.xml exists in the effective data/GRF hierarchy.");
    }

    public static RagnarokLoginEndpoint Parse(ReadOnlySpan<byte> bytes, string source)
    {
        var text = DecodeXml(bytes);
        XDocument document;
        try { document = XDocument.Parse(text, LoadOptions.None); }
        catch (Exception ex) { throw new InvalidOperationException($"Client service configuration '{source}' is not valid XML.", ex); }

        var connection = document.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("connection", StringComparison.OrdinalIgnoreCase));
        if (connection == null) throw new InvalidOperationException($"Client service configuration '{source}' contains no connection.");
        var host = Value(connection, "address")?.Trim();
        var portText = Value(connection, "port")?.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"Client service configuration '{source}' has an invalid login host.");
        if (!int.TryParse(portText, out var port) || port is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException($"Client service configuration '{source}' has an invalid login port.");
        return new RagnarokLoginEndpoint(host, port);
    }

    private static string? Value(XElement parent, string name) => parent.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string DecodeXml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        return Encoding.UTF8.GetString(bytes);
    }
}

public sealed class EndpointResolver : IEndpointResolver
{
    public async Task<IPAddress> ResolveIpv4Async(RagnarokLoginEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Port is < 1 or > ushort.MaxValue) throw new InvalidOperationException("Official login port is invalid.");
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("The official login endpoint must resolve to IPv4.");
            return literal;
        }
        if (Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns) throw new InvalidOperationException("Official login hostname is invalid.");
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        return addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Official login hostname '{endpoint.Host}' did not resolve to IPv4.");
    }
}

public sealed class RagnarokInstallationLocator : IRagnarokInstallationLocator
{
    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\WOW6432Node\Gravity Interactive, Inc.\Ragnarok Online",
        @"SOFTWARE\Gravity Interactive, Inc.\Ragnarok Online",
        @"SOFTWARE\WOW6432Node\WarpPortal\Ragnarok Online",
        @"SOFTWARE\WarpPortal\Ragnarok Online",
    ];
    private readonly ILauncherLog _log;
    public RagnarokInstallationLocator(ILauncherLog log) => _log = log;

    public Task<RagnarokInstallation> LocateAsync(LauncherOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RagnarokPath)) candidates.Add(Environment.ExpandEnvironmentVariables(options.RagnarokPath));
        if (OperatingSystem.IsWindows())
        {
            foreach (var keyName in RegistryKeys)
            using (var key = Registry.LocalMachine.OpenSubKey(keyName))
            {
                foreach (var valueName in new[] { "InstallPath", "Path", "InstallDir" })
                    if (key?.GetValue(valueName) is string value) candidates.Add(value);
            }
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates.Add(Path.Combine(programFiles, "Gravity", "Ragnarok Online"));
            candidates.Add(Path.Combine(programFiles, "WarpPortal", "Ragnarok Online"));
        }

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            var ragexe = Path.Combine(directory, "Ragexe.exe");
            var eac = FindFile(directory, "EasyAntiCheat.exe");
            var dataIni = Path.Combine(directory, "data.ini");
            var updater = FindUpdater(directory, options.UpdaterExecutable);
            if (File.Exists(ragexe) && eac != null && updater != null && File.Exists(dataIni))
            {
                var result = new RagnarokInstallation(Path.GetFullPath(directory), ragexe, eac, updater, dataIni);
                _log.Information("installation.detected", "Ragnarok installation detected.", new Dictionary<string, object?> { ["path"] = result.DirectoryPath, ["updater"] = Path.GetFileName(updater) });
                return Task.FromResult(result);
            }
        }
        throw new DirectoryNotFoundException("A valid Ragnarok installation was not found. Set RagnarokPath in launcher.settings.json.");
    }

    private static string? FindFile(string root, string name) => Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();

    private static string? FindUpdater(string root, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured);
            return File.Exists(path) ? path : null;
        }
        var excluded = new HashSet<string>(["ragexe.exe", "easyanticheat.exe"], StringComparer.OrdinalIgnoreCase);
        var executables = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly).Where(x => !excluded.Contains(Path.GetFileName(x))).ToArray();
        foreach (var path in executables)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var description = $"{Path.GetFileNameWithoutExtension(path)} {info.FileDescription} {info.ProductName}";
            if (description.Contains("patch", StringComparison.OrdinalIgnoreCase) || description.Contains("updater", StringComparison.OrdinalIgnoreCase) || description.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return path;
        }
        return executables.FirstOrDefault(x => Path.GetFileName(x).Equals("Ragnarok.exe", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RagnarokUpdater : IRagnarokUpdater
{
    private readonly ILauncherLog _log;
    public RagnarokUpdater(ILauncherLog log) => _log = log;
    public async Task RunAsync(RagnarokInstallation installation, CancellationToken cancellationToken)
    {
        var process = Process.Start(new ProcessStartInfo(installation.UpdaterPath) { WorkingDirectory = installation.DirectoryPath, UseShellExecute = true })
            ?? throw new InvalidOperationException("The official Ragnarok updater could not be started.");
        _log.Information("updater.started", "Official updater started.", new Dictionary<string, object?> { ["pid"] = process.Id, ["path"] = installation.UpdaterPath });
        await process.WaitForExitAsync(cancellationToken);
        _log.Information("updater.exited", "Official updater exited.", new Dictionary<string, object?> { ["pid"] = process.Id, ["exitCode"] = process.ExitCode });
    }
}

public sealed class RagnarokInstallationValidator : IRagnarokInstallationValidator
{
    public Task ValidateAsync(RagnarokInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(installation.DirectoryPath)) throw new DirectoryNotFoundException("Ragnarok installation disappeared after update.");
        foreach (var path in new[] { installation.RagexePath, installation.EasyAntiCheatPath, installation.DataIniPath })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required Ragnarok client file is missing after update.", path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        foreach (var grf in ReadGrfPaths(installation))
        {
            if (!File.Exists(grf)) throw new FileNotFoundException("A GRF referenced by data.ini is missing.", grf);
            using var stream = new FileStream(grf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) throw new InvalidDataException($"GRF '{grf}' is empty.");
        }
        return Task.CompletedTask;
    }

    internal static IReadOnlyList<string> ReadGrfPaths(RagnarokInstallation installation)
    {
        var result = new List<(int Priority, string Path)>();
        foreach (var raw in File.ReadLines(installation.DataIniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            if (!int.TryParse(line[..separator].Trim(), out var priority)) continue;
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (value.Length == 0) continue;
            result.Add((priority, Path.GetFullPath(Path.Combine(installation.DirectoryPath, value))));
        }
        if (result.Count == 0) throw new InvalidDataException("data.ini contains no GRF entries.");
        return result.OrderBy(x => x.Priority).Select(x => x.Path).ToArray();
    }
}
