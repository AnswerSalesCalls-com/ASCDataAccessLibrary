# ASCDataAccessLibrary Documentation

## Overview

ASCDataAccessLibrary is a comprehensive .NET library for interacting with Azure Storage services, including Blob Storage and Table Storage. This library provides simplified access to Azure storage resources through easy-to-use classes and methods.

## Table of Contents

1. [Azure Blob Storage](#azure-blob-storage)
   - [Overview](#blob-overview)
   - [Initialization](#blob-initialization)
   - [File Operations](#file-operations)
   - [Search and Management](#search-and-management)
2. [Azure Table Storage](#azure-table-storage)
   - [Data Access](#data-access)
   - [Query Capabilities](#query-capabilities)
   - [Batch Operations](#batch-operations)
3. [Session Management](#session-management)
4. [Models](#models)
   - [Base Models](#base-models)
   - [Specialized Models](#specialized-models)
5. [Common Extensions](#common-extensions)

---

## Azure Blob Storage

<a id="blob-overview"></a>
### Overview

The `AzureBlobs` class provides a robust interface for uploading, downloading, and managing files in Azure Blob Storage. It includes functionality for file type restrictions, size limits, and advanced search capabilities.

<a id="blob-initialization"></a>
### Initialization

```csharp
// Create new blob storage handler with default 5MB limit
var blobStorage = new AzureBlobs(connectionString);

// Create with custom size limit (10MB)
var blobStorage = new AzureBlobs(connectionString, 10 * 1024 * 1024);
```

<a id="file-operations"></a>
### File Operations

#### Upload Files

```csharp
// Upload a single file
Uri fileUri = await blobStorage.UploadFileAsync("container-name", "C:/path/to/file.pdf");

// Upload a single file with custom name
Uri fileUri = await blobStorage.UploadFileAsync("container-name", "C:/path/to/file.pdf", "custom-name.pdf");

// Upload with additional parameters
Uri fileUri = await blobStorage.UploadFileAsync(
    containerName: "container-name",
    filePath: "C:/path/to/file.pdf",
    blobName: "custom-name.pdf",
    overwrite: true,
    enforceFileTypeRestriction: true,
    maxFileSizeBytes: 8 * 1024 * 1024 // 8MB
);

// Upload multiple files
Dictionary<string, Uri> results = await blobStorage.UploadMultipleFilesAsync(
    "container-name", 
    new[] { "C:/path/to/file1.pdf", "C:/path/to/file2.docx" }
);

// Upload a stream
using (var stream = File.OpenRead("C:/path/to/file.pdf"))
{
    Uri fileUri = await blobStorage.UploadStreamAsync(
        "container-name", 
        stream, 
        "file.pdf", 
        originalFileName: "original.pdf"
    );
}
```

#### Download Files

```csharp
// Download to file
bool success = await blobStorage.DownloadFileAsync(
    "container-name", 
    "file.pdf", 
    "C:/downloads/file.pdf"
);

// Download to stream
using (var stream = new MemoryStream())
{
    bool success = await blobStorage.DownloadToStreamAsync(
        "container-name",
        "file.pdf",
        stream
    );
    
    if (success)
    {
        // Use the stream data
        stream.Position = 0;
        // Process the stream...
    }
}
```

#### Delete Files

```csharp
// Delete a single blob
bool deleted = await blobStorage.DeleteBlobAsync("container-name", "file.pdf");

// Delete multiple blobs
Dictionary<string, bool> results = await blobStorage.DeleteMultipleBlobsAsync(
    "container-name",
    new[] { "file1.pdf", "file2.docx" }
);
```

<a id="search-and-management"></a>
### Search and Management

```csharp
// List all blobs in a container
List<BlobData> blobs = await blobStorage.ListBlobsAsync("container-name");

// List blobs with a prefix filter
List<BlobData> blobs = await blobStorage.ListBlobsAsync("container-name", "prefix-");

// Search blobs by name and date range
List<BlobData> searchResults = await blobStorage.SearchBlobsAsync(
    "container-name",
    searchText: "document",
    startDate: DateTime.Now.AddDays(-7),
    endDate: DateTime.Now
);
```

#### File Type Management

```csharp
// Check if a file type is allowed
bool isAllowed = blobStorage.IsFileTypeAllowed("file.pdf");

// Add a new allowed file type
blobStorage.AddAllowedFileType(".custom", "application/custom");

// Remove an allowed file type
blobStorage.RemoveAllowedFileType(".exe");

// Get content type for a file
string contentType = blobStorage.GetContentType("file.jpg"); // Returns "image/jpeg"
```

## Azure Table Storage

The library provides a robust data access layer for Azure Table Storage with the `DataAccess<T>` generic class.

<a id="data-access"></a>
### Data Access

```csharp
// Create a data access instance for a specific entity type
var dataAccess = new DataAccess<MyEntity>(accountName, accountKey);

// Create or update a single entity
dataAccess.ManageData(entity, TableOperationType.InsertOrReplace);

// Get all data from a table
var allEntities = dataAccess.GetAllTableData();

// Get a specific row by its rowKey
var entity = dataAccess.GetRowObject("rowKey123");
```

<a id="query-capabilities"></a>
### Query Capabilities

The library supports a variety of query approaches:

```csharp
// Get collection by partition key
var entities = dataAccess.GetCollection("partitionKeyValue");

// Simple field comparison query
var entity = dataAccess.GetRowObject("FieldName", ComparisonTypes.eq, "Value");

// Complex queries with multiple conditions
var queryItems = new List<DBQueryItem>
{
    new DBQueryItem 
    { 
        FieldName = "Status", 
        FieldValue = "Active", 
        HowToCompare = ComparisonTypes.eq 
    },
    new DBQueryItem 
    { 
        FieldName = "CreateDate", 
        FieldValue = DateTime.Now.AddDays(-7).ToString(), 
        HowToCompare = ComparisonTypes.gt,
        IsDateTime = true
    }
};

var results = dataAccess.GetCollection(queryItems, QueryCombineStyle.And);
```

<a id="batch-operations"></a>
### Batch Operations

```csharp
// Batch update multiple entities
var entities = new List<MyEntity>(); // Populate with entities
await dataAccess.BatchUpdateList(entities, TableOperationType.InsertOrReplace);
```

## Session Management

The `Session` class provides functionality for managing session data in Azure Table Storage:

```csharp
// Create a new session
var session = new Session(accountName, accountKey, "session123");

// Store session data
session["username"] = new AppSessionData { Key = "username", Value = "john.doe" };
session["lastActivity"] = new AppSessionData { Key = "lastActivity", Value = DateTime.Now.ToString() };

// Commit session data to storage
await session.CommitData();

// Retrieve session data
var username = session["username"];

// Get all session data
var allSessionData = session.SessionData;

// Clean up old sessions
session.CleanSessionData();

// Find stale sessions
var staleSessions = session.GetStaleSessions();

// Restart a session
session.RestartSession();
```

## Models

<a id="base-models"></a>
### Base Models

The library includes base models for Azure Table entities:

#### ITableExtra Interface

All table entities implement this interface to provide common functionality:

```csharp
public interface ITableExtra
{
    string TableReference { get; }
    string GetIDValue();
}
```

#### TableEntityBase

Base class for table entities that handles large data chunking:

```csharp
public class MyEntity : TableEntityBase
{
    // Properties...
}
```

<a id="specialized-models"></a>
### Specialized Models

#### AppSessionData

```csharp
public class AppSessionData : TableEntityBase
{
    public string SessionID { get; set; }
    public string Key { get; set; }
    public string Value { get; set; }
}
```

#### BlobData

```csharp
public class BlobData
{
    public string Name { get; set; }
    public string OriginalFilename { get; set; }
    public string ContentType { get; set; }
    public long Size { get; set; }
    public DateTime UploadDate { get; set; }
    public string Url { get; set; }
    
    // Static list of supported file types and their MIME types
    public static Dictionary<string, string> FileTypes { get; }
}
```

## Common Extensions

```csharp
// Check if a time is between two intervals
bool isBetween = dateTimeOffset.IsTimeBetween(
    compareDateTime,
    startIntervalMinutes: 30,
    endIntervalMinutes: 60
);
```

---

## Best Practices

1. Create a new `DataAccess<T>` instance for each table you want to interact with.
2. Use the appropriate comparison types for your queries (ComparisonTypes enum).
3. Take advantage of batch operations for better performance when updating multiple entities.
4. Set appropriate file size limits and type restrictions for your blob storage implementation.
5. Use the session management functionality for handling temporary user data.
