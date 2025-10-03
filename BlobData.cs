using ASCTableStorage.Models;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Newtonsoft.Json.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ASCTableStorage.Blobs
{
    /// <summary>
    /// Service to handle Azure Blob Storage operations with tag-based indexing and lambda search support.
    /// Operates on a single container throughout the object's lifetime.
    /// Supports up to 10 customizable index tags per blob for fast searching.
    /// </summary>
    public class AzureBlobs
    {
        private readonly BlobServiceClient m_client;
        private readonly BlobContainerClient m_containerClient;
        private readonly long m_maxFileSize;
        private readonly string m_accountName;
        private readonly string m_containerName;

        /// <summary>
        /// Constructor initializing the blob service client with account credentials for a specific container
        /// </summary>
        /// <param name="accountName">Azure Storage account name</param>
        /// <param name="accountKey">Azure Storage account key</param>
        /// <param name="containerName">Container name to operate on</param>
        /// <param name="defaultMaxFileSizeBytes">Default maximum file size in bytes (default: 5MB)</param>
        public AzureBlobs(string accountName, string accountKey, string containerName, long defaultMaxFileSizeBytes = 5 * 1024 * 1024)
        {
            if (string.IsNullOrEmpty(accountName))
                throw new ArgumentNullException(nameof(accountName));
            if (string.IsNullOrEmpty(accountKey))
                throw new ArgumentNullException(nameof(accountKey));
            if (string.IsNullOrEmpty(containerName))
                throw new ArgumentNullException(nameof(containerName));

            m_accountName = accountName;
            m_containerName = containerName;
            var credential = new StorageSharedKeyCredential(accountName, accountKey);
            m_client = new BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                credential
            );
            m_containerClient = m_client.GetBlobContainerClient(containerName);
            m_maxFileSize = defaultMaxFileSizeBytes;

            // Ensure container exists
            _ = Task.Run(async () => await m_containerClient.CreateIfNotExistsAsync(PublicAccessType.None));
        }

        /// <summary>
        /// Gets the container name this instance operates on
        /// </summary>
        public string ContainerName => m_containerName;

        /// <summary>
        /// Adds or updates a single allowed file type
        /// </summary>
        /// <param name="extension">File extension including dot (e.g. ".pdf")</param>
        /// <param name="contentType">MIME content type</param>
        public void AddAllowedFileType(string extension, string contentType)
        {
            if (string.IsNullOrEmpty(extension) || !extension.StartsWith("."))
                throw new ArgumentException("Extension must start with a dot", nameof(extension));

            BlobData.FileTypes[extension.ToLowerInvariant()] = contentType;
        }

        /// <summary>
        /// Adds multiple allowed file types from a collection
        /// </summary>
        /// <param name="fileTypes">Dictionary of extensions and their MIME types</param>
        public void AddAllowedFileType(Dictionary<string, string> fileTypes)
        {
            if (fileTypes == null)
                throw new ArgumentNullException(nameof(fileTypes));

            foreach (var fileType in fileTypes)
            {
                AddAllowedFileType(fileType.Key, fileType.Value);
            }
        }

        /// <summary>
        /// Adds multiple allowed file types from arrays
        /// </summary>
        /// <param name="extensions">Array of file extensions</param>
        /// <param name="contentTypes">Array of corresponding MIME types</param>
        public void AddAllowedFileType(string[] extensions, string[] contentTypes)
        {
            if (extensions == null)
                throw new ArgumentNullException(nameof(extensions));
            if (contentTypes == null)
                throw new ArgumentNullException(nameof(contentTypes));
            if (extensions.Length != contentTypes.Length)
                throw new ArgumentException("Extensions and content types arrays must have the same length");

            for (int i = 0; i < extensions.Length; i++)
            {
                AddAllowedFileType(extensions[i], contentTypes[i]);
            }
        }

        /// <summary>
        /// Removes allowed file type
        /// </summary>
        /// <param name="extension">File extension to remove</param>
        /// <returns>True if removed, false if not found</returns>
        public bool RemoveAllowedFileType(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return false;

            return BlobData.FileTypes.Remove(extension.ToLowerInvariant());
        }

        /// <summary>
        /// Checks if a file type is allowed. Returns true if no file type restrictions are configured.
        /// </summary>
        /// <param name="filePath">File path or name to check</param>
        /// <returns>True if allowed or no restrictions configured, false otherwise</returns>
        public bool IsFileTypeAllowed(string filePath)
        {
            // If no file types are configured, allow all files
            if (BlobData.FileTypes == null || BlobData.FileTypes.Count == 0)
                return true;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return BlobData.FileTypes.ContainsKey(extension);
        }

        /// <summary>
        /// Gets content type for a file
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>Content type or default if not found</returns>
        public string GetContentType(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            return BlobData.FileTypes.TryGetValue(extension, out string? contentType)
                ? contentType
                : "application/octet-stream";
        }

        #region Upload Methods with Tag Support

        /// <summary>
        /// Uploads a file to Azure Blob Storage with size validation and optional index tags
        /// </summary>
        /// <param name="filePath">The local file path</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <param name="indexTags">Optional dictionary of up to 10 index tags for fast searching</param>
        /// <param name="metadata">Optional metadata dictionary (not searchable but accessible)</param>
        /// <returns>The URI of the uploaded blob</returns>
        public async Task<Uri> UploadFileAsync(string filePath, bool enforceFileTypeRestriction = true,
            long? maxFileSizeBytes = null, Dictionary<string, string>? indexTags = null,
            Dictionary<string, string>? metadata = null)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            FileInfo fi = new FileInfo(filePath);

            // Check file size
            long maxSize = maxFileSizeBytes ?? m_maxFileSize;
            if (fi.Length > maxSize)
                throw new ArgumentException($"File size exceeds the maximum allowed size of {maxSize} bytes");

            // Check file type if enforcing restrictions
            if (enforceFileTypeRestriction && !IsFileTypeAllowed(filePath))
                throw new ArgumentException($"File type {Path.GetExtension(filePath)} is not allowed");

            // Validate index tags (Azure limit: 10 tags max)
            ValidateIndexTags(indexTags);

            // Use the original file name as blob name
            string blobName = Path.GetFileName(filePath);

            // Get a reference to the blob
            BlobClient bc = m_containerClient.GetBlobClient(blobName);

            // Prepare upload options
            var uploadOptions = CreateUploadOptions(filePath, fi.Length, indexTags, metadata);

            // Upload the file (overwrite by default)
            await bc.UploadAsync(filePath, uploadOptions);

            return bc.Uri;
        }

        /// <summary>
        /// Allows for Blob Data to be created from a string input.
        /// Returns the link to the newly created file in Blob Storage
        /// </summary>
        /// <param name="blobName">Name of the blob file to store</param>
        /// <param name="data">The string data to store</param>
        /// <param name="format">
        /// The data format. ex:text/html : text/javascript : text/css : text/x-csharp : text/plain
        /// </param>
        /// <param name="tags">The searchable tags to store</param>
        /// <param name="metaTags">The extra internal meta data about the file</param>
        public async Task<string> UploadStringDataAsync(string blobName, string data, string format, Dictionary<string, string> tags = null!, Dictionary<string, string> metaTags = null!)
        {
            string url = string.Empty;
            try
            {
                // Convert string to stream
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(data)))
                {
                    // Upload the stream to blob storage
                    Uri blobUri = await this.UploadStreamAsync(
                        stream: stream,                         // The stream containing your string data
                        fileName: blobName,                     // The name the blob will have
                        contentType: format,                    // Optional: specify content type (can infer from extension)
                        enforceFileTypeRestriction: false,       // Optional: enforce file type rules
                        maxFileSizeBytes: null,                 // Optional: use default or specify
                        indexTags: tags,                        // Optional: add tags for searching
                        metadata: metaTags                      // Optional: add metadata
                    );

                    url = blobUri.ToString();
                    // The 'blobUri' now points to your newly created blob containing the string content.
                } // The 'using' statement ensures the stream is disposed after upload
            }
            catch (Exception ex)
            {
                url = ex.Message;
            }

            return url;
        }

        /// <summary>
        /// Uploads a stream to Azure Blob Storage with size validation and optional index tags
        /// </summary>
        /// <param name="stream">The stream to upload</param>
        /// <param name="fileName">The file name to use for the blob</param>
        /// <param name="contentType">The content type of the blob (optional, will be determined from filename if not provided)</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <param name="indexTags">Optional dictionary of up to 10 index tags for fast searching</param>
        /// <param name="metadata">Optional metadata dictionary (not searchable but accessible)</param>
        /// <returns>The URI of the uploaded blob</returns>
        public async Task<Uri> UploadStreamAsync(Stream stream, string fileName, string? contentType = null,
            bool enforceFileTypeRestriction = true, long? maxFileSizeBytes = null,
            Dictionary<string, string>? indexTags = null, Dictionary<string, string>? metadata = null)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            // Check file size
            long maxSize = maxFileSizeBytes ?? m_maxFileSize;
            if (stream.Length > maxSize)
                throw new ArgumentException($"Stream size exceeds the maximum allowed size of {maxSize} bytes");

            // Check file type if enforcing restrictions
            if (enforceFileTypeRestriction && !IsFileTypeAllowed(fileName))
                throw new ArgumentException($"File type {Path.GetExtension(fileName)} is not allowed");

            // Validate index tags
            ValidateIndexTags(indexTags);

            // Get a reference to the blob
            BlobClient bc = m_containerClient.GetBlobClient(fileName);

            // Determine content type
            if (string.IsNullOrEmpty(contentType))
            {
                contentType = GetContentType(fileName);
            }

            // Prepare upload options
            var uploadOptions = CreateUploadOptions(contentType, fileName, stream.Length, indexTags, metadata);

            // Upload the stream (overwrite by default)
            await bc.UploadAsync(stream, uploadOptions);

            return bc.Uri;
        }

        /// <summary>
        /// Uploads multiple files to Azure Blob Storage with optional index tags
        /// </summary>
        /// <param name="files">Collection of file upload information</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <returns>Dictionary of filename to upload result</returns>
        public async Task<Dictionary<string, BlobUploadResult>> UploadMultipleFilesAsync(
            IEnumerable<BlobUploadInfo> files, bool enforceFileTypeRestriction = true,
            long? maxFileSizeBytes = null)
        {
            if (files == null)
                throw new ArgumentNullException(nameof(files));

            var results = new Dictionary<string, BlobUploadResult>();
            var exceptions = new List<Exception>();

            foreach (var fileInfo in files)
            {
                try
                {
                    var blobUri = await UploadFileAsync(
                        fileInfo.FilePath,
                        enforceFileTypeRestriction,
                        maxFileSizeBytes,
                        fileInfo.IndexTags,
                        fileInfo.Metadata);

                    var fileName = Path.GetFileName(fileInfo.FilePath);
                    results.Add(fileName, new BlobUploadResult
                    {
                        Success = true,
                        BlobUri = blobUri,
                        FileName = fileName
                    });
                }
                catch (Exception ex)
                {
                    var fileName = Path.GetFileName(fileInfo.FilePath);
                    results.Add(fileName, new BlobUploadResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message,
                        FileName = fileName
                    });
                    exceptions.Add(new Exception($"Error uploading {fileName}: {ex.Message}", ex));
                }
            }

            if (exceptions.Count > 0 && exceptions.Count == results.Count)
            {
                throw new AggregateException("All files failed to upload", exceptions);
            }

            return results;
        }

        #endregion Upload Methods

        #region Search and Query Methods

        /// <summary>
        /// Searches blobs using lambda expressions with hybrid tag/metadata filtering
        /// </summary>
        /// <param name="predicate">Lambda expression for filtering blobs</param>
        /// <param name="prefix">Optional prefix filter</param>
        /// <returns>List of matching blob data</returns>
        /// <example>
        /// var blobs = await azureBlobs.GetCollectionAsync(b => b.Tags["brand"] == "volvo" && b.Tags["type"] == "invoice");
        /// var recent = await azureBlobs.GetCollectionAsync(b => b.UploadDate > DateTime.Today.AddDays(-7));
        /// </example>
        public async Task<List<BlobData>> GetCollectionAsync(Expression<Func<BlobData, bool>> predicate, string? prefix = null)
        {
            var filterResult = ConvertLambdaToHybridBlobFilter(predicate);
            List<BlobData> results = new();

            // 1. Try server-side filtering via tag query
            if (!string.IsNullOrEmpty(filterResult.TagQuery))
            {
                var serverResults = await SearchBlobsByTagsAsync(filterResult.TagQuery);

                // Internally track which required tag keys are present in each blob
                foreach (var blob in serverResults)
                {
                    var matchedKeys = filterResult.RequiredTagKeys
                        .Where(key => blob.Tags.ContainsKey(key))
                        .ToList();

                    if (matchedKeys.Count > 0)
                        results.Add(blob); // Include blobs with at least one matching tag key
                }
            }

            // 2. If no matches found, fallback to listing blobs scoped to your data via prefix
            if (results.Count == 0 && !string.IsNullOrEmpty(prefix))
                results = await ListBlobsAsync(prefix); // Assumes prefix scopes to your tenant/client

            // 3. Apply client-side filtering if needed
            if (filterResult.RequiresClientFiltering && filterResult.UntranslatableExpression != null)
            {
                filterResult.ClientSidePredicate = filterResult.UntranslatableExpression.Compile();
                results = results
                    .Where(blob =>
                        filterResult.RequiredTagKeys.All(key => blob.Tags.ContainsKey(key)) &&
                        filterResult.ClientSidePredicate(blob))
                    .ToList();
            }

            return results;
        }

        /// <summary>
        /// Searches blobs by tag query string
        /// </summary>
        /// <param name="tagQuery">OData-style tag query (container filter will be added automatically)</param>
        /// <param name="loadContent">
        /// True if you also want to have the data within the blog loaded.
        /// This is a performance hit but it's TRUE by default because its the assumption you want the data
        /// </param>
        /// <returns>List of matching blob data</returns>
        /// <example>
        /// var query = "brand = 'volvo' AND type = 'pdf'";
        /// var blobs = await azureBlobs.SearchBlobsByTagsAsync(query);
        /// </example>
        public async Task<List<BlobData>> SearchBlobsByTagsAsync(string tagQuery, bool loadContent = true)
        {
            var results = new List<BlobData>();
            // Build the complete query
            string fullQuery = BuildTagQuery(tagQuery);
            if (string.IsNullOrEmpty(fullQuery))
            {
                // If no query, list all blobs in the container
                return await ListBlobsAsync();
            }
            try
            {
                await foreach (var taggedBlobItem in m_client.FindBlobsByTagsAsync(fullQuery))
                {
                    // Only process blobs from our container
                    if (taggedBlobItem.BlobContainerName != m_containerName)
                        continue;
                    // Get detailed blob information
                    var blobClient = m_containerClient.GetBlobClient(taggedBlobItem.BlobName);
                    try
                    {
                        var properties = await blobClient.GetPropertiesAsync();
                        var tags = await blobClient.GetTagsAsync();
                        var blobData = new BlobData
                        {
                            Name = taggedBlobItem.BlobName,
                            OriginalFilename = properties.Value.Metadata.TryGetValue("OriginalFilename", out var origName) ? origName : taggedBlobItem.BlobName,
                            ContentType = properties.Value.ContentType,
                            Size = properties.Value.ContentLength,
                            UploadDate = properties.Value.Metadata.TryGetValue("UploadedOn", out var uploadDate) && DateTime.TryParse(uploadDate, out var parsedDate) ? parsedDate : properties.Value.CreatedOn.DateTime,
                            Url = blobClient.Uri,
                            ContainerName = m_containerName,
                            Tags = tags.Value.Tags?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>(),
                            Metadata = properties.Value.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>()
                        };

                        if (loadContent)
                        {
                            // Download content for this blob
                            using var memoryStream = new MemoryStream();
                            await blobClient.DownloadToAsync(memoryStream);
                            blobData.Data = memoryStream.ToArray();
                        }
                        results.Add(blobData);
                    }
                    catch (RequestFailedException)
                    {
                        // Blob might have been deleted between the tag search and property retrieval
                        continue;
                    }
                }
            }
            catch (RequestFailedException ex)
            {
                throw new InvalidOperationException($"Error searching blobs by tags: {ex.Message}", ex);
            }
            return results;
        }

        /// <summary>
        /// Lists all blobs in the container with optional filtering
        /// </summary>
        /// <param name="prefix">Optional prefix filter</param>
        /// <param name="loadContent">
        /// True if you also want to have the data within the blog loaded.
        /// This is a performance hit so it's FALSE by default because its the 
        /// assumption you DON'T want the data for the Whole Container
        /// </param>
        /// <returns>A list of blob items with metadata and tags</returns>
        public async Task<List<BlobData>> ListBlobsAsync(string? prefix = null, bool loadContent = false)
        {
            var results = new List<BlobData>();
            // Create blob listing options
            BlobTraits traits = BlobTraits.Metadata | BlobTraits.Tags;
            BlobStates states = BlobStates.None;
            // List blobs
            AsyncPageable<BlobItem> blobs = m_containerClient.GetBlobsAsync(traits, states, prefix);
            await foreach (BlobItem bi in blobs)
            {
                DateTime uploadDate = bi.Properties.CreatedOn?.DateTime ?? DateTime.UtcNow;
                string originalFilename = bi.Name;
                long fileSize = bi.Properties.ContentLength ?? 0;
                // Extract metadata if available
                if (bi.Metadata != null)
                {
                    if (bi.Metadata.TryGetValue("UploadedOn", out string? uploadedOn))
                        DateTime.TryParse(uploadedOn, out uploadDate);
                    if (bi.Metadata.TryGetValue("OriginalFilename", out string? filename))
                        originalFilename = filename;
                    if (bi.Metadata.TryGetValue("FileSize", out string? size))
                        long.TryParse(size, out fileSize);
                }
                var blobData = new BlobData
                {
                    Name = bi.Name,
                    OriginalFilename = originalFilename,
                    ContentType = bi.Properties.ContentType,
                    Size = fileSize,
                    UploadDate = uploadDate,
                    Url = GetBlobUri(bi.Name),
                    ContainerName = m_containerName,
                    Tags = bi.Tags?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>(),
                    Metadata = bi.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>()
                };

                if (loadContent)
                {
                    // Download content for this blob
                    using var memoryStream = new MemoryStream();
                    var blobClient = m_containerClient.GetBlobClient(bi.Name);
                    await blobClient.DownloadToAsync(memoryStream);
                    blobData.Data = memoryStream.ToArray();
                }
                results.Add(blobData);
            }
            return results;
        }

        /// <summary>
        /// Gets blob data with content loaded
        /// </summary>
        /// <param name="blobName">The blob name</param>
        /// <returns>BlobData with content loaded, or null if not found</returns>
        public async Task<BlobData?> GetBlobWithContentAsync(string blobName)
        {
            var blobClient = m_containerClient.GetBlobClient(blobName);
            var properties = await blobClient.GetPropertiesAsync();

            // Download the content
            using var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            var data = memoryStream.ToArray();

            var blobData = new BlobData
            {
                Name = blobName,
                OriginalFilename = properties.Value.Metadata.TryGetValue("OriginalFilename", out var origName) ? origName : blobName,
                ContentType = properties.Value.ContentType,
                Size = properties.Value.ContentLength,
                UploadDate = properties.Value.Metadata.TryGetValue("UploadedOn", out var uploadDate) && DateTime.TryParse(uploadDate, out var parsedDate) ? parsedDate : properties.Value.CreatedOn.DateTime,
                Url = blobClient.Uri,
                ContainerName = m_containerName,
                Tags = (await blobClient.GetTagsAsync()).Value.Tags?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>(),
                Metadata = properties.Value.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>(),
                Data = data
            };

            return blobData;
        }

        /// <summary>
        /// Gets multiple blob datas with content loaded
        /// </summary>
        /// <param name="blobNames">List of blob names</param>
        /// <returns>List of BlobData objects with content loaded</returns>
        public async Task<List<BlobData>> GetBlobsWithContentAsync(IEnumerable<string> blobNames)
        {
            var results = new List<BlobData>();
            foreach (var blobName in blobNames)
            {
                var blobData = await GetBlobWithContentAsync(blobName);
                if (blobData != null)
                    results.Add(blobData);
            }
            return results;
        }

        /// <summary>
        /// Searches for blobs by filename, tags, and/or date range
        /// </summary>
        /// <param name="searchText">Text to search in filenames (case-insensitive)</param>
        /// <param name="tagFilters">Dictionary of tag key-value pairs to filter by</param>
        /// <param name="startDate">Optional start date for filtering</param>
        /// <param name="endDate">Optional end date for filtering</param>
        /// <returns>List of matching blob info objects</returns>
        public async Task<List<BlobData>> SearchBlobsAsync(string? searchText = null,
            Dictionary<string, string>? tagFilters = null, DateTime? startDate = null, DateTime? endDate = null)
        {
            List<BlobData> results;

            // If we have tag filters, use tag-based search
            if (tagFilters != null && tagFilters.Count > 0)
            {
                var tagQuery = BuildTagQueryFromDictionary(tagFilters);
                results = await SearchBlobsByTagsAsync(tagQuery);
            }
            else
            {
                // Otherwise list all blobs in container
                results = await ListBlobsAsync();
            }

            // Apply additional filters
            return results
                .Where(b =>
                    // Filter by filename if search text is provided
                    (string.IsNullOrEmpty(searchText) ||
                     b.Name!.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                     b.OriginalFilename!.Contains(searchText, StringComparison.OrdinalIgnoreCase)) &&
                    // Filter by start date if provided
                    (!startDate.HasValue || b.UploadDate >= startDate.Value) &&
                    // Filter by end date if provided
                    (!endDate.HasValue || b.UploadDate <= endDate.Value))
                .ToList();
        }

        #endregion Search and Query Methods

        #region Download Methods

        /// <summary>
        /// Downloads a file from Azure Blob Storage
        /// </summary>
        /// <param name="blobName">The name of the blob to download</param>
        /// <param name="destinationPath">The local path to save the file</param>
        /// <returns>True if download was successful</returns>
        public async Task<bool> DownloadFileAsync(string blobName, string destinationPath)
        {
            try
            {
                // Get a reference to the blob
                BlobClient bc = m_containerClient.GetBlobClient(blobName);

                // Check if the blob exists
                if (!await bc.ExistsAsync())
                    return false;

                // Ensure the directory exists
                string dir = Path.GetDirectoryName(destinationPath)!;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Download the blob
                BlobDownloadInfo download = await bc.DownloadAsync();
                using (FileStream fileStream = File.OpenWrite(destinationPath))
                {
                    await download.Content.CopyToAsync(fileStream);
                }

                return true;
            }
            catch (RequestFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads multiple files based on lambda search criteria
        /// </summary>
        /// <param name="predicate">Lambda expression to filter which blobs to download</param>
        /// <param name="destinationDirectory">Directory to save the downloaded files</param>
        /// <param name="preserveOriginalNames">Whether to use original filenames or blob names</param>
        /// <returns>Dictionary of blob name to download result</returns>
        /// <example>
        /// var results = await azureBlobs.DownloadFilesAsync(
        ///     b => b.Tags["type"] == "invoice" && b.UploadDate > DateTime.Today.AddDays(-30),
        ///     @"C:\Downloads\Invoices"
        /// );
        /// </example>
        public async Task<Dictionary<string, BlobOperationResult>> DownloadFilesAsync(
            Expression<Func<BlobData, bool>> predicate, string destinationDirectory, bool preserveOriginalNames = true)
        {
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentNullException(nameof(destinationDirectory));

            // Ensure destination directory exists
            if (!Directory.Exists(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            // Find matching blobs
            var matchingBlobs = await GetCollectionAsync(predicate);
            var results = new Dictionary<string, BlobOperationResult>();

            foreach (var blob in matchingBlobs)
            {
                try
                {
                    var fileName = preserveOriginalNames ? blob.OriginalFilename : blob.Name;
                    var destinationPath = Path.Combine(destinationDirectory, fileName!);

                    var success = await DownloadFileAsync(blob.Name!, destinationPath);

                    results.Add(blob.Name!, new BlobOperationResult
                    {
                        Success = success,
                        BlobName = blob.Name!,
                        DestinationPath = success ? destinationPath : null,
                        ErrorMessage = success ? null : "Download failed"
                    });
                }
                catch (Exception ex)
                {
                    results.Add(blob.Name!, new BlobOperationResult
                    {
                        Success = false,
                        BlobName = blob.Name!,
                        ErrorMessage = ex.Message
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Downloads a blob to a stream
        /// </summary>
        /// <param name="blobName">The blob name</param>
        /// <param name="targetStream">The stream to download to</param>
        /// <returns>True if download was successful</returns>
        public async Task<bool> DownloadToStreamAsync(string blobName, Stream targetStream)
        {
            try
            {
                // Get a reference to the blob
                BlobClient bc = m_containerClient.GetBlobClient(blobName);

                // Check if the blob exists
                if (!await bc.ExistsAsync())
                    return false;

                // Download the blob to the stream
                await bc.DownloadToAsync(targetStream);
                return true;
            }
            catch (RequestFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads multiple files to streams based on lambda search criteria
        /// </summary>
        /// <param name="predicate">Lambda expression to filter which blobs to download</param>
        /// <param name="streamFactory">Function to create a stream for each blob</param>
        /// <returns>Dictionary of blob name to download result</returns>
        /// <example>
        /// var results = await azureBlobs.DownloadFilesToStreamAsync(
        ///     b => b.Tags["type"] == "temp",
        ///     blobName => new MemoryStream()
        /// );
        /// </example>
        public async Task<Dictionary<string, BlobStreamDownloadResult>> DownloadFilesToStreamAsync(
            Expression<Func<BlobData, bool>> predicate, Func<string, Stream> streamFactory)
        {
            if (streamFactory == null)
                throw new ArgumentNullException(nameof(streamFactory));

            // Find matching blobs
            var matchingBlobs = await GetCollectionAsync(predicate);
            var results = new Dictionary<string, BlobStreamDownloadResult>();

            foreach (var blob in matchingBlobs)
            {
                Stream? targetStream = null;
                try
                {
                    targetStream = streamFactory(blob.Name!);
                    var success = await DownloadToStreamAsync(blob.Name!, targetStream);

                    results.Add(blob.Name!, new BlobStreamDownloadResult
                    {
                        Success = success,
                        BlobName = blob.Name!,
                        Stream = success ? targetStream : null,
                        ErrorMessage = success ? null : "Download failed"
                    });

                    if (!success)
                    {
                        targetStream?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    targetStream?.Dispose();
                    results.Add(blob.Name!, new BlobStreamDownloadResult
                    {
                        Success = false,
                        BlobName = blob.Name!,
                        ErrorMessage = ex.Message
                    });
                }
            }

            return results;
        }

        #endregion Download Methods

        #region Delete Methods

        /// <summary>
        /// Deletes a blob from Azure Blob Storage
        /// </summary>
        /// <param name="blobName">The blob name to delete</param>
        /// <returns>True if deletion was successful</returns>
        public async Task<bool> DeleteBlobAsync(string blobName)
        {
            try
            {
                // Get a reference to the blob
                BlobClient blobClient = m_containerClient.GetBlobClient(blobName);

                // Delete the blob
                await blobClient.DeleteIfExistsAsync();
                return true;
            }
            catch (RequestFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes multiple blobs from Azure Blob Storage by name
        /// </summary>
        /// <param name="blobNames">List of blob names to delete</param>
        /// <returns>Dictionary with results for each blob (true=success, false=failure)</returns>
        public async Task<Dictionary<string, bool>> DeleteMultipleBlobsAsync(IEnumerable<string> blobNames)
        {
            var results = new Dictionary<string, bool>();

            foreach (string blobName in blobNames)
                results[blobName] = await DeleteBlobAsync(blobName);

            return results;
        }

        /// <summary>
        /// Deletes multiple blobs based on lambda search criteria
        /// </summary>
        /// <param name="predicate">Lambda expression to filter which blobs to delete</param>
        /// <returns>Dictionary with results for each blob (true=success, false=failure)</returns>
        /// <example>
        /// var results = await azureBlobs.DeleteMultipleBlobsAsync(
        ///     b => b.Tags["temp"] == "true" && b.UploadDate < DateTime.Today.AddDays(-7));
        /// </example>
        public async Task<Dictionary<string, bool>> DeleteMultipleBlobsAsync(Expression<Func<BlobData, bool>> predicate)
        {
            // Find matching blobs
            var matchingBlobs = await GetCollectionAsync(predicate);

            // Extract blob names and use the existing delete method
            var blobNames = matchingBlobs.Select(b => b.Name!).ToList();

            return await DeleteMultipleBlobsAsync(blobNames);
        }

        #endregion Delete Methods

        #region Tag Management Methods

        /// <summary>
        /// Updates the tags for an existing blob
        /// </summary>
        /// <param name="blobName">The blob name</param>
        /// <param name="tags">Dictionary of tags to set (replaces existing tags)</param>
        /// <returns>True if update was successful</returns>
        public async Task<bool> UpdateBlobTagsAsync(string blobName, Dictionary<string, string> tags)
        {
            try
            {
                ValidateIndexTags(tags);

                var blobClient = m_containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                    return false;

                await blobClient.SetTagsAsync(tags);
                return true;
            }
            catch (RequestFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the tags for a blob
        /// </summary>
        /// <param name="blobName">The blob name</param>
        /// <returns>Dictionary of tags or null if blob doesn't exist</returns>
        public async Task<Dictionary<string, string>?> GetBlobTagsAsync(string blobName)
        {
            try
            {
                var blobClient = m_containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                    return null;

                var response = await blobClient.GetTagsAsync();
                return response.Value.Tags?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            catch (RequestFailedException)
            {
                return null;
            }
        }

        #endregion Tag Management Methods

        #region Helper Methods

        /// <summary>
        /// Gets the full URI for a blob
        /// </summary>
        /// <param name="blobName">Blob name</param>
        /// <returns>Full URI to the blob</returns>
        private Uri GetBlobUri(string blobName)
        {
            BlobClient blobClient = m_containerClient.GetBlobClient(blobName);
            return blobClient.Uri;
        }

        /// <summary>
        /// Validates that index tags don't exceed Azure's 10-tag limit
        /// </summary>
        /// <param name="indexTags">Tags to validate</param>
        private static void ValidateIndexTags(Dictionary<string, string>? indexTags)
        {
            if (indexTags != null && indexTags.Count > 10)
            {
                throw new ArgumentException("Azure Blob Storage supports a maximum of 10 index tags per blob", nameof(indexTags));
            }

            // Validate tag key/value constraints
            if (indexTags != null)
            {
                foreach (var tag in indexTags)
                {
                    if (string.IsNullOrEmpty(tag.Key) || tag.Key.Length > 128)
                        throw new ArgumentException($"Tag key '{tag.Key}' must be 1-128 characters long");
                    if (tag.Value != null && tag.Value.Length > 256)
                        throw new ArgumentException($"Tag value for key '{tag.Key}' must be 0-256 characters long");
                }
            }
        }

        /// <summary>
        /// Creates upload options with proper headers, metadata, and tags
        /// </summary>
        private BlobUploadOptions CreateUploadOptions(string filePath, long fileSize,
            Dictionary<string, string>? indexTags, Dictionary<string, string>? metadata)
        {
            var combinedMetadata = new Dictionary<string, string>
            {
                { "UploadedOn", DateTime.UtcNow.ToString("o") },
                { "OriginalFilename", Path.GetFileName(filePath) },
                { "FileSize", fileSize.ToString() }
            };

            // Add user-provided metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    combinedMetadata[kvp.Key] = kvp.Value;
                }
            }

            return new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = GetContentType(filePath) },
                Metadata = combinedMetadata,
                Tags = indexTags
            };
        }

        /// <summary>
        /// Creates upload options for stream uploads
        /// </summary>
        private BlobUploadOptions CreateUploadOptions(string contentType, string originalFileName, long fileSize,
            Dictionary<string, string>? indexTags, Dictionary<string, string>? metadata)
        {
            var combinedMetadata = new Dictionary<string, string>
            {
                { "UploadedOn", DateTime.UtcNow.ToString("o") },
                { "OriginalFilename", originalFileName },
                { "FileSize", fileSize.ToString() }
            };

            // Add user-provided metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    combinedMetadata[kvp.Key] = kvp.Value;
                }
            }

            return new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = combinedMetadata,
                Tags = indexTags
            };
        }

        /// <summary>
        /// Builds a tag query string with container filter
        /// </summary>
        private string BuildTagQuery(string? tagQuery)
        {
            var queryParts = new List<string>
            {
                $"@container = '{m_containerName}'"
            };

            if (!string.IsNullOrEmpty(tagQuery))
                queryParts.Add(tagQuery);

            return string.Join(" AND ", queryParts);
        }

        /// <summary>
        /// Builds a tag query from a dictionary of tag filters
        /// </summary>
        private string BuildTagQueryFromDictionary(Dictionary<string, string> tagFilters)
        {
            var queryParts = new List<string>
            {
                $"@container = '{m_containerName}'"
            };

            foreach (var tag in tagFilters)
            {
                queryParts.Add($"{tag.Key} = '{tag.Value}'");
            }

            return string.Join(" AND ", queryParts);
        }

        /// <summary>
        /// Converts lambda expression to hybrid blob filter with tag and metadata support
        /// </summary>
        private HybridBlobFilterResult ConvertLambdaToHybridBlobFilter(Expression<Func<BlobData, bool>> predicate)
        {
            var builder = new BlobFilterBuilder();

            // Build hybrid filter from expression tree
            var result = builder.BuildHybridFilter(predicate.Body);

            // Always compile the full predicate for client-side fallback
            result.ClientSidePredicate = predicate.Compile();

            // Even if client-side filtering is required, preserve partial tag query
            // This allows server-side filtering to reduce blob scope
            result.TagQuery = builder.BuildTagQueryFromExtractedConditions();

            return result;
        }
        #endregion Helper Methods

        #region Lambda Expression Processing for Blobs

        /// <summary>
        /// Result of hybrid blob filter processing
        /// </summary>
        private class HybridBlobFilterResult
        {
            /// <summary>
            /// The set of tag keys required for safe client-side evaluation.
            /// These are extracted from the expression tree and used to guard against missing tags.
            /// </summary>
            public IReadOnlyCollection<string> RequiredTagKeys { get; set; } = Array.Empty<string>();

            /// <summary>
            /// The server-side tag query string, if one could be constructed.
            /// Used with SearchBlobsByTagsAsync for efficient filtering.
            /// </summary>
            public string? TagQuery { get; set; }

            /// <summary>
            /// The compiled predicate for client-side filtering, if needed.
            /// Only used when server-side filtering is insufficient.
            /// </summary>
            public Func<BlobData, bool>? ClientSidePredicate { get; set; }

            /// <summary>
            /// Indicates whether the filter requires client-side evaluation.
            /// True if the expression contains logic that cannot be translated to a tag query.
            /// </summary>
            public bool RequiresClientFiltering { get; set; }

            /// <summary>
            /// Holds the untranslatable parts of the expression tree for client side processing
            /// </summary>
            public Expression<Func<BlobData, bool>>? UntranslatableExpression { get; set; }
        }

        /// <summary>
        /// Helper class to build tag filter strings from expression trees for blob searching.
        /// It separates translatable parts (for server-side TagQuery) from untranslatable parts (for client-side ClientSidePredicate).
        /// </summary>
        private class BlobFilterBuilder : ExpressionVisitor
        {
            private readonly List<string> _tagFilters = new();
            private bool _hasClientSideOperations = false;
            private readonly HashSet<string> _requiredTagKeys = new();
            private readonly Stack<List<string>> _groupStack = new();
            private readonly List<Expression> _untranslatableParts = new();

            public IReadOnlyCollection<string> RequiredTagKeys => _requiredTagKeys;

            /// <summary>
            /// Processes the expression tree, populating tag filters, required keys, and identifying untranslatable parts.
            /// </summary>
            /// <param name="expression">The lambda expression body to process.</param>
            /// <returns>A result object containing the server-side query, required keys, and a lambda for client-side filtering.</returns>
            public HybridBlobFilterResult BuildHybridFilter(Expression expression)
            {
                _tagFilters.Clear();
                _hasClientSideOperations = false;
                _requiredTagKeys.Clear();
                _untranslatableParts.Clear();

                Visit(expression);

                var result = new HybridBlobFilterResult
                {
                    TagQuery = _tagFilters.Count > 0 ? string.Join(" AND ", _tagFilters) : null,
                    RequiresClientFiltering = _hasClientSideOperations,
                    RequiredTagKeys = _requiredTagKeys
                };

                if (_untranslatableParts.Count > 0)
                {
                    Expression combinedUntranslatable = _untranslatableParts[0];
                    for (int i = 1; i < _untranslatableParts.Count; i++)
                    {
                        combinedUntranslatable = Expression.AndAlso(combinedUntranslatable, _untranslatableParts[i]);
                    }

                    if (combinedUntranslatable.Type == typeof(bool))
                    {
                        var parameter = Expression.Parameter(typeof(BlobData), "b");
                        var untranslatableLambda = Expression.Lambda<Func<BlobData, bool>>(combinedUntranslatable, parameter);
                        result.UntranslatableExpression = untranslatableLambda;

                        try
                        {
                            result.ClientSidePredicate = untranslatableLambda.Compile();
                        }
                        catch (Exception ex)
                        {
                            result.ClientSidePredicate = null;
                        }
                    }
                    else
                    {
                        result.ClientSidePredicate = null;
                    }
                }
                else if (_hasClientSideOperations)
                {
                    // If RequiresClientFiltering is true but no untranslatable parts were collected,
                    // the predicate will be compiled in ConvertLambdaToHybridBlobFilter.
                }

                return result;
            }

            /// <summary>
            /// Exposes the tag-safe portion of the query for reuse or inspection.
            /// </summary>
            public string BuildTagQueryFromExtractedConditions()
            {
                return _tagFilters.Count > 0 ? string.Join(" AND ", _tagFilters) : string.Empty;
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (node.NodeType == ExpressionType.AndAlso || node.NodeType == ExpressionType.OrElse)
                {
                    Visit(node.Left);
                    Visit(node.Right);
                    return node;
                }

                if (node.NodeType == ExpressionType.Equal && TryExtractTagFilter(node, out string? tagFilter))
                {
                    (_groupStack.Count > 0 ? _groupStack.Peek() : _tagFilters).Add(tagFilter!);
                    return node;
                }

                if (TryExtractOrChain(node, out string key, out List<string> values))
                {
                    var safeValues = values
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => $"{key} = '{v.Replace("'", "''")}'");
                    var orExpression = string.Join(" OR ", safeValues);
                    if (!string.IsNullOrWhiteSpace(orExpression))
                    {
                        (_groupStack.Count > 0 ? _groupStack.Peek() : _tagFilters).Add($"({orExpression})");
                    }
                    return node;
                }

                if (node.Type == typeof(bool))
                {
                    _hasClientSideOperations = true;
                    _untranslatableParts.Add(node);
                }
                else
                {
                    _hasClientSideOperations = true;
                }
                return node;
            }

            private bool TryExtractOrChain(Expression expr, out string key, out List<string> values)
            {
                key = null!;
                values = new List<string>();

                if (expr is BinaryExpression binary)
                {
                    if (binary.NodeType == ExpressionType.OrElse)
                    {
                        if (TryExtractOrChain(binary.Left, out var leftKey, out var leftVals) &&
                            TryExtractOrChain(binary.Right, out var rightKey, out var rightVals) &&
                            leftKey == rightKey)
                        {
                            key = leftKey;
                            values.AddRange(leftVals);
                            values.AddRange(rightVals);
                            return true;
                        }
                        return false;
                    }

                    if (TryExtractTagComparison(binary, out var tagKey, out var tagValue))
                    {
                        key = tagKey;
                        values.Add(tagValue);
                        _requiredTagKeys.Add(tagKey);
                        return true;
                    }
                }

                return false;
            }

            private bool TryExtractTagComparison(BinaryExpression expr, out string key, out string value)
            {
                key = null!;
                value = null!;

                if (expr.Left is MethodCallExpression leftCall &&
                    leftCall.Method.Name == "get_Item" &&
                    leftCall.Object is MemberExpression tagsMember &&
                    tagsMember.Member.Name == "Tags" &&
                    leftCall.Arguments[0] is ConstantExpression keyConst &&
                    expr.Right is ConstantExpression valueConst)
                {
                    key = keyConst.Value?.ToString() ?? "";
                    value = valueConst.Value?.ToString() ?? "";
                    return !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value);
                }

                return false;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name == "Any" &&
                    node.Arguments.Count == 2 &&
                    node.Arguments[1] is UnaryExpression unary &&
                    unary.Operand is LambdaExpression lambda)
                {
                    if (!TryExtractTagKey(lambda.Body, out string visitTagKey))
                    {
                        _hasClientSideOperations = true;
                        if (node.Type == typeof(bool))
                        {
                            _untranslatableParts.Add(node);
                        }
                        return node;
                    }

                    var values = ExtractValuesFromExpression(node.Arguments[0]);
                    if (values == null || !values.Any())
                    {
                        _hasClientSideOperations = true;
                        if (node.Type == typeof(bool))
                        {
                            _untranslatableParts.Add(node);
                        }
                        return node;
                    }

                    var orConditions = values.Select(v => $"{visitTagKey} = '{v.Replace("'", "''")}'");
                    var orExpression = string.Join(" OR ", orConditions);
                    (_groupStack.Count > 0 ? _groupStack.Peek() : _tagFilters).Add($"({orExpression})");
                    _requiredTagKeys.Add(visitTagKey);
                    return node;
                }

                if (node.Object is MethodCallExpression tagAccess &&
                    tagAccess.Method.Name == "get_Item" &&
                    tagAccess.Object is MemberExpression memberExpr &&
                    memberExpr.Member.Name == "Tags" &&
                    tagAccess.Arguments.Count == 1 &&
                    tagAccess.Arguments[0] is ConstantExpression keyExpr &&
                    keyExpr.Value is string tagKey &&
                    node.Arguments.Count == 1 &&
                    node.Arguments[0] is ConstantExpression valueExpr &&
                    valueExpr.Value is string tagValue)
                {
                    _requiredTagKeys.Add(tagKey);
                    string likePattern = node.Method.Name switch
                    {
                        "Contains" => $"%{tagValue}%",
                        "StartsWith" => $"{tagValue}%",
                        "EndsWith" => $"%{tagValue}",
                        _ => null!
                    };
                    if (likePattern != null)
                    {
                        _tagFilters.Add($"{tagKey} LIKE '{likePattern}'");
                        return node;
                    }
                }

                if (node.Type == typeof(bool))
                {
                    _hasClientSideOperations = true;
                    _untranslatableParts.Add(node);
                }
                else
                {
                    _hasClientSideOperations = true;
                }
                return node;
            }

            private List<string>? ExtractValuesFromExpression(Expression expr)
            {
                if (expr is ConstantExpression constExpr && constExpr.Value is IEnumerable<string> stringEnum)
                {
                    return stringEnum.ToList();
                }

                if (expr is MemberExpression memberExpr)
                {
                    var containerExpr = memberExpr.Expression;
                    if (containerExpr is UnaryExpression unary && unary.Operand is ConstantExpression unaryConst)
                        containerExpr = unaryConst;

                    if (containerExpr is ConstantExpression closure && closure.Value != null)
                    {
                        var container = closure.Value;
                        var member = memberExpr.Member;

                        object? capturedValue = null;
                        switch (member)
                        {
                            case FieldInfo field:
                                capturedValue = field.GetValue(container);
                                break;
                            case PropertyInfo prop:
                                capturedValue = prop.GetValue(container);
                                break;
                        }

                        if (capturedValue is IEnumerable<string> capturedStringEnum)
                        {
                            return capturedStringEnum.ToList();
                        }
                    }
                }

                return null;
            }

            private bool TryExtractTagKey(Expression expr, out string key)
            {
                key = null!;

                if (expr is BinaryExpression binary && binary.NodeType == ExpressionType.Equal)
                {
                    if (binary.Left is MethodCallExpression methodCall &&
                        methodCall.Method.Name == "get_Item" &&
                        methodCall.Object is MemberExpression memberExpr &&
                        memberExpr.Member.Name == "Tags" &&
                        methodCall.Arguments.Count == 1 &&
                        methodCall.Arguments[0] is ConstantExpression keyExpr &&
                        keyExpr.Value is string tagKey)
                    {
                        key = tagKey;
                        return true;
                    }
                    if (binary.Right is MethodCallExpression methodCallRev &&
                        methodCallRev.Method.Name == "get_Item" &&
                        methodCallRev.Object is MemberExpression memberExprRev &&
                        memberExprRev.Member.Name == "Tags" &&
                        methodCallRev.Arguments.Count == 1 &&
                        methodCallRev.Arguments[0] is ConstantExpression keyExprRev &&
                        keyExprRev.Value is string tagKeyRev)
                    {
                        key = tagKeyRev;
                        return true;
                    }
                }

                return false;
            }

            private bool TryExtractTagFilter(BinaryExpression node, out string? tagFilter)
            {
                tagFilter = null;

                if (node.Left is MethodCallExpression methodCall &&
                    methodCall.Method.Name == "get_Item" &&
                    methodCall.Object is MemberExpression memberExpr &&
                    memberExpr.Member.Name == "Tags" &&
                    memberExpr.Expression is ParameterExpression)
                {
                    if (methodCall.Arguments.Count == 1 &&
                        methodCall.Arguments[0] is ConstantExpression keyExpr &&
                        keyExpr.Value is string tagKey)
                    {
                        if (TryExtractValue(node.Right, out string tagValue))
                        {
                            tagFilter = $"{tagKey} = '{tagValue.Replace("'", "''")}'";
                            _requiredTagKeys.Add(tagKey);
                            return true;
                        }
                    }
                }

                return false;
            }

            private bool TryExtractValue(Expression expr, out string value)
            {
                value = null!;

                if (expr is ConstantExpression constExpr && constExpr.Value is string strValue)
                {
                    value = strValue;
                    return true;
                }

                if (expr is MemberExpression memberExpr)
                {
                    var containerExpr = memberExpr.Expression;
                    if (containerExpr is UnaryExpression unary && unary.Operand is ConstantExpression unaryConst)
                        containerExpr = unaryConst;

                    if (containerExpr is ConstantExpression closure && closure.Value != null)
                    {
                        var container = closure.Value;
                        var member = memberExpr.Member;

                        switch (member)
                        {
                            case FieldInfo field when field.GetValue(container) is string fieldValue:
                                value = fieldValue;
                                return true;

                            case PropertyInfo prop when prop.GetValue(container) is string propValue:
                                value = propValue;
                                return true;
                        }
                    }
                }

                return false;
            }
        } // end class BlobFilterBuilder

        #endregion Lambda Expression Processing for Blobs
    }
} //end namespace ASCTableStorage.Blobs