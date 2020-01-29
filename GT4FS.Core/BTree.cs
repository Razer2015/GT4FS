using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using GT.Shared.Threading;
using GT.Shared.Logging;
using System.Diagnostics;
using GT.Shared;
using GT.Shared.Polyphony;

namespace GT4FS.Core {
    public class BTree : IDisposable {
        private readonly Volume _volume;
        private readonly QueueWriter _queueWriter;
        public List<Node> Nodes { get; set; }
        private IEnumerable<NodeEntry> _nodeEntries;

        public BTree(Volume volume, ILogWriter logWriter = null) {
            _volume = volume;
            Nodes = new List<Node>();
            _queueWriter = logWriter != null ? new QueueWriter(logWriter) : null;
        }

        public bool ExtractAllFiles(string outputPath, string volName = null, bool overwrite = false) {
            Directory.CreateDirectory(outputPath);
            using (var sw = new StreamWriter(Path.Combine(outputPath, $"{(string.IsNullOrEmpty(volName) ? "" : $"{ volName }_")}extract.log"), true)) {
                sw.WriteLine(string.Format("Extraction started: {0}", DateTime.Now.ToString()));
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                _volume.VOLReader.Endianess = EndianType.BIG_ENDIAN;
                var files = GetAllFiles();
                foreach (var file in files) {
                    var destPath = Path.Combine(outputPath, file.Path).Replace("/", "\\");
                    if (ExtractFile(_volume.VOLReader, file.Offset, file.PackedSize, file.RealSize, destPath, out var sourcePath, overwrite))
                        sw.WriteLine(string.Format("Succesfully extracted: {0} to {1}", sourcePath, destPath));
                    else
                        sw.WriteLine(string.Format("Failed to extract: {0} to {1}", sourcePath, destPath));
                }

                _queueWriter?.Enqueue("All files extracted!");

                stopwatch.Stop();
                // Get the elapsed time as a TimeSpan value.
                TimeSpan ts = stopwatch.Elapsed;

                // Format and display the TimeSpan value.
                string elapsedTime = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
                    ts.Hours, ts.Minutes, ts.Seconds,
                    ts.Milliseconds / 10);
                sw.WriteLine("Time elapsed for extraction: " + elapsedTime);
                sw.WriteLine(string.Empty);
            }

            return true;
        }

        public bool WriteFileList(string outputPath, string volName = null, bool debugInfo = false) {
            if (_nodeEntries == null) {
                Read();
            }

            var path = Path.Combine(outputPath, $"{(string.IsNullOrEmpty(volName) ? "" : $"{volName}_")}filelist.txt");
            Directory.CreateDirectory(outputPath);
            _queueWriter?.Enqueue("Writing file list...");
            using (var sw = new StreamWriter(path, false)) {
                sw.WriteLine("###################################");
                sw.WriteLine("#   Made by - Team eventHorizon   #");
                sw.WriteLine("#    GT4FS - File list creator    #");
                sw.WriteLine("###################################");

                sw.WriteLine(".");
                var folders = new List<string>();
                TraverseNodes(_nodeEntries.ToList(), sw, 1, 1, folders, debugInfo);
            }
            Console.WriteLine($"File list written to {path}");

            return true;
        }

        private void TraverseNodes(IList<NodeEntry> nodeEntries, StreamWriter sw, int parentNodeID, int depth, List<string> prefixFolders, bool debugInfo) {
            var nodes = nodeEntries.Where(x => x.ParentNode == parentNodeID);
            for (int i = 0; i < nodes.Count(); i++) {
                var node = nodes.ElementAtOrDefault(i);

                switch (node.Flag) {
                    case 0x00: // Dir
                        var folders = new List<string>(prefixFolders);
                        if (nodes.Count() - 1 > i) {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name}");
                            folders.Add("│   ");
                        }
                        else {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name}");
                            folders.Add("    ");
                        }
                        TraverseNodes(nodeEntries, sw, node.NodeID, depth++, folders, debugInfo);
                        break;
                    case 0x01: // File
                    case 0x02: // Compressed file
                        if (nodes.Count() - 1 > i) {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name}{(debugInfo ? $" (Offset: 0x{_volume.GetFileOffset(node.PageOffset).ToString("X8")} - Size: 0x{node.PackedSize.ToString("X8")} - RealSize: 0x{node.RealSize.ToString("X8")} - Unk: 0x{node.Unk.ToString("X8")})" : "")}");
                        }
                        else {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name}{(debugInfo ? $" (Offset: 0x{_volume.GetFileOffset(node.PageOffset).ToString("X8")} - Size: 0x{node.PackedSize.ToString("X8")} - RealSize: 0x{node.RealSize.ToString("X8")} - Unk: 0x{node.Unk.ToString("X8")})" : "")}");
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException($"Unknown flag {node.Flag}");
                }
            }
        }

        private void Read() {
            foreach (var (Offset, Length) in _volume.Blocks) {
                var buffer = _volume.DecryptBlock(Offset, Length);
                Nodes.Add(new Node(buffer));
            }

            _nodeEntries = Nodes.Where(x => x.Flag == 0).SelectMany(x => x.NodeEntries);
        }

        private List<(string Path, long Offset, uint PackedSize, uint RealSize)> GetAllFiles() {
            if (_nodeEntries == null) {
                Read();
            }
            var files = new List<(string Path, long Offset, uint PackedSize, uint RealSize)>();
            foreach (var nodeEntry in _nodeEntries.Where(x => x.Flag != 0)) {
                files.Add((BuildPath(nodeEntry), _volume.GetFileOffset(nodeEntry.PageOffset), nodeEntry.PackedSize, nodeEntry.RealSize));
            }

            return files;
        }

        private string BuildPath(NodeEntry nodeEntry) {
            if (nodeEntry.ParentNode == 0) {
                return string.Empty;
            }
            return Path.Combine(BuildPath(_nodeEntries.FirstOrDefault(x => x.NodeID == nodeEntry.ParentNode)), nodeEntry.Name);
        }

        private bool ExtractFile(EndianBinReader reader, long offset, uint packedSize, uint realSize, string destPath, out string sourcePath, bool overWrite) {
            sourcePath = $"VOL offset {offset.ToString("X8")} - size {packedSize.ToString("X8")}";
            try {
                if (File.Exists(destPath) && !overWrite) {
                    return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                reader.BaseStream.Position = offset;
                var data = reader.ReadBytes((int)packedSize);
                if (Util.DataAtUInt32(data, 0) == 0xC5EEF7FFu)
                    data = PS2Zip.Inflate(data);

                Debug.Assert(data.Length == realSize);

                File.WriteAllBytes(destPath, data);

                _queueWriter?.Enqueue($"Extacted: {destPath}");

                return true;
            }
            catch (Exception e) {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public void Dispose() {
            _queueWriter?.Dispose();
        }
    }
}
