using DiscUtils.Iso9660;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GT.Shared.FileSystem
{
    public class FilePacker
    {
        private readonly string _inputDir;

        public FilePacker(string dir)
        {
            _inputDir = dir;
        }

        public void WriteISO(string outputISO)
        {
            WriteISOFile(outputISO);
        }

        private void WriteISOFile(string outputISO)
        {
            using var fs = new FileStream(outputISO, FileMode.Create, FileAccess.Write, FileShare.Write, 4096);

            // Primary builder
            var primaryBuilder = new CDBuilder {
                VolumeIdentifier = "GRANTURISMO4"
            };

            // First layer files
            var files = Directory.GetFiles(_inputDir, "*", SearchOption.AllDirectories);
                //.Where(x => !x.Contains("GT4L1.VOL"));

            foreach (var file in files)
            {
                var filePath = file.Replace(_inputDir, string.Empty);
                primaryBuilder.AddFile(filePath, file);
            }

            primaryBuilder.Build(fs);

            // Supplementary layer if one should exist
            var l1File = Directory.GetFiles(_inputDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(x => x.Contains("GT4L1.VOL"));

            if (!string.IsNullOrWhiteSpace(l1File))
            {
                var supplementaryBuilder = new CDBuilder {
                    VolumeIdentifier = "GRANTURISMO4",
                    UseJoliet = true
                };

                var filePath = l1File.Replace(_inputDir, string.Empty);
                supplementaryBuilder.AddFile(filePath, l1File);

                var stream = supplementaryBuilder.Build();

                stream.Seek(0x8000, SeekOrigin.Begin);
                new OffsetStreamDecorator(stream).CopyTo(fs);
            }
        }
    }
}
