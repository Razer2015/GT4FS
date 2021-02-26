using DiscUtils.Iso9660;
using GT.Shared.Enums;
using GT.Shared.Polyphony;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GT.Shared.FileSystem {
    public class FileLoader {
        const ulong TOC22_MAGIC = 0xAD90B9AC02000200;
        const ulong TOC31_MAGIC = 0xAD90B9AC01000300;
        const ulong TOCPSP_MAGIC = 0xF319D371600A5E82;

        private readonly string _filePath;
        private bool _parsed;
        private FileType _fileType;

        public FileLoader(string filePath) {
            _filePath = filePath;
        }

        /// <summary>
        ///     Get the type of file given
        /// </summary>
        /// <returns></returns>
        public FileType GetFileType() {
            ValidateIsFile();

            _parsed = true;

            // Check if ISO file
            if (Path.GetExtension(_filePath).Equals(".iso", StringComparison.OrdinalIgnoreCase)) {
                _fileType = GetISOType();
                return _fileType;
            }
            else if (Path.GetExtension(_filePath).Equals(".vol", StringComparison.OrdinalIgnoreCase)) {
                _fileType = GetVOLType();
                return _fileType;
            }

            throw new ArgumentException("The file given was invalid.");
        }

        /// <summary>
        ///     Retrieve the VOL stream(s) and the corresponding filename.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<(Stream stream, string fileName)> GetVOLStreams() {
            if (!_parsed) GetFileType();

            switch (_fileType) {
                case FileType.TOC31_VOL:
                    yield return (new FileStream(_filePath, FileMode.Open, FileAccess.Read), Path.GetFileName(_filePath));
                    break;
                case FileType.TOC31_ISO:
                    using (FileStream isoStream = File.Open(_filePath, FileMode.Open)) {
                        CDReader cd = new CDReader(isoStream, true, true);
                        var nextDescriptor = cd.ClusterSize * cd.TotalClusters;
                        var vols = cd.GetFiles("", "*.*", SearchOption.AllDirectories)
                            .ToList()
                            .Where(x => x.EndsWith(".VOL", StringComparison.OrdinalIgnoreCase))
                            .ToArray();

                        foreach (var volFile in vols) {
                            yield return (cd.OpenFile(volFile, FileMode.Open), Path.GetFileName(volFile));
                        }

                        // Check for second descriptor
                        if (nextDescriptor != isoStream.Length) {
                            isoStream.Seek(nextDescriptor - 0x8000, SeekOrigin.Begin);
                            CDReader cd2 = new CDReader(new OffsetStreamDecorator(isoStream), true, true);
                            vols = cd2.GetFiles("", "*.*", SearchOption.AllDirectories)
                                .ToList()
                                .Where(x => x.EndsWith(".VOL", StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                            foreach (var volFile in vols) {
                                yield return (cd2.OpenFile(volFile, FileMode.Open), Path.GetFileName(volFile));
                            }
                        }
                    }
                    break;
                case FileType.UNKNOWN:
                case FileType.TOC22_VOL:
                case FileType.TOC22_ISO:
                case FileType.GTPSP_VOL:
                case FileType.GTPSP_ISO:
                default:
                    throw new Exception("Invalid VOL type, can't get streams.");
            }
        }

        /// <summary>
        ///     Retrieve all files from (both) descriptors
        /// </summary>
        /// <returns></returns>
        public IEnumerable<(Stream stream, string filePath)> GetStreams()
        {
            if (!_parsed) GetFileType();

            switch (_fileType)
            {
                case FileType.TOC31_ISO:
                case FileType.TOC22_ISO:
                case FileType.GTPSP_ISO:
                    using (FileStream isoStream = File.Open(_filePath, FileMode.Open))
                    {
                        long nextDescriptor = 0x8000;
                        do
                        {
                            isoStream.Seek(nextDescriptor -= 0x8000, SeekOrigin.Begin);
                            CDReader cd = new CDReader(new OffsetStreamDecorator(isoStream), true, true);
                            nextDescriptor += cd.ClusterSize * cd.TotalClusters;
                            var files = cd.GetFiles("", "*.*", SearchOption.AllDirectories)
                                .ToArray();

                            foreach (var file in files)
                            {
                                yield return (cd.OpenFile(file, FileMode.Open), file);
                            }
                        } while (nextDescriptor != isoStream.Length);
                    }
                    break;
                case FileType.TOC31_VOL:
                case FileType.UNKNOWN:
                case FileType.TOC22_VOL:
                case FileType.GTPSP_VOL:
                default:
                    throw new Exception("Invalid VOL type, can't get streams.");
            }
        }

        /// <summary>
        ///     Validate that the given path is a file and that the file exists.
        /// </summary>
        private void ValidateIsFile() {
            if (!File.Exists(_filePath)) throw new FileNotFoundException("File not found.", Path.GetFileName(_filePath));

            FileAttributes attr = File.GetAttributes(_filePath);
            if ((attr & FileAttributes.Directory) == FileAttributes.Directory) {
                throw new ArgumentException("Only files are accepted.");
            }
        }

        /// <summary>
        ///     Determine which kind of VOL(s) the ISO contains (if any).
        /// </summary>
        /// <returns>FileType</returns>
        private FileType GetISOType() {
            var fileType = FileType.UNKNOWN;
            using (FileStream isoStream = File.Open(_filePath, FileMode.Open)) {
                CDReader cd = new CDReader(isoStream, true, true);
                var vols = cd.GetFiles("", "*.*", SearchOption.AllDirectories)
                    .ToList()
                    .Where(x => x.EndsWith(".VOL", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (vols.Length <= 0) throw new ArgumentException("Invalid ISO file, no VOL inside.");

                try {
                    using (var fs = cd.OpenFile(vols.First(), FileMode.Open)) {
                        var volType = GetVOLType(fs);
                        switch (volType) {
                            case FileType.TOC22_VOL:
                                fileType = FileType.TOC22_ISO;
                                break;
                            case FileType.TOC31_VOL:
                                fileType = FileType.TOC31_ISO;
                                break;
                            case FileType.GTPSP_VOL:
                                fileType = FileType.GTPSP_ISO;
                                break;
                            case FileType.TOC22_ISO:
                            case FileType.TOC31_ISO:
                            case FileType.GTPSP_ISO:
                                throw new Exception("Something went wrong when parsing the VOL file inside the ISO file.");
                            case FileType.UNKNOWN:
                            default:
                                fileType = FileType.UNKNOWN;
                                break;
                        }
                    }
                }
                catch (ArgumentException) {
                    throw new ArgumentException("Invalid ISO file, no VOL inside.");
                }
            }

            return fileType;
        }

        /// <summary>
        ///     Determine if the VOL file is of type TOC 2.2, TOC 3.1 or GTPSP (others aren't supported).
        /// </summary>
        /// <returns>FileType</returns>
        private FileType GetVOLType() {
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read)) {
                return GetVOLType(fs);
            }
        }

        /// <summary>
        ///     Determine if the VOL file is of type TOC 2.2, TOC 3.1 or GTPSP (others aren't supported).
        /// </summary>
        /// <param name="stream">Stream containing the VOL file.</param>
        /// <returns>FileType</returns>
        private FileType GetVOLType(Stream stream) {
            var fileType = FileType.UNKNOWN;
            using (var reader = new EndianBinReader(stream, EndianType.BIG_ENDIAN)) {
                if (reader.BaseStream.Length - reader.BaseStream.Position < 0x08) throw new ArgumentException("Invalid VOL file.");

                var magic = reader.ReadUInt64();
                if (magic == TOC22_MAGIC) {
                    fileType = FileType.TOC22_VOL;
                    if (TOC31Offset(reader) > -1) {
                        fileType = FileType.TOC31_VOL;
                    }
                }
                else if (magic == TOCPSP_MAGIC) {
                    fileType = FileType.GTPSP_VOL;
                }
            }

            return fileType;
        }

        /// <summary>
        ///     Search for the TOC 3.1 in the VOL file and return its offset if found
        /// </summary>
        private long TOC31Offset(EndianBinReader reader) {
            for (int i = 0; i < Math.Min((reader.BaseStream.Length / 0x800), 10000); i++) {
                reader.BaseStream.Seek(i * 0x800, SeekOrigin.Begin);
                if (reader.ReadUInt64() == TOC31_MAGIC) {
                    return i * 0x800;
                }
            }

            return -1;
        }
    }
}
