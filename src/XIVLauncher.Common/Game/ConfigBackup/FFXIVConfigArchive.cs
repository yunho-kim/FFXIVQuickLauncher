using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace XIVLauncher.Common.Game.ConfigBackup;

public sealed record FFXIVConfigArchiveResult(
    int CharacterCount,
    int FileCount,
    int SkippedFileCount = 0);

public sealed class FFXIVConfigArchiveException : Exception
{
    public FFXIVConfigArchiveException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public FFXIVConfigArchiveException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Reads and writes the FFXIV launcher configuration archive format (*.fea).
/// The archive contains character-local DAT files, not machine-specific
/// FFXIV.cfg settings, screenshots, chat logs, or login credentials.
/// </summary>
public static class FFXIVConfigArchive
{
    private static readonly byte[] Magic = [0xff, 0x14, 0x0f, 0xea];

    private const ushort FormatVersion = 1;
    private const string CharacterDirectoryPrefix = "FFXIV_CHR";
    private const int HeaderSize = 16;
    private const int MaximumCharacters = 256;
    private const int MaximumFilesPerCharacter = 256;
    private const int MaximumFileNameBytes = 512;
    private const int MaximumCompressedFileBytes = 128 * 1024 * 1024;
    private const int MaximumUncompressedFileBytes = 64 * 1024 * 1024;
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumTotalUncompressedBytes = 512L * 1024 * 1024;

    public static FFXIVConfigArchiveResult Export(DirectoryInfo configDirectory, FileInfo destination)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        ArgumentNullException.ThrowIfNull(destination);

        var groups = FindCharacterGroups(configDirectory);
        if (groups.Count == 0)
        {
            throw new FFXIVConfigArchiveException(
                "NoCharacterSettings",
                "No FFXIV character settings were found to export.");
        }

        using var archiveStream = new MemoryStream();
        using var writer = new BinaryWriter(archiveStream, Encoding.Unicode, true);

        writer.Write(Magic);
        writer.Write(0u); // Total archive size, patched below.
        writer.Write(0u); // Byte-sum checksum, patched below.
        writer.Write(FormatVersion);
        writer.Write(checked((ushort)groups.Count));
        WriteCompressedPayload(writer, []);

        var fileCount = 0;
        foreach (var group in groups)
        {
            writer.Write(ToFileTimeUtc(group.Directory.CreationTimeUtc));
            writer.Write(group.CharacterId);
            writer.Write(checked((uint)group.Files.Count));

            var endOffsetPosition = archiveStream.Position;
            writer.Write(0u);

            foreach (var file in group.Files)
            {
                WriteFile(writer, file);
                fileCount++;
            }

            var endOffset = checked((uint)archiveStream.Position);
            var returnPosition = archiveStream.Position;
            archiveStream.Position = endOffsetPosition;
            writer.Write(endOffset);
            archiveStream.Position = returnPosition;
        }

        writer.Flush();
        if (archiveStream.Length > MaximumArchiveBytes)
            throw ArchiveTooLarge();

        var archive = archiveStream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            archive.AsSpan(4, sizeof(uint)), checked((uint)archive.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            archive.AsSpan(8, sizeof(uint)), CalculateChecksum(archive));

        WriteAtomically(destination, archive);
        return new FFXIVConfigArchiveResult(groups.Count, fileCount);
    }

    public static FFXIVConfigArchiveResult Inspect(FileInfo source)
    {
        var archive = ReadArchive(source);
        return new FFXIVConfigArchiveResult(
            archive.Groups.Count,
            archive.Groups.Sum(group => group.Files.Count));
    }

    public static FFXIVConfigArchiveResult Restore(
        FileInfo source,
        DirectoryInfo configDirectory,
        bool preserveNewerFiles)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        var archive = ReadArchive(source);
        configDirectory.Create();

        var transactionRoot = Path.Combine(
            configDirectory.FullName,
            $".xomkr-fea-import-{Guid.NewGuid():N}");
        var stagedRoot = Path.Combine(transactionRoot, "staged");
        var rollbackRoot = Path.Combine(transactionRoot, "rollback");
        var applied = new List<AppliedFile>();
        var createdDirectories = new HashSet<string>(StringComparer.Ordinal);
        var skippedFiles = 0;

        try
        {
            foreach (var group in archive.Groups)
            {
                var characterDirectoryName = $"{CharacterDirectoryPrefix}{group.CharacterId:X16}";
                var targetDirectory = Path.Combine(configDirectory.FullName, characterDirectoryName);
                var stagedDirectory = Path.Combine(stagedRoot, characterDirectoryName);
                Directory.CreateDirectory(stagedDirectory);

                foreach (var file in group.Files)
                {
                    var stagedPath = Path.Combine(stagedDirectory, file.Name);
                    File.WriteAllBytes(stagedPath, file.Data);
                }

                if (!Directory.Exists(targetDirectory))
                    createdDirectories.Add(targetDirectory);
            }

            foreach (var group in archive.Groups)
            {
                var characterDirectoryName = $"{CharacterDirectoryPrefix}{group.CharacterId:X16}";
                var targetDirectory = Path.Combine(configDirectory.FullName, characterDirectoryName);
                var stagedDirectory = Path.Combine(stagedRoot, characterDirectoryName);
                var rollbackDirectory = Path.Combine(rollbackRoot, characterDirectoryName);
                Directory.CreateDirectory(targetDirectory);

                foreach (var file in group.Files)
                {
                    var targetPath = Path.Combine(targetDirectory, file.Name);
                    if (preserveNewerFiles && File.Exists(targetPath)
                        && File.GetLastWriteTimeUtc(targetPath) > file.LastWriteTimeUtc)
                    {
                        skippedFiles++;
                        continue;
                    }

                    var stagedPath = Path.Combine(stagedDirectory, file.Name);
                    string? rollbackPath = null;
                    if (File.Exists(targetPath))
                    {
                        Directory.CreateDirectory(rollbackDirectory);
                        rollbackPath = Path.Combine(rollbackDirectory, file.Name);
                        File.Copy(targetPath, rollbackPath, true);
                    }

                    File.Move(stagedPath, targetPath, true);
                    applied.Add(new AppliedFile(targetPath, rollbackPath));
                    TrySetFileTimes(targetPath, file);
                }
            }
        }
        catch (Exception exception)
        {
            RollBack(applied, createdDirectories);
            throw new FFXIVConfigArchiveException(
                "RestoreFailed",
                "The configuration backup could not be restored.",
                exception);
        }
        finally
        {
            TryDeleteDirectory(transactionRoot);
        }

        return new FFXIVConfigArchiveResult(
            archive.Groups.Count,
            archive.Groups.Sum(group => group.Files.Count),
            skippedFiles);
    }

    private static List<CharacterGroupSource> FindCharacterGroups(DirectoryInfo configDirectory)
    {
        if (!configDirectory.Exists)
            return [];

        var groups = new List<CharacterGroupSource>();
        foreach (var path in Directory.EnumerateDirectories(
                     configDirectory.FullName,
                     $"{CharacterDirectoryPrefix}*",
                     SearchOption.TopDirectoryOnly))
        {
            var directory = new DirectoryInfo(path);
            if (!TryParseCharacterId(directory.Name, out var characterId))
                continue;

            var files = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => file.Extension.Equals(".DAT", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
                continue;
            if (files.Count > MaximumFilesPerCharacter)
                throw new FFXIVConfigArchiveException("TooManyFiles", "A character settings folder contains too many files.");

            groups.Add(new CharacterGroupSource(directory, characterId, files));
        }

        if (groups.Count > MaximumCharacters)
            throw new FFXIVConfigArchiveException("TooManyCharacters", "Too many character settings folders were found.");

        return groups.OrderBy(group => group.CharacterId).ToList();
    }

    private static void WriteFile(BinaryWriter writer, FileInfo file)
    {
        ValidateFileName(file.Name);
        if (file.Length > MaximumUncompressedFileBytes)
            throw ArchiveTooLarge();

        writer.Write(ToFileTimeUtc(file.CreationTimeUtc));
        writer.Write(ToFileTimeUtc(file.LastWriteTimeUtc));
        writer.Write(ToFileTimeUtc(file.LastAccessTimeUtc));

        var encodedName = Encoding.Unicode.GetBytes(file.Name + '\0');
        writer.Write(checked((uint)encodedName.Length));
        writer.Write(encodedName);
        WriteCompressedPayload(writer, File.ReadAllBytes(file.FullName));
    }

    private static void WriteCompressedPayload(BinaryWriter writer, byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
            zlib.Write(payload);

        var compressed = output.ToArray();
        if (compressed.Length > MaximumCompressedFileBytes)
            throw ArchiveTooLarge();
        writer.Write(checked((uint)compressed.Length));
        writer.Write(compressed);
    }

    private static ParsedArchive ReadArchive(FileInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Refresh();
        if (!source.Exists)
            throw new FFXIVConfigArchiveException("FileNotFound", "The configuration backup file does not exist.");
        if (source.Length < HeaderSize || source.Length > MaximumArchiveBytes)
            throw InvalidArchive();

        byte[] data;
        try
        {
            data = File.ReadAllBytes(source.FullName);
        }
        catch (Exception exception)
        {
            throw new FFXIVConfigArchiveException("ReadFailed", "The configuration backup could not be read.", exception);
        }

        var reader = new ArchiveReader(data);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw InvalidArchive();

        var declaredLength = reader.ReadUInt32();
        var declaredChecksum = reader.ReadUInt32();
        if (declaredLength != data.Length || declaredChecksum != CalculateChecksum(data))
            throw InvalidArchive();
        if (reader.ReadUInt16() != FormatVersion)
            throw new FFXIVConfigArchiveException("UnsupportedVersion", "The configuration backup version is not supported.");

        var characterCount = reader.ReadUInt16();
        if (characterCount == 0 || characterCount > MaximumCharacters)
            throw InvalidArchive();

        var emptyPayloadLength = reader.ReadUInt32AsInt(1024);
        if (Decompress(reader.ReadBytes(emptyPayloadLength), 1).Length != 0)
            throw InvalidArchive();

        var groups = new List<CharacterGroup>(characterCount);
        var characterIds = new HashSet<ulong>();
        long totalUncompressedBytes = 0;

        for (var groupIndex = 0; groupIndex < characterCount; groupIndex++)
        {
            var creationTime = ReadFileTime(reader.ReadUInt64());
            var characterId = reader.ReadUInt64();
            if (characterId == 0 || !characterIds.Add(characterId))
                throw InvalidArchive();

            var fileCount = reader.ReadUInt32AsInt(MaximumFilesPerCharacter);
            var endOffset = reader.ReadUInt32AsInt(data.Length);
            if (endOffset <= reader.Position)
                throw InvalidArchive();

            var files = new List<ArchivedFile>(fileCount);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var fileCreationTime = ReadFileTime(reader.ReadUInt64());
                var lastWriteTime = ReadFileTime(reader.ReadUInt64());
                var lastAccessTime = ReadFileTime(reader.ReadUInt64());
                var nameLength = reader.ReadUInt32AsInt(MaximumFileNameBytes);
                if (nameLength < 2 || (nameLength & 1) != 0)
                    throw InvalidArchive();

                var nameBytes = reader.ReadBytes(nameLength);
                if (nameBytes[^2] != 0 || nameBytes[^1] != 0)
                    throw InvalidArchive();
                var name = Encoding.Unicode.GetString(nameBytes[..^2]);
                ValidateFileName(name);
                if (!fileNames.Add(name))
                    throw InvalidArchive();

                var compressedLength = reader.ReadUInt32AsInt(MaximumCompressedFileBytes);
                var payload = Decompress(
                    reader.ReadBytes(compressedLength),
                    MaximumUncompressedFileBytes);
                totalUncompressedBytes += payload.Length;
                if (totalUncompressedBytes > MaximumTotalUncompressedBytes)
                    throw ArchiveTooLarge();

                files.Add(new ArchivedFile(
                    name,
                    payload,
                    fileCreationTime,
                    lastWriteTime,
                    lastAccessTime));
            }

            if (reader.Position != endOffset)
                throw InvalidArchive();
            groups.Add(new CharacterGroup(characterId, creationTime, files));
        }

        if (reader.Position != data.Length)
            throw InvalidArchive();
        return new ParsedArchive(groups);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed, int maximumOutputBytes)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = zlib.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > maximumOutputBytes)
                    throw ArchiveTooLarge();
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (FFXIVConfigArchiveException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FFXIVConfigArchiveException("InvalidArchive", "The configuration backup is damaged.", exception);
        }
    }

    private static void WriteAtomically(FileInfo destination, byte[] data)
    {
        var destinationDirectory = destination.Directory
                                   ?? throw new FFXIVConfigArchiveException("InvalidDestination", "The backup destination is invalid.");
        destinationDirectory.Create();
        var temporaryPath = Path.Combine(
            destinationDirectory.FullName,
            $".{destination.Name}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, destination.FullName, true);
        }
        catch (Exception exception)
        {
            throw new FFXIVConfigArchiveException("WriteFailed", "The configuration backup could not be written.", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup after an interrupted export.
            }
        }
    }

    private static void RollBack(IEnumerable<AppliedFile> applied, IEnumerable<string> createdDirectories)
    {
        foreach (var file in applied.Reverse())
        {
            try
            {
                if (file.RollbackPath != null && File.Exists(file.RollbackPath))
                    File.Move(file.RollbackPath, file.TargetPath, true);
                else
                    File.Delete(file.TargetPath);
            }
            catch
            {
                // Continue restoring the remaining files even if one rollback fails.
            }
        }

        foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static void TrySetFileTimes(string path, ArchivedFile file)
    {
        try
        {
            File.SetCreationTimeUtc(path, file.CreationTimeUtc);
            File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(path, file.LastAccessTimeUtc);
        }
        catch
        {
            // Content restoration is still valid on filesystems that cannot
            // represent all Windows timestamps.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool TryParseCharacterId(string directoryName, out ulong characterId)
    {
        characterId = 0;
        return directoryName.Length == CharacterDirectoryPrefix.Length + 16
               && directoryName.StartsWith(CharacterDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
               && ulong.TryParse(
                   directoryName.AsSpan(CharacterDirectoryPrefix.Length),
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out characterId)
               && characterId != 0;
    }

    private static void ValidateFileName(string name)
    {
        if (name.Length == 0
            || name.Length > (MaximumFileNameBytes / 2) - 1
            || !name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)
            || name.Any(character => !char.IsAsciiLetterOrDigit(character)
                                     && character != '.'
                                     && character != '_'
                                     && character != '-'))
        {
            throw InvalidArchive();
        }
    }

    private static ulong ToFileTimeUtc(DateTime dateTime)
    {
        try
        {
            return checked((ulong)dateTime.ToUniversalTime().ToFileTimeUtc());
        }
        catch
        {
            return checked((ulong)DateTime.UnixEpoch.ToFileTimeUtc());
        }
    }

    private static DateTime ReadFileTime(ulong fileTime)
    {
        try
        {
            return DateTime.FromFileTimeUtc(checked((long)fileTime));
        }
        catch (Exception exception)
        {
            throw new FFXIVConfigArchiveException("InvalidArchive", "The configuration backup contains an invalid timestamp.", exception);
        }
    }

    private static uint CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint checksum = 0;
        for (var index = HeaderSize; index < data.Length; index++)
            checksum = unchecked(checksum + data[index]);
        return checksum;
    }

    private static FFXIVConfigArchiveException InvalidArchive()
    {
        return new FFXIVConfigArchiveException("InvalidArchive", "The configuration backup is damaged or is not an FFXIV FEA file.");
    }

    private static FFXIVConfigArchiveException ArchiveTooLarge()
    {
        return new FFXIVConfigArchiveException("ArchiveTooLarge", "The configuration backup exceeds the supported size limit.");
    }

    private sealed record CharacterGroupSource(
        DirectoryInfo Directory,
        ulong CharacterId,
        List<FileInfo> Files);

    private sealed record CharacterGroup(
        ulong CharacterId,
        DateTime CreationTimeUtc,
        List<ArchivedFile> Files);

    private sealed record ArchivedFile(
        string Name,
        byte[] Data,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        DateTime LastAccessTimeUtc);

    private sealed record ParsedArchive(List<CharacterGroup> Groups);

    private sealed record AppliedFile(string TargetPath, string? RollbackPath);

    private sealed class ArchiveReader
    {
        private readonly byte[] data;

        public ArchiveReader(byte[] data)
        {
            this.data = data;
        }

        public int Position { get; private set; }

        public ushort ReadUInt16()
        {
            var bytes = ReadBytes(sizeof(ushort));
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        public uint ReadUInt32()
        {
            var bytes = ReadBytes(sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public int ReadUInt32AsInt(int maximum)
        {
            var value = ReadUInt32();
            if (value > maximum)
                throw InvalidArchive();
            return checked((int)value);
        }

        public ulong ReadUInt64()
        {
            var bytes = ReadBytes(sizeof(ulong));
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || Position > data.Length - count)
                throw InvalidArchive();
            var bytes = data.AsSpan(Position, count);
            Position += count;
            return bytes;
        }
    }
}
