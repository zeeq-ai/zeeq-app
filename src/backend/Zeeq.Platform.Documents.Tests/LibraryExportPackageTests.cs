using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents.Tests;

public sealed class LibraryExportPackageTests
{
    [Test]
    public async Task Protect_ValidPackage_VerifiesAndReturnsHeaderAndZipBytes()
    {
        var service = new LibraryExportPackageService();
        var protector = CreateProtector();
        var zip = service.CreateZipPayload([
            new LibraryDocument
            {
                Id = "doc_1",
                OrganizationId = "org_123",
                LibraryId = "lib_123",
                Path = "/docs/guide.md",
                Title = "Guide",
                TitleNormalized = "guide",
                Content = "# Guide",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var envelope = protector.Protect(zip, DateTimeOffset.Parse("2026-07-16T18:00:00Z"), 1);
        var verified = protector.TryUnprotect(envelope, out var header, out var verifiedZip);
        var package = service.ParseZipPayload(verifiedZip);

        await Assert.That(verified).IsTrue();
        await Assert.That(header).IsNotNull();
        await Assert.That(header!.Kind).IsEqualTo("library-local-documents");
        await Assert.That(header.DocumentCount).IsEqualTo(1);
        await Assert.That(verifiedZip).IsEquivalentTo(zip);
        await Assert.That(package.Documents).HasSingleItem();
        await Assert.That(package.Documents[0].Path).IsEqualTo("docs/guide.md");
        await Assert.That(package.Documents[0].Content).IsEqualTo("# Guide");
    }

    [Test]
    public async Task TryUnprotect_TamperedHeader_ReturnsFalse()
    {
        var envelope = CreateEnvelope();
        envelope[12] ^= 0x01;

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_TamperedPayload_ReturnsFalse()
    {
        var envelope = CreateEnvelope();
        envelope[^20] ^= 0x01;

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_TamperedTag_ReturnsFalse()
    {
        var envelope = CreateEnvelope();
        envelope[^1] ^= 0x01;

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_WrongKey_ReturnsFalse()
    {
        var envelope = CreateEnvelope();

        var verified = CreateProtector("different-secret")
            .TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_BadMagic_ReturnsFalse()
    {
        var envelope = CreateEnvelope();
        envelope[0] = (byte)'X';

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_TruncatedEnvelope_ReturnsFalseWithoutOpeningZip()
    {
        var envelope = CreateEnvelope()[..10];

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task TryUnprotect_ImpossibleHeaderLength_ReturnsFalse()
    {
        var envelope = new byte["ZEEQEXP1".Length + sizeof(int) + 16];
        Encoding.ASCII.GetBytes("ZEEQEXP1").CopyTo(envelope, 0);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan("ZEEQEXP1".Length, sizeof(int)), 1024);

        var verified = CreateProtector().TryUnprotect(envelope, out var header, out var zip);

        await Assert.That(verified).IsFalse();
        await Assert.That(header).IsNull();
        await Assert.That(zip).IsEmpty();
    }

    [Test]
    public async Task Protect_BlankSigningKey_ThrowsInvalidOperationException()
    {
        var protector = new LibraryExportPackageProtector(new DocumentSettings());

        void Act() => protector.Protect([], DateTimeOffset.UtcNow, 0);

        await Assert.That(Act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CreateZipPayload_ExcludesRemoteDocuments()
    {
        var service = new LibraryExportPackageService();

        var zip = service.CreateZipPayload([
            LocalDocument("/local.md", "local"),
            LocalDocument("/synced.md", "synced", syncRunId: "sync_123"),
            LocalDocument(
                "/remote.md",
                "remote",
                sourceOrigin: new LibraryDocumentSourceOrigin("GitHub", "owner/repo")
            ),
        ]);
        var package = service.ParseZipPayload(zip);

        await Assert.That(package.Documents).HasSingleItem();
        await Assert.That(package.Documents[0].Path).IsEqualTo("local.md");
    }

    [Test]
    public async Task ParseZipPayload_DuplicateNormalizedPaths_ThrowsValidationException()
    {
        var duplicateHash = ComputeSha256Hex("one");
        var zip = CreateZip(
            new LibraryExportManifest([
                new("docs/guide.md", duplicateHash),
                new("/docs/guide.md", duplicateHash),
            ]),
            [("documents/docs/guide.md", "one")]
        );
        var service = new LibraryExportPackageService();

        void Act() => service.ParseZipPayload(zip);

        await Assert.That(Act).Throws<LibraryExportPackageValidationException>();
    }

    [Test]
    public async Task ParseZipPayload_ParentDirectoryPath_ThrowsValidationException()
    {
        var zip = CreateZip(
            new LibraryExportManifest([new("docs/../guide.md", ComputeSha256Hex("one"))]),
            [("documents/docs/../guide.md", "one")]
        );
        var service = new LibraryExportPackageService();

        void Act() => service.ParseZipPayload(zip);

        await Assert.That(Act).Throws<LibraryExportPackageValidationException>();
    }

    [Test]
    public async Task ParseZipPayload_HashMismatch_ThrowsValidationException()
    {
        var zip = CreateZip(
            new LibraryExportManifest([new("docs/guide.md", new string('0', 64))]),
            [("documents/docs/guide.md", "one")]
        );
        var service = new LibraryExportPackageService();

        void Act() => service.ParseZipPayload(zip);

        await Assert.That(Act).Throws<LibraryExportPackageValidationException>();
    }

    [Test]
    public async Task ParseZipPayload_LargeSingleDocumentUnderPackageLimit_ReturnsDocument()
    {
        var content = new string('a', 250_000);
        var zip = CreateZip(
            new LibraryExportManifest([new("docs/guide.md", ComputeSha256Hex(content))]),
            [("documents/docs/guide.md", content)]
        );
        var service = new LibraryExportPackageService();

        var package = service.ParseZipPayload(zip);

        await Assert.That(package.Documents).HasSingleItem();
        await Assert.That(package.Documents[0].Content).IsEqualTo(content);
    }

    [Test]
    public async Task ParseZipPayload_SingleDocumentOverPackageContentLimit_ThrowsValidationException()
    {
        var contentBytes = Encoding.UTF8.GetBytes(new string('a', 600_000));
        var zip = CreateZip(
            new LibraryExportManifest([new("docs/guide.md", ComputeSha256Hex(contentBytes))]),
            [("documents/docs/guide.md", contentBytes)]
        );
        var service = new LibraryExportPackageService();

        void Act() => service.ParseZipPayload(zip);

        await Assert.That(Act).Throws<LibraryExportPackageValidationException>();
    }

    [Test]
    public async Task ParseZipPayload_AggregateContentOverByteLimit_ThrowsValidationException()
    {
        var firstBytes = Encoding.UTF8.GetBytes(new string('a', 300_000));
        var secondBytes = Encoding.UTF8.GetBytes(new string('b', 300_000));
        var zip = CreateZip(
            new LibraryExportManifest([
                new("docs/one.md", ComputeSha256Hex(firstBytes)),
                new("docs/two.md", ComputeSha256Hex(secondBytes)),
            ]),
            [("documents/docs/one.md", firstBytes), ("documents/docs/two.md", secondBytes)]
        );
        var service = new LibraryExportPackageService();

        void Act() => service.ParseZipPayload(zip);

        await Assert.That(Act).Throws<LibraryExportPackageValidationException>();
    }

    private static LibraryExportPackageProtector CreateProtector(string key = "test-secret") =>
        new(new DocumentSettings { LibraryExportSigningKey = key });

    private static byte[] CreateEnvelope()
    {
        var service = new LibraryExportPackageService();
        var zip = service.CreateZipPayload([LocalDocument("/docs/guide.md", "# Guide")]);

        return CreateProtector().Protect(zip, DateTimeOffset.UtcNow, 1);
    }

    private static LibraryDocument LocalDocument(
        string path,
        string content,
        string? syncRunId = null,
        LibraryDocumentSourceOrigin? sourceOrigin = null
    ) =>
        new()
        {
            Id = $"doc_{Guid.CreateVersion7():N}",
            OrganizationId = "org_123",
            LibraryId = "lib_123",
            Path = path,
            Title = Path.GetFileNameWithoutExtension(path),
            TitleNormalized = Path.GetFileNameWithoutExtension(path),
            Content = content,
            SourceOrigin = sourceOrigin,
            SyncRunId = syncRunId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static byte[] CreateZip(
        LibraryExportManifest manifest,
        IReadOnlyCollection<(string Name, string Content)> entries
    ) =>
        CreateZip(
            manifest,
            entries
                .Select(entry => (entry.Name, Bytes: Encoding.UTF8.GetBytes(entry.Content)))
                .ToArray()
        );

    private static byte[] CreateZip(
        LibraryExportManifest manifest,
        IReadOnlyCollection<(string Name, byte[] Bytes)> entries
    )
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var manifestStream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(
                    manifestStream,
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                );
            }

            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    private static string ComputeSha256Hex(string content) =>
        ComputeSha256Hex(Encoding.UTF8.GetBytes(content));

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
