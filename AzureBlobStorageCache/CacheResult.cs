using System.IO;

namespace ImageResizer.Plugins.AzureBlobStorageCache
{
    public enum CacheQueryResult
    {
        /// <summary>
        /// Failed to acquire a lock on the cached item within the timeout period
        /// </summary>
        Failed,
        /// <summary>
        /// The item wasn't cached, but was successfully added to the cache (or queued, in which case you should read .Data)
        /// </summary>
        Miss,
        /// <summary>
        /// The item was already in the cache.
        /// </summary>
        Hit
    }

    public class CacheResult
    {
        public CacheResult(CacheQueryResult result, string path)
        {
            this.result = result;
        }
        public CacheResult(CacheQueryResult result, Stream data, string path)
        {
            this.result = result;
            this.data = data;
            this.physicalPath = path;
        }

        private string physicalPath = null;

        /// <summary>
        /// The path to the cached item in blob container. Verify .Data is null before trying to read from this item.
        /// </summary>
        public string Path
        {
            get { return physicalPath; }
        }

        private Stream data = null;

        /// <summary>
        /// Provides a read-only stream to the data. Usually a MemoryStream instance, but you should dispose it once you are done. 
        /// If this value is not null, it indicates that the file has not yet been written to blob storage, and you should read it from this stream instead.
        /// </summary>
        public Stream Data
        {
            get { return data; }
            set { data = value; }
        }

        private CacheQueryResult result;

        /// <summary>
        /// The result of the cache check
        /// </summary>
        public CacheQueryResult Result
        {
            get { return result; }
            set { result = value; }
        }
    }
}
