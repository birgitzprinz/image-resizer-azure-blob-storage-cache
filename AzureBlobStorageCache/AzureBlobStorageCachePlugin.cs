using ImageResizer.Caching;
using ImageResizer.Configuration;
using ImageResizer.Configuration.Issues;
using ImageResizer.Configuration.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;
using System.Web;

namespace ImageResizer.Plugins.AzureBlobStorageCache
{
    /// <summary>
    /// Indicates a problem with blob storage caching. Causes include a missing BlobCacheContainer setting, and severe I/O locking preventing 
    /// the cache container from being written at all.
    /// </summary>
    public class BlobStorageCacheException : Exception
    {
        public BlobStorageCacheException(string message) : base(message) { }

        public BlobStorageCacheException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Provides methods for creating, maintaining the blob storage cache.
    /// </summary>
    public class AzureBlobStorageCachePlugin : ICache, IPlugin, IIssueProvider, ILoggerProvider
    {
        private bool enabled = true;

        /// <summary>
        /// Allows blob storage caching to be disabled for debuginng purposes. Defaults to true.
        /// </summary>
        public bool Enabled
        {
            get {
                return enabled;
            }
            set {
                BeforeSettingChanged();
                enabled = value;
            }
        }

        /// <summary>
        /// Gets the azure blob storage connection string name.
        /// </summary>
        /// <value>
        /// The connection string.
        /// </value>
        public string ConnectionStringName { get; private set; }

        private string _storageContainer;

        /// <summary>
        /// Gets or sets the blob storage container for the cache mechanism.
        /// </summary>
        /// <value>
        /// The storage container.
        /// </value>
        public string StorageContainer
        {
            get
            {
                return _storageContainer;
            }
            set
            {
                BeforeSettingChanged();
                _storageContainer = value;
            }
        }

        protected int cacheAccessTimeout = 30000;

        /// <summary>
        /// How many milliseconds to wait for a cached item to be available. Values below 0 are set to 0. Defaults to 30 seconds.
        /// Actual time spent waiting may be 2 or 3x this value, if multiple layers of synchronization require a wait.
        /// </summary>
        public int CacheAccessTimeout
        {
            get { return cacheAccessTimeout; }
            set { BeforeSettingChanged(); cacheAccessTimeout = Math.Max(value, 0); }
        }

        private bool _asyncWrites = false;

        /// <summary>
        /// If true, writes to the blob storage cache will be performed outside the request thread, allowing responses to return to the client quicker. 
        /// </summary>
        public bool AsyncWrites
        {
            get { return _asyncWrites; }
            set { BeforeSettingChanged(); _asyncWrites = value; }
        }

        private int _asyncBufferSize = 1024 * 1024 * 10;

        /// <summary>
        /// If more than this amount of memory (in bytes) is currently allocated by queued writes, the request will be processed synchronously instead of asynchronously.
        /// </summary>
        public int AsyncBufferSize
        {
            get { return _asyncBufferSize; }
            set { BeforeSettingChanged(); _asyncBufferSize = value; }
        }

        /// <summary>
        /// Throws an exception if the class is already modified
        /// </summary>
        protected void BeforeSettingChanged()
        {
            if (_started) throw new InvalidOperationException("AzureBlobStorageCache settings may not be adjusted after it is started.");
        }

        /// <summary>
        /// Creates a default blob storage cache.
        /// </summary>
        public AzureBlobStorageCachePlugin() { }

        /// <summary>
        /// Creates a BlobStorageCache instance with the specified container. Must be installed as a plugin to be operational.
        /// </summary>
        /// <param name="container">The blob storage container used for image cache</param>
        public AzureBlobStorageCachePlugin(string container)
        {
            StorageContainer = container;
        }
        /// <summary>
        /// Uses the defaults from the resizing.blobstoragecache section in the specified configuration.
        /// Throws an invalid operation exception if the BlobStorageCache is already started.
        /// </summary>
        public void LoadSettings(Config c)
        {
            Enabled = c.get("azureblobstoragecache.enabled", Enabled);
            ConnectionStringName = c.get("azureblobstoragecache.connectionstringname", null);
            CacheAccessTimeout = c.get("azureblobstoragecache.cacheAccessTimeout", CacheAccessTimeout);
            AsyncBufferSize = c.get("azureblobstoragecache.asyncBufferSize", AsyncBufferSize);
            AsyncWrites = c.get("azureblobstoragecache.asyncWrites", AsyncWrites);
            StorageContainer = c.get("azureblobstoragecache.container", "scaledimages");
        }

        protected ILogger log = null;

        public ILogger Logger { get { return log; } }

        /// <summary>
        /// Loads the settings from 'c', starts the cache, and registers the plugin.
        /// Will throw an invalidoperationexception if already started.
        /// </summary>
        /// <param name="c"></param>
        /// <returns></returns>
        public IPlugin Install(Config config)
        {
            if (config.get("azureblobstoragecache.logging", false))
            {
                if (config.Plugins.LogManager != null)
                    log = config.Plugins.LogManager.GetLogger("ImageResizer.Plugins.AzureBlobStorageCache");
                else
                    config.Plugins.LoggingAvailable += delegate (ILogManager mgr) {
                        if (log != null) log = config.Plugins.LogManager.GetLogger("ImageResizer.Plugins.AzureBlobStorageCache");
                    };
            }
            LoadSettings(config);
            Start();

            config.Plugins.add_plugin(this);
            return this;
        }

        public bool Uninstall(Config c)
        {
            c.Plugins.remove_plugin(this);
            return this.Stop();
        }

        /// <summary>
        /// Returns true if the configured settings are valid.
        /// </summary>
        /// <returns></returns>
        public bool IsConfigurationValid()
        {
            return !string.IsNullOrEmpty(StorageContainer) 
                && this.Enabled && HasBlobAccessPermission();
        }

        /// <summary>
        /// Returns true if the configuration for blob storage account & container is correct. 
        /// </summary>
        /// <returns></returns>
        protected bool HasBlobAccessPermission()
        {
            var container = GetBlobContainer();

            // Create the container if it does not exist.
            try
            {
                container.CreateIfNotExistsAsync();
            }
            catch(StorageException sx)
            {
                if (log != null) log.Error($"Failed to get container reference: {sx.Message}");
                return false;
            }

            return true;
        }


        /// <summary>
        /// The inner cache implementation
        /// </summary>
        protected CustomBlobStorageCache cache = null;

        protected readonly object _startSync = new object();
        protected volatile bool _started = false;

        /// <summary>
        /// Returns true if the DiskCache instance is operational.
        /// </summary>
        public bool Started { get { return _started; } }

        /// <summary>
        /// Attempts to start the Blob Storage Cache using the current settings. 
        /// Returns true if succesful or if already started. Returns false on a configuration error.
        /// Called by Install()
        /// </summary>
        public bool Start()
        {
            if (!IsConfigurationValid()) return false;
            lock (_startSync)
            {
                if (_started) return true;
                if (!IsConfigurationValid()) return false;

                // Retrieve the blob container & Init the inner cache
                var container = GetBlobContainer();
                cache = new CustomBlobStorageCache(this, container, AsyncBufferSize);

                if (log != null) log.Info("AzureBlobStorageCache started successfully.");

                // Started successfully
                _started = true;
                return true;
            }
        }

        /// <summary>
        /// Gets the BLOB container from settings.
        /// </summary>
        /// <returns></returns>
        private CloudBlobContainer GetBlobContainer()
        {
            var blobConnectionString = ConfigurationManager.ConnectionStrings[ConnectionStringName];
            if (string.IsNullOrEmpty(blobConnectionString?.ConnectionString))
            {
                return null;
            }

            var storageClient = CloudStorageAccount.Parse(blobConnectionString.ConnectionString).CreateCloudBlobClient();
            var container = storageClient.GetContainerReference(StorageContainer);

            return container;
        }

        /// <summary>
        /// Returns true if stopped succesfully. Cannot be restarted
        /// </summary>
        /// <returns></returns>
        public bool Stop()
        {
            return true;
        }

        public bool CanProcess(HttpContext current, IResponseArgs e)
        {
            // Blob storage caching will 'pass on' caching requests if 'cache=no'.
            if (((ResizeSettings)e.RewrittenQuerystring).Cache == ServerCacheMode.No) return false;
            return Started; //Add support for nocache
        }


        public void Process(HttpContext context, IResponseArgs e)
        {

            CacheResult r = Process(e);
            context.Items["FinalCachedFile"] = r.Path;

            if (r.Data == null)
            {
                var keyBasis = e.RequestKey;

                // Path to the file in the blob container
                string path = new UrlHasher().Hash(keyBasis) + '.' + e.SuggestedExtension;
                var container = GetBlobContainer();

                using (var stream = container.GetBlockBlobReference(path).OpenRead())
                {
                    HandleResponseStream(context, e, stream);
                }
            }
            else
            {
                HandleResponseStream(context, e, (MemoryStream)r.Data);
            }
        }

        public CacheResult Process(IResponseArgs e)
        {
            // Cache the data to blob and return a cached result.
            CacheResult r = cache.GetCachedFile(e.RequestKey, e.SuggestedExtension, e.ResizeImageToStream, CacheAccessTimeout, AsyncWrites);

            // Fail
            if (r.Result == CacheQueryResult.Failed)
                throw new ImageProcessingException("Failed to acquire a lock on blob \"" + r.Path + "\" within " + CacheAccessTimeout + "ms. Caching failed.");

            return r;
        }

        protected string GetExecutingUser()
        {
            try
            {
                return Thread.CurrentPrincipal.Identity.Name;
            }
            catch
            {
                return "[Unknown - please check App Pool configuration]";
            }
        }

        public IEnumerable<IIssue> GetIssues()
        {
            List<IIssue> issues = new List<IIssue>();

            if (!HasBlobAccessPermission())
                issues.Add(new Issue("AzureBlobStorageCache", "Failed to start: Cannot access specified blob container in configuration.",
                "Please configure your blob connection string and container correctly.", IssueSeverity.ConfigurationError));

            if (!Started && !Enabled) issues.Add(new Issue("AzureBlobStorageCache", "AzureBlobStorageCache is disabled in Web.config. Set enabled=true on the <azureblobstoragecache /> element to fix.", null, IssueSeverity.ConfigurationError));

            if (this.AsyncBufferSize < 1024 * 1024 * 2)
                issues.Add(new Issue("AzureBlobStorageCache", "The asyncBufferSize should not be set below 2 megabytes (2097152). Found in the <azureblobstoragecache /> element in Web.config.",
                    "A buffer that is too small will cause requests to be processed synchronously. Remember to set the value to at least 4x the maximum size of an output image.", IssueSeverity.ConfigurationError));

            return issues;
        }

        /// <summary>
        /// Handles the response stream for the request to images.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="stream">The stream.</param>
        private void HandleResponseStream(HttpContext context, IResponseArgs args, Stream stream)
        {
            context.Response.Clear();
            context.Response.StatusCode = 200;
            context.Response.BufferOutput = true;
            args.ResponseHeaders.ApplyDuringPreSendRequestHeaders = false;
            args.ResponseHeaders.ApplyToResponse(args.ResponseHeaders, context);
            stream.CopyTo(context.Response.OutputStream);
            context.Response.OutputStream.Flush();
            context.Response.End();
        }
    }
}
