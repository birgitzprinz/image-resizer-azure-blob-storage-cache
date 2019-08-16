using System;
using System.Collections.Generic;

namespace ImageResizer.Plugins.AzureBlobStorageCache.Index
{
    /// <summary>
    /// Provides thread-safe access to the index of the blob storage cache
    /// </summary>
    public class CacheIndex
    {
        protected Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void AddCachedFileToIndex(string path)
        {
            files.Add(path, path);
        }

        public bool PathExistInIndex(string path)
        {
            return files.ContainsKey(path);
        }

        public void ClearIndex()
        {
            files.Clear();
        }
    }
}
