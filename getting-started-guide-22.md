# ASCDataAccessLibrary v2.2 Documentation

## What's New in Version 2.2

### Major Features
- **StateList<T>**: Position-aware collection that maintains current index through serialization
- **Enhanced QueueData<T>**: Advanced queue management with StateList integration
- **Position-Aware Paging**: Intelligent pagination that remembers processing position
- **Progress Tracking**: Built-in progress monitoring for queue processing
- **Batch Queue Operations**: Optimized batch processing with checkpoint support

### Key Improvements
- `StateList<T>` class for stateful collection management
- Enhanced `QueueData<T>` with position tracking and paging
- Progress summary and monitoring capabilities
- Optimized batch operations for queue processing
- Full backward compatibility with v2.1

---

## Table of Contents

1. [Installation](#installation)
2. [Basic Concepts](#basic-concepts)
3. [StateList - Position-Aware Collections (NEW)](#statelist---position-aware-collections-new)
4. [Enhanced Queue Management (NEW)](#enhanced-queue-management-new)
5. [Dynamic Entities](#dynamic-entities)
6. [Working with Data Access](#working-with-data-access)
7. [Session Management](#session-management)
8. [Error Logging](#error-logging)
9. [Blob Storage Operations](#blob-storage-operations)
10. [Advanced Features](#advanced-features)
11. [Performance Optimizations](#performance-optimizations)
12. [Migration Guide](#migration-guide)
13. [Best Practices](#best-practices)

---

## Installation

Install the package from NuGet:

```bash
Install-Package ASCDataAccessLibrary -Version 2.2.0
```

Or using the .NET CLI:

```bash
dotnet add package ASCDataAccessLibrary --version 2.2.0
```

---

## Basic Concepts

### Core Components

- **DataAccess<T>**: Generic class for CRUD operations with Azure Table Storage
- **TableEntityBase**: Base class for all table entities with automatic field chunking
- **StateList<T>**: Position-aware collection that maintains state through serialization (NEW in v2.2)
- **QueueData<T>**: Enhanced queue management with StateList integration (Enhanced in v2.2)
- **DynamicEntity**: Pre-built implementation for schema-less operations
- **Session**: Advanced session management for stateful applications
- **ErrorLogData**: Comprehensive error logging with caller information
- **AzureBlobs**: Blob storage operations with tag-based indexing

### Architecture Overview

```
┌─────────────────────────────────────┐
│         Your Application            │
├─────────────────────────────────────┤
│     StateList<T> Collections        │ ← NEW: Position-aware
│            ↓                        │
│     QueueData<T> Management         │ ← Enhanced with paging
│            ↓                        │
│     Strongly-Typed Entities         │
│            ↓                        │
│     TableEntityBase                 │
│            ↓                        │
│     ITableExtra                     │
├─────────────────────────────────────┤
│      Dynamic Entities               │
│            ↓                        │
│      DynamicEntity                  │
│            ↓                        │
│     IDynamicProperties              │
├─────────────────────────────────────┤
│      DataAccess<T>                  │
├─────────────────────────────────────┤
│      Azure Table Storage            │
└─────────────────────────────────────┘
```

---

## StateList - Position-Aware Collections (NEW)

### Overview

StateList<T> is a powerful collection that maintains its current position through serialization/deserialization. Perfect for:
- Processing large datasets with interruption recovery
- Maintaining state across application restarts
- Queue processing with position tracking
- Paginated data processing with resume capability

### Creating and Using StateList

```csharp
// Create a StateList from existing data
var items = new List<string> { "Item1", "Item2", "Item3", "Item4", "Item5" };
var stateList = new StateList<string>(items);

// Set description for context
stateList.Description = "Customer Processing Queue";

// Navigate through the list
stateList.First();  // Move to first item
Console.WriteLine($"Current: {stateList.Current}"); // "Item1"

stateList.MoveNext(); // Move forward
Console.WriteLine($"Current: {stateList.Current}"); // "Item2"
Console.WriteLine($"Position: {stateList.CurrentIndex}"); // 1

// Check navigation capabilities
if (stateList.HasNext)
    stateList.MoveNext();

if (stateList.HasPrevious)
    stateList.MovePrevious();

// Get current item with index
var peek = stateList.Peek; // Returns (Data: "Item2", Index: 1)
```

### Position Preservation Through Serialization

```csharp
// Process part of a list
var stateList = new StateList<Order>(orders);
stateList.Description = "Order Processing";

// Process some items
while (stateList.CurrentIndex < 50 && stateList.MoveNext())
{
    ProcessOrder(stateList.Current);
}

// Save state (position is preserved)
string json = JsonConvert.SerializeObject(stateList);
await SaveToStorage(json);

// Later: Restore and continue from last position
var restored = JsonConvert.DeserializeObject<StateList<Order>>(json);
Console.WriteLine($"Resuming from position {restored.CurrentIndex}");

// Continue processing from where we left off
while (restored.MoveNext())
{
    ProcessOrder(restored.Current);
}
```

### StateList Advanced Features

```csharp
// String-based indexer for quick searches
var stateList = new StateList<Product>(products);
var product = stateList["ProductName123"]; // Finds by ToString() comparison

// Add items with state management
stateList.Add(newProduct, setCurrent: true); // Adds and sets as current

// Add range with position control
stateList.AddRange(moreProducts, 
    setCurrentToFirst: false, 
    setCurrentToLast: true); // Sets current to last added

// Find and set as current
var found = stateList.Find(p => p.Price > 100); // Sets found item as current

// Filter to new StateList
var expensive = stateList.Where(p => p.Price > 1000); // Returns new StateList

// Sort while maintaining current item
var currentItem = stateList.Current;
stateList.Sort((a, b) => a.Name.CompareTo(b.Name));
// Current item is tracked and index updated after sort

// Implicit conversions
StateList<string> list1 = new[] { "a", "b", "c" }; // From array
StateList<int> list2 = new List<int> { 1, 2, 3 };  // From List
```

---

## Enhanced Queue Management (NEW)

### Overview

QueueData<T> now integrates with StateList<T> to provide advanced queue management with:
- Position-aware processing
- Automatic pagination
- Progress tracking
- Checkpoint support
- Batch operations

### Basic Queue Operations with StateList

```csharp
// Create a queue from StateList
var items = new StateList<Invoice>(invoices);
items.Description = "Invoice Processing Queue";
items.CurrentIndex = 25; // Already processed 25 items

var queue = QueueData<Invoice>.CreateFromStateList(items, "InvoiceQueue");
queue.ProcessingStatus = "In Progress";
queue.PercentComplete = 25.0;

// Save queue with position
await queue.SaveQueueAsync(accountName, accountKey);

// Retrieve queue with position preserved
var retrieved = await QueueData<Invoice>.GetQueuesAsync(
    "InvoiceQueue", accountName, accountKey, deleteAfterRetrieve: false);

Console.WriteLine($"Resuming from position {retrieved.CurrentIndex}");
```

### Paged Queue Processing

```csharp
// Process queues in pages with automatic position tracking
var result = await QueueData<Order>.ProcessQueuesPagesAsync(
    name: "OrderProcessing",
    accountName: accountName,
    accountKey: accountKey,
    pageSize: 50,
    processPage: async (stateList) =>
    {
        foreach (var order in stateList)
        {
            await ProcessOrder(order);
            stateList.MoveNext(); // Update position
        }
        
        // Save checkpoint after each page
        await QueueData<Order>.SaveProgressAsync(
            stateList, accountName, accountKey);
        
        return true; // Continue processing
    },
    deleteAfterProcess: true,
    resumeFromLastPosition: true, // Resume from last checkpoint
    maxPages: 10
);

Console.WriteLine($"Processed {result.TotalProcessed} items");
Console.WriteLine($"Success: {result.Success}");
```

### Position-Based Pagination

```csharp
// Get paged results starting from specific position
var pagedResult = await QueueData<Task>.GetPagedQueuesFromPositionAsync(
    name: "TaskQueue",
    accountName: accountName,
    accountKey: accountKey,
    startFromIndex: 150, // Start from item 150
    pageSize: 25
);

Console.WriteLine($"Current position: {pagedResult.CurrentGlobalPosition}");
Console.WriteLine($"Has more: {pagedResult.HasMore}");

// Process the page
foreach (var task in pagedResult.Items)
{
    await ProcessTask(task);
}

// Get next page
if (pagedResult.HasMore)
{
    var nextPage = await QueueData<Task>.GetPagedQueuesAsync(
        "TaskQueue", accountName, accountKey, 
        pageSize: 25,
        continuationToken: pagedResult.ContinuationToken);
}
```

### Progress Monitoring

```csharp
// Get progress summary across all queue segments
var progress = await QueueData<Report>.GetProgressSummaryAsync(
    "ReportGeneration", accountName, accountKey);

Console.WriteLine($"Total items: {progress.TotalItems}");
Console.WriteLine($"Processed: {progress.TotalProcessed}");
Console.WriteLine($"Percent complete: {progress.OverallPercentComplete:F1}%");
Console.WriteLine($"Active segments: {progress.ActiveSegments}");
Console.WriteLine($"Average progress: {progress.AverageProgress:F1}%");

// Peek at queue without modifying
var currentState = await QueueData<Report>.PeekQueueAsync(
    progress.LastActiveQueueId, accountName, accountKey);
Console.WriteLine($"Current position: {currentState.GetData().CurrentIndex}");
```

### Advanced Queue Operations

```csharp
// Batch delete completed queues
await QueueData<Job>.DeleteQueuesMatchingAsync(
    accountName, accountKey,
    q => q.PercentComplete >= 100);

// Save checkpoint without deleting
var stateList = new StateList<Document>(documents);
stateList.CurrentIndex = 75; // Processed 75 documents

await QueueData<Document>.SaveProgressAsync(
    stateList, accountName, accountKey);

// Optimized batch retrieval with position awareness
var result = await QueueData<Email>.GetPagedQueuesFromPositionAsync(
    "EmailQueue", accountName, accountKey,
    startFromIndex: 200,
    pageSize: 100
);

// Process with automatic position updates
while (result.Items.MoveNext())
{
    await SendEmail(result.Items.Current);
    
    // Save progress every 10 items
    if (result.Items.CurrentIndex % 10 == 0)
    {
        await QueueData<Email>.SaveProgressAsync(
            result.Items, accountName, accountKey);
    }
}
```

### Queue Data Segmentation

```csharp
// Large dataset segmentation with position tracking
var largeDataset = new StateList<Record>(millionRecords);
var segmentSize = 10000;

for (int i = 0; i < largeDataset.Count; i += segmentSize)
{
    var segment = largeDataset.GetRange(i, 
        Math.Min(segmentSize, largeDataset.Count - i));
    
    var segmentList = new StateList<Record>(segment)
    {
        CurrentIndex = 0,
        Description = $"Segment starting at {i}"
    };
    
    var queue = QueueData<Record>.CreateFromStateList(
        segmentList, "LargeDataProcessing");
    
    queue.SegmentStartIndex = i;
    queue.TotalItemCount = largeDataset.Count;
    queue.ProcessingStatus = "Pending";
    
    await queue.SaveQueueAsync(accountName, accountKey);
}

// Process segments with global position tracking
var globalProgress = await QueueData<Record>.GetProgressSummaryAsync(
    "LargeDataProcessing", accountName, accountKey);
Console.WriteLine($"Total across all segments: {globalProgress.TotalItems}");
```

---

## Dynamic Entities

### Overview

Dynamic entities allow you to work with Azure Table Storage without defining compile-time schemas. Perfect for:
- Multi-tenant applications with varying schemas
- Integration with external systems
- Rapid prototyping
- Scenarios where table structure changes frequently

### Creating Dynamic Entities

```csharp
// Create a dynamic entity with runtime-defined table name
var entity = new DynamicEntity("SystemName_EntityType", "partitionKey", "rowKey");

// Add properties using indexer syntax
entity["EmployeeName"] = "John Doe";
entity["HireDate"] = DateTime.UtcNow;
entity["Salary"] = 95000.50;
entity["IsActive"] = true;

// Alternative: Use method syntax
entity.SetProperty("Department", "Engineering");
entity.SetProperty("YearsExperience", 8);
```

### Working with Dynamic Properties

```csharp
// Retrieve properties with type conversion
string name = entity.GetProperty<string>("EmployeeName");
DateTime hireDate = entity.GetProperty<DateTime>("HireDate");
bool isActive = entity.GetProperty<bool>("IsActive");

// Check if property exists
if (entity.HasProperty("Salary"))
{
    decimal salary = entity.GetProperty<decimal>("Salary");
}

// Get all properties
Dictionary<string, object> allProps = entity.GetAllProperties();
```

---

## Working with Data Access

### Creating Strongly-Typed Entities

```csharp
public class Customer : TableEntityBase, ITableExtra
{
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Name { get; set; }
    public string Email { get; set; }
    public string LargeDataField { get; set; } // Auto-chunked if >32KB
    
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
    Email = "contact@acme.com"
};
await dataAccess.ManageDataAsync(customer);

// Retrieve with lambda
var active = await dataAccess.GetCollectionAsync(c => 
    c.Email == "contact@acme.com" && c.Status == "Active");

// Pagination
var page = await dataAccess.GetPagedCollectionAsync(pageSize: 50);

// Batch operations with progress
var progress = new Progress<BatchUpdateProgress>(p =>
    Console.WriteLine($"Progress: {p.PercentComplete:F1}%"));

await dataAccess.BatchUpdateListAsync(
    customers, 
    TableOperationType.InsertOrReplace, 
    progress);
```

---

## Session Management

### Creating and Using Sessions

```csharp
// Asynchronous creation (recommended)
var session = await Session.CreateAsync(accountName, accountKey, sessionId);

// Store values
session["UserName"] = "John Doe";
session["LastLogin"] = DateTime.Now.ToString();
session["CartItems"] = JsonConvert.SerializeObject(items);

// Retrieve values
string userName = session["UserName"]?.Value;

// Auto-commit with using statement
await using var session = await Session.CreateAsync(accountName, accountKey, sessionId);
session["Key"] = "Value";
// Automatically committed on disposal
```

---

## Error Logging

### Enhanced Error Logging

```csharp
try
{
    // Your code
}
catch (Exception ex)
{
    // With automatic caller information
    var errorLog = ErrorLogData.CreateWithCallerInfo(
        "Failed to process customer data",
        ErrorCodeTypes.Error,
        customerId);
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}

// Clear old error logs
await ErrorLogData.ClearOldDataAsync(accountName, accountKey, daysOld: 30);
```

---

## Blob Storage Operations

### Upload and Search with Tags

```csharp
var azureBlobs = new AzureBlobs(accountName, accountKey, "my-container");

// Upload with tags
var uri = await azureBlobs.UploadFileAsync(
    "path/to/file.pdf",
    indexTags: new Dictionary<string, string>
    {
        { "category", "invoices" },
        { "year", "2024" }
    });

// Search by tags using lambda
var invoices = await azureBlobs.GetCollectionAsync(b => 
    b.Tags["category"] == "invoices" && b.Tags["year"] == "2024");

// Batch operations
var downloaded = await azureBlobs.DownloadFilesAsync(
    b => b.Tags["type"] == "report" && b.UploadDate > DateTime.Today.AddDays(-7),
    @"C:\Downloads");
```

---

## Advanced Features

### Combining StateList with QueueData for Complex Workflows

```csharp
public class DataProcessor
{
    private readonly string _accountName;
    private readonly string _accountKey;
    
    public async Task ProcessLargeDatasetWithRecovery(List<DataItem> items)
    {
        // Create StateList for position tracking
        var stateList = new StateList<DataItem>(items)
        {
            Description = "Large Dataset Processing",
            CurrentIndex = -1 // Start from beginning
        };
        
        try
        {
            // Process items with checkpoint saves
            while (stateList.MoveNext())
            {
                await ProcessItem(stateList.Current);
                
                // Save checkpoint every 100 items
                if (stateList.CurrentIndex % 100 == 0)
                {
                    var queue = QueueData<DataItem>.CreateFromStateList(
                        stateList, "DataProcessing");
                    queue.ProcessingStatus = "In Progress";
                    queue.PercentComplete = 
                        (double)stateList.CurrentIndex / stateList.Count * 100;
                    
                    await queue.SaveQueueAsync(_accountName, _accountKey);
                    Console.WriteLine($"Checkpoint saved at position {stateList.CurrentIndex}");
                }
            }
            
            // Mark as complete
            var finalQueue = QueueData<DataItem>.CreateFromStateList(
                stateList, "DataProcessing");
            finalQueue.ProcessingStatus = "Completed";
            finalQueue.PercentComplete = 100;
            await finalQueue.SaveQueueAsync(_accountName, _accountKey);
        }
        catch (Exception ex)
        {
            // Save current state for recovery
            var errorQueue = QueueData<DataItem>.CreateFromStateList(
                stateList, "DataProcessing");
            errorQueue.ProcessingStatus = $"Error at position {stateList.CurrentIndex}";
            await errorQueue.SaveQueueAsync(_accountName, _accountKey);
            
            // Log error
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                $"Processing failed at position {stateList.CurrentIndex}: {ex.Message}",
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync(_accountName, _accountKey);
            
            throw;
        }
    }
    
    public async Task<bool> ResumeProcessing()
    {
        // Check for incomplete processing
        var progress = await QueueData<DataItem>.GetProgressSummaryAsync(
            "DataProcessing", _accountName, _accountKey);
        
        if (progress.OverallPercentComplete >= 100)
        {
            Console.WriteLine("Processing already complete");
            return true;
        }
        
        // Resume from last position
        var stateList = await QueueData<DataItem>.GetQueuesAsync(
            "DataProcessing", _accountName, _accountKey, 
            deleteAfterRetrieve: false);
        
        Console.WriteLine($"Resuming from position {stateList.CurrentIndex} of {stateList.Count}");
        
        // Continue processing
        while (stateList.MoveNext())
        {
            await ProcessItem(stateList.Current);
        }
        
        return true;
    }
}
```

### Parallel Queue Processing with StateList

```csharp
public class ParallelProcessor
{
    public async Task ProcessInParallel(List<WorkItem> items, int parallelism = 4)
    {
        // Segment work into parallel queues
        var segmentSize = items.Count / parallelism;
        var tasks = new List<Task>();
        
        for (int i = 0; i < parallelism; i++)
        {
            var start = i * segmentSize;
            var count = (i == parallelism - 1) 
                ? items.Count - start 
                : segmentSize;
            
            var segment = items.GetRange(start, count);
            var stateList = new StateList<WorkItem>(segment)
            {
                Description = $"Parallel segment {i + 1}"
            };
            
            var queue = QueueData<WorkItem>.CreateFromStateList(
                stateList, $"ParallelQueue_{i}");
            queue.SegmentStartIndex = start;
            queue.TotalItemCount = items.Count;
            
            await queue.SaveQueueAsync(accountName, accountKey);
            
            // Process segment in parallel
            tasks.Add(ProcessSegmentAsync($"ParallelQueue_{i}", i));
        }
        
        await Task.WhenAll(tasks);
        
        // Aggregate results
        var totalProgress = await QueueData<WorkItem>.GetProgressSummaryAsync(
            "ParallelQueue", accountName, accountKey);
        Console.WriteLine($"Total processed: {totalProgress.TotalProcessed}");
    }
    
    private async Task ProcessSegmentAsync(string queueName, int segmentId)
    {
        await QueueData<WorkItem>.ProcessQueuesPagesAsync(
            queueName, accountName, accountKey,
            pageSize: 50,
            processPage: async (stateList) =>
            {
                while (stateList.MoveNext())
                {
                    await ProcessWorkItem(stateList.Current);
                }
                return true;
            },
            deleteAfterProcess: true,
            resumeFromLastPosition: true);
    }
}
```

---

## Performance Optimizations

### Performance Metrics

| Feature | v2.1 | v2.2 | Improvement |
|---------|------|------|-------------|
| Queue retrieval (1000 items) | ~450ms | ~200ms | ~2.25x faster |
| Position-based pagination | N/A | ~50ms | New feature |
| Progress tracking overhead | N/A | <5ms | Minimal |
| StateList serialization (1000 items) | N/A | ~15ms | Optimized |
| Checkpoint save | N/A | ~25ms | Fast recovery |

### Best Practices for Performance

1. **Use StateList for large collections** - Automatic position tracking
2. **Enable checkpoint saves** - Fast recovery from failures
3. **Use position-based pagination** - Efficient for large queues
4. **Batch queue operations** - Reduce round trips
5. **Monitor progress asynchronously** - Non-blocking status checks

---

## Migration Guide

### From v2.1 to v2.2

Version 2.2 is fully backward compatible. Existing QueueData usage continues to work:

```csharp
// Old way (still works)
var queue = new QueueData<Item>();
queue.PutData(itemList); // Converts List to StateList internally

// New way (recommended)
var stateList = new StateList<Item>(itemList);
var queue = QueueData<Item>.CreateFromStateList(stateList, "QueueName");
```

### Adopting StateList

To upgrade existing queue processing:

```csharp
// OLD - Basic list processing
List<Task> tasks = GetTasks();
foreach (var task in tasks)
{
    ProcessTask(task);
}

// NEW - With position tracking and recovery
var stateList = new StateList<Task>(GetTasks());
while (stateList.MoveNext())
{
    ProcessTask(stateList.Current);
    
    // Can save and resume from CurrentIndex
    if (needToSaveProgress)
    {
        SaveProgress(stateList);
    }
}
```

### Upgrading Queue Processing

```csharp
// OLD - Manual queue processing
var queues = await QueueData<Order>.GetQueuesAsync(
    "Orders", accountName, accountKey);
foreach (var order in queues)
{
    ProcessOrder(order);
}

// NEW - Automated with progress tracking
await QueueData<Order>.ProcessQueuesPagesAsync(
    "Orders", accountName, accountKey,
    pageSize: 100,
    processPage: async (stateList) =>
    {
        while (stateList.MoveNext())
        {
            await ProcessOrder(stateList.Current);
        }
        return true;
    },
    resumeFromLastPosition: true);
```

---

## Best Practices

### StateList Best Practices

1. **Always set Description** for debugging and monitoring
2. **Save checkpoints regularly** for large processing tasks
3. **Use CurrentIndex for progress reporting**
4. **Leverage Find() for navigation** - automatically sets current
5. **Use implicit conversions** for cleaner code

### Queue Management Best Practices

1. **Use position-based pagination** for large datasets
2. **Enable resumeFromLastPosition** for fault tolerance
3. **Monitor with GetProgressSummaryAsync** for real-time status
4. **Set appropriate page sizes** - balance memory vs round trips
5. **Clean up completed queues** with DeleteQueuesMatchingAsync

### General Guidelines

1. **Choose the right collection type**:
   - Use StateList for position-aware processing
   - Use List for simple, one-time operations
   - Use QueueData for persistent, recoverable processing

2. **Optimize for failure recovery**:
   - Save checkpoints frequently
   - Use ProcessingStatus for debugging
   - Log errors with position information

3. **Monitor processing efficiently**:
   - Use PeekQueueAsync for non-destructive checks
   - Track PercentComplete for user feedback
   - Aggregate progress across segments

---

## Troubleshooting

### Common Issues

**Issue**: StateList position not preserved
- **Solution**: Ensure using JsonConvert with proper settings
- **Check**: Items property must be serialized

**Issue**: Queue processing starts from beginning
- **Solution**: Set resumeFromLastPosition = true
- **Check**: Verify CurrentIndex is being saved

**Issue**: Progress shows incorrect percentage
- **Solution**: Set TotalItemCount for segments
- **Check**: Use GetProgressSummaryAsync for accurate totals

**Issue**: Pagination returns wrong items
- **Solution**: Use position-based methods for consistency
- **Check**: Don't mix token-based and position-based paging

---

## Support and Resources

- **NuGet Package**: [ASCDataAccessLibrary](https://www.nuget.org/packages/ASCDataAccessLibrary)
- **Source Code**: [GitHub Repository](https://github.com/your-repo/ASCDataAccessLibrary)
- **Issues**: [GitHub Issues](https://github.com/your-repo/ASCDataAccessLibrary/issues)
- **Documentation**: This document
- **Version**: 2.2.0
- **License**: MIT

---

## Changelog

### Version 2.2.0 (Current)
- Added `StateList<T>` for position-aware collections
- Enhanced `QueueData<T>` with StateList integration
- Added position-based pagination methods
- Implemented progress tracking and monitoring
- Added checkpoint and recovery support
- Optimized batch queue operations
- Added `ProcessQueuesPagesAsync` for automated processing
- Improved serialization for state preservation

### Version 2.1.0
- Added `DynamicEntity` for runtime-defined schemas
- Introduced `IDynamicProperties` interface
- Implemented `TableEntityTypeCache` for performance
- Enhanced `TableEntityBase` serialization
- Added error log cleanup methods

### Version 2.0.0
- Lambda expression support
- Hybrid server/client filtering
- Batch operations with progress tracking
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

*Last Updated: 2024*
*Version: 2.2.0*