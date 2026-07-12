using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Cfs.Core;

/// <summary>Windows ProjFS view of a CFS manifest. File payloads are read only when Windows requests them.</summary>
public sealed class CfsProjFsMount : IDisposable
{
    private const int S_OK = 0;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultInsufficientBuffer = unchecked((int)0x8007007A);
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileStateTombstone = 0x10;
    private const uint NotifyPreDelete = 0x00000010;
    private const uint NotifyFileDeleted = 0x00000800;
    private const uint NotifyOpen = 0x00000002;
    private const uint NotifyNewFile = 0x00000004;
    private const uint NotifyRenamed = 0x00000080;
    private const uint NotifyChangeMask = NotifyOpen | NotifyNewFile | NotifyPreDelete | NotifyFileDeleted | NotifyRenamed;
    private readonly string _archivePath;
    private readonly Dictionary<string, CfsEntry> _entries;
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<byte[]>> _hydrated = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<CfsProjFsReadRequest> _readRequests = new();
    private readonly ConcurrentDictionary<Guid, int> _enumerations = new();
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private int _notificationCount;
    private readonly StartDirectoryEnumerationCallback _startEnumeration;
    private readonly EndDirectoryEnumerationCallback _endEnumeration;
    private readonly GetDirectoryEnumerationCallback _getEnumeration;
    private readonly GetPlaceholderInfoCallback _getPlaceholder;
    private readonly GetFileDataCallback _getFileData;
    private readonly NotificationCallback _notification;
    private IntPtr _context;
    private IntPtr _notificationRoot, _notificationMapping, _startOptions;
    private bool _disposed;

    private CfsProjFsMount(string archivePath, string rootPath, IReadOnlyList<CfsEntry> manifest)
    {
        _archivePath = archivePath;
        RootPath = rootPath;
        _entries = manifest.ToDictionary(entry => Normalize(entry.Path), Clone, StringComparer.OrdinalIgnoreCase);
        BuildTree();
        _startEnumeration = StartEnumeration;
        _endEnumeration = EndEnumeration;
        _getEnumeration = GetEnumeration;
        _getPlaceholder = GetPlaceholder;
        _getFileData = GetFileData;
        _notification = Notification;
    }

    public string RootPath { get; }
    public int HydratedFileCount => _hydrated.Count;
    public IReadOnlyList<string> HydratedPaths => _hydrated.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<CfsProjFsReadRequest> ReadRequests => _readRequests.ToArray();
    public int NotificationCount => Volatile.Read(ref _notificationCount);

    /// <summary>
    /// Turns remaining projected archive files into ordinary hydrated files before the
    /// provider stops. Saving needs a stable on-disk snapshot while preserving the
    /// original CFS blocks for entries whose hashes did not change.
    /// </summary>
    public void MaterializeForSave(IReadOnlySet<string>? deletedPaths = null)
    {
        ThrowIfDisposed();
        var tombstones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Values.Where(entry => entry.Type == ArchiveEntryType.File))
        {
            var path = Path.Combine(RootPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                tombstones.Add(Path.GetFullPath(path));
                continue;
            }
            var stateResult = Native.PrjGetOnDiskFileState(path, out var state);
            if (stateResult != S_OK || (state & FileStateTombstone) != 0)
                tombstones.Add(Path.GetFullPath(path));
        }

        // Enumerate the projected namespace rather than the original manifest: Explorer may
        // have renamed, deleted, or created entries since the mount began.
        foreach (var filePath in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Normalize(Path.GetRelativePath(RootPath, filePath));
            if (deletedPaths?.Contains(relativePath) == true) continue;
            if (tombstones.Contains(Path.GetFullPath(filePath))) continue;
            if (Native.PrjGetOnDiskFileState(filePath, out var state) >= 0 && (state & FileStateTombstone) != 0)
                continue;
            _ = File.ReadAllBytes(filePath);
        }
    }

    public static CfsProjFsMount Create(string archivePath, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ProjFS mounts require Windows.");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("CFS archive was not found.", archivePath);
        if (Directory.Exists(rootPath) && Directory.EnumerateFileSystemEntries(rootPath).Any())
            throw new CfsArchiveException($"ProjFS mount root must be empty: '{rootPath}'.");

        CfsDiagnostics.Logger.WritePathEvent("projfs.initialize", archivePath, "starting");
        Directory.CreateDirectory(rootPath);
        var mount = new CfsProjFsMount(Path.GetFullPath(archivePath), Path.GetFullPath(rootPath), CfsArchive.LoadManifestEntries(archivePath));
        try
        {
            mount.Start();
            CfsDiagnostics.Logger.WritePathEvent("projfs.initialize", archivePath, "success");
            return mount;
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("projfs.initialize", ex);
            mount.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context != IntPtr.Zero)
        {
            Native.PrjStopVirtualizing(_context);
            _context = IntPtr.Zero;
        }
        if (_startOptions != IntPtr.Zero) Marshal.FreeHGlobal(_startOptions);
        if (_notificationMapping != IntPtr.Zero) Marshal.FreeHGlobal(_notificationMapping);
        if (_notificationRoot != IntPtr.Zero) Marshal.FreeHGlobal(_notificationRoot);
    }

    private void Start()
    {
        var instanceId = Guid.NewGuid();
        ThrowOnFailure(Native.PrjMarkDirectoryAsPlaceholder(RootPath, null, IntPtr.Zero, ref instanceId), "mark virtualization root");
        var callbacks = new Native.Callbacks
        {
            StartDirectoryEnumerationCallback = Marshal.GetFunctionPointerForDelegate(_startEnumeration),
            EndDirectoryEnumerationCallback = Marshal.GetFunctionPointerForDelegate(_endEnumeration),
            GetDirectoryEnumerationCallback = Marshal.GetFunctionPointerForDelegate(_getEnumeration),
            GetPlaceholderInfoCallback = Marshal.GetFunctionPointerForDelegate(_getPlaceholder),
            GetFileDataCallback = Marshal.GetFunctionPointerForDelegate(_getFileData),
            NotificationCallback = Marshal.GetFunctionPointerForDelegate(_notification)
        };
        _notificationMapping = Marshal.AllocHGlobal(Marshal.SizeOf<Native.NotificationMapping>());
        _notificationRoot = Marshal.StringToHGlobalUni(string.Empty);
        Marshal.StructureToPtr(new Native.NotificationMapping { NotificationBitMask = NotifyChangeMask, NotificationRoot = _notificationRoot }, _notificationMapping, false);
        _startOptions = Marshal.AllocHGlobal(Marshal.SizeOf<Native.StartOptions>());
        Marshal.StructureToPtr(new Native.StartOptions { NotificationMappings = _notificationMapping, NotificationMappingsCount = 1 }, _startOptions, false);
        ThrowOnFailure(Native.PrjStartVirtualizing(RootPath, ref callbacks, IntPtr.Zero, _startOptions, out _context), "start virtualization");
    }

    private int StartEnumeration(IntPtr callbackData, IntPtr enumerationId)
    {
        try { _enumerations[Marshal.PtrToStructure<Guid>(enumerationId)] = 0; return S_OK; }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private int EndEnumeration(IntPtr callbackData, IntPtr enumerationId)
    {
        _enumerations.TryRemove(Marshal.PtrToStructure<Guid>(enumerationId), out _);
        return S_OK;
    }

    private int GetEnumeration(IntPtr callbackData, IntPtr enumerationId, string? searchExpression, IntPtr buffer)
    {
        try
        {
            var data = Marshal.PtrToStructure<Native.CallbackData>(callbackData);
            var id = Marshal.PtrToStructure<Guid>(enumerationId);
            var directory = Normalize(Marshal.PtrToStringUni(data.FilePathName) ?? string.Empty);
            var entries = _children.TryGetValue(directory, out var names) ? names : [];
            var start = _enumerations.GetOrAdd(id, 0);
            if ((data.Flags & 1u) != 0) start = 0; // PRJ_CB_DATA_FLAG_ENUM_RESTART_SCAN
            for (var index = start; index < entries.Count; index++)
            {
                var name = entries[index];
                if (!string.IsNullOrEmpty(searchExpression) && !Native.PrjFileNameMatch(name, searchExpression)) continue;
                var childPath = directory.Length == 0 ? name : directory + "/" + name;
                if (_deleted.ContainsKey(childPath)) continue;
                var entry = _entries[childPath];
                var basic = ToBasicInfo(entry);
                var result = Native.PrjFillDirEntryBuffer(name, ref basic, buffer);
                if (result == HResultInsufficientBuffer)
                {
                    _enumerations[id] = index;
                    return index == start ? result : S_OK;
                }
                ThrowOnFailure(result, "enumerate projected directory");
                _enumerations[id] = index + 1;
            }
            return S_OK;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private int GetPlaceholder(IntPtr callbackData)
    {
        try
        {
            var data = Marshal.PtrToStructure<Native.CallbackData>(callbackData);
            var path = Normalize(Marshal.PtrToStringUni(data.FilePathName) ?? string.Empty);
            if (_deleted.ContainsKey(path)) return HResultFileNotFound;
            if (!_entries.TryGetValue(path, out var entry)) return HResultFileNotFound;
            var info = new Native.PlaceholderInfo { FileBasicInfo = ToBasicInfo(entry), VersionInfo = new byte[256] };
            return Native.PrjWritePlaceholderInfo(data.NamespaceVirtualizationContext, path.Replace('/', '\\'), ref info, (uint)Marshal.SizeOf<Native.PlaceholderInfo>());
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private int GetFileData(IntPtr callbackData, ulong byteOffset, uint length)
    {
        var requestedPath = string.Empty;
        try
        {
            var data = Marshal.PtrToStructure<Native.CallbackData>(callbackData);
            var path = Normalize(Marshal.PtrToStringUni(data.FilePathName) ?? string.Empty);
            requestedPath = path;
            if (_deleted.ContainsKey(path)) return HResultFileNotFound;
            if (!_entries.TryGetValue(path, out var entry) || entry.Type != ArchiveEntryType.File) return HResultFileNotFound;
            _readRequests.Enqueue(new CfsProjFsReadRequest(path, byteOffset, length));
            var bytes = _hydrated.GetOrAdd(path, _ => new Lazy<byte[]>(() => CfsArchive.ReadManifestEntry(_archivePath, entry), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            var requestedBytes = GetRequestedBytes(bytes, byteOffset, length);
            if (requestedBytes.Length == 0) return S_OK;
            var buffer = Native.PrjAllocateAlignedBuffer(data.NamespaceVirtualizationContext, (nuint)requestedBytes.Length);
            if (buffer == IntPtr.Zero) return unchecked((int)0x8007000E);
            try
            {
                Marshal.Copy(requestedBytes, 0, buffer, requestedBytes.Length);
                var result = Native.PrjWriteFileData(data.NamespaceVirtualizationContext, ref data.DataStreamId, buffer, byteOffset, (uint)requestedBytes.Length);
                if (result >= 0) CfsDiagnostics.Logger.Write("projfs.hydration", $"entry={CfsDiagnosticLogger.DescribePath(requestedPath)} offset={byteOffset} length={requestedBytes.Length} result=success");
                return result;
            }
            finally { Native.PrjFreeAlignedBuffer(buffer); }
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("projfs.hydration", ex);
            return Marshal.GetHRForException(ex);
        }
    }

    private int Notification(IntPtr callbackData, bool isDirectory, uint notification, string? destinationFileName, IntPtr operationParameters)
    {
        try
        {
            Interlocked.Increment(ref _notificationCount);
            if (notification is NotifyPreDelete or NotifyFileDeleted)
            {
                var data = Marshal.PtrToStructure<Native.CallbackData>(callbackData);
                _deleted.TryAdd(Normalize(Marshal.PtrToStringUni(data.FilePathName) ?? string.Empty), 0);
            }
            return S_OK;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private void BuildTree()
    {
        _children[string.Empty] = [];
        foreach (var entry in _entries.Values.ToList())
        {
            var parts = Normalize(entry.Path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var directory = string.Join('/', parts.Take(i));
                if (!_entries.ContainsKey(directory)) _entries[directory] = new CfsEntry { Path = directory, Type = ArchiveEntryType.Directory, LastWriteTimeUtc = entry.LastWriteTimeUtc, CompressionMethod = CfsArchive.CompressionNone };
            }
        }
        foreach (var entry in _entries.Values)
        {
            var path = Normalize(entry.Path); var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? string.Empty : path[..slash]; var name = slash < 0 ? path : path[(slash + 1)..];
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
            if (entry.Type == ArchiveEntryType.Directory && !_children.ContainsKey(path)) _children[path] = [];
        }
        foreach (var list in _children.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static Native.FileBasicInfo ToBasicInfo(CfsEntry entry) => new()
    {
        IsDirectory = entry.Type == ArchiveEntryType.Directory ? (byte)1 : (byte)0,
        FileSize = entry.OriginalSize,
        CreationTime = entry.LastWriteTimeUtc.UtcDateTime.ToFileTimeUtc(),
        LastAccessTime = entry.LastWriteTimeUtc.UtcDateTime.ToFileTimeUtc(),
        LastWriteTime = entry.LastWriteTimeUtc.UtcDateTime.ToFileTimeUtc(),
        ChangeTime = entry.LastWriteTimeUtc.UtcDateTime.ToFileTimeUtc(),
        FileAttributes = entry.Type == ArchiveEntryType.Directory ? FileAttributeDirectory : 0u
    };
    private static CfsEntry Clone(CfsEntry e) => new() { Path = e.Path, Type = e.Type, OriginalSize = e.OriginalSize, CompressedSize = e.CompressedSize, Offset = e.Offset, CompressionMethod = e.CompressionMethod, Sha256 = e.Sha256, LastWriteTimeUtc = e.LastWriteTimeUtc };
    internal static byte[] GetRequestedBytes(byte[] bytes, ulong byteOffset, uint length)
    {
        if (byteOffset >= (ulong)bytes.LongLength || length == 0) return [];
        var count = checked((int)Math.Min(length, (ulong)bytes.LongLength - byteOffset));
        return bytes.AsSpan(checked((int)byteOffset), count).ToArray();
    }
    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
    private static void ThrowOnFailure(int result, string action) { if (result < 0) Marshal.ThrowExceptionForHR(result, new IntPtr(-1)); }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CfsProjFsMount)); }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StartDirectoryEnumerationCallback(IntPtr callbackData, IntPtr enumerationId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EndDirectoryEnumerationCallback(IntPtr callbackData, IntPtr enumerationId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDirectoryEnumerationCallback(IntPtr callbackData, IntPtr enumerationId, [MarshalAs(UnmanagedType.LPWStr)] string? searchExpression, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPlaceholderInfoCallback(IntPtr callbackData);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFileDataCallback(IntPtr callbackData, ulong byteOffset, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NotificationCallback(IntPtr callbackData, [MarshalAs(UnmanagedType.U1)] bool isDirectory, uint notification, [MarshalAs(UnmanagedType.LPWStr)] string? destinationFileName, IntPtr operationParameters);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Callbacks { public IntPtr StartDirectoryEnumerationCallback, EndDirectoryEnumerationCallback, GetDirectoryEnumerationCallback, GetPlaceholderInfoCallback, GetFileDataCallback, QueryFileNameCallback, NotificationCallback, CancelCommandCallback; }
        [StructLayout(LayoutKind.Sequential)] internal struct NotificationMapping { public uint NotificationBitMask; public IntPtr NotificationRoot; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartOptions { public uint Flags, PoolThreadCount, ConcurrentThreadCount; public IntPtr NotificationMappings; public uint NotificationMappingsCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct CallbackData { public uint Size, Flags; public IntPtr NamespaceVirtualizationContext; public int CommandId; public Guid FileId, DataStreamId; public IntPtr FilePathName, VersionInfo; public uint TriggeringProcessId; public IntPtr TriggeringProcessImageFileName, InstanceContext; }
        [StructLayout(LayoutKind.Sequential)] internal struct FileBasicInfo { public byte IsDirectory; public long FileSize, CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
        // The native structure ends in VariableData[1]. Keep that byte so Marshal.SizeOf
        // matches sizeof(PRJ_PLACEHOLDER_INFO), including native trailing alignment.
        [StructLayout(LayoutKind.Sequential)] internal struct PlaceholderInfo { public FileBasicInfo FileBasicInfo; public uint EaBufferSize, OffsetToFirstEa, SecurityBufferSize, OffsetToSecurityDescriptor, StreamsInfoBufferSize, OffsetToFirstStreamInfo; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] VersionInfo; public byte VariableData; }
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern int PrjMarkDirectoryAsPlaceholder(string root, string? target, IntPtr versionInfo, ref Guid instanceId);
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern int PrjStartVirtualizing(string root, ref Callbacks callbacks, IntPtr instanceContext, IntPtr options, out IntPtr context);
        [DllImport("ProjectedFSLib.dll")] internal static extern void PrjStopVirtualizing(IntPtr context);
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern int PrjFillDirEntryBuffer(string name, ref FileBasicInfo info, IntPtr buffer);
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern bool PrjFileNameMatch(string name, string pattern);
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern int PrjWritePlaceholderInfo(IntPtr context, string path, ref PlaceholderInfo info, uint size);
        [DllImport("ProjectedFSLib.dll")] internal static extern IntPtr PrjAllocateAlignedBuffer(IntPtr context, nuint size);
        [DllImport("ProjectedFSLib.dll")] internal static extern void PrjFreeAlignedBuffer(IntPtr buffer);
        [DllImport("ProjectedFSLib.dll")] internal static extern int PrjWriteFileData(IntPtr context, ref Guid dataStreamId, IntPtr buffer, ulong offset, uint length);
        [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)] internal static extern int PrjGetOnDiskFileState(string path, out uint fileState);
    }
}

public sealed record CfsProjFsReadRequest(string Path, ulong ByteOffset, uint Length);
