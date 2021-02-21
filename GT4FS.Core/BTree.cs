using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using GT.Shared.Threading;
using GT.Shared.Logging;
using System.Diagnostics;
using System.Globalization;

using GT.Shared;

using Syroot.BinaryData;

namespace GT4FS.Core {
    public class BTree : IDisposable
    {
        private readonly Volume _volume;
        private readonly QueueWriter _queueWriter;
        public List<Node> Nodes { get; set; }
        private IEnumerable<NodeEntry> _nodeEntries;

        public BTree(Volume volume, ILogWriter logWriter = null)
        {
            _volume = volume;
            Nodes = new List<Node>();
            _queueWriter = logWriter != null ? new QueueWriter(logWriter) : null;
        }

        public bool ExtractAllFiles(string outputPath, string volName = null, bool overwrite = false)
        {
            Directory.CreateDirectory(outputPath);
            using (var sw = new StreamWriter(Path.Combine(outputPath, $"{(string.IsNullOrEmpty(volName) ? "" : $"{ volName }_")}extract.log"), true))
            {
                sw.WriteLine($"Extraction started: {DateTime.Now.ToString(CultureInfo.InvariantCulture)}");
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                _volume.VOLReader.ByteConverter = ByteConverter.Big;
                var files = GetAllFiles();
                foreach (var file in files)
                {
                    var destPath = Path.Combine(outputPath, file.Path).Replace("/", "\\");
                    if (ExtractFile(_volume.VOLReader, file.Offset, file.PackedSize, file.RealSize, file.ModifiedDate, destPath, out var sourcePath, overwrite))
                        sw.WriteLine($"Successfully extracted: {sourcePath} to {destPath}");
                    else
                        sw.WriteLine($"Failed to extract: {sourcePath} to {destPath}");
                }

                _queueWriter?.Enqueue("All files extracted!");

                stopwatch.Stop();
                // Get the elapsed time as a TimeSpan value.
                TimeSpan ts = stopwatch.Elapsed;

                // Format and display the TimeSpan value.
                string elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
                sw.WriteLine("Time elapsed for extraction: " + elapsedTime);
                sw.WriteLine(string.Empty);
            }

            return true;
        }

        public bool WriteFileList(string outputPath, string volName = null, bool debugInfo = false)
        {
            if (_nodeEntries == null)
                Read();

            var path = Path.Combine(outputPath, $"{(string.IsNullOrEmpty(volName) ? "" : $"{volName}_")}filelist.txt");
            Directory.CreateDirectory(outputPath);
            _queueWriter?.Enqueue("Writing file list...");
            using (var sw = new StreamWriter(path, false))
            {
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

        private void TraverseNodes(IList<NodeEntry> nodeEntries, StreamWriter sw, uint parentNodeID, int depth, List<string> prefixFolders, bool debugInfo)
        {
            var nodes = nodeEntries.Where(x => x.ParentNode == parentNodeID);
            for (int i = 0; i < nodes.Count(); i++)
            {
                var node = nodes.ElementAtOrDefault(i);

                switch (node.Flag)
                {
                    case VolumeEntryType.Directory:
                        var folders = new List<string>(prefixFolders);
                        if (nodes.Count() - 1 > i)
                        {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name} [ID: {node.NodeId} - P:{node.ParentNode}]");
                            folders.Add("│   ");
                        }
                        else
                        {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name} [ID: {node.NodeId} - P:{node.ParentNode}]");
                            folders.Add("    ");
                        }
                        TraverseNodes(nodeEntries, sw, node.NodeId, depth++, folders, debugInfo);
                        break;
                    case VolumeEntryType.File: // File
                    case VolumeEntryType.CompressedFile: // Compressed file
                        if (nodes.Count() - 1 > i)
                        {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] (Offset: 0x{_volume.GetFileOffset(node.PageOffset):X8} - Size: 0x{node.PackedSize:X8} - RealSize: 0x{node.RealSize:X8} - ModifiedDate: {node.ModifiedDate:s})" : "")}");
                        }
                        else
                        {
                            sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] (Offset: 0x{_volume.GetFileOffset(node.PageOffset):X8} - Size: 0x{node.PackedSize:X8} - RealSize: 0x{node.RealSize:X8} - ModifiedDate: {node.ModifiedDate:s})" : "")}");
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException($"Unknown flag {node.Flag}");
                }
            }
        }

        private void Read()
        {
            foreach (var (Offset, Length) in _volume.Blocks)
            {
                var buffer = _volume.DecryptBlock(Offset, Length);
                Nodes.Add(new Node(buffer));
            }

            _nodeEntries = Nodes.Where(x => x.Flag == 0).SelectMany(x => x.NodeEntries);
        }

        private List<(string Path, long Offset, uint PackedSize, uint RealSize, DateTime ModifiedDate)> GetAllFiles()
        {
            if (_nodeEntries == null)
                Read();

            var files = new List<(string Path, long Offset, uint PackedSize, uint RealSize, DateTime ModifiedDate)>();
            foreach (var nodeEntry in _nodeEntries.Where(x => x.Flag != 0))
                files.Add((BuildPath(nodeEntry), _volume.GetFileOffset(nodeEntry.PageOffset), nodeEntry.PackedSize, nodeEntry.RealSize, nodeEntry.ModifiedDate));

            return files;
        }

        private string BuildPath(NodeEntry nodeEntry)
        {
            return nodeEntry.ParentNode == 0 ? string.Empty : Path.Combine(BuildPath(_nodeEntries.FirstOrDefault(x => x.NodeId == nodeEntry.ParentNode)), nodeEntry.Name);
        }

        private bool ExtractFile(BinaryStream reader, long offset, uint packedSize, uint realSize, DateTime modifiedDate, string destPath, out string sourcePath, bool overWrite)
        {
            sourcePath = $"VOL offset {offset:X8} - size {packedSize:X8}";
            try
            {
                if (File.Exists(destPath) && !overWrite)
                {
                    return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? throw new InvalidOperationException());
                reader.BaseStream.Position = offset;
                var data = reader.ReadBytes((int)packedSize);
                if (Util.DataAtUInt32(data, 0) == 0xC5EEF7FFu)
                    data = PS2Zip.Inflate(data);

                Debug.Assert(data.Length == realSize);

                File.WriteAllBytes(destPath, data);
                File.SetLastWriteTimeUtc(destPath, modifiedDate);

                _queueWriter?.Enqueue($"Extracted: {destPath}");

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public void Dispose()
        {
            _queueWriter?.Dispose();
        }
    }
}
