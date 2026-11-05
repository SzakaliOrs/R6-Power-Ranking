using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Rankings.Admin;

/// <summary>What was pulled: the R2 copy's ETag, when, and the local file's hash right after.</summary>
public sealed record SyncRecord(string ETag, DateTimeOffset PulledAt, string LocalHash);

public sealed record SyncStatus(bool Configured, bool LocalExists, DateTimeOffset? PulledAt, bool LocalChanged, bool InJobWindow);

/// <summary>
/// Moves rankings.db between R2 and the admin's machine. The daily job downloads the file at
/// 04:17 UTC and uploads it when done, so a push is refused from 04:00 to 06:00 UTC, and refused
/// whenever R2's copy is no longer the one pulled: either case would lose a day's ratings or the
/// admin's edits. A pull is refused while local edits are not pushed, unless overwriting is asked for.
/// </summary>
public sealed class R2Sync(string databasePath, IAmazonS3? s3, string? bucket, TimeProvider? time = null)
{
    public const string Key = "rankings.db";

    public static readonly TimeOnly JobWindowStart = new(4, 0);
    public static readonly TimeOnly JobWindowEnd = new(6, 0);

    readonly TimeProvider _time = time ?? TimeProvider.System;

    string RecordPath => databasePath + ".r2.json";

    /// <summary>From R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT and R2_BUCKET; unconfigured when any is missing.</summary>
    public static R2Sync FromEnvironment(string databasePath)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        var (keyId, secret, endpoint, bucket) = (Env("R2_ACCESS_KEY_ID"), Env("R2_SECRET_ACCESS_KEY"), Env("R2_ENDPOINT"), Env("R2_BUCKET"));
        if (keyId is null || secret is null || endpoint is null || bucket is null)
            return new R2Sync(databasePath, null, null);
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new R2Sync(databasePath, new AmazonS3Client(new BasicAWSCredentials(keyId, secret), config), bucket);
    }

    public SyncStatus Status()
    {
        var record = LoadRecord();
        var localExists = File.Exists(databasePath);
        return new SyncStatus(s3 is not null, localExists, record?.PulledAt,
            localExists && record is not null && Hash(databasePath) != record.LocalHash, InJobWindow(_time.GetUtcNow()));
    }

    public async Task PullAsync(bool overwriteLocalChanges, CancellationToken cancellationToken = default)
    {
        var client = Client();
        var record = LoadRecord();
        if (!overwriteLocalChanges && File.Exists(databasePath) && (record is null || Hash(databasePath) != record.LocalHash))
            throw new AdminException("The local rankings.db has changes that were never pushed. Push them, or pull with overwrite to drop them.");

        var temp = databasePath + ".download";
        string etag;
        try
        {
            using var response = await client.GetObjectAsync(bucket, Key, cancellationToken);
            etag = response.ETag;
            await using (var file = File.Create(temp))
                await response.ResponseStream.CopyToAsync(file, cancellationToken);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AdminException($"R2 has no {Key} yet; the first daily run creates it.");
        }
        File.Move(temp, databasePath, overwrite: true);
        SaveRecord(new SyncRecord(etag, _time.GetUtcNow(), Hash(databasePath)));
    }

    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        var client = Client();
        if (!File.Exists(databasePath))
            throw new AdminException("There is no local rankings.db to push.");
        string? remoteETag;
        try
        {
            remoteETag = (await client.GetObjectMetadataAsync(bucket, Key, cancellationToken)).ETag;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            remoteETag = null;
        }
        var localHash = Hash(databasePath);
        if (PushRefusal(_time.GetUtcNow(), LoadRecord(), remoteETag, localHash) is { } refusal)
            throw new AdminException(refusal);

        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = Key,
            FilePath = databasePath,
            ContentType = "application/vnd.sqlite3",
            UseChunkEncoding = false,
        }, cancellationToken);
        SaveRecord(new SyncRecord(response.ETag, _time.GetUtcNow(), localHash));
    }

    /// <summary>Why a push must not happen now, or null when it may.</summary>
    public static string? PushRefusal(DateTimeOffset now, SyncRecord? pulled, string? remoteETag, string localHash)
    {
        if (InJobWindow(now))
            return $"The daily job runs at 04:17 UTC, so pushing is blocked from {JobWindowStart:HH\\:mm} to {JobWindowEnd:HH\\:mm} UTC. Try again later.";
        if (pulled is null)
            return "Pull from R2 first: pushing a file that never came from R2 could replace the live database.";
        if (remoteETag != pulled.ETag)
            return "R2's copy changed since you pulled it (the daily job has run). Pull again and redo the changes, or the day's ratings would be lost.";
        if (localHash == pulled.LocalHash)
            return "Nothing changed since the pull, so there is nothing to push.";
        return null;
    }

    public static bool InJobWindow(DateTimeOffset now)
    {
        var timeOfDay = TimeOnly.FromDateTime(now.UtcDateTime);
        return timeOfDay >= JobWindowStart && timeOfDay < JobWindowEnd;
    }

    IAmazonS3 Client() =>
        s3 ?? throw new AdminException("R2 is not set up: set R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT and R2_BUCKET before starting the dashboard.");

    SyncRecord? LoadRecord() =>
        File.Exists(RecordPath) ? JsonSerializer.Deserialize<SyncRecord>(File.ReadAllText(RecordPath)) : null;

    void SaveRecord(SyncRecord record) => File.WriteAllText(RecordPath, JsonSerializer.Serialize(record));

    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
