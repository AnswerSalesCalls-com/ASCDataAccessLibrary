# ASCDataAccessLibrary v3.0 Documentation

## 🚀 Overview

ASCDataAccessLibrary v3.0 is a comprehensive .NET library for Azure Table Storage and Blob Storage operations with enhanced features including universal logging, optimized session management, lambda expression support, and automatic data management capabilities.

### Key Features
- **Universal ILogger Implementation** - Production-ready logging for all .NET application types
- **Optimized Session Management** - Distributed-safe sessions without Redis dependency
- **Lambda Expression Support** - Intuitive queries with automatic hybrid filtering
- **Blob Storage with Tags** - Advanced file management with searchable metadata
- **Automatic Cleanup** - Configurable retention policies with level-based cleanup
- **StateList & Queue Management** - Position-aware collections with state persistence
- **Dynamic Entities** - Runtime-defined schemas for flexible data storage
- **Batch Operations** - Efficient bulk data processing with progress tracking

---

## 📋 Table of Contents

1. [Installation & Setup](#installation--setup)
2. [Quick Start Guide](#quick-start-guide)
3. [Core Components](#core-components)
   - [Data Access](#data-access)
   - [Universal Logging](#universal-logging)
   - [Session Management](#session-management)
   - [Blob Storage](#blob-storage)
   - [Dynamic Entities](#dynamic-entities)
   - [StateList & Queues](#statelist--queues)
   - [Error Handling](#error-handling)
4. [Advanced Features](#advanced-features)
5. [Migration Guide](#migration-guide)
6. [API Reference](#api-reference)
7. [Best Practices](#best-practices)
8. [Troubleshooting](#troubleshooting)

---

## Installation & Setup

### Prerequisites
- .NET 9.0 or later
- Azure Storage Account with Table Storage enabled
- Account Name and Account Key (connection strings not required)

### NuGet Installation

```bash
# Package Manager Console
Install-Package ASCDataAccessLibrary -Version 3.0.0

# .NET CLI
dotnet add package ASCDataAccessLibrary --version 3.0.0

# PackageReference in .csproj
<PackageReference Include="ASCDataAccessLibrary" Version="3.0.0" />
```

### Configuration

#### Environment Variables
```bash
# Windows
set AZURE_STORAGE_ACCOUNT=myaccount
set AZURE_STORAGE_KEY=base64key==

# Linux/Mac
export AZURE_STORAGE_ACCOUNT=myaccount
export AZURE_STORAGE_KEY=base64key==
```

#### appsettings.json
```json
{
  "Azure": {
    "TableStorageName": "myaccount",
    "TableStorageKey": "base64key=="
  },
  "Logging": {
    "AzureTable": {
      "MinimumLevel": "Information",
      "RetentionDays": 30,
      "EnableAutoCleanup": true,
      "RetentionByLevel": {
        "Trace": 7,
        "Debug": 14,
        "Information": 30,
        "Warning": 60,
        "Error": 90,
        "Critical": 180
      }
    }
  },
  "Sessions": {
    "SessionTimeout": "00:20:00",
    "BatchWriteDelay": "00:00:00.500",
    "IdStrategy": "Auto"
  }
}
```

---

## Quick Start Guide

### ASP.NET Core Application

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add Azure Table logging
builder.Logging.AddAzureTableLogging("accountName", "accountKey");

// Add Azure Table sessions
builder.Services.AddAzureTableSessions("accountName", "accountKey");

var app = builder.Build();

// Controller usage
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly DataAccess<Customer> _dataAccess;
    
    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
        _dataAccess = new DataAccess<Customer>("accountName", "accountKey");
    }
    
    public async Task<IActionResult> Index()
    {
        _logger.LogInformation("Home page visited");
        
        // Use session
        SessionManager.Current["userId"] = "user123";
        
        // Query data with lambda
        var activeCustomers = await _dataAccess.GetCollectionAsync(
            c => c.Status == "Active" && c.CreatedDate > DateTime.Today.AddDays(-30)
        );
        
        return View(activeCustomers);
    }
}
```

### Desktop Application (WPF/WinForms)

```csharp
// App.xaml.cs or Program.cs
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize logging
        AzureTableLogging.Initialize("accountName", "accountKey", options =>
        {
            options.MinimumLevel = LogLevel.Debug;
            options.EnableAutoCleanup = true;
        });
        
        // Initialize sessions
        SessionManager.Initialize("accountName", "accountKey");
        
        base.OnStartup(e);
    }
}

// Usage in any window/form
public partial class MainWindow : Window
{
    private readonly ILogger _logger;
    private readonly DataAccess<UserData> _dataAccess;
    
    public MainWindow()
    {
        InitializeComponent();
        _logger = AzureTableLogging.CreateLogger<MainWindow>();
        _dataAccess = new DataAccess<UserData>("accountName", "accountKey");
    }
    
    private async void LoadData()
    {
        _logger.LogInformation("Loading user data");
        
        // Store in session
        SessionManager.Current["theme"] = "dark";
        
        // Query with lambda
        var userData = await _dataAccess.GetRowObjectAsync(
            u => u.Username == "john.doe"
        );
    }
}
```

### Console Application

```csharp
class Program
{
    static async Task Main(string[] args)
    {
        // Initialize services
        AzureTableLogging.Initialize("accountName", "accountKey");
        SessionManager.Initialize("accountName", "accountKey");
        
        var logger = AzureTableLogging.CreateLogger<Program>();
        logger.LogInformation("Application started");
        
        // Data operations
        var dataAccess = new DataAccess<DataRecord>("accountName", "accountKey");
        
        // Process with progress tracking
        var progress = new Progress<BatchUpdateProgress>(p =>
        {
            Console.WriteLine($"Progress: {p.PercentComplete:F1}%");
        });
        
        var records = await GenerateRecords();
        var result = await dataAccess.BatchUpdateListAsync(
            records, 
            TableOperationType.InsertOrReplace,
            progress
        );
        
        logger.LogInformation($"Processed {result.SuccessfulItems} records");
        
        // Cleanup happens automatically on exit
    }
}
```

---

## Core Components

### Data Access

The `DataAccess<T>` class provides comprehensive CRUD operations with lambda expression support and automatic hybrid filtering.

#### Basic Operations

```csharp
var dataAccess = new DataAccess<Customer>("accountName", "accountKey");

// Single entity operations
var customer = new Customer 
{ 
    PartitionKey = "US", 
    RowKey = "CUST001",
    Name = "John Doe",
    Status = "Active" 
};

// Insert/Update
await dataAccess.ManageDataAsync(customer);

// Retrieve by RowKey
var retrieved = await dataAccess.GetRowObjectAsync("CUST001");

// Delete
await dataAccess.ManageDataAsync(customer, TableOperationType.Delete);
```

#### Lambda Expression Queries

```csharp
// Simple query
var activeCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active"
);

// Complex query with automatic hybrid filtering
var results = await dataAccess.GetCollectionAsync(
    c => c.Name.ToLower().Contains("tech") ||      // Client-side
         c.Revenue > 1000000 &&                     // Server-side
         c.Country == "US"                          // Server-side
);

// Single row with lambda
var customer = await dataAccess.GetRowObjectAsync(
    c => c.Email == "john@example.com" && c.IsVerified
);
```

#### Pagination

```csharp
// Get paginated results
var firstPage = await dataAccess.GetPagedCollectionAsync(
    predicate: c => c.Status == "Active",
    pageSize: 100
);

// Process all pages
var allResults = new List<Customer>();
var currentPage = firstPage;

while (currentPage.HasMore)
{
    allResults.AddRange(currentPage.Data);
    currentPage = await dataAccess.GetPagedCollectionAsync(
        predicate: c => c.Status == "Active",
        pageSize: 100,
        continuationToken: currentPage.ContinuationToken
    );
}
```

#### Batch Operations

```csharp
// Batch insert/update with progress
var progress = new Progress<BatchUpdateProgress>(p =>
{
    Console.WriteLine($"Batch {p.CompletedBatches}/{p.TotalBatches}");
    Console.WriteLine($"Items: {p.ProcessedItems}/{p.TotalItems} ({p.PercentComplete:F1}%)");
});

var result = await dataAccess.BatchUpdateListAsync(
    largeDataset,  // Can handle thousands of items
    TableOperationType.InsertOrReplace,
    progress
);

if (!result.Success)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
}
```

### Universal Logging

The v3.0 logging system provides a complete ILogger implementation that works across all .NET application types.

#### Configuration

```csharp
public class AzureTableLoggerOptions
{
    public string AccountName { get; set; }
    public string AccountKey { get; set; }
    public string TableName { get; set; } = "ApplicationLogs";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public int RetentionDays { get; set; } = 60;
    public bool EnableAutoCleanup { get; set; } = true;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public Dictionary<LogLevel, int> RetentionByLevel { get; set; }
}
```

#### Usage Examples

```csharp
// Initialize with auto-discovery
AzureTableLogging.Initialize();

// Initialize with configuration
AzureTableLogging.Initialize(options =>
{
    options.MinimumLevel = LogLevel.Debug;
    options.RetentionDays = 30;
    options.RetentionByLevel = new Dictionary<LogLevel, int>
    {
        { LogLevel.Trace, 7 },
        { LogLevel.Debug, 14 },
        { LogLevel.Information, 30 },
        { LogLevel.Warning, 60 },
        { LogLevel.Error, 90 },
        { LogLevel.Critical, 180 }
    };
});

// Create typed logger
var logger = AzureTableLogging.CreateLogger<MyClass>();

// Standard logging
logger.LogInformation("Processing item {ItemId} for customer {CustomerId}", 
    itemId, customerId);

// With exception
logger.LogError(exception, "Failed to process item {ItemId}", itemId);

// With scopes
using (logger.BeginScope("Transaction {TransactionId}", transId))
{
    logger.LogInformation("Starting transaction");
    // All logs within scope include transaction context
}

// Structured logging
logger.LogInformation("Order processed", new 
{ 
    OrderId = orderId,
    Amount = 99.99,
    Customer = customerId,
    Items = itemCount
});
```

#### Desktop-Specific Extensions

```csharp
// Log from UI thread safely
logger.LogFromUI(LogLevel.Information, "Button clicked: {ButtonName}", "Submit");

// Log user actions
logger.LogUserAction("ButtonClick", new { Button = "Submit", Form = "MainForm" });

// Application lifecycle
logger.LogApplicationLifecycle("Startup");
logger.LogApplicationLifecycle("Shutdown");
```

#### Console/Service Extensions

```csharp
// Progress tracking
using (logger.LogProgress("DataImport", totalRecords))
{
    foreach (var record in records)
    {
        ProcessRecord(record);
        // Progress automatically logged every 10 seconds
    }
}

// Heartbeat monitoring
await logger.LogHeartbeat("DataProcessor");

// Batch results
logger.LogBatchResult("Import", 
    processed: 1000, 
    failed: 5, 
    duration: TimeSpan.FromSeconds(45));
```

### Session Management

Optimized session management that's distributed-safe without Redis dependency.

#### Configuration

```csharp
public class SessionOptions
{
    public string AccountName { get; set; }
    public string AccountKey { get; set; }
    public string TableName { get; set; } = "AppSessionData";
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan BatchWriteDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public SessionIdStrategy IdStrategy { get; set; } = SessionIdStrategy.Auto;
}
```

#### Session ID Strategies

- **Auto**: Automatically detects application type
- **HttpContext**: Uses Session.SessionID for web apps
- **UserMachine**: Username + machine name for desktop
- **MachineProcess**: Machine + process ID for services
- **Custom**: Your own provider function
- **Guid**: Always generates new GUID

#### Usage

```csharp
// Initialize once at startup
SessionManager.Initialize("accountName", "accountKey", options =>
{
    options.SessionTimeout = TimeSpan.FromMinutes(30);
    options.IdStrategy = SessionIdStrategy.Auto;
});

// Use anywhere in application
SessionManager.Current["userId"] = "user123";
SessionManager.Current["cartItems"] = JsonConvert.SerializeObject(cart);
SessionManager.Current["preferences"] = userPrefs.ToJson();

// Retrieve values
var userId = SessionManager.Current["userId"]?.Value;
var cartJson = SessionManager.Current["cartItems"]?.Value;

// Check existence
if (SessionManager.Current.ContainsKey("userId"))
{
    // User is logged in
}

// Remove items
await SessionManager.Current.RemoveAsync("tempData");

// Clear session
await SessionManager.Current.ClearAsync();

// Force flush (usually automatic)
await SessionManager.FlushAsync();
```

#### Performance Characteristics

- **Write Operations**: Batched every 500ms for optimal performance
- **Read Operations**: Direct Azure Table access (10-50ms latency)
- **Auto-commit**: Configurable interval (default 10 seconds)
- **Cleanup**: Automatic removal of stale sessions

```csharp
// Writes are batched
SessionManager.Current["key1"] = "value1";  // Queued
SessionManager.Current["key2"] = "value2";  // Queued
SessionManager.Current["key3"] = "value3";  // Queued
// All written together after 500ms

// Reads are direct
var value = SessionManager.Current["key"]?.Value;  // Direct Azure read
```

### Blob Storage

Advanced blob storage with tag-based indexing and lambda search support.

#### Setup

```csharp
var azureBlobs = new AzureBlobs("accountName", "accountKey", "documents");

// Configure allowed file types
azureBlobs.AddAllowedFileType(".pdf", "application/pdf");
azureBlobs.AddAllowedFileType(".docx", 
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

// Or add multiple at once
var allowedTypes = new Dictionary<string, string>
{
    { ".jpg", "image/jpeg" },
    { ".png", "image/png" },
    { ".gif", "image/gif" }
};
azureBlobs.AddAllowedFileType(allowedTypes);
```

#### Upload with Tags

```csharp
// Single file upload with searchable tags
var uri = await azureBlobs.UploadFileAsync(
    "invoice.pdf",
    indexTags: new Dictionary<string, string>
    {
        { "type", "invoice" },
        { "year", "2024" },
        { "customer", "ACME" },
        { "status", "pending" },
        { "amount", "5000" }  // Max 10 tags
    },
    metadata: new Dictionary<string, string>
    {
        { "uploadedBy", "john.doe" },
        { "department", "accounting" },
        { "notes", "Q4 services invoice" }  // No limit on metadata
    }
);

// Stream upload
using (var stream = File.OpenRead("document.pdf"))
{
    var blobUri = await azureBlobs.UploadStreamAsync(
        stream,
        "document.pdf",
        contentType: "application/pdf",
        indexTags: tags,
        metadata: metadata
    );
}

// Batch upload
var files = new List<BlobUploadInfo>
{
    new BlobUploadInfo 
    { 
        FilePath = "file1.pdf",
        IndexTags = new Dictionary<string, string> { { "type", "report" } }
    },
    new BlobUploadInfo 
    { 
        FilePath = "file2.docx",
        IndexTags = new Dictionary<string, string> { { "type", "contract" } }
    }
};

var results = await azureBlobs.UploadMultipleFilesAsync(files);
```

#### Search with Lambda Expressions

```csharp
// Search by tags using lambda
var pendingInvoices = await azureBlobs.GetCollectionAsync(
    b => b.Tags["type"] == "invoice" && 
         b.Tags["status"] == "pending" &&
         b.UploadDate > DateTime.Today.AddDays(-30)
);

// Complex searches
var documents = await azureBlobs.GetCollectionAsync(
    b => b.Tags["year"] == "2024" &&
         b.Size < 10 * 1024 * 1024 &&  // Less than 10MB
         (b.Tags["department"] == "sales" || b.Tags["department"] == "marketing")
);

// Search by multiple criteria
var results = await azureBlobs.SearchBlobsAsync(
    searchText: "invoice",
    tagFilters: new Dictionary<string, string> 
    { 
        { "status", "pending" },
        { "year", "2024" }
    },
    startDate: DateTime.Today.AddMonths(-1),
    endDate: DateTime.Today
);
```

#### Download Operations

```csharp
// Download single file
bool success = await azureBlobs.DownloadFileAsync(
    "invoice.pdf", 
    @"C:\Downloads\invoice.pdf"
);

// Download matching files
var downloadResults = await azureBlobs.DownloadFilesAsync(
    b => b.Tags["type"] == "invoice" && b.Tags["year"] == "2024",
    @"C:\Downloads\2024_Invoices",
    preserveOriginalNames: true
);

// Download to streams
var streamResults = await azureBlobs.DownloadFilesToStreamAsync(
    b => b.Tags["temp"] == "true",
    blobName => new MemoryStream()
);
```

#### Delete Operations

```csharp
// Delete single blob
await azureBlobs.DeleteBlobAsync("old-file.pdf");

// Delete multiple by names
var blobNames = new[] { "file1.pdf", "file2.docx", "file3.xlsx" };
var deleteResults = await azureBlobs.DeleteMultipleBlobsAsync(blobNames);

// Delete by lambda expression
var deleted = await azureBlobs.DeleteMultipleBlobsAsync(
    b => b.Tags["temp"] == "true" && 
         b.UploadDate < DateTime.Today.AddDays(-7)
);
```

#### Tag Management

```csharp
// Update tags for existing blob
await azureBlobs.UpdateBlobTagsAsync("document.pdf", 
    new Dictionary<string, string>
    {
        { "status", "approved" },
        { "reviewer", "jane.doe" }
    });

// Get tags
var tags = await azureBlobs.GetBlobTagsAsync("document.pdf");
```

### Dynamic Entities

Create table entities with runtime-defined schemas.

```csharp
// Create dynamic entity
var entity = new DynamicEntity("Products", "Electronics", "PROD-123");

// Add properties dynamically
entity["Name"] = "Laptop";
entity["Price"] = 999.99;
entity["InStock"] = true;
entity["Categories"] = JsonConvert.SerializeObject(new[] { "Electronics", "Computers" });
entity["Specifications"] = JsonConvert.SerializeObject(new 
{
    CPU = "Intel i7",
    RAM = "16GB",
    Storage = "512GB SSD"
});

// Alternative property methods
entity.SetProperty("Brand", "Dell");
entity.SetProperty("WarrantyYears", 3);

// Save to Azure
var dataAccess = new DataAccess<DynamicEntity>("accountName", "accountKey");
await dataAccess.ManageDataAsync(entity);

// Query dynamic entities
var products = await dataAccess.GetCollectionAsync(
    p => p.PartitionKey == "Electronics"
);

foreach (var product in products)
{
    var name = product.GetProperty<string>("Name");
    var price = product.GetProperty<decimal>("Price");
    var inStock = product.GetProperty<bool>("InStock");
    
    if (product.HasProperty("Brand"))
    {
        var brand = product["Brand"];
    }
    
    // Get all properties
    var allProps = product.GetAllProperties();
}
```

### StateList & Queues

Position-aware collections that maintain state through serialization.

#### StateList

```csharp
// Create StateList with position tracking
var tasks = new StateList<ProcessTask>(taskList)
{
    Description = "Daily Processing Tasks"
};

// Navigate through items
tasks.First();  // Move to first item
while (tasks.HasNext)
{
    tasks.MoveNext();
    var currentTask = tasks.Current;
    
    Console.WriteLine($"Processing task {tasks.CurrentIndex + 1} of {tasks.Count}");
    await ProcessTask(currentTask);
    
    // Position is preserved
    if (needToSave)
    {
        await SaveState(tasks);
    }
}

// Advanced navigation
tasks.Last();        // Move to last item
tasks.MovePrevious(); // Move back
tasks.Reset();       // Reset to beginning

// Search and update position
var found = tasks.Find(t => t.Priority == "High");
if (found != null)
{
    // Current index now points to found item
    Console.WriteLine($"Found at index: {tasks.CurrentIndex}");
}

// Filtering creates new StateList
var highPriorityTasks = tasks.Where(t => t.Priority == "High");
```

#### Queue Management

```csharp
// Create queue from StateList
var queue = QueueData<WorkItem>.CreateFromStateList(
    stateList, 
    "ProcessingQueue",  // Category name
    Guid.NewGuid().ToString()  // Queue ID
);

// Save queue to Azure
await queue.SaveQueueAsync("accountName", "accountKey");

// Resume processing later (even after restart)
var savedQueue = await QueueData<WorkItem>.GetQueueAsync(
    queueId, "accountName", "accountKey"
);

if (savedQueue != null)
{
    Console.WriteLine($"Status: {savedQueue.ProcessingStatus}");
    Console.WriteLine($"Progress: {savedQueue.PercentComplete:F1}%");
    Console.WriteLine($"Last processed: {savedQueue.LastProcessedIndex}");
    
    // Continue processing
    while (savedQueue.Data.HasNext)
    {
        savedQueue.Data.MoveNext();
        await ProcessItem(savedQueue.Data.Current);
        
        // Periodic save for recovery
        if (savedQueue.Data.CurrentIndex % 10 == 0)
        {
            await savedQueue.SaveQueueAsync("accountName", "accountKey");
        }
    }
    
    // Cleanup when done
    await QueueData<WorkItem>.DeleteQueueAsync(
        queueId, "accountName", "accountKey"
    );
}

// Query multiple queues
var allQueues = await QueueData<WorkItem>.GetQueuesAsync(
    "ProcessingQueue", "accountName", "accountKey"
);

// Delete matching queues
await QueueData<WorkItem>.DeleteQueuesMatchingAsync(
    "accountName", "accountKey",
    q => q.PercentComplete == 100
);
```

### Error Handling

Enhanced error logging with automatic caller information capture.

```csharp
// Traditional error logging
try
{
    await ProcessData();
}
catch (Exception ex)
{
    var errorLog = new ErrorLogData(
        ex, 
        "Failed to process data", 
        ErrorCodeTypes.Error,
        customerId
    );
    await errorLog.LogErrorAsync("accountName", "accountKey");
}

// Enhanced with caller info
var error = ErrorLogData.CreateWithCallerInfo(
    "Validation failed", 
    ErrorCodeTypes.Warning,
    customerId
);
await error.LogErrorAsync("accountName", "accountKey");

// Using ILogger (recommended)
try
{
    await ProcessData();
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to process data for customer {CustomerId}", customerId);
}

// Cleanup old errors
await ErrorLogData.ClearOldDataAsync("accountName", "accountKey", daysOld: 60);

// Cleanup by type
await ErrorLogData.ClearOldDataByType(
    "accountName", "accountKey", 
    ErrorCodeTypes.Information, 
    daysOld: 7
);
```

---

## Advanced Features

### Hybrid Filtering

The library automatically optimizes queries for best performance:

```csharp
// This complex query is automatically optimized
var results = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" &&                    // Server-side
         c.Country == "US" &&                        // Server-side
         c.Name.ToLower().Contains("tech") &&        // Client-side
         c.Revenue > 1000000 &&                      // Server-side
         c.Tags.Contains("enterprise")               // Client-side
);

// The library will:
// 1. Execute server-side filters first to reduce data transfer
// 2. Apply client-side filters on the reduced dataset
// 3. Return the final filtered results
```

### Circuit Breaker Pattern

Automatic fallback when Azure is unavailable:

```csharp
// Logging automatically falls back to local storage
logger.LogInformation("This will go to Azure if available, local file if not");

// The circuit breaker opens after failures and periodically retries
// No manual intervention needed
```

### Progress Tracking

Monitor long-running operations:

```csharp
var progress = new Progress<BatchUpdateProgress>(p =>
{
    Console.WriteLine($"Batch {p.CompletedBatches}/{p.TotalBatches}");
    Console.WriteLine($"Items: {p.ProcessedItems}/{p.TotalItems}");
    Console.WriteLine($"Progress: {p.PercentComplete:F1}%");
    Console.WriteLine($"Current batch size: {p.CurrentBatchSize}");
});

await dataAccess.BatchUpdateListAsync(data, TableOperationType.InsertOrReplace, progress);
```

### Statistics & Monitoring

```csharp
// Logging statistics
var logStats = LoggingBackgroundService.Statistics;
Console.WriteLine($"Logs queued: {logStats.TotalLogsQueued}");
Console.WriteLine($"Logs written: {logStats.TotalLogsWritten}");
Console.WriteLine($"Success rate: {logStats.SuccessRate:F1}%");
Console.WriteLine($"Average latency: {logStats.AverageWriteLatency.TotalMilliseconds}ms");

// Session statistics
var sessionStats = SessionManager.GetStatistics();
Console.WriteLine($"Active sessions: {sessionStats.ActiveSessions}");
Console.WriteLine($"Total reads: {sessionStats.TotalReads}");
Console.WriteLine($"Write success rate: {sessionStats.WriteSuccessRate:F1}%");
```

---

## Migration Guide

### From v2.x to v3.0

Version 3.0 is fully backward compatible. Existing code continues to work while new features can be adopted incrementally.

#### Add Universal Logging

```csharp
// Old: Manual ErrorLogData
var error = new ErrorLogData(ex, "Error", ErrorCodeTypes.Error);
await error.LogErrorAsync(accountName, accountKey);

// New: ILogger integration
builder.Logging.AddAzureTableLogging(accountName, accountKey);
logger.LogError(ex, "Error occurred");
```

#### Add Session Management

```csharp
// Old: Manual Session class
using (var session = new Session(accountName, accountKey, sessionId))
{
    session["key"] = value;
    session.CommitData();
}

// New: Global SessionManager
SessionManager.Initialize(accountName, accountKey);
SessionManager.Current["key"] = value;  // Auto-commits
```

#### Automatic Cleanup

```csharp
// Old: Manual cleanup required
await ErrorLogData.ClearOldDataAsync(accountName, accountKey, 60);
session.CleanSessionData();

// New: Automatic with configuration
AzureTableLogging.Initialize(options =>
{
    options.EnableAutoCleanup = true;
    options.RetentionDays = 60;
});
```

#### Lambda Expressions

```csharp
// Old: String-based queries
var queryTerms = new List<DBQueryItem>
{
    new DBQueryItem 
    { 
        FieldName = "Status", 
        FieldValue = "Active", 
        HowToCompare = ComparisonTypes.eq 
    }
};
var results = dataAccess.GetCollection(queryTerms);

// New: Lambda expressions
var results = await dataAccess.GetCollectionAsync(
    x => x.Status == "Active"
);
```

---

## API Reference

### Core Classes

#### DataAccess<T>
- `ManageDataAsync(T entity, TableOperationType operation)` - CRUD operations
- `GetCollectionAsync(Expression<Func<T, bool>> predicate)` - Query with lambda
- `GetPagedCollectionAsync(...)` - Paginated queries
- `BatchUpdateListAsync(...)` - Batch operations

#### AzureBlobs
- `UploadFileAsync(...)` - Upload with tags
- `GetCollectionAsync(Expression<Func<BlobData, bool>> predicate)` - Search blobs
- `DownloadFilesAsync(...)` - Bulk download
- `DeleteMultipleBlobsAsync(...)` - Bulk delete

#### SessionManager
- `Current` - Access current session
- `Initialize(...)` - Setup sessions
- `FlushAsync()` - Force write pending changes
- `GetStatistics()` - Get session statistics

#### AzureTableLogging
- `Initialize(...)` - Setup logging
- `CreateLogger<T>()` - Create typed logger
- `ShutdownAsync()` - Flush and shutdown

### Extension Methods

#### ILogger Extensions
- `LogFromUI(...)` - Thread-safe UI logging
- `LogUserAction(...)` - Track user actions
- `LogProgress(...)` - Track long operations
- `LogHeartbeat(...)` - Service monitoring

#### IServiceCollection Extensions
- `AddAzureTableLogging(...)` - Add logging to DI
- `AddAzureTableSessions(...)` - Add sessions to DI

---

## Best Practices

### 1. Initialization
- Initialize logging and sessions once at application startup
- Use auto-discovery for credentials when possible
- Configure retention policies based on compliance requirements

### 2. Performance
- Reuse `DataAccess` instances per table
- Use batch operations for bulk data (100 items per batch)
- Let session batching optimize write performance (500ms delay)
- Use pagination for large result sets

### 3. Error Handling
- Use ILogger instead of direct ErrorLogData
- Configure appropriate log levels
- Set up retention by severity level
- Monitor statistics for issues

### 4. Security
- Never hardcode credentials
- Use environment variables or secure configuration
- Implement proper access controls
- Regular cleanup of sensitive data

### 5. Monitoring
- Check statistics regularly
- Monitor circuit breaker status
- Track session activity
- Review error logs periodically

---

## Troubleshooting

### Common Issues & Solutions

#### Logs not appearing in Azure
- **Check**: Credentials are correct
- **Verify**: Table creation permissions
- **Test**: Network connectivity to Azure
- **Review**: Minimum log level settings

#### Session data not persisting
- **Ensure**: `FlushAsync()` is called or wait for auto-batch (500ms)
- **Check**: Session timeout settings
- **Verify**: Proper initialization

#### High latency for operations
- **Expected**: 10-50ms for Azure Table reads
- **Consider**: Batch operations for writes
- **Use**: Pagination for large datasets
- **Enable**: Circuit breaker for resilience

#### Memory issues with large datasets
- **Use**: Pagination (`GetPagedCollectionAsync`)
- **Implement**: Batch processing
- **Consider**: StateList for position tracking
- **Monitor**: Memory usage during operations

#### Cleanup not running
- **Verify**: `EnableAutoCleanup = true`
- **Check**: `CleanupInterval` setting
- **Review**: Retention configuration
- **Monitor**: Cleanup statistics

---

## Performance Benchmarks

| Operation | Average Time | Notes |
|-----------|-------------|--------|
| Single Write | 30-50ms | Direct to Azure |
| Batched Write (100 items) | 45ms | Optimized chunking |
| Single Read | 10-50ms | Direct from Azure |
| Lambda Query (1000 items) | 100-200ms | Hybrid filtering |
| Log Write | <1ms | Batched background |
| Session Write | <1ms | Batched every 500ms |
| Session Read | 10-50ms | Direct Azure access |
| Blob Upload (5MB) | 200-500ms | With tags |
| Blob Search (by tags) | 50-100ms | Indexed search |

---

## Configuration Reference

### Complete appsettings.json

```json
{
  "Azure": {
    "TableStorageName": "myaccount",
    "TableStorageKey": "base64key=="
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "AzureTable": {
      "AccountName": "myaccount",
      "AccountKey": "base64key==",
      "TableName": "ApplicationLogs",
      "MinimumLevel": "Information",
      "BatchSize": 100,
      "FlushInterval": "00:00:02",
      "RetentionDays": 30,
      "EnableAutoCleanup": true,
      "CleanupInterval": "1.00:00:00",
      "CleanupTimeOfDay": "02:00:00",
      "RetentionByLevel": {
        "Trace": 7,
        "Debug": 14,
        "Information": 30,
        "Warning": 60,
        "Error": 90,
        "Critical": 180
      },
      "IncludeScopes": true,
      "EnableFallback": true,
      "MaxQueueSize": 10000
    }
  },
  "Sessions": {
    "AccountName": "myaccount",
    "AccountKey": "base64key==",
    "TableName": "AppSessionData",
    "SessionTimeout": "00:20:00",
    "StaleDataCleanupAge": "02:00:00",
    "BatchWriteDelay": "00:00:00.500",
    "MaxBatchSize": 100,
    "IdStrategy": "Auto",
    "EnableAutoCleanup": true,
    "CleanupInterval": "01:00:00",
    "AutoCommitInterval": "00:00:10",
    "TrackActivity": true
  }
}
```

---

## Support & Resources

- **Package**: [NuGet - ASCDataAccessLibrary](https://www.nuget.org/packages/ASCDataAccessLibrary)
- **Source**: [GitHub Repository](https://github.com/your-repo/ASCDataAccessLibrary)
- **Issues**: [GitHub Issues](https://github.com/your-repo/ASCDataAccessLibrary/issues)
- **Documentation**: This document and inline XML documentation
- **Version**: 3.0.0
- **License**: MIT
- **Authors**: O. Brown | M. Chukwuemeka
- **Company**: Answer Sales Calls Inc.
- **Support Email**: support@answersalescalls.com

---

## Changelog Summary

### Version 3.0.0 (Current - 2025)
- ✨ Universal ILogger implementation for all .NET app types
- ✨ Optimized session management without Redis dependency
- ✨ Lambda expression support with hybrid filtering
- ✨ Automatic cleanup with configurable retention
- ✨ Enhanced blob storage with tag-based search
- ✨ Circuit breaker pattern for resilience
- ✨ Comprehensive statistics and monitoring
- ✨ Full backward compatibility

### Version 2.5.0
- StateList with full IList<T> implementation
- Enhanced QueueData with StateList integration
- Improved error logging with caller information
- Performance optimizations

### Version 2.1.0
- DynamicEntity for runtime schemas
- IDynamicProperties interface
- TableEntityTypeCache optimizations

---

*Documentation Version: 3.0.0*  
*Last Updated: January 2025*  
*© 2025 Answer Sales Calls Inc. All Rights Reserved*