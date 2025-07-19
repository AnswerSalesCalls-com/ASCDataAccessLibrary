# **ASCDataAccessLibrary: Complete Getting Started Guide v2.0**

This guide provides a comprehensive introduction to working with the ASCDataAccessLibrary, a powerful .NET library for managing Azure Table Storage and Azure Blob Storage operations with enhanced capabilities for large data fields, session management, error logging, tag-based blob indexing, and both synchronous and asynchronous operations.

## **Table of Contents**

1. [Installation](#installation)
2. [Basic Concepts](#basic-concepts)
3. [Working with Data Access (Table Storage)](#working-with-data-access-table-storage)
4. [Working with Blob Storage](#working-with-blob-storage)
5. [Session Management](#session-management)
6. [Error Logging](#error-logging)
7. [Advanced Features](#advanced-features)
8. [Examples](#examples)
9. [Best Practices](#best-practices)

## **Installation**

Install the package from NuGet:

```
Install-Package ASCDataAccessLibrary
```

Or using the .NET CLI:

```
dotnet add package ASCDataAccessLibrary
```

## **Basic Concepts**

### **Key Components**

- **DataAccess<T>**: Core component for CRUD operations with Azure Table Storage (supports both sync and async operations)
- **AzureBlobs**: Advanced blob storage manager with tag-based indexing and lambda search capabilities
- **Session**: Advanced session management utility for maintaining stateful web applications with async support
- **ErrorLogData**: Comprehensive error logging system with automatic caller information capture
- **TableEntityBase**: Base class that automatically handles large data fields by chunking
- **ITableExtra**: Interface that your model classes must implement
- **BlobData**: Enhanced blob metadata model with tag and metadata support

### **Creating a Model for Table Storage**

Your model classes need to inherit from TableEntityBase and implement ITableExtra:

```csharp
using ASCTableStorage.Models;

public class Customer : TableEntityBase, ITableExtra
{
    // Primary key field - stored in PartitionKey
    public string CompanyId
    {
        get { return this.PartitionKey; }
        set { this.PartitionKey = value; }
    }
    
    // Unique identifier - stored in RowKey
    public string CustomerId
    {
        get { return this.RowKey; }
        set { this.RowKey = value; }
    }
    
    // Your custom properties
    public string Name { get; set; }
    public string Email { get; set; }
    public string PhoneNumber { get; set; }
    public string LargeDataField { get; set; } // Will be automatically chunked if needed
    
    // Required by ITableExtra
    public string TableReference => "Customers";
    
    // Required by ITableExtra - defines the unique identifier
    public string GetIDValue() => this.CustomerId;
}
```

## **Working with Data Access (Table Storage)**

### **Initialization**

Create a new instance of DataAccess<T> for each entity type:

```csharp
// Initialize data access with your Azure Storage credentials
var customerAccess = new DataAccess<Customer>("your-azure-account-name", "your-azure-account-key");
```

**Important**: Create a separate DataAccess instance for each table you need to work with.

### **Synchronous vs Asynchronous Operations**

The library supports both synchronous and asynchronous operations. All synchronous methods have corresponding async versions:

```csharp
// Synchronous operations
var customer = customerAccess.GetRowObject("cust456");
customerAccess.ManageData(customer);

// Asynchronous operations (recommended for better performance)
var customer = await customerAccess.GetRowObjectAsync("cust456");
await customerAccess.ManageDataAsync(customer);
```

### **Lambda Expression Support for Table Storage**

The library now supports advanced lambda expressions with hybrid server/client-side filtering:

```csharp
// Simple equality filters (processed server-side for performance)
var activeCustomers = await customerAccess.GetCollectionAsync(x => x.Status == "Active");

// Complex expressions (hybrid filtering - server-side where possible, client-side for complex operations)
var recentActiveCustomers = await customerAccess.GetCollectionAsync(x => 
    x.Status == "Active" && 
    x.CreatedDate > DateTime.Today.AddDays(-30) &&
    x.CompanyName.ToLower().Contains("microsoft"));

// Date range queries
var thisMonthCustomers = await customerAccess.GetCollectionAsync(x => 
    x.CreatedDate >= DateTime.Today.AddDays(-30));
```

### **Pagination Support with Lambda Expressions**

```csharp
// Get paginated results with lambda filtering
var pagedActiveCustomers = await customerAccess.GetPagedCollectionAsync(
    x => x.Status == "Active" && x.CompanyName.Contains("Tech"), 
    pageSize: 100);

// Process results
foreach (var customer in pagedActiveCustomers.Items)
{
    Console.WriteLine($"Customer: {customer.Name}");
}

// Get next page using continuation token
if (pagedActiveCustomers.HasMore)
{
    var nextPage = await customerAccess.GetPagedCollectionAsync(
        x => x.Status == "Active" && x.CompanyName.Contains("Tech"),
        pageSize: 100, 
        continuationToken: pagedActiveCustomers.ContinuationToken);
}
```

### **Batch Operations with Progress Tracking**

```csharp
// Batch operations with progress tracking
var progress = new Progress<DataAccess<Customer>.BatchUpdateProgress>(progress =>
{
    Console.WriteLine($"Progress: {progress.PercentComplete:F1}% " +
                     $"({progress.ProcessedItems}/{progress.TotalItems} items, " +
                     $"Batch {progress.CompletedBatches}/{progress.TotalBatches})");
});

var result = await customerAccess.BatchUpdateListAsync(
    customersToUpdate, 
    TableOperationType.InsertOrReplace, 
    progress);

// Handle results
if (result.Success)
{
    Console.WriteLine($"Successfully updated {result.SuccessfulItems} customers");
}
else
{
    Console.WriteLine($"Failed items: {result.FailedItems}");
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
}
```

## **Working with Blob Storage**

The library includes a powerful `AzureBlobs` class that provides advanced blob storage operations with tag-based indexing and lambda search capabilities.

### **Initialization**

```csharp
// Initialize for a specific container (operates on one container throughout object lifetime)
var azureBlobs = new AzureBlobs("your-account-name", "your-account-key", "documents");

// Optional: Set maximum file size (default is 5MB)
var azureBlobs = new AzureBlobs("account", "key", "container", maxFileSizeBytes: 10 * 1024 * 1024); // 10MB
```

### **File Type Management**

```csharp
// Add single allowed file type
azureBlobs.AddAllowedFileType(".pdf", "application/pdf");

// Add multiple file types from dictionary
var fileTypes = new Dictionary<string, string>
{
    [".pdf"] = "application/pdf",
    [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
};
azureBlobs.AddAllowedFileType(fileTypes);

// Add multiple file types from arrays
azureBlobs.AddAllowedFileType(
    new[] { ".jpg", ".png", ".gif" },
    new[] { "image/jpeg", "image/png", "image/gif" }
);

// Remove file type
azureBlobs.RemoveAllowedFileType(".pdf");

// Check if file type is allowed (returns true if no restrictions are configured)
bool isAllowed = azureBlobs.IsFileTypeAllowed("document.pdf");
```

### **Upload Operations with Tag-Based Indexing**

Azure Blob Storage supports up to 10 searchable index tags per blob for ultra-fast queries:

```csharp
// Simple upload (blob name automatically derived from filename)
var uri = await azureBlobs.UploadFileAsync(@"C:\Documents\invoice.pdf");

// Upload with searchable tags and metadata
var tags = new Dictionary<string, string>
{
    ["department"] = "finance",        // Searchable
    ["type"] = "invoice",             // Searchable  
    ["year"] = "2025",                // Searchable
    ["status"] = "pending"            // Searchable
};

var metadata = new Dictionary<string, string>
{
    ["processedBy"] = "John Doe",     // Not searchable, but accessible
    ["notes"] = "Quarterly report"    // Not searchable, but accessible
};

var uri = await azureBlobs.UploadFileAsync(
    @"C:\Documents\invoice.pdf",
    indexTags: tags,
    metadata: metadata
);

// Upload stream with tags
using var stream = File.OpenRead(@"C:\data.xlsx");
var uri = await azureBlobs.UploadStreamAsync(
    stream, 
    "financial-report.xlsx",
    indexTags: new Dictionary<string, string> 
    {
        ["category"] = "financial",
        ["quarter"] = "Q1"
    }
);

// Batch upload multiple files
var uploadInfos = new List<BlobUploadInfo>
{
    new BlobUploadInfo 
    { 
        FilePath = @"C:\file1.pdf",
        IndexTags = new Dictionary<string, string> { ["type"] = "contract" }
    },
    new BlobUploadInfo 
    { 
        FilePath = @"C:\file2.docx",
        IndexTags = new Dictionary<string, string> { ["type"] = "proposal" }
    }
};

var results = await azureBlobs.UploadMultipleFilesAsync(uploadInfos);
```

### **Advanced Search with Lambda Expressions**

The library provides powerful lambda-based search capabilities that automatically optimize between server-side tag queries and client-side filtering:

```csharp
// Search by tags (ultra-fast server-side tag query)
var invoices = await azureBlobs.GetCollectionAsync(b => 
    b.Tags["type"] == "invoice" && 
    b.Tags["department"] == "finance");

// Search by upload date (client-side filtering)
var recentFiles = await azureBlobs.GetCollectionAsync(b => 
    b.UploadDate > DateTime.Today.AddDays(-7));

// Complex hybrid queries (server-side tags + client-side date filtering)
var recentInvoices = await azureBlobs.GetCollectionAsync(b => 
    b.Tags["type"] == "invoice" && 
    b.Tags["status"] == "pending" &&
    b.UploadDate > DateTime.Today.AddDays(-30));

// File type and size filtering
var largeImages = await azureBlobs.GetCollectionAsync(b => 
    b.IsImage() && 
    b.Size > 1024 * 1024); // Files larger than 1MB

// Search by filename patterns
var reports = await azureBlobs.GetCollectionAsync(b => 
    b.Name.Contains("report", StringComparison.OrdinalIgnoreCase));
```

### **Traditional Search Methods**

```csharp
// Direct tag query (OData syntax)
var query = "type = 'invoice' AND department = 'finance' AND year = '2025'";
var blobs = await azureBlobs.SearchBlobsByTagsAsync(query);

// Search with multiple criteria
var searchResults = await azureBlobs.SearchBlobsAsync(
    searchText: "invoice",
    tagFilters: new Dictionary<string, string> { ["type"] = "financial" },
    startDate: DateTime.Today.AddMonths(-1),
    endDate: DateTime.Today
);

// List all blobs in container
var allBlobs = await azureBlobs.ListBlobsAsync();

// List with prefix filter
var prefixedBlobs = await azureBlobs.ListBlobsAsync("invoice-");
```

### **Download Operations with Lambda Search**

```csharp
// Download specific file
bool success = await azureBlobs.DownloadFileAsync("invoice.pdf", @"C:\Downloads\invoice.pdf");

// Download multiple files based on search criteria
var downloadResults = await azureBlobs.DownloadFilesAsync(
    b => b.Tags["type"] == "invoice" && b.Tags["year"] == "2025",
    @"C:\Downloads\Invoices",
    preserveOriginalNames: true
);

foreach (var result in downloadResults)
{
    if (result.Value.Success)
        Console.WriteLine($"Downloaded: {result.Value.DestinationPath}");
    else
        Console.WriteLine($"Failed to download {result.Key}: {result.Value.ErrorMessage}");
}

// Download to streams for processing
var streamResults = await azureBlobs.DownloadFilesToStreamAsync(
    b => b.Tags["process"] == "pending",
    blobName => new MemoryStream()
);

foreach (var result in streamResults)
{
    if (result.Value.Success && result.Value.Stream != null)
    {
        // Process the stream
        result.Value.Stream.Position = 0;
        // ... your processing logic
        result.Value.Stream.Dispose();
    }
}
```

### **Delete Operations with Lambda Search**

```csharp
// Delete single blob
bool deleted = await azureBlobs.DeleteBlobAsync("old-file.pdf");

// Delete multiple blobs by name
var blobNames = new[] { "file1.pdf", "file2.docx" };
var deleteResults = await azureBlobs.DeleteMultipleBlobsAsync(blobNames);

// Delete multiple blobs using lambda search
var cleanupResults = await azureBlobs.DeleteMultipleBlobsAsync(
    b => b.Tags["temp"] == "true" && b.UploadDate < DateTime.Today.AddDays(-7)
);

foreach (var result in cleanupResults)
{
    Console.WriteLine($"Delete {result.Key}: {(result.Value ? "Success" : "Failed")}");
}
```

### **Tag Management**

```csharp
// Update tags for existing blob
var newTags = new Dictionary<string, string>
{
    ["status"] = "processed",
    ["processedDate"] = DateTime.Today.ToString("yyyy-MM-dd")
};
bool updated = await azureBlobs.UpdateBlobTagsAsync("invoice.pdf", newTags);

// Get tags for a blob
var tags = await azureBlobs.GetBlobTagsAsync("invoice.pdf");
if (tags != null)
{
    foreach (var tag in tags)
        Console.WriteLine($"{tag.Key}: {tag.Value}");
}
```

### **Working with BlobData Objects**

```csharp
// The BlobData class provides rich metadata and helper methods
var blobs = await azureBlobs.ListBlobsAsync();

foreach (var blob in blobs)
{
    // Basic properties
    Console.WriteLine($"Name: {blob.Name}");
    Console.WriteLine($"Original Filename: {blob.OriginalFilename}");
    Console.WriteLine($"Size: {blob.GetFormattedSize()}"); // "1.5 MB"
    Console.WriteLine($"Upload Date: {blob.UploadDate}");
    Console.WriteLine($"Container: {blob.ContainerName}");
    
    // File type checking
    Console.WriteLine($"Is Image: {blob.IsImage()}");
    Console.WriteLine($"Is Document: {blob.IsDocument()}");
    Console.WriteLine($"Is Video: {blob.IsVideo()}");
    Console.WriteLine($"Is Audio: {blob.IsAudio()}");
    
    // Tag and metadata access
    string department = blob.GetTag("department") ?? "Unknown";
    string notes = blob.GetMetadata("notes") ?? "No notes";
    
    // Check for specific tags
    if (blob.HasTag("urgent", "true"))
    {
        Console.WriteLine("This is an urgent file!");
    }
    
    // Extension and content type
    Console.WriteLine($"Extension: {blob.GetFileExtension()}");
    Console.WriteLine($"Content Type: {blob.ContentType}");
}
```

## **Session Management**

The Session class provides advanced session management with both synchronous and asynchronous support.

### **Basic Session Usage**

```csharp
// Synchronous session creation
var session = new Session("your-account", "your-key", "user-session-id");

// Asynchronous session creation (recommended)
var session = await Session.CreateAsync("your-account", "your-key", "user-session-id");
```

### **Working with Session Data**

```csharp
// Store values in session
session["UserName"] = "John Doe";
session["LastLogin"] = DateTime.Now.ToString();
session["ShoppingCart"] = "Product1,Product2,Product3";

// Read values from session
string userName = session["UserName"]?.Value;
var lastLogin = DateTime.Parse(session["LastLogin"]?.Value ?? DateTime.MinValue.ToString());

// Asynchronous commit (recommended)
await session.CommitDataAsync();

// Synchronous commit
session.CommitData();
```

### **Advanced Session Operations**

```csharp
// Load session data manually
await session.LoadSessionDataAsync("different-session-id");

// Refresh current session from database
await session.RefreshSessionDataAsync();

// Find stale/abandoned sessions
var staleSessions = await session.GetStaleSessionsAsync();
foreach (var staleSessionId in staleSessions)
{
    Console.WriteLine($"Found stale session: {staleSessionId}");
}

// Clean up old session data
await session.CleanSessionDataAsync();

// Restart session (clears all data)
await session.RestartSessionAsync();
```

### **Session Disposal**

```csharp
// Using statement automatically commits data on disposal
await using var session = await Session.CreateAsync("account", "key", "session-id");
session["Key"] = "Value";
// Data is automatically committed when session is disposed

// Or manually dispose
session.Dispose(); // Synchronous disposal
await session.DisposeAsync(); // Asynchronous disposal
```

## **Error Logging**

The library includes a comprehensive error logging system with automatic caller information capture.

### **Basic Error Logging**

```csharp
try
{
    // Your code that might throw an exception
    throw new InvalidOperationException("Something went wrong");
}
catch (Exception ex)
{
    // Create error log with exception
    var errorLog = new ErrorLogData(ex, "Failed to process customer data", 
                                   ErrorCodeTypes.Error, "customer123");
    
    // Log the error asynchronously
    await errorLog.LogErrorAsync("your-account", "your-key");
}
```

### **Error Logging with Automatic Caller Information**

```csharp
public async Task ProcessCustomerData(string customerId)
{
    try
    {
        // Your processing logic
    }
    catch (Exception ex)
    {
        // Create error log with automatic caller information
        var errorLog = ErrorLogData.CreateWithCallerInfo(
            "Failed to process customer data: " + ex.Message,
            ErrorCodeTypes.Critical,
            customerId);
        
        await errorLog.LogErrorAsync("your-account", "your-key");
    }
}
```

### **Error Severity Levels**

```csharp
// Different severity levels available
var infoLog = ErrorLogData.CreateWithCallerInfo("User logged in", ErrorCodeTypes.Information);
var warningLog = ErrorLogData.CreateWithCallerInfo("Low disk space", ErrorCodeTypes.Warning);
var errorLog = ErrorLogData.CreateWithCallerInfo("Database connection failed", ErrorCodeTypes.Error);
var criticalLog = ErrorLogData.CreateWithCallerInfo("System failure", ErrorCodeTypes.Critical);

// Log all at once
await Task.WhenAll(
    infoLog.LogErrorAsync("account", "key"),
    warningLog.LogErrorAsync("account", "key"),
    errorLog.LogErrorAsync("account", "key"),
    criticalLog.LogErrorAsync("account", "key"));
```

## **Advanced Features**

### **Queue Data Management**

```csharp
// Create a queue for background processing
var queue = new QueueData<CustomerProcessingTask>
{
    Name = "CustomerProcessing",
    QueueID = Guid.NewGuid().ToString()
};

// Add data to queue
var tasks = new List<CustomerProcessingTask> { /* your tasks */ };
queue.PutData(tasks);

// Save queue to database
await queue.SaveQueueAsync("your-account", "your-key");

// Later, retrieve and process queued data
var queueProcessor = new QueueData<CustomerProcessingTask>();
var pendingTasks = await queueProcessor.GetQueuesAsync("CustomerProcessing", "your-account", "your-key");

foreach (var task in pendingTasks)
{
    // Process each task
    Console.WriteLine($"Processing task: {task.TaskId}");
}
```

## **Examples**

### **Complete Document Management System**

```csharp
public class DocumentService
{
    private readonly AzureBlobs _blobStorage;
    private readonly DataAccess<DocumentMetadata> _documentAccess;
    private readonly Session _session;
    
    public DocumentService(string accountName, string accountKey, string sessionId)
    {
        _blobStorage = new AzureBlobs(accountName, accountKey, "documents");
        _documentAccess = new DataAccess<DocumentMetadata>(accountName, accountKey);
        _session = new Session(accountName, accountKey, sessionId);
        
        // Configure allowed file types
        _blobStorage.AddAllowedFileType(new Dictionary<string, string>
        {
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        });
    }
    
    public async Task<bool> UploadDocumentAsync(string filePath, string department, string category, string userId)
    {
        try
        {
            // Upload to blob storage with tags
            var tags = new Dictionary<string, string>
            {
                ["department"] = department,
                ["category"] = category,
                ["uploadedBy"] = userId,
                ["year"] = DateTime.Now.Year.ToString(),
                ["month"] = DateTime.Now.ToString("yyyy-MM")
            };
            
            var metadata = new Dictionary<string, string>
            {
                ["uploadedBy"] = userId,
                ["uploadDate"] = DateTime.UtcNow.ToString("o")
            };
            
            var blobUri = await _blobStorage.UploadFileAsync(filePath, indexTags: tags, metadata: metadata);
            
            // Store metadata in table storage
            var documentMetadata = new DocumentMetadata
            {
                DocumentId = Guid.NewGuid().ToString(),
                Department = department,
                FileName = Path.GetFileName(filePath),
                BlobUri = blobUri.ToString(),
                Category = category,
                UploadedBy = userId,
                UploadDate = DateTime.UtcNow
            };
            
            await _documentAccess.ManageDataAsync(documentMetadata);
            
            // Update session with recent activity
            _session["LastUpload"] = DateTime.UtcNow.ToString();
            _session["LastUploadFile"] = Path.GetFileName(filePath);
            await _session.CommitDataAsync();
            
            // Log success
            var successLog = ErrorLogData.CreateWithCallerInfo(
                $"Document uploaded successfully: {Path.GetFileName(filePath)}",
                ErrorCodeTypes.Information,
                userId);
            await successLog.LogErrorAsync("account", "key");
            
            return true;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to upload document: " + ex.Message,
                ErrorCodeTypes.Error,
                userId);
            await errorLog.LogErrorAsync("account", "key");
            
            return false;
        }
    }
    
    public async Task<List<BlobData>> SearchDocumentsAsync(string department, string category, DateTime? fromDate = null)
    {
        try
        {
            // Use lambda search for complex criteria
            var documents = await _blobStorage.GetCollectionAsync(b =>
                b.Tags["department"] == department &&
                b.Tags["category"] == category &&
                (fromDate == null || b.UploadDate >= fromDate.Value));
            
            return documents;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to search documents: " + ex.Message,
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync("account", "key");
            
            return new List<BlobData>();
        }
    }
    
    public async Task<Dictionary<string, BlobOperationResult>> DownloadDocumentsBulkAsync(
        string department, string category, string downloadPath)
    {
        try
        {
            // Download multiple documents based on criteria
            var results = await _blobStorage.DownloadFilesAsync(
                b => b.Tags["department"] == department && b.Tags["category"] == category,
                downloadPath,
                preserveOriginalNames: true
            );
            
            // Update session with download activity
            _session["LastBulkDownload"] = DateTime.UtcNow.ToString();
            _session["LastDownloadCount"] = results.Count.ToString();
            await _session.CommitDataAsync();
            
            return results;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to download documents: " + ex.Message,
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync("account", "key");
            
            return new Dictionary<string, BlobOperationResult>();
        }
    }
    
    public async Task<Dictionary<string, bool>> CleanupOldTempFilesAsync(int daysOld = 7)
    {
        try
        {
            // Delete old temporary files using lambda search
            var cutoffDate = DateTime.Today.AddDays(-daysOld);
            var deleteResults = await _blobStorage.DeleteMultipleBlobsAsync(
                b => b.Tags["temporary"] == "true" && b.UploadDate < cutoffDate
            );
            
            var successLog = ErrorLogData.CreateWithCallerInfo(
                $"Cleaned up {deleteResults.Count(r => r.Value)} old temporary files",
                ErrorCodeTypes.Information);
            await successLog.LogErrorAsync("account", "key");
            
            return deleteResults;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to cleanup temp files: " + ex.Message,
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync("account", "key");
            
            return new Dictionary<string, bool>();
        }
    }
    
    public async Task DisposeAsync()
    {
        await _session?.DisposeAsync();
    }
}

// Supporting model class
public class DocumentMetadata : TableEntityBase, ITableExtra
{
    public string DocumentId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Department
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string FileName { get; set; }
    public string BlobUri { get; set; }
    public string Category { get; set; }
    public string UploadedBy { get; set; }
    public DateTime UploadDate { get; set; }
    
    public string TableReference => "DocumentMetadata";
    public string GetIDValue() => this.DocumentId;
}
```

### **Usage Example**

```csharp
// Initialize the document service
var docService = new DocumentService("accountName", "accountKey", "user-session-123");

// Upload a document with tags
await docService.UploadDocumentAsync(
    @"C:\Reports\Q1-Financial-Report.pdf", 
    "Finance", 
    "Report", 
    "john.doe@company.com"
);

// Search for documents
var financeReports = await docService.SearchDocumentsAsync(
    "Finance", 
    "Report", 
    DateTime.Today.AddMonths(-3)
);

Console.WriteLine($"Found {financeReports.Count} finance reports from the last 3 months");

// Download all matching documents
var downloadResults = await docService.DownloadDocumentsBulkAsync(
    "Finance", 
    "Report", 
    @"C:\Downloads\FinanceReports"
);

foreach (var result in downloadResults)
{
    if (result.Value.Success)
        Console.WriteLine($"Downloaded: {result.Value.DestinationPath}");
    else
        Console.WriteLine($"Failed: {result.Value.ErrorMessage}");
}

// Cleanup old temporary files
var cleanupResults = await docService.CleanupOldTempFilesAsync(7);
Console.WriteLine($"Cleaned up {cleanupResults.Count(r => r.Value)} old temp files");

// Dispose resources
await docService.DisposeAsync();
```

## **Best Practices**

### **General Guidelines**

1. **Use Async Methods**: Always prefer async methods for better performance and scalability.

2. **Create Separate Instances**: Always create a new DataAccess instance for each table and a new AzureBlobs instance for each container.

3. **Leverage Lambda Expressions**: Use lambda expressions for complex queries - the library automatically optimizes between server and client-side filtering.

4. **Handle Large Data**: Let TableEntityBase handle chunking of large text fields automatically.

### **Blob Storage Best Practices**

5. **Design Tag Strategy**: Plan your tag strategy carefully - you have only 10 searchable tags per blob. Use tags for frequently searched criteria.

6. **Use Metadata for Non-Searchable Data**: Store descriptive information that doesn't need to be searchable in metadata.

7. **Container per Purpose**: Create separate AzureBlobs instances for different containers (documents, images, temp files, etc.).

8. **File Type Management**: Configure allowed file types only when needed - leaving it empty allows all file types.

9. **Batch Operations**: Use lambda-based bulk operations for downloading/deleting multiple files.

### **Table Storage Best Practices**

10. **Use Hybrid Filtering**: Let the library optimize your lambda expressions - simple equality operations use fast server-side filtering, complex operations fall back to client-side.

11. **Use Pagination**: For large datasets, use `GetPagedCollectionAsync()` to avoid memory issues.

12. **Batch Operations**: Group multiple operations using batch methods with progress tracking.

### **Session Management**

13. **Use Using Statements**: Wrap sessions in `using` statements or call `DisposeAsync()` to ensure data is committed.

14. **Clean Up Regularly**: Implement periodic jobs to call `CleanSessionDataAsync()`.

15. **Monitor Stale Sessions**: Use `GetStaleSessionsAsync()` to identify abandoned sessions.

### **Error Logging**

16. **Use Automatic Caller Info**: Prefer `ErrorLogData.CreateWithCallerInfo()` for automatic method/line number capture.

17. **Log Appropriately**: Use appropriate severity levels and include relevant context.

### **Performance Optimization**

18. **Tag-Based Searches**: Use tag-based searches for maximum performance with large blob collections.

19. **Optimize Lambda Expressions**: Simple equality checks on indexed fields (PartitionKey, RowKey) or tags perform best.

20. **Progress Tracking**: Use progress callbacks for long-running batch operations.

This library provides a comprehensive solution for Azure Storage operations with modern C# features including lambda expressions, hybrid filtering, tag-based indexing, and full async/await support for scalable cloud applications.

## **Migration Guide from v1.x**

### **Breaking Changes in v2.0**

#### **Blob Storage**
- **Constructor Change**: `AzureBlobs` now requires `containerName` parameter and operates on a single container
- **Method Signatures**: Removed `containerName` parameters from all methods
- **Upload Behavior**: `overwrite` parameter removed (now always true by default)
- **Result Classes**: Custom result classes may conflict with Azure SDK classes

#### **Lambda Expression Support**
- **New Feature**: Lambda expressions now supported for both Table Storage and Blob Storage
- **Hybrid Filtering**: Automatic optimization between server-side and client-side filtering
- **Performance**: Tag-based blob searches provide significant performance improvements

### **Migration Steps**

1. **Update Blob Storage Initialization**:
   ```csharp
   // v1.x
   var blobs = new AzureBlobs("account", "key");
   await blobs.UploadFileAsync("container", filePath);
   
   // v2.0
   var blobs = new AzureBlobs("account", "key", "container");
   await blobs.UploadFileAsync(filePath);
   ```

2. **Update Search Operations**:
   ```csharp
   // v1.x
   var results = await dataAccess.GetCollection("partitionKey");
   
   // v2.0 (enhanced with lambda support)
   var results = await dataAccess.GetCollectionAsync(x => x.PartitionKey == "partitionKey");
   ```

3. **Leverage New Tag Features**:
   ```csharp
   // v2.0 - Add searchable tags to blobs
   await blobs.UploadFileAsync(filePath, indexTags: new Dictionary<string, string>
   {
       ["category"] = "document",
       ["department"] = "finance"
   });
   
   // Fast tag-based searches
   var documents = await blobs.GetCollectionAsync(b => 
       b.Tags["category"] == "document" && 
       b.Tags["department"] == "finance");
   ```

## **API Reference Summary**

### **DataAccess<T> Class**

#### **Core CRUD Operations**
- `ManageDataAsync(T obj, TableOperationType direction = InsertOrReplace)` - Create/Update single entity
- `GetRowObjectAsync(string rowKeyID)` - Get by RowKey
- `GetRowObjectAsync(Expression<Func<T, bool>> predicate)` - Get with lambda expression
- `GetCollectionAsync(Expression<Func<T, bool>> predicate)` - Get collection with lambda
- `GetAllTableDataAsync()` - Get all entities
- `BatchUpdateListAsync(List<T> data, TableOperationType direction, IProgress<BatchUpdateProgress> progress)` - Batch operations

#### **Pagination**
- `GetPagedCollectionAsync(Expression<Func<T, bool>> predicate, int pageSize, string continuationToken)` - Paginated results with lambda
- `GetInitialDataLoadAsync(Expression<Func<T, bool>> predicate, int initialLoadSize)` - Quick initial load

### **AzureBlobs Class**

#### **File Management**
- `AddAllowedFileType(string extension, string contentType)` - Add single file type
- `AddAllowedFileType(Dictionary<string, string> fileTypes)` - Add multiple file types
- `AddAllowedFileType(string[] extensions, string[] contentTypes)` - Add from arrays
- `IsFileTypeAllowed(string filePath)` - Check if file type allowed

#### **Upload Operations**
- `UploadFileAsync(string filePath, bool enforceFileTypeRestriction, long? maxFileSizeBytes, Dictionary<string, string> indexTags, Dictionary<string, string> metadata)` - Upload file with tags
- `UploadStreamAsync(Stream stream, string fileName, string contentType, bool enforceFileTypeRestriction, long? maxFileSizeBytes, Dictionary<string, string> indexTags, Dictionary<string, string> metadata)` - Upload stream
- `UploadMultipleFilesAsync(IEnumerable<BlobUploadInfo> files, bool enforceFileTypeRestriction, long? maxFileSizeBytes)` - Batch upload

#### **Search Operations**
- `GetCollectionAsync(Expression<Func<BlobData, bool>> predicate, string prefix)` - Lambda search
- `SearchBlobsByTagsAsync(string tagQuery)` - Direct tag query
- `SearchBlobsAsync(string searchText, Dictionary<string, string> tagFilters, DateTime? startDate, DateTime? endDate)` - Multi-criteria search
- `ListBlobsAsync(string prefix)` - List all blobs

#### **Download Operations**
- `DownloadFileAsync(string blobName, string destinationPath)` - Download single file
- `DownloadFilesAsync(Expression<Func<BlobData, bool>> predicate, string destinationDirectory, bool preserveOriginalNames)` - Lambda-based bulk download
- `DownloadToStreamAsync(string blobName, Stream targetStream)` - Download to stream
- `DownloadFilesToStreamAsync(Expression<Func<BlobData, bool>> predicate, Func<string, Stream> streamFactory)` - Lambda-based stream download

#### **Delete Operations**
- `DeleteBlobAsync(string blobName)` - Delete single blob
- `DeleteMultipleBlobsAsync(IEnumerable<string> blobNames)` - Delete by names
- `DeleteMultipleBlobsAsync(Expression<Func<BlobData, bool>> predicate)` - Lambda-based bulk delete

#### **Tag Management**
- `UpdateBlobTagsAsync(string blobName, Dictionary<string, string> tags)` - Update blob tags
- `GetBlobTagsAsync(string blobName)` - Get blob tags

### **Session Class**

#### **Core Operations**
- `CreateAsync(string accountName, string accountKey, string sessionID)` - Async factory method
- `LoadSessionDataAsync(string sessionID)` - Load session data
- `CommitDataAsync()` - Save session data
- `RestartSessionAsync()` - Clear session data
- `GetStaleSessionsAsync()` - Find abandoned sessions
- `CleanSessionDataAsync()` - Remove old sessions

#### **Data Access**
- `this[string key]` - Indexer for session values
- `SessionData` - Read-only access to session data
- `SessionID` - Current session identifier

### **ErrorLogData Class**

#### **Creation Methods**
- `new ErrorLogData(Exception e, string errDescription, ErrorCodeTypes severity, string cID)` - From exception
- `CreateWithCallerInfo(string errDescription, ErrorCodeTypes severity, string cID)` - With automatic caller info

#### **Logging**
- `LogErrorAsync(string accountName, string accountKey)` - Async logging
- `LogError(string accountName, string accountKey)` - Sync logging

### **BlobData Class**

#### **Properties**
- `Name` - Blob name in Azure Storage
- `OriginalFilename` - Original filename when uploaded
- `ContentType` - MIME content type
- `Size` - Size in bytes
- `UploadDate` - Upload timestamp
- `Url` - Full URI to blob
- `ContainerName` - Container name
- `Tags` - Searchable index tags (max 10)
- `Metadata` - Non-searchable metadata

#### **Helper Methods**
- `GetTag(string tagKey)` - Get tag value
- `SetTag(string tagKey, string tagValue)` - Set tag value
- `HasTag(string tagKey, string tagValue)` - Check for tag
- `GetMetadata(string metadataKey)` - Get metadata value
- `SetMetadata(string metadataKey, string metadataValue)` - Set metadata value
- `GetFormattedSize()` - Human-readable size
- `GetFileExtension()` - File extension
- `IsImage()` - Check if image file
- `IsDocument()` - Check if document file
- `IsVideo()` - Check if video file
- `IsAudio()` - Check if audio file

## **Troubleshooting**

### **Common Issues and Solutions**

#### **Blob Storage Issues**

**Q: Tag queries not returning expected results**
```csharp
// Problem: Complex OR queries
var results = await blobs.GetCollectionAsync(b => 
    b.Tags["type"] == "doc" || b.Tags["type"] == "pdf"); // May be slow

// Solution: Use simpler queries or multiple searches
var docs = await blobs.GetCollectionAsync(b => b.Tags["type"] == "doc");
var pdfs = await blobs.GetCollectionAsync(b => b.Tags["type"] == "pdf");
var combined = docs.Concat(pdfs).ToList();
```

**Q: File type restrictions not working**
```csharp
// Check if restrictions are configured
if (BlobData.FileTypes.Count == 0)
{
    // No restrictions - all files allowed
    blobs.AddAllowedFileType(".pdf", "application/pdf");
}
```

**Q: Upload fails with tag limit error**
```csharp
// Problem: Too many tags
var tags = new Dictionary<string, string>(); // More than 10 tags

// Solution: Use metadata for non-searchable data
var tags = new Dictionary<string, string>
{
    ["category"] = "document",  // Searchable
    ["type"] = "invoice"        // Searchable
};

var metadata = new Dictionary<string, string>
{
    ["notes"] = "Long description...",  // Non-searchable
    ["processedBy"] = "John Doe"        // Non-searchable
};
```

#### **Table Storage Issues**

**Q: Lambda expressions performing slowly**
```csharp
// Problem: Complex client-side operations
var results = await dataAccess.GetCollectionAsync(x => 
    x.Description.ToLower().Contains("search")); // Client-side

// Solution: Use server-side filtering where possible
var results = await dataAccess.GetCollectionAsync(x => 
    x.PartitionKey == "known_partition" && 
    x.Status == "Active"); // Server-side
```

**Q: Batch operations failing**
```csharp
// Problem: Mixed partition keys in batch
var mixedData = customers.Where(c => c.CompanyId != customers.First().CompanyId);

// Solution: Group by partition key
var grouped = customers.GroupBy(c => c.CompanyId);
foreach (var group in grouped)
{
    await customerAccess.BatchUpdateListAsync(group.ToList());
}
```

#### **Session Issues**

**Q: Session data not persisting**
```csharp
// Problem: Not committing data
session["key"] = "value";
// Missing: await session.CommitDataAsync();

// Solution: Always commit
session["key"] = "value";
await session.CommitDataAsync();

// Or use using statement for auto-commit
await using var session = await Session.CreateAsync("account", "key", "session-id");
session["key"] = "value";
// Automatically committed on disposal
```

### **Performance Tips**

1. **Use Async Methods**: Always prefer async versions for better scalability
2. **Batch Operations**: Group multiple operations together
3. **Tag-Based Searches**: Use tags for frequently searched blob criteria
4. **Pagination**: Use pagination for large result sets
5. **Progress Tracking**: Monitor long-running operations
6. **Proper Disposal**: Use `using` statements or manual disposal
7. **Connection Reuse**: Create service instances once and reuse them
8. **Error Handling**: Implement proper retry logic for transient failures

## **Support and Resources**

- **GitHub Repository**: [Link to repository]
- **NuGet Package**: `ASCDataAccessLibrary`
- **API Documentation**: [Link to API docs]
- **Sample Projects**: [Link to samples]
- **Issue Tracking**: [Link to issues]

For additional support, please refer to the GitHub repository or contact the development team.