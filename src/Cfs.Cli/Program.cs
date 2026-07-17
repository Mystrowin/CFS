using Cfs.Core;

var controlledTestLogDirectory = Environment.GetEnvironmentVariable("CFS_TEST_LOG_DIRECTORY");
if (!string.IsNullOrWhiteSpace(controlledTestLogDirectory))
    CfsDiagnostics.Logger = new CfsDiagnosticLogger(controlledTestLogDirectory);

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "create" when args.Length == 3:
            CfsArchive.CreateFromFolder(args[1], args[2]);
            Console.WriteLine($"Created {args[2]}");
            return 0;

        case "create-empty" when args.Length == 2:
            CfsArchive.CreateEmpty(args[1]);
            Console.WriteLine($"Created empty archive {args[1]}");
            return 0;

        case "list" when args.Length == 2:
            foreach (var entry in CfsArchive.Load(args[1]).ListEntries())
            {
                Console.WriteLine($"{entry.Type,-9} {entry.OriginalSize,10} {entry.Path}");
            }

            return 0;

        case "manifest" when args.Length == 2:
            foreach (var entry in CfsArchive.LoadManifestEntries(args[1]))
            {
                Console.WriteLine($"{entry.Type}\t{entry.OriginalSize}\t{entry.CompressedSize}\t{entry.Offset}\t{entry.CompressionMethod}\t{entry.Path}");
            }

            return 0;

        case "extract" when args.Length == 3:
            CfsArchive.Load(args[1]).ExtractAll(args[2]);
            Console.WriteLine($"Extracted to {args[2]}");
            return 0;

        case "extract-file" when args.Length == 4:
            CfsArchive.Load(args[1]).ExtractFile(args[2], args[3]);
            Console.WriteLine($"Extracted {args[2]} to {args[3]}");
            return 0;

        case "validate" when args.Length == 2:
            var result = CfsArchive.Validate(args[1]);
            Console.WriteLine(result.IsValid
                ? $"Valid: {result.FileCount} files, {result.DirectoryCount} folders"
                : $"Invalid: {result.Message}");
            return result.IsValid ? 0 : 3;

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("CFS CLI");
    Console.WriteLine("  create <folder> <archive.cfs>");
    Console.WriteLine("  create-empty <archive.cfs>");
    Console.WriteLine("  list <archive.cfs>");
    Console.WriteLine("  manifest <archive.cfs>");
    Console.WriteLine("  extract <archive.cfs> <output-folder>");
    Console.WriteLine("  extract-file <archive.cfs> <path> <output-file>");
    Console.WriteLine("  validate <archive.cfs>");
}
