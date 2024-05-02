using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;

using GT.Shared.Threading;
using GT.Shared.Logging;
using GT.Shared;
using GT4FS.Core.Entries;

using Syroot.BinaryData;

namespace GT4FS.Core;

public class BTree : IDisposable
{
    private readonly Volume _volume;
    private readonly QueueWriter _queueWriter;
    public List<ToCPage> Pages { get; set; }
    private IEnumerable<Entry> _nodeEntries;

    public BTree(Volume volume, ILogWriter logWriter = null)
    {
        _volume = volume;
        Pages = new List<ToCPage>();
        _queueWriter = logWriter != null ? new QueueWriter(logWriter) : null;
    }

    public long GetRealToCOffset()
    {
        return _volume.GetRealTocOffset();
    }

    public long GetBaseDataOffset()
    {
        return _volume.GetBaseDataOffset();
    }

    public bool ExtractAllFiles(string outputPath, string volName = null, bool overwrite = false)
    {
        Directory.CreateDirectory(outputPath);
        using (var sw = new StreamWriter(Path.Combine(outputPath, $"{(string.IsNullOrEmpty(volName) ? "" : $"{volName}_")}extract.log"), true))
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

    private void TraverseNodes(IList<Entry> nodeEntries, StreamWriter sw, uint parentNodeID, int depth, List<string> prefixFolders, bool debugInfo)
    {
        var nodes = nodeEntries.Where(x => x.ParentNode == parentNodeID);
        for (int i = 0; i < nodes.Count(); i++)
        {
            var node = nodes.ElementAtOrDefault(i);

            switch (node.EntryType)
            {
                case VolumeEntryType.Directory:
                    DirEntry dir = node as DirEntry;
                    var folders = new List<string>(prefixFolders);
                    if (nodes.Count() - 1 > i)
                    {
                        sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name} [ID: {dir.NodeID} - P:{node.ParentNode}]");
                        folders.Add("│   ");
                    }
                    else
                    {
                        sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name} [ID: {dir.NodeID} - P:{node.ParentNode}]");
                        folders.Add("    ");
                    }
                    TraverseNodes(nodeEntries, sw, (uint)dir.NodeID, depth++, folders, debugInfo);
                    break;
                case VolumeEntryType.File: // File
                    var file = node as FileEntry;
                    if (nodes.Count() - 1 > i)
                        sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] (Offset: 0x{_volume.GetFileOffset((uint)file.PageOffset):X8} - RealSize: 0x{file.Size:X8} - ModifiedDate: {file.ModifiedDate:s})" : "")}");
                    else
                        sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] (Offset: 0x{_volume.GetFileOffset((uint)file.PageOffset):X8} - RealSize: 0x{file.Size:X8} - ModifiedDate: {file.ModifiedDate:s})" : "")}");
                    break;

                case VolumeEntryType.CompressedFile: // Compressed file
                    var cfile = node as CompressedFileEntry;
                    if (nodes.Count() - 1 > i)
                        sw?.WriteLine($"{string.Join("", prefixFolders)}├── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] [Z] (Offset: 0x{_volume.GetFileOffset((uint)cfile.PageOffset):X8} - Size: 0x{cfile.CompressedSize:X8} - RealSize: 0x{cfile.Size:X8} - ModifiedDate: {cfile.ModifiedDate:s})" : "")}");
                    else
                        sw?.WriteLine($"{string.Join("", prefixFolders)}└── {node.Name}{(debugInfo ? $" [P:{node.ParentNode}] [Z] (Offset: 0x{_volume.GetFileOffset((uint)cfile.PageOffset):X8} - Size: 0x{cfile.CompressedSize:X8} - RealSize: 0x{cfile.Size:X8} - ModifiedDate: {cfile.ModifiedDate:s})" : "")}");
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown flag {node.EntryType}");
            }
        }
    }

    private void Read()
    {
        foreach (var (Offset, Length) in _volume.Pages)
        {
            var buffer = _volume.GetPage(Offset, Length);
            Pages.Add(new ToCPage(buffer));
        }

        _nodeEntries = Pages.Where(x => x.Flag == 0).SelectMany(x => x.NodeEntries);
    }

    public IEnumerable<Entry> GetNodes()
    {
        if (_nodeEntries == null)
            Read();

        // Build relationships/children
        foreach (Entry node in _nodeEntries)
        {
            if (node.Name == ".")
                continue;

            if (node is DirEntry dir)
            {
                var parentDir = _nodeEntries.FirstOrDefault(e => e.NodeID == node.ParentNode) as DirEntry;
                parentDir.ChildEntries.Add(dir.Name, dir);
            }
            else
            {
                var parentDir = _nodeEntries.FirstOrDefault(e => e.NodeID == node.ParentNode) as DirEntry;
                parentDir.ChildEntries.Add(node.Name, node);
            }
        }

        return _nodeEntries;
    }

    private List<(string Path, long Offset, uint PackedSize, uint RealSize, DateTime ModifiedDate)> GetAllFiles()
    {
        if (_nodeEntries == null)
            Read();

        var files = new List<(string Path, long Offset, uint PackedSize, uint RealSize, DateTime ModifiedDate)>();
        foreach (var nodeEntry in _nodeEntries.Where(x => x.EntryType != VolumeEntryType.Directory))
        {
            if (nodeEntry is FileEntry file)
                files.Add((BuildPath(nodeEntry), _volume.GetFileOffset((uint)file.PageOffset), (uint)file.Size, (uint)file.Size, file.ModifiedDate));
            else if (nodeEntry is CompressedFileEntry cFile)
                files.Add((BuildPath(nodeEntry), _volume.GetFileOffset((uint)cFile.PageOffset), (uint)cFile.CompressedSize, (uint)cFile.Size, cFile.ModifiedDate));
        }

        return files;
    }

    private string BuildPath(Entry nodeEntry)
    {
        return nodeEntry.ParentNode == 0 ? string.Empty : Path.Combine(BuildPath(_nodeEntries.FirstOrDefault(x => x.NodeID == nodeEntry.ParentNode)), nodeEntry.Name);
    }

    private bool ExtractFile(BinaryStream reader, long offset, uint packedSize, uint realSize, DateTime modifiedDate, string destPath, out string sourcePath, bool overWrite)
    {
        sourcePath = $"VOL offset {offset:X8} - size {packedSize:X8}";
        try
        {
            if (File.Exists(destPath) && !overWrite)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? throw new InvalidOperationException());
            reader.BaseStream.Position = offset;
            var data = reader.ReadBytes((int)packedSize);

            /* Apparently we're just going to check if its 0 as net core isn't cooperating
             *  BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4))
             *  -> 0xc5eef7ff
             *  (uint)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) == 0xC5EEF7FFu
             *  -> false
             *  (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) == (int)0xC5EEF7FFu
             *  -> false
             *  BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) - 0xC5EEF7FFu
             *  -> 0x00000000
            */
            if (data.Length > 4 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) - 0xC5EEF7FFu == 0)
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
        _volume?.Dispose();
        _queueWriter?.Dispose();
    }
}