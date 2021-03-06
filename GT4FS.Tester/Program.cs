﻿using CommandLine;
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

            /* Wouldn't work when set to 0x1000 (boot crash), not sure why. Needs more debugging.
            [Option('b', "block-size", HelpText = "Advanced Users. Sets the file system's block size. Default is 0x800/2048.")]
            public ushort BlockSize { get; set; } */

            [Option('d', "decrypted", HelpText = "Build the volume without header encryption. Default is encrypted.")]
            public bool Decrypted { get; set; }

            [Option("no-compress", HelpText = "Build the volume without encryption. (Speeds up packing but overall volume size is greatly increased!)")]
            public bool NoCompress { get; set; }
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

            Parser.Default.ParseArguments<InfoOptions, ExtractOptions, PackOptions>(args)
                .MapResult(
                (InfoOptions opts) => RunInfoAndReturnExitCode(opts),
                (ExtractOptions opts) => RunAndReturnExitCode(opts),
                (PackOptions opts) => RunPackAndReturnExitCode(opts),
                errs => 1);

            if (cmdWait)
            {
                Console.WriteLine("Press any key to exit...");
                Console.ReadLine();
            }
        }

        private static object RunInfoAndReturnExitCode(InfoOptions options)
        {
            try
            {
                var fileLoader = new FileLoader(options.Input);
                var fileType = fileLoader.GetFileType();

                switch (fileType)
                {
                    case FileType.TOC31_VOL:
                    case FileType.TOC31_ISO:
                        foreach (var (stream, fileName) in fileLoader.GetStreams())
                        {
                            using (stream)
                            {
                                var volume = new Volume(stream);
                                volume.ReadVolume();
                                var logWriter = options.Verbose ? new ConsoleWriter() : null;
                                using (var btree = new BTree(volume, logWriter))
                                {
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

                                    Console.WriteLine($"Writing file list for {fileName}...");
                                    btree.WriteFileList(options.Output, volName: fileName, debugInfo: options.Debug);
                                }
                            }
                        }
                        return 0;
                    case FileType.TOC22_VOL:
                    case FileType.TOC22_ISO:
                        Console.WriteLine("There are other tools that can handle extraction of this type of VOLs. Please use those (for example the one made by pez2k).");
                        return 0;
                    case FileType.GTPSP_VOL:
                    case FileType.GTPSP_ISO:
                        Console.WriteLine("Gran Turismo PSP versions aren't supported by this tool. Wait for the next one ;)");
                        return 0;
                    case FileType.UNKNOWN:
                    default:
                        Console.WriteLine("Unknown game type.");
                        return 0;
                }
            }
            catch (ArgumentException aex)
            {
                Console.WriteLine(aex.Message);
                return 1;
            }
            catch (InvalidFileSystemException fsex)
            {
                Console.WriteLine(fsex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static object RunAndReturnExitCode(ExtractOptions options)
        {
            try
            {
                var fileLoader = new FileLoader(options.Input);
                var fileType = fileLoader.GetFileType();

                switch (fileType)
                {
                    case FileType.TOC31_VOL:
                    case FileType.TOC31_ISO:
                        foreach (var (stream, fileName) in fileLoader.GetStreams())
                        {
                            using (stream)
                            {
                                var volume = new Volume(stream);
                                volume.ReadVolume();
                                var logWriter = options.Verbose ? new ConsoleWriter() : null;
                                using (var btree = new BTree(volume, logWriter))
                                {
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

                                    Console.WriteLine($"Extracting files from {fileName}...");
                                    btree.ExtractAllFiles(options.Output, volName: fileName, overwrite: options.Overwrite);
                                }
                            }
                        }
                        return 0;
                    case FileType.TOC22_VOL:
                    case FileType.TOC22_ISO:
                        Console.WriteLine("There are other tools that can handle extraction of this type of VOLs. Please use those (for example the one made by pez2k).");
                        return 0;
                    case FileType.GTPSP_VOL:
                    case FileType.GTPSP_ISO:
                        Console.WriteLine("Gran Turismo PSP versions aren't supported by this tool. Wait for the next one ;)");
                        return 0;
                    case FileType.UNKNOWN:
                    default:
                        Console.WriteLine("Unknown game type.");
                        return 0;
                }
            }
            catch (ArgumentException aex)
            {
                Console.WriteLine(aex.Message);
                return 1;
            }
            catch (InvalidFileSystemException fsex)
            {
                Console.WriteLine(fsex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static object RunPackAndReturnExitCode(PackOptions options)
        {
            if (!Enum.TryParse(options.GameType, out GameVolumeType game) || game is GameVolumeType.Unknown)
            {
                Console.WriteLine("Error: Invalid game type provided.");
                return 1;
            }

            uint tocOffset;
            if (game == GameVolumeType.CUSTOM)
            {
                if (options.TocOffset <= -1)
                {
                    Console.WriteLine("Error: No custom toc offset provided.");
                    return 1;
                }

                tocOffset = (uint)options.TocOffset;
            }
            else
                tocOffset = RoFSBuilder.GetRealToCOffsetForGame(game);

            var fsBuilder = new RoFSBuilder();
            /*
            if (options.BlockSize != 0)
                fsBuilder.SetBlockSize(options.BlockSize);
            */

            fsBuilder.SetCompressed(!options.NoCompress);
            fsBuilder.SetEncrypted(!options.Decrypted);
            fsBuilder.RegisterFilesToPack(options.Input);
            fsBuilder.Build(options.Output, tocOffset);

            return 0;
        }

        private static bool PrintEasterEgg()
        {
            string coolstory = @"New Team.
New Rules.
New Release-Platform.
New Tools.


GT4FS Extractor/Packer 3.0, by team eventHorizon";

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
