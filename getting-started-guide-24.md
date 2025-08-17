# ASCDataAccessLibrary v2.4 Documentation

## What's New in Version 2.4

### Major Features
- **Enhanced Queue System**: `QueueData<T>` now directly integrates with `StateList<T>` for automatic position tracking
- **Simplified Queue API**: Queue methods now return `QueueData<T>` objects instead of `StateList<T>` for consistency
- **StateList Improvements**: Full `IList<T>` implementation with preserved position through serialization
- **Better Queue State Management**: Automatic computation of processing status and progress from StateList position

### Key Improvements
- Direct `Data` property on `QueueData<T>` - no more `GetData()`/`PutData()` methods needed
- Queue position automatically preserved through Azure Table Storage serialization
- Computed properties (`ProcessingStatus`, `PercentComplete`) derived from live StateList state
- Cleaner API for queue operations - work with queue objects, not just data

### Breaking Changes from v2.3
- `QueueData<T>.GetQueueAsync()` now returns `QueueData<T>` instead of `StateList<T>`
- Removed `GetData()`/`PutData()` methods - use `Data` property directly
- Removed `SaveProgressAsync()` - use `SaveQueueAsync()` on the queue instance

---

## Table of Contents

1. [Installation](#installation)
2. [Basic Concepts](#basic-concepts)
3. [Queue Management (Enhanced in v2.4)](#queue-management-enhanced-in-v24)
4. [StateList - Position-Aware Collections](#statelist---position-aware-collections)
5. [Working with Data Access](#working-with-data-access)
6. [Dynamic Entities](#dynamic-entities)
7. [Session Management](#session-management)
8. [Error Logging](#error-logging)
9. [Blob Storage Operations](#blob-storage-operations)
10. [Advanced Features](#advanced-features)
11. [Migration Guide](#migration-guide)
12. [Best Practices](#best-practices)

---

## Installation

Install the package from NuGet:

```bash
Install-Package ASCDataAccessLibrary -Version 2.4.0
```

Or using the .NET CLI:

```bash
dotnet add package ASCDataAccessLibrary --version 2.4.0
```

---

## Basic Concepts

### Core Components

- **DataAccess<T>**: Generic class for CRUD operations with Azure Table Storage
- **TableEntityBase**: Base class for all table entities with automatic field chunking
- **ITableExtra**: Interface requiring `TableReference` and `GetIDValue()` implementation
- **IDynamicProperties**: Interface for entities supporting runtime properties
- **DynamicEntity**: Pre-built implementation for schema-less operations
- **QueueData<T>**: State-managed queue with position tracking (Enhanced in v2.4)
- **StateList<T>**: Position-aware list that maintains state through serialization (Enhanced in v2.4)
- **Session**: Advanced session management for stateful applications
- **ErrorLogData**: Comprehensive error logging with caller information
- **AzureBlobs**: Blob storage operations with tag-based indexing

### Architecture Overview

```
┌─────────────────────────────────────┐
│         Your Application            │
├─────────────────────────────────────┤
│     Queue Management (v2.4)         │
│     QueueData<T> ←→ StateList<T>    │
├─────────────────────────────────────┤
│     Strongly-Typed Entities         │
│            ↕                        │
│     TableEntityBase                 │
│            ↕                        │
│     ITableExtra                     │
├─────────────────────────────────────┤
│      Dynamic Entities               │
│            ↕                        │
│      DynamicEntity                  │
│            ↕                        │
│     IDynamicProperties              │
├─────────────────────────────────────┤
│      DataAccess<T>                  │
├─────────────────────────────────────┤
│      Azure Table Storage            │
└─────────────────────────────────────┘
```

---

## Queue Management (Enhanced in v2.4)

### Overview

The Queue system in v2.4 provides stateful, resumable processing of work items with automatic position tracking. Perfect for:
- Long-running batch operations
- Resumable data processing
- Work distribution systems
- ETL pipelines
- Migration tasks

### Key Concepts

- **One Queue = One StateList = One Logical Unit of Work**
- Position automatically preserved through interruptions
- Progress computed from StateList position
- Queues are first-class objects (not just data containers)

### Creating and Saving Queues

```csharp
// Create from a list of items
var items = new List<Order> 
{
    new Order { Id = "001", Amount = 100 },
    new Order { Id = "002", Amount = 200 },
    new Order { Id = "003", Amount = 150 }
};

// Create queue with automatic ID
var queue = QueueData<Order>.CreateFromList(items, "OrderProcessing");

// Or create with specific ID
var queue = QueueData<Order>.CreateFromList(items, "OrderProcessing", "batch_2025_01");

// Save to Azure Table Storage
await queue.SaveQueueAsync(accountName, accountKey);

Console.WriteLine($"Queue Status: {queue.ProcessingStatus}"); // "Not Started"
Console.WriteLine($"Total Items: {queue.TotalItemCount}");   // 3
```

### Retrieving and Processing Queues

```csharp
public async Task ProcessQueueAsync(string queueId)
{
    // Retrieve the queue (not just the data)
    var queue = await QueueData<Order>.GetQueueAsync(queueId, accountName, accountKey);
    
    if (queue == null)
    {
        Console.WriteLine("Queue not found");
        return;
    }
    
    Console.WriteLine($"Starting: {queue.ProcessingStatus}");
    Console.WriteLine($"Progress: {queue.PercentComplete:F1}%");
    
    // Process items using StateList navigation
    while (queue.Data.MoveNext())
    {
        var currentOrder = queue.Data.Current;
        
        try
        {
            // Process the current item
            await ProcessOrder(currentOrder);
            
            // Save progress every 5 items or at 50% completion
            if (queue.Data.CurrentIndex % 5 == 0 || queue.PercentComplete >= 50)
            {
                Console.WriteLine($"Checkpoint: {queue.ProcessingStatus}");
                await queue.SaveQueueAsync(accountName, accountKey);
            }
        }
        catch (Exception ex)
        {
            // Save current position for resume capability
            Console.WriteLine($"Error at position {queue.Data.CurrentIndex}: {ex.Message}");
            await queue.SaveQueueAsync(accountName, accountKey);
            throw;
        }
    }
    
    // Delete queue after successful completion
    await QueueData<Order>.DeleteQueueAsync(queueId, accountName, accountKey);
    Console.WriteLine("Processing complete");
}
```

### Resuming Interrupted Processing

```csharp
public async Task ResumeProcessingAsync(string queueId)
{
    var queue = await QueueData<Order>.GetQueueAsync(queueId, accountName, accountKey);
    
    if (queue == null)
    {
        Console.WriteLine("No queue to resume");
        return;
    }
    
    // Queue automatically maintains position
    Console.WriteLine($"Resuming from: {queue.ProcessingStatus}");
    Console.WriteLine($"Progress: {queue.PercentComplete:F1}% complete");
    Console.WriteLine($"Current Position: {queue.Data.CurrentIndex + 1} of {queue.Data.Count}");
    
    // Continue from where we left off
    while (queue.Data.MoveNext())
    {
        var item = queue.Data.Current;
        await ProcessOrder(item);
        
        // Periodic checkpoint
        if (queue.Data.CurrentIndex % 10 == 0)
        {
            await queue.SaveQueueAsync(accountName, accountKey);
        }
    }
    
    // Cleanup
    await QueueData<Order>.DeleteQueueAsync(queueId, accountName, accountKey);
}
```

### Queue Status Properties

```csharp
var queue = await QueueData<Order>.GetQueueAsync(queueId, accountName, accountKey);

// Computed properties (derived from StateList position)
string status = queue.ProcessingStatus;  
// Returns: "Not Started", "In Progress (Item X of Y)", "Completed", or "Empty"

double progress = queue.PercentComplete;  
// Returns: 0-100 based on CurrentIndex

int total = queue.TotalItemCount;        
// Returns: Count of items in StateList

int lastProcessed = queue.LastProcessedIndex;  
// Returns: Current position in StateList
```

### Batch Queue Operations

```csharp
// Get all queues in a category
var allQueues = await QueueData<Order>.GetQueuesAsync("OrderProcessing", accountName, accountKey);
Console.WriteLine($"Found {allQueues.Count} queues");

foreach (var q in allQueues)
{
    Console.WriteLine($"Queue {q.QueueID}: {q.ProcessingStatus} - {q.PercentComplete:F1}%");
}

// Delete multiple queues
var queueIds = new List<string> { "batch_001", "batch_002", "batch_003" };
int deleted = await QueueData<Order>.DeleteQueuesAsync(queueIds, accountName, accountKey);

// Delete queues matching a condition
int deletedOld = await QueueData<Order>.DeleteQueuesMatchingAsync(
    accountName, 
    accountKey,
    q => q.Timestamp < DateTime.UtcNow.AddDays(-7)
);

// Get and delete (for migration scenarios)
var queue = await QueueData<Order>.GetAndDeleteQueueAsync(queueId, accountName, accountKey);
if (queue != null)
{
    // Process the queue data
    ProcessMigrationData(queue.Data);
}
```

---

## StateList - Position-Aware Collections

### Overview

`StateList<T>` is an enhanced List<T> that maintains its current position through serialization. It's the foundation of the queue system's resumability.

### Key Features

- Maintains `CurrentIndex` through serialization/deserialization
- Full `IList<T>` implementation
- Navigation methods (`MoveNext`, `MovePrevious`, `First`, `Last`)
- Direct access to current item via `Current` property
- String-based indexer for searches

### Basic Usage

```csharp
// Create a StateList
var list = new StateList<string> { "First", "Second", "Third" };

// Navigate through items
while (list.MoveNext())
{
    Console.WriteLine($"Processing: {list.Current} at position {list.CurrentIndex}");
}

// Check position
if (list.HasNext)
{
    list.MoveNext();
}

// Move to specific positions
list.First();   // CurrentIndex = 0
list.Last();    // CurrentIndex = Count - 1
list.Reset();   // CurrentIndex = -1 (before first)

// Access current item info
var current = list.Current;  // Gets item at CurrentIndex
var peek = list.Peek;        // Gets (Data, Index) tuple
```

### Advanced StateList Operations

```csharp
// Add items with position management
var stateList = new StateList<Order>();
stateList.Add(order1, setCurrent: true);  // Adds and sets as current
stateList.AddRange(moreOrders, setCurrentToLast: true);

// Find and set as current
var found = stateList.Find(o => o.Id == "ORD-123");  // Sets as current if found

// String-based search (updates CurrentIndex)
var order = stateList["ORD-456"];  // Case-insensitive search by ToString()

// Filtering (returns new StateList)
var activeOrders = stateList.Where(o => o.Status == "Active");
var uniqueOrders = stateList.Except(processedOrders);

// Sorting (maintains current item)
stateList.Sort((a, b) => a.Date.CompareTo(b.Date));
```

### Serialization and Position Preservation

```csharp
// StateList position is automatically preserved
var original = new StateList<string> { "A", "B", "C", "D" };
original.MoveNext();  // CurrentIndex = 0
original.MoveNext();  // CurrentIndex = 1

// Serialize (using QueueData)
var queue = QueueData<string>.CreateFromStateList(original, "Test");
await queue.SaveQueueAsync(accountName, accountKey);

// Retrieve later
var retrieved = await QueueData<string>.GetQueueAsync(queue.QueueID, accountName, accountKey);
Console.WriteLine(retrieved.Data.CurrentIndex);  // Still 1
Console.WriteLine(retrieved.Data.Current);       // "B"
```

---

## Working with Data Access

### Creating Strongly-Typed Entities

```csharp
public class Customer : TableEntityBase, ITableExtra
{
    // Primary key - stored in PartitionKey
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    // Unique identifier - stored in RowKey
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    // Custom properties
    public string Name { get; set; }
    public string Email { get; set; }
    public string LargeDataField { get; set; } // Auto-chunked if >32KB
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    
    // ITableExtra implementation
    public string TableReference => "Customers";
    public string GetIDValue() => this.CustomerId;
}
```

### CRUD Operations

```csharp
var dataAccess = new DataAccess<Customer>(accountName, accountKey);

// Create/Update
var customer = new Customer
{
    CompanyId = "company123",
    CustomerId = "cust456",
    Name = "Acme Corporation",
    Email = "contact@acme.com",
    IsActive = true,
    CreatedDate = DateTime.UtcNow
};
await dataAccess.ManageDataAsync(customer);

// Retrieve by RowKey
var retrieved = await dataAccess.GetRowObjectAsync("cust456");

// Retrieve with lambda (hybrid filtering)
var activeCustomers = await dataAccess.GetCollectionAsync(c => 
    c.IsActive && c.CreatedDate > DateTime.Today.AddDays(-30));

// Complex queries with automatic optimization
var results = await dataAccess.GetCollectionAsync(c =>
    c.Email.Contains("@acme.com") || c.Name.StartsWith("Test"));

// Delete
await dataAccess.ManageDataAsync(customer, TableOperationType.Delete);
```

### Pagination Support

```csharp
// Get first page
var page1 = await dataAccess.GetPagedCollectionAsync(pageSize: 50);
Console.WriteLine($"Page 1: {page1.Items.Count} items");

// Get next page
if (page1.HasMore)
{
    var page2 = await dataAccess.GetPagedCollectionAsync(
        pageSize: 50, 
        continuationToken: page1.ContinuationToken);
}

// Paginate with filtering
var pagedResults = await dataAccess.GetPagedCollectionAsync(
    c => c.IsActive && c.CompanyId == "company123",
    pageSize: 25);
```

### Batch Operations

```csharp
// Batch insert/update with progress tracking
var customers = GenerateCustomers(1000);

var progress = new Progress<BatchUpdateProgress>(p =>
    Console.WriteLine($"Progress: {p.PercentComplete:F1}% ({p.ProcessedItems}/{p.TotalItems})"));

var result = await dataAccess.BatchUpdateListAsync(
    customers, 
    TableOperationType.InsertOrReplace, 
    progress);

Console.WriteLine($"Success: {result.SuccessfulItems}, Failed: {result.FailedItems}");

// Batch delete
var toDelete = await dataAccess.GetCollectionAsync(c => !c.IsActive);
await dataAccess.BatchUpdateListAsync(toDelete, TableOperationType.Delete);
```

---

## Dynamic Entities

### Overview

Dynamic entities allow schema-less table operations, perfect for:
- Multi-tenant applications
- Integration scenarios
- Rapid prototyping
- Evolving schemas

### Creating and Using Dynamic Entities

```csharp
// Create with runtime-defined table
var entity = new DynamicEntity("TenantData", "tenant123", "record456");

// Add properties dynamically
entity["CustomerName"] = "John Doe";
entity["OrderDate"] = DateTime.UtcNow;
entity["Amount"] = 1234.56;
entity["IsProcessed"] = true;
entity["Tags"] = JsonConvert.SerializeObject(new[] { "urgent", "vip" });

// Alternative syntax
entity.SetProperty("Status", "Active");
entity.SetProperty("Priority", 1);

// Save
var dataAccess = new DataAccess<DynamicEntity>(accountName, accountKey);
await dataAccess.ManageDataAsync(entity);
```

### Retrieving Dynamic Data

```csharp
// Retrieve and access properties
var retrieved = await dataAccess.GetRowObjectAsync("record456");

string name = retrieved.GetProperty<string>("CustomerName");
DateTime date = retrieved.GetProperty<DateTime>("OrderDate");
decimal amount = retrieved.GetProperty<decimal>("Amount");

// Check property existence
if (retrieved.HasProperty("Status"))
{
    var status = retrieved.GetProperty("Status");
}

// Get all properties
var allProps = retrieved.GetAllProperties();
foreach (var prop in allProps)
{
    Console.WriteLine($"{prop.Key}: {prop.Value}");
}
```

---

## Session Management

### Creating and Using Sessions

```csharp
// Create session asynchronously (recommended)
var session = await Session.CreateAsync(accountName, accountKey, sessionId);

// Store session values
session["UserName"] = "John Doe";
session["LoginTime"] = DateTime.UtcNow.ToString();
session["CartCount"] = "5";

// Retrieve values
var userName = session["UserName"]?.Value;

// Commit changes
await session.CommitDataAsync();

// Auto-commit with using statement
await using (var session = await Session.CreateAsync(accountName, accountKey, sessionId))
{
    session["LastAction"] = "ViewProduct";
    // Automatically committed on disposal
}
```

### Session Maintenance

```csharp
// Find and process stale sessions
var staleSessions = await session.GetStaleSessionsAsync();
foreach (var staleId in staleSessions)
{
    await ProcessAbandonedSession(staleId);
}

// Clean old session data (older than 2 hours by default)
await session.CleanSessionDataAsync();

// Restart session (clear all data)
await session.RestartSessionAsync();
```

---

## Error Logging

### Logging Errors with Automatic Context

```csharp
try
{
    // Your code here
    await ProcessCustomerData(customerId);
}
catch (Exception ex)
{
    // Automatic caller information capture
    var errorLog = ErrorLogData.CreateWithCallerInfo(
        $"Failed to process customer: {ex.Message}",
        ErrorCodeTypes.Error,
        customerId);
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}
```

### Error Severity Levels

```csharp
// Different severity levels
ErrorLogData.CreateWithCallerInfo("User logged in", ErrorCodeTypes.Information, userId);
ErrorLogData.CreateWithCallerInfo("Low memory warning", ErrorCodeTypes.Warning);
ErrorLogData.CreateWithCallerInfo("Database connection failed", ErrorCodeTypes.Error);
ErrorLogData.CreateWithCallerInfo("Security breach detected", ErrorCodeTypes.Critical);
```

### Error Log Maintenance

```csharp
// Clear old error logs
await ErrorLogData.ClearOldDataAsync(accountName, accountKey, daysOld: 30);

// Clear specific error types
await ErrorLogData.ClearOldDataByType(
    accountName, 
    accountKey, 
    ErrorCodeTypes.Information, 
    daysOld: 7);
```

---

## Blob Storage Operations

### Upload Operations

```csharp
var azureBlobs = new AzureBlobs(accountName, accountKey, "my-container");

// Upload with index tags (searchable)
var uri = await azureBlobs.UploadFileAsync(
    "path/to/document.pdf",
    indexTags: new Dictionary<string, string>
    {
        { "category", "contracts" },
        { "year", "2025" },
        { "client", "acme" }
    },
    metadata: new Dictionary<string, string>
    {
        { "uploadedBy", "john.doe" },
        { "department", "legal" }
    });

// Upload stream
using var stream = File.OpenRead("data.csv");
await azureBlobs.UploadStreamAsync(
    stream, 
    "reports/data.csv",
    contentType: "text/csv");

// Batch upload
var files = new[]
{
    new BlobUploadInfo 
    { 
        FilePath = "report1.pdf",
        IndexTags = new Dictionary<string, string> { { "type", "monthly" } }
    },
    new BlobUploadInfo 
    { 
        FilePath = "report2.pdf",
        IndexTags = new Dictionary<string, string> { { "type", "quarterly" } }
    }
};
var results = await azureBlobs.UploadMultipleFilesAsync(files);
```

### Search and Retrieval

```csharp
// Search by tags using lambda
var contracts = await azureBlobs.GetCollectionAsync(b => 
    b.Tags["category"] == "contracts" && 
    b.Tags["year"] == "2025");

// Search by tag query
var results = await azureBlobs.SearchBlobsByTagsAsync(
    "category = 'contracts' AND client = 'acme'");

// Search with multiple criteria
var filtered = await azureBlobs.SearchBlobsAsync(
    searchText: "report",
    tagFilters: new Dictionary<string, string> { { "type", "monthly" } },
    startDate: DateTime.Today.AddMonths(-3),
    endDate: DateTime.Today);

// Download files
await azureBlobs.DownloadFileAsync("document.pdf", @"C:\Downloads\document.pdf");

// Download matching files
var downloaded = await azureBlobs.DownloadFilesAsync(
    b => b.Tags["category"] == "contracts" && b.UploadDate > DateTime.Today.AddDays(-7),
    @"C:\Downloads\Contracts");
```

### Blob Management

```csharp
// Update tags
await azureBlobs.UpdateBlobTagsAsync("document.pdf", 
    new Dictionary<string, string>
    {
        { "status", "approved" },
        { "reviewer", "jane.smith" }
    });

// Delete blobs
await azureBlobs.DeleteBlobAsync("old-file.pdf");

// Delete multiple blobs by criteria
await azureBlobs.DeleteMultipleBlobsAsync(b => 
    b.Tags["temp"] == "true" && b.UploadDate < DateTime.Today.AddDays(-1));
```

---

## Advanced Features

### Hybrid Filtering

The library automatically optimizes lambda expressions for best performance:

```csharp
// Server-side operations (fast)
var results = await dataAccess.GetCollectionAsync(x => 
    x.Status == "Active" && 
    x.CreatedDate > DateTime.Today.AddDays(-30) &&
    x.Amount >= 1000);

// Complex operations use hybrid filtering (server + client)
// Server filters what it can, client handles the rest
var companies = await dataAccess.GetCollectionAsync(c => 
    c.CompanyName.ToLower().Contains("tech") ||  // Client-side
    c.Status == "Active");                        // Server-side

// The library automatically determines the optimal approach
```

### Large Data Handling

```csharp
public class Document : TableEntityBase, ITableExtra
{
    public string DocumentId { get; set; }
    
    // Automatically chunked if > 32KB
    public string Content { get; set; }
    
    // No special handling needed
    public string LargeJsonData { get; set; }
    
    public string TableReference => "Documents";
    public string GetIDValue() => DocumentId;
}

// Use normally - chunking is automatic
var doc = new Document
{
    DocumentId = "doc123",
    Content = veryLargeString,  // Can be > 32KB
    LargeJsonData = hugeJsonString  // Automatically handled
};

await dataAccess.ManageDataAsync(doc);
```

### Performance Optimizations

```csharp
// 1. Reuse DataAccess instances
private readonly DataAccess<Customer> _customerAccess = 
    new DataAccess<Customer>(accountName, accountKey);

// 2. Use batch operations for multiple updates
await _customerAccess.BatchUpdateListAsync(customers, TableOperationType.InsertOrReplace);

// 3. Use pagination for large datasets
var page = await _customerAccess.GetPagedCollectionAsync(pageSize: 100);

// 4. Use appropriate filtering
// Good: Server-side filtering
var active = await _customerAccess.GetCollectionAsync(c => c.Status == "Active");

// Less efficient: Client-side filtering
var containing = await _customerAccess.GetCollectionAsync(c => c.Name.Contains("test"));
```

---

## Migration Guide

### From v2.3 to v2.4

#### Queue System Changes

**Old (v2.3):**
```csharp
// Retrieved StateList directly
var stateList = await QueueData<T>.GetQueueAsync(queueId, accountName, accountKey);

// Had to recreate queue for saving
var queue = QueueData<T>.CreateFromStateList(stateList, "category", queueId);
await queue.SaveProgressAsync(stateList, accountName, accountKey);

// Used GetData()/PutData()
var data = queue.GetData();
queue.PutData(updatedData);
```

**New (v2.4):**
```csharp
// Retrieve queue object directly
var queue = await QueueData<T>.GetQueueAsync(queueId, accountName, accountKey);

// Work with queue.Data (StateList)
while (queue.Data.MoveNext())
{
    ProcessItem(queue.Data.Current);
}

// Save directly on queue instance
await queue.SaveQueueAsync(accountName, accountKey);

// Direct property access
var stateList = queue.Data;  // No GetData() needed
```

#### StateList Enhancements

**New capabilities in v2.4:**
```csharp
// Full IList<T> implementation
stateList.AddRange(items);
stateList.InsertRange(0, newItems);
stateList.RemoveAll(x => x.Status == "Cancelled");

// Enhanced navigation
var current = stateList.Current;  // Direct access
var (item, index) = stateList.Peek.Value;  // Tuple access

// Sorting maintains position
stateList.Sort((a, b) => a.Priority.CompareTo(b.Priority));
```

### Backward Compatibility

Most v2.3 code continues to work, but consider updating:

1. **Update queue retrieval** to work with `QueueData<T>` objects
2. **Remove** `GetData()`/`PutData()` calls - use `Data` property
3. **Remove** `SaveProgressAsync()` - use `SaveQueueAsync()`
4. **Leverage** new StateList capabilities for cleaner code

---

## Best Practices

### Queue Management

1. **Always save progress periodically**
```csharp
// Save every N items or at percentage milestones
if (queue.Data.CurrentIndex % 10 == 0 || queue.PercentComplete >= 50)
{
    await queue.SaveQueueAsync(accountName, accountKey);
}
```

2. **Use meaningful queue IDs and categories**
```csharp
// Good: Descriptive and organized
var queue = QueueData<T>.CreateFromList(items, "CustomerImport", $"batch_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

// Avoid: Generic names
var queue = QueueData<T>.CreateFromList(items, "Queue", "q1");
```

3. **Handle interruptions gracefully**
```csharp
try
{
    await ProcessQueue(queue);
}
catch (Exception ex)
{
    // Save position for resume
    await queue.SaveQueueAsync(accountName, accountKey);
    await LogError(ex, $"Queue {queue.QueueID} interrupted at position {queue.Data.CurrentIndex}");
    throw;
}
```

### Performance

1. **Batch operations over individual updates**
```csharp
// Good: Single batch operation
await dataAccess.BatchUpdateListAsync(entities);

// Avoid: Multiple individual operations
foreach (var entity in entities)
{
    await dataAccess.ManageDataAsync(entity);
}
```

2. **Use server-side filtering when possible**
```csharp
// Efficient: Server-side
var results = await dataAccess.GetCollectionAsync(x => x.Status == "Active");

// Less efficient: Forces client-side
var results = await dataAccess.GetCollectionAsync(x => x.Name.ToLower().Contains("test"));
```

3. **Appropriate pagination**
```csharp
// Good: Process in pages
var token = null;
do
{
    var page = await dataAccess.GetPagedCollectionAsync(100, token);
    await ProcessPage(page.Items);
    token = page.ContinuationToken;
} while (token != null);
```

### Error Handling

1. **Use structured error logging**
```csharp
var error = ErrorLogData.CreateWithCallerInfo(
    $"Queue processing failed: {ex.Message}",
    ErrorCodeTypes.Error,
    customerId);
await error.LogErrorAsync(accountName, accountKey);
```

2. **Clean up old logs periodically**
```csharp
// Run as scheduled job
await ErrorLogData.ClearOldDataAsync(accountName, accountKey, daysOld: 30);
```

### Table Design

1. **Choose appropriate partition keys**
   - Balance between query performance and scalability
   - Consider access patterns

2. **Use meaningful row keys**
   - Make them human-readable when possible
   - Consider natural vs. generated IDs

3. **Leverage dynamic entities for flexibility**
   - Multi-tenant scenarios
   - Rapidly evolving schemas
   - Integration points

---

## Troubleshooting

### Common Issues and Solutions

**Queue not resuming from correct position:**
- Ensure you're saving the queue after position changes
- Verify the StateList is being properly serialized

**Large data causing errors:**
- TableEntityBase handles chunking automatically
- Ensure your entity inherits from TableEntityBase

**Lambda expressions not filtering correctly:**
- Complex operations use hybrid filtering (expected behavior)
- Check the operation type (server vs. client)

**Batch operations failing:**
- Azure limit is 100 operations per batch (handled automatically)
- Ensure all entities in batch have same PartitionKey

**Dynamic entity properties not persisting:**
- Check property names are valid (alphanumeric + underscore)
- Avoid reserved names (PartitionKey, RowKey, Timestamp, ETag)

---

## API Reference Summary

### Key Classes

| Class | Purpose |
|-------|---------|
| `DataAccess<T>` | CRUD operations for Azure Table Storage |
| `QueueData<T>` | State-managed queue with position tracking |
| `StateList<T>` | Position-aware list with serialization |
| `DynamicEntity` | Schema-less entity for flexible storage |
| `Session` | Session state management |
| `ErrorLogData` | Structured error logging |
| `AzureBlobs` | Blob storage with tag-based search |

### Key Methods

**QueueData<T>:**
- `CreateFromList()` - Create queue from list
- `CreateFromStateList()` - Create queue from StateList
- `SaveQueueAsync()` - Save/update queue state
- `GetQueueAsync()` - Retrieve queue by ID
- `GetQueuesAsync()` - Get all queues in category
- `DeleteQueueAsync()` - Delete single queue
- `DeleteQueuesAsync()` - Delete multiple queues

**StateList<T>:**
- `MoveNext()` - Advance position
- `MovePrevious()` - Move back
- `First()`, `Last()`, `Reset()` - Position control
- `Current` - Get current item
- `CurrentIndex` - Get current position
- All standard `IList<T>` methods

**DataAccess<T>:**
- `ManageDataAsync()` - Insert/Update/Delete
- `GetCollectionAsync()` - Query with lambda
- `GetPagedCollectionAsync()` - Paginated retrieval
- `BatchUpdateListAsync()` - Batch operations
- `GetRowObjectAsync()` - Get single entity

---

## Support and Resources

- **NuGet Package**: [ASCDataAccessLibrary](https://www.nuget.org/packages/ASCDataAccessLibrary)
- **Source Code**: [GitHub Repository](https://github.com/your-repo/ASCDataAccessLibrary)
- **Issues**: [GitHub Issues](https://github.com/your-repo/ASCDataAccessLibrary/issues)
- **Documentation**: This document
- **Version**: 2.4.0
- **License**: MIT

---

## Changelog

### Version 2.4.0 (2025)
- Enhanced `QueueData<T>` with direct StateList integration
- Simplified queue API - returns queue objects instead of data
- Improved `StateList<T>` with full IList implementation
- Automatic position preservation through serialization
- Computed queue status properties from live state
- Removed redundant `GetData()`/`PutData()` methods

### Version 2.3.0
- Added `QueueData<T>` for state management
- Introduced `StateList<T>` for position tracking
- Queue checkpoint and resume capabilities

### Version 2.2.0
- Performance optimizations with type caching
- Enhanced batch processing
- Improved error handling

### Version 2.1.0
- Added `DynamicEntity` for runtime schemas
- Introduced `IDynamicProperties` interface
- Implemented `TableEntityTypeCache` for performance

### Version 2.0.0
- Lambda expression support
- Hybrid server/client filtering
- Pagination support
- Async-first pattern
- Blob storage with tag indexing

### Version 1.0.0
- Initial release
- Basic CRUD operations
- Session management
- Error logging
- Field chunking support

---

*Last Updated: January 2025*  
*Version: 2.4.0*  
*Copyright © 2025 - All Rights Reserved*