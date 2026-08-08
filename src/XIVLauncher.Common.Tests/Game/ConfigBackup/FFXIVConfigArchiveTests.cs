using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XIVLauncher.Common.Game.ConfigBackup;

namespace XIVLauncher.Common.Tests.Game.ConfigBackup;

[TestClass]
public class FFXIVConfigArchiveTests
{
    private const string CharacterDirectory = "FFXIV_CHR0040000012345678";

    [TestMethod]
    public void ExportAndRestoreRoundTripCharacterSettings()
    {
        WithTemporaryDirectory(root =>
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source"));
            var character = Directory.CreateDirectory(Path.Combine(source.FullName, CharacterDirectory));
            File.WriteAllBytes(Path.Combine(character.FullName, "ADDON.DAT"), [0, 1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(character.FullName, "HOTBAR.DAT"), [9, 8, 7]);
            Directory.CreateDirectory(Path.Combine(character.FullName, "log"));
            File.WriteAllText(Path.Combine(character.FullName, "log", "chat.log"), "not exported");
            File.WriteAllText(Path.Combine(source.FullName, "FFXIV.cfg"), "not exported");

            var archive = new FileInfo(Path.Combine(root, "FFXIVconf.fea"));
            var exported = FFXIVConfigArchive.Export(source, archive);
            Assert.AreEqual(1, exported.CharacterCount);
            Assert.AreEqual(2, exported.FileCount);

            var bytes = File.ReadAllBytes(archive.FullName);
            CollectionAssert.AreEqual(new byte[] { 0xff, 0x14, 0x0f, 0xea }, bytes[..4]);
            Assert.AreEqual((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            Assert.AreEqual(CalculateChecksum(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));

            var inspected = FFXIVConfigArchive.Inspect(archive);
            Assert.AreEqual(1, inspected.CharacterCount);
            Assert.AreEqual(2, inspected.FileCount);

            var destination = new DirectoryInfo(Path.Combine(root, "destination"));
            var restored = FFXIVConfigArchive.Restore(archive, destination, false);
            Assert.AreEqual(1, restored.CharacterCount);
            Assert.AreEqual(2, restored.FileCount);
            CollectionAssert.AreEqual(
                new byte[] { 0, 1, 2, 3, 4 },
                File.ReadAllBytes(Path.Combine(destination.FullName, CharacterDirectory, "ADDON.DAT")));
            CollectionAssert.AreEqual(
                new byte[] { 9, 8, 7 },
                File.ReadAllBytes(Path.Combine(destination.FullName, CharacterDirectory, "HOTBAR.DAT")));
            Assert.IsFalse(File.Exists(Path.Combine(destination.FullName, "FFXIV.cfg")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination.FullName, CharacterDirectory, "log")));
        });
    }

    [TestMethod]
    public void RestoreCanPreserveNewerFiles()
    {
        WithTemporaryDirectory(root =>
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source"));
            var sourceCharacter = Directory.CreateDirectory(Path.Combine(source.FullName, CharacterDirectory));
            var sourceFile = Path.Combine(sourceCharacter.FullName, "ADDON.DAT");
            File.WriteAllText(sourceFile, "from backup");
            File.SetLastWriteTimeUtc(sourceFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var archive = new FileInfo(Path.Combine(root, "FFXIVconf.fea"));
            FFXIVConfigArchive.Export(source, archive);

            var destination = Directory.CreateDirectory(Path.Combine(root, "destination"));
            var destinationCharacter = Directory.CreateDirectory(Path.Combine(destination.FullName, CharacterDirectory));
            var destinationFile = Path.Combine(destinationCharacter.FullName, "ADDON.DAT");
            File.WriteAllText(destinationFile, "newer local settings");
            File.SetLastWriteTimeUtc(destinationFile, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var preserved = FFXIVConfigArchive.Restore(archive, destination, true);
            Assert.AreEqual(1, preserved.SkippedFileCount);
            Assert.AreEqual("newer local settings", File.ReadAllText(destinationFile));

            var overwritten = FFXIVConfigArchive.Restore(archive, destination, false);
            Assert.AreEqual(0, overwritten.SkippedFileCount);
            Assert.AreEqual("from backup", File.ReadAllText(destinationFile));
        });
    }

    [TestMethod]
    public void RestoreRejectsDamagedChecksumBeforeWriting()
    {
        WithTemporaryDirectory(root =>
        {
            var archive = CreateArchive(root);
            var bytes = File.ReadAllBytes(archive.FullName);
            bytes[^1] ^= 0xff;
            File.WriteAllBytes(archive.FullName, bytes);

            var destination = new DirectoryInfo(Path.Combine(root, "destination"));
            var exception = Assert.ThrowsException<FFXIVConfigArchiveException>(
                () => FFXIVConfigArchive.Restore(archive, destination, false));
            Assert.AreEqual("InvalidArchive", exception.Code);
            Assert.IsFalse(destination.Exists);
        });
    }

    [TestMethod]
    public void RestoreRejectsPathTraversalFileNames()
    {
        WithTemporaryDirectory(root =>
        {
            var archive = CreateArchive(root);
            var bytes = File.ReadAllBytes(archive.FullName);
            var originalName = Encoding.Unicode.GetBytes("ADDON.DAT");
            var maliciousName = Encoding.Unicode.GetBytes("../ON.DAT");
            var offset = bytes.AsSpan().IndexOf(originalName);
            Assert.IsTrue(offset > 0);
            maliciousName.CopyTo(bytes.AsSpan(offset, maliciousName.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), CalculateChecksum(bytes));
            File.WriteAllBytes(archive.FullName, bytes);

            var destination = new DirectoryInfo(Path.Combine(root, "destination"));
            var exception = Assert.ThrowsException<FFXIVConfigArchiveException>(
                () => FFXIVConfigArchive.Restore(archive, destination, false));
            Assert.AreEqual("InvalidArchive", exception.Code);
            Assert.IsFalse(File.Exists(Path.Combine(destination.FullName, "ON.DAT")));
        });
    }

    private static FileInfo CreateArchive(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var character = Directory.CreateDirectory(Path.Combine(source.FullName, CharacterDirectory));
        File.WriteAllText(Path.Combine(character.FullName, "ADDON.DAT"), "settings");
        var archive = new FileInfo(Path.Combine(root, "FFXIVconf.fea"));
        FFXIVConfigArchive.Export(source, archive);
        return archive;
    }

    private static uint CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint checksum = 0;
        for (var index = 16; index < data.Length; index++)
            checksum = unchecked(checksum + data[index]);
        return checksum;
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xomkr-fea-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            action(path);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }
}
