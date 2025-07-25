﻿using ASCTableStorage.Models;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Linq.Expressions;

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
            // Convert lambda to tag-based and metadata-based filters
            var filterResult = ConvertLambdaToHybridBlobFilter(predicate);

            List<BlobData> results;

            // If we have tag-based filters, use the fast tag search
            if (!string.IsNullOrEmpty(filterResult.TagQuery))
            {
                results = await SearchBlobsByTagsAsync(filterResult.TagQuery);
            }
            else
            {
                // Fallback to listing all blobs in container
                results = await ListBlobsAsync(prefix);
            }

            // Apply any client-side filtering that couldn't be done server-side
            if (filterResult.RequiresClientFiltering && filterResult.ClientSidePredicate != null)
            {
                results = results.Where(filterResult.ClientSidePredicate).ToList();
            }

            return results;
        }

        /// <summary>
        /// Searches blobs by tag query string
        /// </summary>
        /// <param name="tagQuery">OData-style tag query (container filter will be added automatically)</param>
        /// <returns>List of matching blob data</returns>
        /// <example>
        /// var query = "brand = 'volvo' AND type = 'pdf'";
        /// var blobs = await azureBlobs.SearchBlobsByTagsAsync(query);
        /// </example>
        public async Task<List<BlobData>> SearchBlobsByTagsAsync(string? tagQuery = null)
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
        /// <returns>A list of blob items with metadata and tags</returns>
        public async Task<List<BlobData>> ListBlobsAsync(string? prefix = null)
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
            var result = builder.BuildHybridFilter(predicate.Body);

            // Set the client-side predicate if needed
            if (result.RequiresClientFiltering)
            {
                result.ClientSidePredicate = predicate.Compile();
            }

            return result;
        }

        #endregion Helper Methods

        #region Lambda Expression Processing for Blobs

        /// <summary>
        /// Result of hybrid blob filter processing
        /// </summary>
        private class HybridBlobFilterResult
        {
            public string? TagQuery { get; set; }
            public Func<BlobData, bool>? ClientSidePredicate { get; set; }
            public bool RequiresClientFiltering { get; set; }
        }

        /// <summary>
        /// Helper class to build tag filter strings from expression trees for blob searching
        /// </summary>
        private class BlobFilterBuilder : ExpressionVisitor
        {
            private List<string> _tagFilters = new List<string>();
            private bool _hasClientSideOperations = false;

            public HybridBlobFilterResult BuildHybridFilter(Expression expression)
            {
                _tagFilters.Clear();
                _hasClientSideOperations = false;

                Visit(expression);

                return new HybridBlobFilterResult
                {
                    TagQuery = _tagFilters.Count > 0 ? string.Join(" AND ", _tagFilters) : null,
                    RequiresClientFiltering = _hasClientSideOperations
                };
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (node.NodeType == ExpressionType.AndAlso)
                {
                    // For AND operations, we can combine tag filters
                    Visit(node.Left);
                    Visit(node.Right);
                    return node;
                }

                if (node.NodeType == ExpressionType.OrElse)
                {
                    // OR operations are complex for tag queries, mark for client-side processing
                    _hasClientSideOperations = true;
                    return node;
                }

                // Handle equality comparisons for tags
                if (node.NodeType == ExpressionType.Equal && TryExtractTagFilter(node, out string? tagFilter))
                {
                    _tagFilters.Add(tagFilter!);
                    return node;
                }

                // Other operations need client-side processing
                _hasClientSideOperations = true;
                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Most method calls require client-side processing
                _hasClientSideOperations = true;
                return node;
            }

            /// <summary>
            /// Tries to extract a tag filter from a binary expression
            /// </summary>
            private bool TryExtractTagFilter(BinaryExpression node, out string? tagFilter)
            {
                tagFilter = null;

                // Look for patterns like: b.Tags["key"] == "value"
                if (node.Left is MethodCallExpression methodCall &&
                    methodCall.Method.Name == "get_Item" &&
                    methodCall.Object is MemberExpression memberExpr &&
                    memberExpr.Member.Name == "Tags" &&
                    memberExpr.Expression is ParameterExpression)
                {
                    // Extract the tag key
                    if (methodCall.Arguments.Count == 1 &&
                        methodCall.Arguments[0] is ConstantExpression keyExpr &&
                        keyExpr.Value is string tagKey)
                    {
                        // Extract the tag value
                        if (node.Right is ConstantExpression valueExpr &&
                            valueExpr.Value is string tagValue)
                        {
                            tagFilter = $"{tagKey} = '{tagValue}'";
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        #endregion Lambda Expression Processing for Blobs
    }
} //end namespace ASCTableStorage.Blobs