using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Rankings.Job.Archive;

/// <summary>Where archived days are kept, by key such as <c>matches/2026/07/08.json.gz</c>.</summary>
public interface IArchiveStore
{
    string Name { get; }

    Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The stored content, or null when nothing is stored under the key.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A folder on disk, mirroring the bucket's keys.</summary>
public sealed class LocalArchiveStore(string root) : IArchiveStore
{
    public string Name => root;

    public async Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    string PathFor(string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Another store with every key under a prefix, e.g. <c>synthetic/</c>, so synthetic days never mix with real ones.</summary>
public sealed class PrefixedArchiveStore(IArchiveStore inner, string prefix) : IArchiveStore
{
    public string Name => $"{inner.Name} ({prefix})";

    public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(prefix + key, content, cancellationToken);

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(prefix + key, cancellationToken);
}

/// <summary>An S3-compatible bucket: Backblaze B2 for the archive.</summary>
public sealed class S3ArchiveStore(IAmazonS3 s3, string bucket) : IArchiveStore
{
    public string Name => $"s3://{bucket}";

    /// <summary>
    /// A client for a B2 bucket. B2 signs with the region named in its endpoint
    /// (s3.eu-central-003.backblazeb2.com) and does not take the SDK's default checksums.
    /// </summary>
    public static S3ArchiveStore ForB2(string keyId, string applicationKey, string endpoint, string bucket)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = B2Region(endpoint),
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new S3ArchiveStore(new AmazonS3Client(new BasicAWSCredentials(keyId, applicationKey), config), bucket);
    }

    public static string B2Region(string endpoint) =>
        Regex.Match(endpoint, @"^(?:https?://)?s3\.([^.]+)\.backblazeb2\.com", RegexOptions.IgnoreCase) is { Success: true } match
            ? match.Groups[1].Value
            : throw new ArgumentException($"'{endpoint}' is not a B2 S3 endpoint like https://s3.eu-central-003.backblazeb2.com.", nameof(endpoint));

    public async Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        // A failed upload throws, which stops the run before the database changes.
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(content),
            ContentType = "application/gzip",
            UseChunkEncoding = false,
        }, cancellationToken);
    }

    /// <summary>Whether anything is stored under the key, without downloading it.</summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
