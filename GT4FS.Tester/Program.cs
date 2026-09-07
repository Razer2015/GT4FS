using DiscUtils;

using GT.Shared.Enums;
using GT.Shared.FileSystem;
using GT.Shared.Logging;

using GT4FS.Core;
using GT4FS.Core.Packing;

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

namespace GT4FS.Tester;

public class Program
{
    static async Task<int> Main(string[] args)
    {
        // Uncomment for debug reading
        // Check debug logs tab in VS.
        // var reader = DebugReader.FromVolume(@"GTNew.VOL", (int)RoFSBuilder.GetRealToCOffsetForGame(GameVolumeType.GT4_ONLINE) * 0x800);
        // var ent = reader.TraversePathFindEntry(1, "dnas/auth_dna_beta.dat");

        bool cmdWait = false;
        if (args.Length <= 0)
        {
            if (!PrintEasterEgg())
                return 0;

            cmdWait = true;
        }

        var infoCommand = new Command("info", "Print out information.")
        {
            new Option<string>("--read", aliases: ["-r"]) { Required = true, Description = "Input file to be processed (GT.VOL file)." },
            new Option<string>("--output", aliases: ["-o"]) { Description = "Directory to output to (Default: information)." },
            new Option<bool>("--force", ["-f"]) { Description = "Overwrite any files if they already exist when extracting?" },
            new Option<bool>("--verbose", ["-v"]) { Description = "Set output to verbose messages." },
        };
        infoCommand.SetAction(RunInfo);

        var extractCommand = new Command("extract", "Extract the GT4, GTHD, TT game content.")
        {
            new Option<string>("--read", aliases: ["-r"]) { Required = true, Description = "Input file to be processed (GT.VOL file)." },
            new Option<string>("--output", aliases: ["-o"]) { Description = "Directory to extract to (Default: extracted)." },
            new Option<bool>("--debug", ["-d"]) { Description = "Write debug information (You most likely don't want this)." },
            new Option<bool>("--verbose", ["-v"]) { Description = "Set output to verbose messages." },
        };
        extractCommand.SetAction(RunExtract);

        var packCommand = new Command("pack", "Pack GT4, GTHD, TT game content.")
        {
            new Option<string>("--read", aliases: ["-r"]) { Required = true, Description = "Input folder to be processed (folder to pack)." },
            new Option<string>("--output", aliases: ["-o"]) { Description = "File to pack to (Default: GTNew.VOL).", DefaultValueFactory = (e) => "GTNew.VOL" },
            new Option<string>("--game", ["-g"]) { Required = true, Description = "Target game to pack the volume for. " +
                "Supported: GT4, GT4_ONLINE, GTHD, TT, TT_DEMO, GT4_MX5_DEMO, GT4_FIRST_PREV, or CUSTOM for a custom one (use --toc-offset)." },
            new Option<int>("--toc-offset") { Description = "Toc offset to use when packing as custom game type.",
                DefaultValueFactory = (e) => -1
            },
            /* Wouldn't work when set to 0x1000 (boot crash), not sure why. Needs more debugging. Probably something that is required to be on a 0x800 alignment basis.
             * new Option<ushort>("--page-size") { Description = "Advanced Users. Sets the file system's page size. Default is 0x800/2048."}, 
             */
            new Option<bool>("--decrypted", ["-d"]) { Description = "Build the volume without header encryption. Default is decrypted (game's default is encrypted).",
                DefaultValueFactory = (e) => true
            },
            new Option<bool>("--no-compress") { Description = "Build the volume without compression. (Speeds up packing but overall volume size is greatly increased!)" },
            new Option<bool>("--no-merge") { Description = "Build the volume and avoids merging data and ToC together. Optional - speeds up building by skipping merge part. Do not use Apache3 for this (broken)." },
            new Option<bool>("--verbose", ["-v"]) { Description = "Set output to verbose messages." },
        };
        packCommand.SetAction(RunPack);

        var packAppendCommand = new Command("pack-append", "Same as 'pack', but will append to the existing VOL instead. Makes VOL edits almost instant, but MAKE A BACKUP OF YOUR ORIGINAL VOL!\n" +
            "Do not use Apache3 for this (broken). You can keep appending files to the VOL afterwards.")
        {
            new Option<string>("--read", aliases: ["-r"]) { Required = true, Description = "Input file to be processed (GT.VOL file). Warning: It will be edited." },
            new Option<string>("--append", aliases: ["-a"]) { Required = true, Description = "Folder with game contents to append to the VOL file. ONLY edited/added files from the game goes there, not the whole folder.\n" +
                "Must match game directory structure to replace files." },
        };
        packAppendCommand.SetAction(RunPackAppend);

        var rootCommand = new RootCommand("GT4FS")
        {
            infoCommand,
            extractCommand,
            packCommand,
            packAppendCommand,
        };

        int exitCode = await rootCommand.Parse(args).InvokeAsync();

        if (cmdWait)
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadLine();
        }

        return exitCode;
    }

    private static void RunInfo(ParseResult parseResult)
    {
        string? inputPath = parseResult.GetValue<string>("--read");
        string? outputPath = parseResult.GetValue<string>("--output");
        bool verbose = parseResult.GetValue<bool>("--verbose");
        bool debug = parseResult.GetValue<bool>("--debug");

        if (string.IsNullOrWhiteSpace(inputPath))
            return;

        BTree? btree = GetBTree(inputPath, verbose ? new ConsoleWriter() : null);
        if (btree is null)
            return;

        // Output check
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            FileAttributes attr = File.GetAttributes(inputPath);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                DirectoryInfo? parentDir = Directory.GetParent(inputPath);
                outputPath = Path.Combine(parentDir.FullName, "information");
            }
            else
            {
                var folder = Path.GetDirectoryName(inputPath);
                outputPath = Path.Combine(folder, "information");
            }
        }

        Console.WriteLine($"Writing file list for {inputPath}...");
        btree.WriteFileList(outputPath, volName: Path.GetFileName(inputPath), debugInfo: debug);
    }

    private void DebugRead(string volFile, int tocPageOffset, string volPath)
    {
        DebugReader rdr = DebugReader.FromVolume(volFile, tocPageOffset);
        rdr.TraversePathFindEntry(1, volPath);
    }

    private static void RunExtract(ParseResult parseResult)
    {
        string? inputPath = parseResult.GetValue<string>("--read");
        string? outputPath = parseResult.GetValue<string>("--output");
        bool verbose = parseResult.GetValue<bool>("--verbose");
        bool overwrite = parseResult.GetValue<bool>("--force");

        if (string.IsNullOrWhiteSpace(inputPath))
            return;

        BTree? btree = GetBTree(inputPath, verbose ? new ConsoleWriter() : null);
        if (btree is null)
            return;

        // Output check
        if (string.IsNullOrEmpty(outputPath))
        {
            FileAttributes attr = File.GetAttributes(inputPath);
            if(attr.HasFlag(FileAttributes.Directory))
            {
                DirectoryInfo? parentDir = Directory.GetParent(inputPath);
                outputPath = Path.Combine(parentDir.FullName, "extracted");
            }
            else
            {
                string? folder = Path.GetDirectoryName(inputPath);
                outputPath = Path.Combine(folder, "extracted");
            }
        }

        Console.WriteLine($"Extracting files from {inputPath}...");
        btree.ExtractAllFiles(outputPath, volName: Path.GetFileName(inputPath), overwrite: overwrite);
    }

    private static void RunPack(ParseResult parseResult)
    {
        string? inputPath = parseResult.GetValue<string>("--read");
        string? outputPath = parseResult.GetValue<string>("--output");
        string? gameType = parseResult.GetValue<string>("--game");
        int inputTocOffset = parseResult.GetValue<int>("--toc-offset");
        // ushort pageSize = parseResult.GetValue<ushort>("--page-size");

        bool noCompress = parseResult.GetValue<bool>("--no-compress");
        bool decrypted = parseResult.GetValue<bool>("--decrypted");
        bool noMerge = parseResult.GetValue<bool>("--no-merge");

        if (!Enum.TryParse(gameType, out GameVolumeType game) || game is GameVolumeType.Unknown)
        {
            Console.WriteLine("Error: Invalid game type provided.");
            return;
        }

        uint tocOffset;
        if (game == GameVolumeType.CUSTOM)
        {
            if (inputTocOffset <= -1)
            {
                Console.WriteLine("Error: No custom toc offset provided.");
                return;
            }

            tocOffset = (uint)inputTocOffset;
        }
        else
            tocOffset = RoFSBuilder.GetRealToCOffsetForGame(game);

        var fsBuilder = new RoFSBuilder();
        /*
        if (pageSize != 0)
            fsBuilder.SetPageSize(pageSize);
        */

        fsBuilder.SetCompressed(!noCompress);
        fsBuilder.SetEncrypted(!decrypted);
        fsBuilder.SetNoMergeTocMode(noMerge);
        fsBuilder.RegisterFilesToPack(inputPath);
        fsBuilder.Build(outputPath, tocOffset);
    }

    private static void RunPackAppend(ParseResult parseResult)
    {
        string? inputPath = parseResult.GetValue<string>("--read");
        string? appendFolder = parseResult.GetValue<string>("--append");

        BTree? btree = GetBTree(inputPath);
        if (btree is null)
            return;

        RoFSBuilder fsBuilder = new RoFSBuilder();
        fsBuilder.RegisterFilesFromBTree(btree, appendFolder);
        btree.Dispose();

        fsBuilder.SetAppendMode(true, btree.GetBaseDataOffset());
        fsBuilder.Build(inputPath, (uint)btree.GetRealToCOffset());
    }

    private static BTree? GetBTree(string? file, ILogWriter? logWriter = null)
    {
        try
        {
            var fileLoader = new FileLoader(file);
            var fileType = fileLoader.GetFileType();

            switch (fileType)
            {
                case FileType.TOC31_VOL:
                case FileType.TOC31_ISO:
                    foreach (var (stream, fileName) in fileLoader.GetStreams())
                    {
                        var volume = new Volume(stream);
                        volume.ReadVolume();
                        return new BTree(volume, logWriter);
                    }
                    return null;
                case FileType.TOC22_VOL:
                case FileType.TOC22_ISO:
                    Console.WriteLine("There are other tools that can handle extraction of this type of VOLs. Please use those (for example the one made by pez2k).");
                    return null;
                case FileType.GTPSP_VOL:
                case FileType.GTPSP_ISO:
                    Console.WriteLine("Gran Turismo PSP versions aren't supported by this tool. Wait for the next one ;)");
                    return null;
                case FileType.UNKNOWN:
                default:
                    Console.WriteLine("Unknown game type.");
                    return null;
            }
        }
        catch (ArgumentException aex)
        {
            Console.WriteLine(aex.Message);
        }
        catch (InvalidFileSystemException fsex)
        {
            Console.WriteLine(fsex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }

        return null;
    }


    private static bool PrintEasterEgg()
    {
        string coolstory = @"New Team.
New Rules.
New Release-Platform.
New Tools.


GT4FS Extractor/Packer 3.4.0, by team eventHorizon";

        Console.WriteLine(coolstory);
        Console.Write("\nDo you agree? (y/n): ");
        string? input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input) && input.Contains('y'))
        {
            Console.Clear();
            return true;
        }
        Console.Clear();

        Console.WriteLine(@"Djinn:
We have some unfinished business.");
        Console.ReadLine();

        return false;
    }
}
