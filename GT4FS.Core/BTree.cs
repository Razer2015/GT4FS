using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using GT.Shared.Threading;
using GT.Shared.Logging;
using System.Diagnostics;
using GT.Shared;

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

        public bool ExtractAllFiles(string outputPath, bool overwrite = false) {
            Directory.CreateDirectory(outputPath);
            using (var sw = new StreamWriter(Path.Combine(outputPath, "extract.log"), true)) {
                sw.WriteLine(string.Format("Extraction started: {0}", DateTime.Now.ToString()));
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                using (var fs = new FileStream(_volume.VolumePath, FileMode.Open, FileAccess.Read))
                using (var reader = new EndianBinReader(fs)) {
                    var files = GetAllFiles();
                    foreach (var file in files) {
                        var destPath = Path.Combine(outputPath, file.Path).Replace("/", "\\");
                        if (ExtractFile(reader, file.Offset, file.PackedSize, file.RealSize, destPath, out var sourcePath, overwrite))
                            sw.WriteLine(string.Format("Succesfully extracted: {0} to {1}", sourcePath, destPath));
                        else
                            sw.WriteLine(string.Format("Failed to extract: {0} to {1}", sourcePath, destPath));
                    }
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
