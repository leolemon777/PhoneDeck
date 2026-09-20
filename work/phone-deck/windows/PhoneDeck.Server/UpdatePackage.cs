using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed record UpdateArtifact(string Name, long Size, string Sha256);
internal sealed record UpdateManifest(int Schema, long Sequence, string WindowsVersion,
    int AndroidVersionCode, UpdateArtifact[] Files);

internal static class UpdatePackage
{
    internal const long MaxBundleBytes = 384L * 1024 * 1024;
    internal const long MaxFileBytes = 192L * 1024 * 1024;
    internal const string PublicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAxIxC27iZPNxRsup8lTlPh4VSP534BZlBCn07PI7qd6aDkCsHJ8qe6Ye5jp3oBTFesXaH1PcY7gwsgHIuZdtbI63+SPAaSBAS9S8UWk/V1v5gcSLb5d4rDMPd3AsfRL2Bqt3a69MOK7bbiYlhg4SkKLbG2sspPwLoSBJgSOKsHyw6oEjaRxnxqMC8Hqc+2LG5Oeqh6i9cgHthA1/ZSzhu+97u6VZxa1At/ISU8QBrO9IzyU47nzQ/qp5+zYe6tN/vxaIOpE2BIs9npQdZ8v6jPerztBK+h021vkHuKj5GB2y6TWxO8cEk8LfSFn6Izq/wnpJxv3BSryOFldJIi76zpHUYqZBUngE8jxgQFzGwBzd9sEEBDe49YMadNAmWxtGlwJneOkQTvWGTDfwGFlTYXJSxO8wwjefVAJJVohpRbe3PUbEXA7Lq3PnFJLYiM5++Hiv7uzpOAIHxvoG7Kp9T/XT9+5zKvVQehHN3RQqLg5fiJ3RQ4LveQXjGjdYlu9YVAgMBAAE=";
    internal static readonly string[] Names = ["PhoneDeck.Server.exe", "PhoneDeck.ControlCenter.exe", "PhoneDeck.apk"];
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static UpdateManifest Verify(string bundle, string? destination = null, string? publicKey = null)
    {
        if (new FileInfo(bundle).Length > MaxBundleBytes) throw new InvalidDataException("更新包过大");
        using var archive = ZipFile.OpenRead(bundle);
        var expected = Names.Concat(["manifest.json", "manifest.sig"]).ToHashSet(StringComparer.Ordinal);
        if (archive.Entries.Count != expected.Count || archive.Entries.Any(e => !expected.Remove(e.FullName)))
            throw new InvalidDataException("更新包包含重复、缺失或未允许的文件");
        byte[] ReadSmall(string name, int max)
        {
            var entry = archive.GetEntry(name)!;
            if (entry.Length > max) throw new InvalidDataException("更新清单过大");
            using var input = entry.Open();
            using var output = new MemoryStream();
            var small = new byte[4096];
            int count;
            while ((count = input.Read(small)) > 0)
            {
                if (output.Length + count > max) throw new InvalidDataException("清单大小超限");
                output.Write(small, 0, count);
            }
            return output.ToArray();
        }
        var manifestBytes = ReadSmall("manifest.json", 16 * 1024);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey ?? PublicKey), out _);
        if (!rsa.VerifyData(manifestBytes, ReadSmall("manifest.sig", 1024), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("发布者签名不匹配，拒绝安装");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json)
            ?? throw new InvalidDataException("缺少更新清单");
        if (manifest.Schema != 1 || manifest.Sequence < 1 || manifest.AndroidVersionCode < 1
            || string.IsNullOrWhiteSpace(manifest.WindowsVersion) || manifest.WindowsVersion.Length > 64
            || manifest.Files is null || manifest.Files.Length != Names.Length
            || !manifest.Files.Select(f => f.Name).Order().SequenceEqual(Names.Order()))
            throw new InvalidDataException("不支持的更新清单");
        if (manifest.Files.Sum(f => f.Size) > 512L * 1024 * 1024) throw new InvalidDataException("解压大小超限");
        foreach (var file in manifest.Files)
        {
            var entry = archive.GetEntry(file.Name)!;
            if (file.Size < 1 || file.Size > MaxFileBytes || file.Size != entry.Length)
                throw new InvalidDataException("文件大小不匹配");
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long actual = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                actual += read;
                if (actual > file.Size) throw new InvalidDataException("解压大小超限");
                hash.AppendData(buffer, 0, read);
            }
            if (actual != file.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件校验失败：" + file.Name);
        }
        // Only extract after every file has passed verification. Never use paths from the network.
        if (destination is not null)
        {
            Directory.CreateDirectory(destination);
            foreach (var name in Names) archive.GetEntry(name)!.ExtractToFile(Path.Combine(destination, name), true);
        }
        return manifest;
    }
}
