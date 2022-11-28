using CommandLine;
using DiscUtils;
using GT.Shared.Enums;
using GT.Shared.FileSystem;
using GT.Shared.Logging;
using GT4FS.Core;
using System;
using System.IO;

using GT4FS.Core.Packing;

namespace GT4FS.Tester
{
    class Program
    {
        [Verb("info", HelpText = "Print out information.")]
        class InfoOptions
        {
            [Option('r', "read", Required = true, HelpText = "Input file to be processed (GT.VOL file).")]
            public string Input { get; set; }

            [Option('o', "output", Required = false, HelpText = "Directory to output to (Default: information).")]
            public string Output { get; set; }

            [Option('d', "debug", Required = false, HelpText = "Write debug information (You most likely don't want this).")]
            public bool Debug { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose { get; set; }
        }

        [Verb("extract", HelpText = "Extract the GT4, GTHD, TT game content.")]
        class ExtractOptions
        {
            [Option('r', "read", Required = true, HelpText = "Input file to be processed (GT.VOL file).")]
            public string Input { get; set; }

            [Option('o', "output", Required = false, HelpText = "Directory to extract to (Default: extracted).")]
            public string Output { get; set; }

            [Option('f', "force", Required = false, HelpText = "Overwrite any files if they already exist when extracting?")]
            public bool Overwrite { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose { get; set; }
        }

        [Verb("pack", HelpText = "Pack GT4, GTHD, TT game content.")]
        class PackOptions
        {
            [Option('r', "read", Required = true, HelpText = "Input folder to be processed (folder to pack).")]
            public string Input { get; set; }

            [Option('o', "output", HelpText = "File to pack to (Default: GTNew.VOL).")]
            public string Output { get; set; } = "GTNew.VOL";

            [Option('g', "game", Required = true, HelpText = "Target game to pack the volume for. " +
                "Supported: GT4, GT4_ONLINE, GTHD, TT, TT_DEMO, GT4_MX5_DEMO, GT4_FIRST_PREV, or CUSTOM for a custom one (use --toc-offset).")]
            public string GameType { get; set; }

            [Option("toc-offset", HelpText = "Toc offset to use when packing as custom game type.")]
            public int TocOffset { get; set; } = -1;

            /* Wouldn't work when set to 0x1000 (boot crash), not sure why. Needs more debugging. Probably something that is required to be on a 0x800 alignment basis.
            [Option('b', "page-size", HelpText = "Advanced Users. Sets the file system's page size. Default is 0x800/2048.")]
            public ushort PageSize { get; set; } */

            [Option('d', "decrypted", HelpText = "Build the volume without header encryption. Default is encrypted.")]
            public bool Decrypted { get; set; }

            [Option("no-compress", HelpText = "Build the volume without compression. (Speeds up packing but overall volume size is greatly increased!)")]
            public bool NoCompress { get; set; }

            [Option("no-merge", HelpText = "Build the volume and avoids merging data and ToC together. Optional - speeds up building by skipping merge part. Do not use Apache3 for this (broken).")]
            public bool NoMerge { get; set; }
        }

        [Verb("pack-append", HelpText = "Same as 'pack', but will append to the existing VOL instead. Makes VOL edits almost instant, but MAKE A BACKUP OF YOUR ORIGINAL VOL! " +
            "Do not use Apache3 for this (broken). You can keep appending files to the VOL afterwards.")]
        class PackAppendOptions
        {
            [Option('r', "read", Required = true, HelpText = "Input file to be processed (GT.VOL file). Warning: It will be edited.")]
            public string Input { get; set; }

            [Option('a', "append", Required = true, HelpText = "Folder with game contents to append to the VOL file. ONLY edited/added files from the game goes there, not the whole folder. " +
                "Must match game directory structure to replace files.")]
            public string AppendFolder { get; set; }
        }

        static void Main(string[] args)
        {
            bool cmdWait = false;
            if (args.Length <= 0)
            {
                if (!PrintEasterEgg())
                    return;

                cmdWait = true;
            }

            Parser.Default.ParseArguments<InfoOptions, ExtractOptions, PackOptions, PackAppendOptions>(args)
                .WithParsed<InfoOptions>(RunInfo)
                .WithParsed<ExtractOptions>(RunExtract)
                .WithParsed<PackOptions>(RunPack)
                .WithParsed<PackAppendOptions>(RunPackAppend);

            if (cmdWait)
            {
                Console.WriteLine("Press any key to exit...");
                Console.ReadLine();
            }
        }

        private static void RunInfo(InfoOptions options)
        {
            BTree btree = GetBTree(options.Input, options.Verbose ? new ConsoleWriter() : null);
            if (btree is null)
                return;

            // Output check
            if (string.IsNullOrEmpty(options.Output))
            {
                FileAttributes attr = File.GetAttributes(options.Input);
                if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    DirectoryInfo parentDir = Directory.GetParent(options.Input);
                    options.Output = Path.Combine(parentDir.FullName, "information");
                }
                else
                {
                    var folder = Path.GetDirectoryName(options.Input);
                    options.Output = Path.Combine(folder, "information");
                }
            }

            Console.WriteLine($"Writing file list for {options.Input}...");
            btree.WriteFileList(options.Output, volName: options.Input, debugInfo: options.Debug);
        }

        private void DebugRead(string volFile, int tocPageOffset, string volPath)
        {
            DebugReader rdr = DebugReader.FromVolume(volFile, tocPageOffset);
            rdr.TraversePathFindEntry(1, volPath);
        }

        private static void RunExtract(ExtractOptions options)
        {
            BTree btree = GetBTree(options.Input, options.Verbose ? new ConsoleWriter() : null);
            if (btree is null)
                return;

            // Output check
            if (string.IsNullOrEmpty(options.Output))
            {
                FileAttributes attr = File.GetAttributes(options.Input);
                if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    DirectoryInfo parentDir = Directory.GetParent(options.Input);
                    options.Output = Path.Combine(parentDir.FullName, "extracted");
                }
                else
                {
                    var folder = Path.GetDirectoryName(options.Input);
                    options.Output = Path.Combine(folder, "extracted");
                }
            }

            Console.WriteLine($"Extracting files from {options.Input}...");
            btree.ExtractAllFiles(options.Output, volName: options.Input, overwrite: options.Overwrite);
        }

        private static void RunPack(PackOptions options)
        {
            if (!Enum.TryParse(options.GameType, out GameVolumeType game) || game is GameVolumeType.Unknown)
            {
                Console.WriteLine("Error: Invalid game type provided.");
                return;
            }

            uint tocOffset;
            if (game == GameVolumeType.CUSTOM)
            {
                if (options.TocOffset <= -1)
                {
                    Console.WriteLine("Error: No custom toc offset provided.");
                    return;
                }

                tocOffset = (uint)options.TocOffset;
            }
            else
                tocOffset = RoFSBuilder.GetRealToCOffsetForGame(game);

            var fsBuilder = new RoFSBuilder();
            /*
            if (options.PageSize != 0)
                fsBuilder.SetPageSize(options.PageSize);
            */

            fsBuilder.SetCompressed(!options.NoCompress);
            fsBuilder.SetEncrypted(!options.Decrypted);
            fsBuilder.SetNoMergeTocMode(options.NoMerge);
            fsBuilder.RegisterFilesToPack(options.Input);
            fsBuilder.Build(options.Output, tocOffset);
        }

        private static void RunPackAppend(PackAppendOptions options)
        {
            var btree = GetBTree(options.Input);
            if (btree is null)
                return;

            RoFSBuilder fsBuilder = new RoFSBuilder();
            fsBuilder.RegisterFilesFromBTree(btree, options.AppendFolder);
            btree.Dispose();

            fsBuilder.SetAppendMode(true, btree.GetBaseDataOffset());
            fsBuilder.Build(options.Input, (uint)btree.GetRealToCOffset());
        }

        private static BTree GetBTree(string file, ILogWriter logWriter = null)
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


GT4FS Extractor/Packer 3.2.1, by team eventHorizon";

            Console.WriteLine(coolstory);
            Console.Write("\nDo you agree? (y/n): ");
            var input = Console.ReadLine();
            if (input.Contains("y"))
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
}
