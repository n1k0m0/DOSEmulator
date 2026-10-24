using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Runtime;

/// <summary>Minimal startup in a new official FreeDOS floppy copy. Boot code, kernel and shell stay byte-identical.</summary>
public static class FreeDosFloppyTestImage
{
    public static object Create(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
            throw new IOException("Use a new path for the private minimal floppy image.");
        byte[] image = File.ReadAllBytes(source);
        string originalHash = Convert.ToHexString(SHA256.HashData(image));
        if (originalHash != "552F7CBB0625960C050A3E682A4D2121CC2FFDFA9DC9D592CCD49EDF179333DC")
            throw new InvalidDataException("Expected the unchanged official FreeDOS 1.4 1.44-MiB boot floppy.");
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
        int fatSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
        int clusterBytes = image[13] * 512, fat = reserved * 512, root = (reserved + image[16] * fatSize) * 512;
        int data = root + ((entries * 32 + 511) / 512) * 512;
        IEnumerable<int> Chain(int first)
        {
            var seen = new HashSet<int>(); int cluster = first;
            while (cluster < 0xff8)
            {
                if (cluster < 2 || data + (cluster - 1) * clusterBytes > image.Length || !seen.Add(cluster)) throw new InvalidDataException("Invalid FAT12 chain.");
                yield return data + (cluster - 2) * clusterBytes;
                int offset = fat + cluster * 3 / 2; int pair = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
                cluster = (cluster & 1) == 0 ? pair & 0xfff : pair >> 4;
            }
        }
        int Find(int directory, string name)
        {
            IEnumerable<int> blocks = directory == 0 ? [root] : Chain(directory);
            foreach (int block in blocks)
                for (int n = 0; n < (directory == 0 ? entries : clusterBytes / 32); n++)
                {
                    int offset = block + n * 32;
                    if (image[offset] == 0) break;
                    if (Encoding.ASCII.GetString(image, offset, 11) == name) return offset;
                }
            throw new InvalidDataException("Missing floppy file " + name);
        }
        int Cluster(int entry) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entry + 26));
        byte[] Read(int entry)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 28))), copied = 0;
            byte[] result = new byte[length];
            foreach (int block in Chain(Cluster(entry)))
            {
                int count = Math.Min(clusterBytes, length - copied); image.AsSpan(block, count).CopyTo(result.AsSpan(copied)); copied += count;
                if (copied == length) return result;
            }
            throw new InvalidDataException("Short FAT12 file.");
        }
        void Replace(int entry, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text); int copied = 0;
            if (bytes.Length > BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 28))) throw new InvalidDataException("Replacement must fit existing allocation.");
            foreach (int block in Chain(Cluster(entry)))
            {
                int count = Math.Min(clusterBytes, bytes.Length - copied); bytes.AsSpan(copied, count).CopyTo(image.AsSpan(block)); copied += count;
                if (copied == bytes.Length) break;
            }
            if (copied != bytes.Length) throw new InvalidDataException("Short FAT12 allocation.");
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 28), (uint)bytes.Length);
        }
        int kernelEntry = Find(0, "KERNEL  SYS");
        int freedos = Cluster(Find(0, "FREEDOS    ")), bin = Cluster(Find(freedos, "BIN        "));
        int shellEntry = Find(bin, "COMMAND COM");
        byte[] kernel = Read(kernelEntry), shell = Read(shellEntry);
        string config = "DOS=LOW\r\nFILES=20\r\nBUFFERS=10\r\nLASTDRIVE=Z\r\nSHELL=A:\\FREEDOS\\BIN\\COMMAND.COM A:\\FREEDOS\\BIN /E:512 /P=A:\\FDAUTO.BAT\r\n";
        string auto = "@ECHO OFF\r\nSET PATH=A:\\FREEDOS\\BIN\r\nVER\r\nECHO M2-FLOPPY-BOOT-OK\r\nDIR A:\\\r\nECHO M2-FLOPPY-DIR-OK\r\n";
        Replace(Find(0, "FDCONFIGSYS"), config); Replace(Find(0, "FDAUTO  BAT"), auto);
        if (!Read(kernelEntry).AsSpan().SequenceEqual(kernel) || !Read(shellEntry).AsSpan().SequenceEqual(shell)) throw new InvalidDataException("Kernel or shell changed.");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(image);
        return new { originalHash, derivedHash = Convert.ToHexString(SHA256.HashData(image)),
            kernelHash = Convert.ToHexString(SHA256.HashData(kernel)), shellHash = Convert.ToHexString(SHA256.HashData(shell)),
            changedFiles = new[] { "FDCONFIG.SYS", "FDAUTO.BAT" }, config, auto };
    }
}
