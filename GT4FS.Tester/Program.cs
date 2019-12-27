using CommandLine;
using GT.Shared.Logging;
using GT4FS.Core;
using System.IO;

namespace GT4FS.Tester {
    class Program {
        [Verb("extract", HelpText = "Extract the GT4, GTHD, TT game content.")]
        class ExtractOptions {
            [Option('r', "read", Required = true, HelpText = "Input file to be processed (GT.VOL file).")]
            public string Input { get; set; }

            [Option('o', "output", Required = false, HelpText = "Directory to extract to (Default: extracted).")]
            public string Output { get; set; }

            [Option('f', "force", Required = false, HelpText = "Overwrite any files if they already exist when extracting?")]
            public bool Overwrite { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose { get; set; }
        }

        static void Main(string[] args) {
            Parser.Default.ParseArguments<ExtractOptions>(args)
                .MapResult(
                (ExtractOptions opts) => RunAndReturnExitCode(opts),
                errs => 1);
        }

        private static object RunAndReturnExitCode(ExtractOptions options) {
            var volume = new Volume(options.Input);
            volume.ReadVolume();
            var logWriter = options.Verbose ? new ConsoleWriter() : null;
            using (var btree = new BTree(volume, logWriter)) {
                // Output check
                if (string.IsNullOrEmpty(options.Output)) {
                    FileAttributes attr = File.GetAttributes(options.Input);
                    if ((attr & FileAttributes.Directory) == FileAttributes.Directory) {
                        DirectoryInfo parentDir = Directory.GetParent(options.Input);
                        options.Output = Path.Combine(parentDir.FullName, "extracted");
                    }
                    else {
                        var folder = Path.GetDirectoryName(options.Input);
                        options.Output = Path.Combine(folder, "extracted");
                    }
                }

                btree.ExtractAllFiles(options.Output, options.Overwrite);
                return 0;
            }
        }
    }
}
