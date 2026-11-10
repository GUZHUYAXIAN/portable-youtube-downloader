using System;
using System.Collections.Generic;

namespace PortableYouTube.StageBridge
{
    public sealed class BridgeSettings
    {
        public string toolsRoot, outputRoot, proxy;
        public int height;
        public int httpChunkSizeBytes;
    }

    public sealed class StageManifest
    {
        public int schemaVersion;
        public string batchId, artist, release;
        public List<StageManifestItem> items;
    }

    public sealed class StageManifestItem
    {
        public string videoId, url, title, channel, eventName, performanceDate, uploadDate, kind, member, sourceUrl;
    }

    public sealed class StageTask
    {
        public string videoId, url, title, channel, artist, release, eventName, performanceDate, uploadDate, kind, member, sourceUrl;
        public string status, targetDirectory, createdUtc, updatedUtc, startedUtc, completedUtc, error;
        public string recoveryReason, recoveredUtc;
        public int attempts;
        public List<string> files = new List<string>();
    }

    public sealed class StageBatch
    {
        public string batchId, artist, release, createdUtc;
        public List<string> videoIds = new List<string>();
    }

    public sealed class BridgeState
    {
        public int schemaVersion = 1;
        public Dictionary<string, StageTask> tasks = new Dictionary<string, StageTask>(StringComparer.Ordinal);
        public Dictionary<string, StageBatch> batches = new Dictionary<string, StageBatch>(StringComparer.Ordinal);
    }

    public sealed class BridgeCounts
    {
        public int total, pending, downloading, succeeded, failed, interrupted, missing;
    }

    public sealed class BridgeReceipt
    {
        public int schemaVersion = 1;
        public string action, batchId, generatedUtc;
        public bool ok, cancelled;
        public int accepted, reused;
        public string error;
        public BridgeCounts counts = new BridgeCounts();
        public List<StageTask> tasks = new List<StageTask>();
        public List<StageBatch> batches = new List<StageBatch>();
    }

    public interface IStageDownloader
    {
        DownloadResult Download(string toolsRoot, DownloadOptions options, Action<DownloadUpdate> update);
        void Cancel();
    }
}
