using ImageResizer.Caching;
using ImageResizer.Configuration.Logging;
using ImageResizer.Plugins.AzureBlobStorageCache.Async;
using ImageResizer.Plugins.AzureBlobStorageCache.Index;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ImageResizer.Plugins.AzureBlobStorageCache
{
    public delegate void CacheResultHandler(CustomBlobStorageCache sender, CacheResult r);

    /// <summary>
    /// Handles access to a blob storage file cache. Handles locking and versioning. 
    /// </summary>
    public class CustomBlobStorageCache
    {
        protected CloudBlobContainer container;

        public CloudBlobContainer Container
        {
            get { return container; }
        }

        protected ILoggerProvider lp;

        public CustomBlobStorageCache(ILoggerProvider lp, CloudBlobContainer container, long asyncMaxQueuedBytes)
        {
            this.lp = lp;
            this.container = container;
            this.CurrentWrites.MaxQueueBytes = asyncMaxQueuedBytes;
        }

        /// <summary>
        /// Fired immediately before GetCachedFile return the result value. 
        /// </summary>
        public event CacheResultHandler CacheResultReturned;


        protected LockProvider locks = new LockProvider();

        /// <summary>
        /// Provides string-based locking for file write access.
        /// </summary>
        public LockProvider Locks
        {
            get { return locks; }
        }

        protected LockProvider queueLocks = new LockProvider();

        /// <summary>
        /// Provides string-based locking for image resizing (not writing, just processing). Prevents duplication of efforts in asynchronous mode, where 'Locks' is not being used.
        /// </summary>
        public LockProvider QueueLocks
        {
            get { return queueLocks; }
        }

        private AsyncWriteCollection _currentWrites = new AsyncWriteCollection();

        /// <summary>
        /// Contains all the queued and in-progress writes to the cache. 
        /// </summary>
        public AsyncWriteCollection CurrentWrites
        {
            get { return _currentWrites; }
        }

        /// <summary>
        /// Provides an in-memory index of the cache.
        /// </summary>
        public CacheIndex Index { get; } = new CacheIndex();

        /// <summary>
        /// If the cached data exists and is up-to-date, returns the path to it (in blob storage). Otherwise, this function tries to cache the data and return the data.
        /// </summary>
        /// <param name="keyBasis">The basis for the key. Should not include the modified date, that is handled inside the function.</param>
        /// <param name="extension">The extension to use for the cached file.</param>
        /// <param name="writeCallback">A method that accepts a Stream argument and writes the data to it.</param>
        /// <param name="timeoutMs"></param>
        /// <returns></returns>
        public CacheResult GetCachedFile(string keyBasis, string extension, ResizeImageDelegate writeCallback, int timeoutMs)
        {
            return GetCachedFile(keyBasis, extension, writeCallback, timeoutMs, false);
        }

        /// <summary>
        /// May return either a physical file name or a MemoryStream with the data. 
        /// Faster than GetCachedFile, as writes are (usually) asynchronous. If the write queue is full, the write is forced to be synchronous again.
        /// Identical to GetCachedFile() when asynchronous=false
        /// </summary>
        /// <param name="keyBasis"></param>
        /// <param name="extension"></param>
        /// <param name="writeCallback"></param>
        /// <param name="timeoutMs"></param>
        /// <returns></returns>
        public CacheResult GetCachedFile(string keyBasis, string extension, ResizeImageDelegate writeCallback, int timeoutMs, bool asynchronous)
        {
            Stopwatch sw = null;
            if (lp.Logger != null) { sw = new Stopwatch(); sw.Start(); }

            // Path to the file in the blob container
            string path = new UrlHasher().Hash(keyBasis) + '.' + extension;
            CacheResult result = new CacheResult(CacheQueryResult.Hit, path);

            bool asyncFailed = false;

            //2013-apr-25: What happens if the file is still being written to blob storage - it's present but not complete? To handle that, we use mayBeLocked.
            bool mayBeLocked = Locks.MayBeLocked(path.ToUpperInvariant());

            // On the first check, verify the file exists by connecting to the blob directly
            if (!asynchronous)
            {
                //May throw an IOException if the file cannot be opened, and is locked by an external processes for longer than timeoutMs. 
                //This method may take longer than timeoutMs under absolute worst conditions. 
                if (!TryWriteFile(result, path, writeCallback, timeoutMs, !mayBeLocked))
                {
                    //On failure
                    result.Result = CacheQueryResult.Failed;
                }
            }
            else if (!Index.PathExistInIndex(path) || mayBeLocked)
            {

                //Looks like a miss. Let's enter a lock for the creation of the file. This is a different locking system than for writing to the file - far less contention, as it doesn't include the 
                //This prevents two identical requests from duplicating efforts. Different requests don't lock.

                //Lock execution using relativePath as the sync basis. Ignore casing differences. This prevents duplicate entries in the write queue and wasted CPU/RAM usage.
                if (!QueueLocks.TryExecute(path.ToUpperInvariant(), timeoutMs,
                    delegate () {

                        //Now, if the item we seek is in the queue, we have a memcached hit. If not, we should check the index. It's possible the item has been written to disk already.
                        //If both are a miss, we should see if there is enough room in the write queue. If not, switch to in-thread writing. 

                        AsyncWrite t = CurrentWrites.Get(path);

                        if (t != null) result.Data = t.GetReadonlyStream();

                        //On the second check, use cached data for speed. The cached data should be updated if another thread updated a file (but not if another process did).
                        //When t == null, and we're inside QueueLocks, all work on the file must be finished, so we have no need to consult mayBeLocked.
                        if (t == null && !Index.PathExistInIndex(path))
                        {

                            result.Result = CacheQueryResult.Miss;
                            //Still a miss, we even rechecked the filesystem. Write to memory.
                            MemoryStream ms = new MemoryStream(4096);  //4K initial capacity is minimal, but this array will get copied around alot, better to underestimate.
                            //Read, resize, process, and encode the image. Lots of exceptions thrown here.
                            writeCallback(ms);
                            ms.Position = 0;

                            AsyncWrite w = new AsyncWrite(CurrentWrites, ms, path);
                            if (CurrentWrites.Queue(w, delegate (AsyncWrite job) {
                                try
                                {
                                    Stopwatch swio = new Stopwatch();

                                    swio.Start();
                                    //TODO: perhaps a different timeout?
                                    if (!TryWriteFile(null, job.Path, delegate (Stream s) { ((MemoryStream)job.GetReadonlyStream()).WriteTo(s); }, timeoutMs, true))
                                    {
                                        swio.Stop();
                                        //We failed to lock the file.
                                        if (lp.Logger != null)
                                            lp.Logger.Warn("Failed to flush async write, timeout exceeded after {1}ms - {0}", result.Path, swio.ElapsedMilliseconds);

                                    }
                                    else
                                    {
                                        swio.Stop();
                                        if (lp.Logger != null)
                                            lp.Logger.Trace("{0}ms: Async write started {1}ms after enqueue for {2}", swio.ElapsedMilliseconds.ToString().PadLeft(4), DateTime.UtcNow.Subtract(w.JobCreatedAt).Subtract(swio.Elapsed).TotalMilliseconds, result.Path);
                                    }

                                }
                                catch (Exception ex)
                                {
                                    if (lp.Logger != null)
                                    {
                                        lp.Logger.Error("Failed to flush async write, {0} {1}\n{2}", ex.ToString(), result.Path, ex.StackTrace);
                                    }
                                }
                                finally
                                {
                                    CurrentWrites.Remove(job); //Remove from the queue, it's done or failed. 
                                }

                            }))
                            {
                                //We queued it! Send back a read-only memory stream
                                result.Data = w.GetReadonlyStream();
                            }
                            else
                            {
                                asyncFailed = false;
                                //We failed to queue it - either the ThreadPool was exhausted or we exceeded the MB limit for the write queue.
                                //Write the MemoryStream to disk using the normal method.
                                //This is nested inside a queuelock because if we failed here, the next one will also. Better to force it to wait until the file is written to blob storage.
                                if (!TryWriteFile(result, path, delegate (Stream s) { ms.WriteTo(s); }, timeoutMs, false))
                                {
                                    if (lp.Logger != null)
                                        lp.Logger.Warn("Failed to queue async write, also failed to lock for sync writing: {0}", result.Path);

                                }
                            }

                        }

                    }))
                {
                    //On failure
                    result.Result = CacheQueryResult.Failed;
                }

            }
            if (lp.Logger != null)
            {
                sw.Stop();
                lp.Logger.Trace("{0}ms: {3}{1} for {2}, Key: {4}", sw.ElapsedMilliseconds.ToString(NumberFormatInfo.InvariantInfo).PadLeft(4), result.Result.ToString(), result.Path, asynchronous ? (asyncFailed ? "Fallback to sync  " : "Async ") : "", keyBasis);
            }

            //Fire event
            if (CacheResultReturned != null) CacheResultReturned(this, result);
            return result;
        }


        /// <summary>
        /// Returns true if the file was written.
        /// Returns false if the in-process lock failed. Throws an exception if any kind of file or processing exception occurs.
        /// </summary>
        /// <param name="result"></param>
        /// <param name="path"></param>
        /// <param name="writeCallback"></param>
        /// <param name="timeoutMs"></param>
        /// <param name="recheckFS"></param>
        /// <returns></returns>
        private bool TryWriteFile(CacheResult result, string path, ResizeImageDelegate writeCallback, int timeoutMs, bool recheckFS)
        {

            bool miss = true;
            if (recheckFS)
            {
                miss = !Index.PathExistInIndex(path);
                if (!miss && !Locks.MayBeLocked(path.ToUpperInvariant())) return true;
            }

            //Lock execution using relativePath as the sync basis. Ignore casing differences. This locking is process-local, but we also have code to handle file locking.
            return Locks.TryExecute(path.ToUpperInvariant(), timeoutMs,
                delegate () {

                    //On the second check, use cached data for speed. The cached data should be updated if another thread updated a file (but not if another process did).
                    if (!Index.PathExistInIndex(path))
                    {

                        //Open stream 
                        //Catch IOException, and if it is a file lock,
                        // - (and hashmodified is true), then it's another process writing to the file, and we can serve the file afterwards
                        // - (and hashmodified is false), then it could either be an IIS read lock or another process writing to the file. Correct behavior is to kill the request here, as we can't guarantee accurate image data.
                        // I.e, hashmodified=true is the only supported setting for multi-process environments.
                        //TODO: Catch UnathorizedAccessException and log issue about file permissions.
                        //... If we can wait for a read handle for a specified timeout.

                        try
                        {
                            var blobRef = container.GetBlockBlobReference(path);

                            // Only write to the blob if it does not exist
                            if (!blobRef.Exists())
                            {
                                CloudBlobStream cloudBlobStream = blobRef.OpenWrite();
                                bool finished = false;
                                try
                                {
                                    using (cloudBlobStream)
                                    {
                                        //Run callback to write the cached data
                                        writeCallback(cloudBlobStream); //Can throw any number of exceptions.
                                        cloudBlobStream.Flush();
                                        finished = true;
                                    }
                                }
                                finally
                                {
                                    //Don't leave half-written files around.
                                    if (!finished) try
                                        {
                                            blobRef.Delete();
                                        }
                                        catch { }
                                }

                                DateTime createdUtc = DateTime.UtcNow;
                            }

                            //Update index
                            Index.AddCachedFileToIndex(path);

                            //This was a cache miss
                            if (result != null) result.Result = CacheQueryResult.Miss;
                        }
                        catch (IOException ex)
                        {
                            
                                //Somehow in between verifying the file didn't exist and trying to create it, the file was created and locked by someone else.
                                //When hashModifiedDate==true, we don't care what the file contains, we just want it to exist. If the file is available for 
                                //reading within timeoutMs, simply do nothing and let the file be returned as a hit.
                                Stopwatch waitForFile = new Stopwatch();
                                bool opened = false;
                                while (!opened && waitForFile.ElapsedMilliseconds < timeoutMs)
                                {
                                    waitForFile.Start();
                                    try
                                    {
                                        using (var stream = container.GetBlockBlobReference(path).OpenRead())
                                            opened = true;
                                    }
                                    catch (IOException iex)
                                    {
                                        Thread.Sleep((int)Math.Min(30, Math.Round((float)timeoutMs / 3.0)));
                                    }
                                    waitForFile.Stop();
                                }
                                if (!opened)
                                    throw; //By not throwing an exception, it is considered a hit by the rest of the code.

                        }
                    }
                });
        }
    }
}
