using AZTableStorage.Models;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AZTableStorage.Blobs
{
    /// <summary>
    /// Service to handle Azure Blob Storage operations for file uploads and downloads. Defaults to 5MB file Sizes
    /// </summary>
    public class AzureBlobs
    {
        private readonly BlobServiceClient m_client;
        private readonly long m_maxFileSize;

        /// <summary>
        /// Constructor initializing the blob service client with connection string and default limitations
        /// </summary>
        /// <param name="connectionString">Azure Storage connection string</param>
        /// <param name="defaultMaxFileSizeBytes">Default maximum file size in bytes (default: 5MB)</param>
        public AzureBlobs(string connectionString, long defaultMaxFileSizeBytes = 5 * 1024 * 1024)
        {
            m_client = new BlobServiceClient(connectionString);
            m_maxFileSize = defaultMaxFileSizeBytes;
        }

        /// <summary>
        /// Adds or updates allowed file types
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
        /// Checks if a file type is allowed
        /// </summary>
        /// <param name="filePath">File path or name to check</param>
        /// <returns>True if allowed, false otherwise</returns>
        public bool IsFileTypeAllowed(string filePath)
        {
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

        /// <summary>
        /// Uploads a file to Azure Blob Storage with size validation
        /// </summary>
        /// <param name="containerName">The container name to upload to</param>
        /// <param name="filePath">The local file path</param>
        /// <param name="blobName">Optional blob name, defaults to file name if not provided</param>
        /// <param name="overwrite">Whether to overwrite if blob exists</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <returns>The URI of the uploaded blob</returns>
        public async Task<Uri> UploadFileAsync(string containerName, string filePath, string? blobName = null, 
            bool overwrite = false, bool enforceFileTypeRestriction = true, long? maxFileSizeBytes = null)
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

            // Create or get a reference to the container
            BlobContainerClient c = await GetContainerClientAsync(containerName);

            // If no blob name is provided, use the original file name
            blobName ??= Path.GetFileName(filePath);

            // Get a reference to the blob
            BlobClient bc = c.GetBlobClient(blobName);

            // Set the content type based on file extension
            BlobUploadOptions options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = GetContentType(filePath) },
                Metadata = new Dictionary<string, string>
                {
                    { "UploadedOn", DateTime.UtcNow.ToString("o") },
                    { "OriginalFilename", Path.GetFileName(filePath) },
                    { "FileSize", fi.Length.ToString() }
                }
            };

            // Upload the file
            await bc.UploadAsync(filePath, options);

            // Return the blob URI
            return bc.Uri;
        }

        /// <summary>
        /// Uploads multiple files to Azure Blob Storage
        /// </summary>
        /// <param name="containerName">The container name to upload to</param>
        /// <param name="filePaths">Collection of file paths to upload</param>
        /// <param name="overwrite">Whether to overwrite if blob exists</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <returns>Dictionary of filename to URI of uploaded blobs</returns>
        public async Task<Dictionary<string, Uri>> UploadMultipleFilesAsync(string containerName, IEnumerable<string> filePaths, 
            bool overwrite = false, bool enforceFileTypeRestriction = true, long? maxFileSizeBytes = null)
        {
            if (filePaths == null)
                throw new ArgumentNullException(nameof(filePaths));

            Dictionary<string, Uri> results = new Dictionary<string, Uri>();
            List<Exception> exceptions = new List<Exception>();

            foreach (string filePath in filePaths)
            {
                try
                {
                    Uri blobUri = await UploadFileAsync(
                        containerName,
                        filePath,
                        null, // Use original filename
                        overwrite,
                        enforceFileTypeRestriction,
                        maxFileSizeBytes);

                    results.Add(Path.GetFileName(filePath), blobUri);
                }
                catch (Exception ex)
                {
                    exceptions.Add(new Exception($"Error uploading {Path.GetFileName(filePath)}: {ex.Message}", ex));
                }
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("One or more files failed to upload", exceptions);
            }

            return results;
        }

        /// <summary>
        /// Uploads a stream to Azure Blob Storage with size validation
        /// </summary>
        /// <param name="containerName">The container name to upload to</param>
        /// <param name="stream">The stream to upload</param>
        /// <param name="blobName">The name for the blob</param>
        /// <param name="originalFileName">Original file name for metadata</param>
        /// <param name="contentType">The content type of the blob</param>
        /// <param name="overwrite">Whether to overwrite if blob exists</param>
        /// <param name="enforceFileTypeRestriction">Whether to enforce file type restrictions</param>
        /// <param name="maxFileSizeBytes">Max file size in bytes, uses default if not specified</param>
        /// <returns>The URI of the uploaded blob</returns>
        public async Task<Uri> UploadStreamAsync(string containerName, Stream stream, string blobName, string? originalFileName = null,
            string? contentType = null, bool overwrite = false, bool enforceFileTypeRestriction = true, long? maxFileSizeBytes = null)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (string.IsNullOrEmpty(blobName))
                throw new ArgumentNullException(nameof(blobName));

            // Check file size
            long maxSize = maxFileSizeBytes ?? m_maxFileSize;
            if (stream.Length > maxSize)
                throw new ArgumentException($"Stream size exceeds the maximum allowed size of {maxSize} bytes");

            string ext = Path.GetExtension(blobName).ToLowerInvariant();

            // Check file type if enforcing restrictions
            if (enforceFileTypeRestriction && !BlobData.FileTypes.ContainsKey(ext))
                throw new ArgumentException($"File type {ext} is not allowed");

            // Create or get a reference to the container
            BlobContainerClient c = await GetContainerClientAsync(containerName);

            // Get a reference to the blob
            BlobClient bc = c.GetBlobClient(blobName);

            // Determine content type
            if (string.IsNullOrEmpty(contentType))
            {
                contentType = BlobData.FileTypes.TryGetValue(ext, out string? type)
                    ? type
                    : "application/octet-stream";
            }

            // Set the content type and metadata
            BlobUploadOptions options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = new Dictionary<string, string>
                {
                    { "UploadedOn", DateTime.UtcNow.ToString("o") },
                    { "OriginalFilename", originalFileName ?? blobName },
                    { "FileSize", stream.Length.ToString() }
                }
            };

            // Upload the stream
            await bc.UploadAsync(stream, options);

            // Return the blob URI
            return bc.Uri;
        }

        /// <summary>
        /// Downloads a file from Azure Blob Storage
        /// </summary>
        /// <param name="containerName">The container name to download from</param>
        /// <param name="blobName">The name of the blob to download</param>
        /// <param name="destinationPath">The local path to save the file</param>
        /// <returns>True if download was successful</returns>
        public async Task<bool> DownloadFileAsync(string containerName, string blobName, string destinationPath)
        {
            try
            {
                // Get a reference to the container
                BlobContainerClient c = await GetContainerClientAsync(containerName);

                // Get a reference to the blob
                BlobClient bc = c.GetBlobClient(blobName);

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
        /// Downloads a blob to a stream
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="blobName">The blob name</param>
        /// <param name="targetStream">The stream to download to</param>
        /// <returns>True if download was successful</returns>
        public async Task<bool> DownloadToStreamAsync(string containerName, string blobName, Stream targetStream)
        {
            try
            {
                // Get a reference to the container
                BlobContainerClient c = await GetContainerClientAsync(containerName);

                // Get a reference to the blob
                BlobClient bc = c.GetBlobClient(blobName);

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
        /// Lists all blobs in a container with optional filtering
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="prefix">Optional prefix filter</param>
        /// <returns>A list of blob items with metadata</returns>
        public async Task<List<BlobData>> ListBlobsAsync(string containerName, string? prefix = null)
        {
            List<BlobData> bd = new List<BlobData>();

            // Get a reference to the container
            BlobContainerClient c = await GetContainerClientAsync(containerName);

            // Create blob listing options
            BlobTraits traits = BlobTraits.Metadata;
            BlobStates states = BlobStates.None;
            string prefix1 = prefix!;

            // List blobs
            AsyncPageable<BlobItem> blobs = c.GetBlobsAsync(traits, states, prefix1);

            await foreach (BlobItem bi in blobs)
            {
                DateTime uploadDate = DateTime.UtcNow;
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

                bd.Add(new BlobData
                {
                    Name = bi.Name,
                    OriginalFilename = originalFilename,
                    ContentType = bi.Properties.ContentType,
                    Size = fileSize,
                    UploadDate = uploadDate,
                    Url = GetBlobUri(containerName, bi.Name)
                });
            }

            return bd;
        }

        /// <summary>
        /// Searches for blobs by filename and/or date range
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="searchText">Text to search in filenames (case-insensitive)</param>
        /// <param name="startDate">Optional start date for filtering</param>
        /// <param name="endDate">Optional end date for filtering</param>
        /// <returns>List of matching blob info objects</returns>
        public async Task<List<BlobData>> SearchBlobsAsync(string containerName, string? searchText = null, DateTime? startDate = null, DateTime? endDate = null)
        {
            // Get all blobs in the container
            List<BlobData> allBlobs = await ListBlobsAsync(containerName);

            // Apply filters
            return allBlobs
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

        /// <summary>
        /// Deletes a blob from Azure Blob Storage
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="blobName">The blob name to delete</param>
        /// <returns>True if deletion was successful</returns>
        public async Task<bool> DeleteBlobAsync(string containerName, string blobName)
        {
            try
            {
                // Get a reference to the container
                BlobContainerClient c = await GetContainerClientAsync(containerName);

                // Get a reference to the blob
                BlobClient blobClient = c.GetBlobClient(blobName);

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
        /// Deletes multiple blobs from Azure Blob Storage
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="blobNames">List of blob names to delete</param>
        /// <returns>Dictionary with results for each blob (true=success, false=failure)</returns>
        public async Task<Dictionary<string, bool>> DeleteMultipleBlobsAsync(string containerName, IEnumerable<string> blobNames)
        {
            Dictionary<string, bool> results = new Dictionary<string, bool>();

            foreach (string blobName in blobNames)
                results[blobName] = await DeleteBlobAsync(containerName, blobName);

            return results;
        }

        /// <summary>
        /// Creates a container if it doesn't exist and returns the container client
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <returns>A BlobContainerClient for the specified container</returns>
        private async Task<BlobContainerClient> GetContainerClientAsync(string containerName)
        {
            // Get a reference to the container
            BlobContainerClient c = m_client.GetBlobContainerClient(containerName);

            // Create the container if it doesn't exist
            await c.CreateIfNotExistsAsync(PublicAccessType.None);

            return c;
        }

        /// <summary>
        /// Gets the full URI for a blob
        /// </summary>
        /// <param name="containerName">Container name</param>
        /// <param name="blobName">Blob name</param>
        /// <returns>Full URI to the blob</returns>
        private Uri GetBlobUri(string containerName, string blobName)
        {
            BlobContainerClient containerClient = m_client.GetBlobContainerClient(containerName);
            BlobClient blobClient = containerClient.GetBlobClient(blobName);
            return blobClient.Uri;
        }
    }// class AzureBlobs
}