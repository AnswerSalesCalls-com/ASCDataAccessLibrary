# ASCDataAccessLibrary v3.1 Complete Documentation

## Comprehensive Guide for Azure Table Storage, Blob Storage, Logging, and Session Management

**Version**: 3.1.0  
**Last Updated**: August 2025  
**Authors**: O. Brown | M. Chukwuemeka  
**Company**: Answer Sales Calls Inc.  
**License**: MIT

---

## Table of Contents

1. [Introduction & Overview](#introduction--overview)
2. [Installation & Setup](#installation--setup)
3. [Core Architecture](#core-architecture)
4. [Azure Table Storage - DataAccess](#azure-table-storage---dataaccess)
5. [Entity Models & TableEntityBase](#entity-models--tableentitybase)
6. [Dynamic Entities](#dynamic-entities)
7. [Lambda Expressions & Hybrid Filtering](#lambda-expressions--hybrid-filtering)
8. [Batch Operations & Pagination](#batch-operations--pagination)
9. [StateList - Position-Aware Collections](#statelist---position-aware-collections)
10. [Queue Management with QueueData](#queue-management-with-queuedata)
11. [Session Management](#session-management)
12. [Universal Logging System](#universal-logging-system)
13. [Error Handling & ErrorLogData](#error-handling--errorlogdata)
14. [Azure Blob Storage Operations](#azure-blob-storage-operations)
15. [Performance Optimization](#performance-optimization)
16. [Migration Guides](#migration-guides)
17. [Best Practices & Patterns](#best-practices--patterns)
18. [Troubleshooting Guide](#troubleshooting-guide)
19. [API Reference](#api-reference)
20. [Code Examples & Recipes](#code-examples--recipes)

---

## Introduction & Overview

ASCDataAccessLibrary v3.1 is a comprehensive .NET library that provides enterprise-grade abstraction over Azure Table Storage and Blob Storage, with built-in support for universal logging, session management, and advanced data operations. This library has evolved through multiple iterations to provide a robust, performant, and developer-friendly interface to Azure Storage services.

### Key Capabilities

- **Azure Table Storage**: Complete CRUD operations with strongly-typed and dynamic entities
- **Automatic Field Chunking**: Handles fields larger than Azure's 64KB limit transparently
- **Lambda Expression Support**: Write intuitive queries that are automatically optimized
- **Hybrid Filtering**: Automatic server/client-side operation optimization
- **Universal Logging**: ILogger implementation that works across all .NET application types
- **Session Management**: Distributed-safe sessions without Redis dependency
- **Blob Storage**: Advanced operations with tag-based indexing and searching
- **State Management**: Position-aware collections that survive serialization
- **Queue Processing**: Resumable queue operations with progress tracking
- **Batch Operations**: Efficient bulk operations with automatic chunking
- **Error Logging**: Comprehensive error tracking with automatic caller information

### What's New in v3.1

- **Enhanced ILogger Integration**: Full Microsoft.Extensions.Logging.ILogger implementation
- **Improved Session Performance**: Optimized batched writes with configurable delays
- **Advanced Cleanup Policies**: Automatic retention management by severity level
- **Circuit Breaker Pattern**: Fallback mechanisms when Azure is unavailable
- **Desktop Application Support**: Specific extensions for WPF/WinForms applications
- **Console/Service Extensions**: Progress tracking and heartbeat monitoring
- **Enhanced Dynamic Entity**: Pattern-based key detection and automatic inference
- **Improved StateList**: Full IList<T> and IReadOnlyList<T> implementation
- **Better Error Recovery**: Comprehensive exception handling and recovery mechanisms

---

## Installation & Setup

### Prerequisites

- .NET 6.0, 7.0, 8.0, or 9.0
- Azure Storage Account with Table Storage and Blob Storage enabled
- Account Name and Account Key (connection strings are not used)

### Installation Methods

#### Package Manager Console
```powershell
Install-Package ASCDataAccessLibrary -Version 3.1.0
```

#### .NET CLI
```bash
dotnet add package ASCDataAccessLibrary --version 3.1.0
```

#### PackageReference
```xml
<PackageReference Include="ASCDataAccessLibrary" Version="3.1.0" />
```

### Initial Configuration

#### ASP.NET Core Web Application
```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Configure Azure Table Logging
builder.Logging.AddAzureTableLogging(options =>
{
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    options.MinimumLevel = LogLevel.Information;
    options.RetentionDays = 60;
    options.EnableAutoCleanup = true;
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

// Configure Session Management
builder.Services.AddAzureTableSessions(options =>
{
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    options.SessionTimeout = TimeSpan.FromMinutes(20);
    options.BatchWriteDelay = TimeSpan.FromMilliseconds(500);
    options.IdStrategy = SessionIdStrategy.HttpContext;
});

var app = builder.Build();
```

#### Desktop Application (WPF/WinForms)
```csharp
// App.xaml.cs or Program.cs
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize logging
        AzureTableLogging.Initialize(options =>
        {
            options.AccountName = ConfigurationManager.AppSettings["AzureStorageAccountName"];
            options.AccountKey = ConfigurationManager.AppSettings["AzureStorageAccountKey"];
            options.ApplicationName = "MyDesktopApp";
            options.Environment = "Production";
            options.MinimumLevel = LogLevel.Information;
        });
        
        // Initialize sessions
        SessionManager.Initialize(
            ConfigurationManager.AppSettings["AzureStorageAccountName"],
            ConfigurationManager.AppSettings["AzureStorageAccountKey"],
            options =>
            {
                options.IdStrategy = SessionIdStrategy.UserMachine;
                options.ApplicationName = "MyDesktopApp";
            });
        
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ensure all logs and sessions are flushed
        AzureTableLogging.ShutdownAsync().Wait();
        SessionManager.Shutdown();
        base.OnExit(e);
    }
}
```

#### Console Application
```csharp
class Program
{
    static async Task Main(string[] args)
    {
        // Initialize services
        AzureTableLogging.Initialize(
            Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT"),
            Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY"));
        
        SessionManager.Initialize(
            Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT"),
            Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY"));
        
        var logger = AzureTableLogging.CreateLogger<Program>();
        
        try
        {
            logger.LogInformation("Application started");
            await RunApplication();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application failed");
        }
        finally
        {
            await AzureTableLogging.ShutdownAsync();
            SessionManager.Shutdown();
        }
    }
}
```

---

## Core Architecture

### Design Principles

1. **Async-First**: All operations have async implementations with sync wrappers
2. **Generic Type Safety**: Strong typing with generic constraints
3. **Automatic Optimization**: Lambda expressions are automatically optimized
4. **Transparent Chunking**: Large fields are automatically split and reassembled
5. **Progressive Enhancement**: Works with basic types, supports advanced features
6. **Fail-Safe Operations**: Circuit breakers and fallback mechanisms
7. **Zero Configuration**: Sensible defaults with extensive customization options

### Component Hierarchy

```
┌─────────────────────────────────────────────┐
│           Application Layer                 │
├─────────────────────────────────────────────┤
│                                             │
│  ┌─────────────┐    ┌──────────────┐       │
│  │   Entities   │    │   Dynamic    │       │
│  │  (Strongly  │    │   Entities   │       │
│  │    Typed)   │    │  (Runtime)   │       │
│  └──────┬──────┘    └──────┬───────┘       │
│         │                   │               │
│         └────────┬──────────┘               │
│                  ▼                          │
│        ┌─────────────────┐                  │
│        │ TableEntityBase │                  │
│        │   (Chunking)    │                  │
│        └────────┬────────┘                  │
│                 ▼                           │
│        ┌─────────────────┐                  │
│        │   ITableExtra   │                  │
│        └────────┬────────┘                  │
├─────────────────┼───────────────────────────┤
│                 ▼                           │
│        ┌─────────────────┐                  │
│        │  DataAccess<T>  │                  │
│        │  (CRUD + Batch) │                  │
│        └────────┬────────┘                  │
│                 ▼                           │
│    ┌────────────────────────┐               │
│    │  Hybrid Filter Engine  │               │
│    │  (Server/Client Split) │               │
│    └────────────┬───────────┘               │
├─────────────────┼───────────────────────────┤
│                 ▼                           │
│    ┌────────────────────────┐               │
│    │  Azure Table Storage   │               │
│    └────────────────────────┘               │
└─────────────────────────────────────────────┘
```

---

## Azure Table Storage - DataAccess

The `DataAccess<T>` class is the primary interface for all table storage operations. It provides a comprehensive set of methods for CRUD operations, batch processing, pagination, and advanced querying.

### Creating a DataAccess Instance

```csharp
// Create an instance for a specific entity type
var dataAccess = new DataAccess<Customer>(accountName, accountKey);

// The instance is reusable for all operations on the Customer table
// Create one instance per table and reuse throughout your application
```

### Core CRUD Operations

#### Insert/Update Operations

```csharp
// Insert or Replace (default operation)
var customer = new Customer
{
    CompanyId = "COMP123",
    CustomerId = "CUST456",
    Name = "Acme Corporation",
    Email = "contact@acme.com",
    Status = "Active",
    CreatedDate = DateTime.UtcNow
};

// Synchronous
dataAccess.ManageData(customer, TableOperationType.InsertOrReplace);

// Asynchronous (recommended)
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrReplace);

// Insert or Merge (updates only provided properties)
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrMerge);

// Delete
await dataAccess.ManageDataAsync(customer, TableOperationType.Delete);
```

#### Retrieve Operations

```csharp
// Get all data from table
var allCustomers = await dataAccess.GetAllTableDataAsync();

// Get by RowKey (primary key)
var customer = await dataAccess.GetRowObjectAsync("CUST456");

// Get by PartitionKey (all entities in partition)
var companyCustomers = await dataAccess.GetCollectionAsync("COMP123");

// Get by field value
var activeCustomer = await dataAccess.GetRowObjectAsync(
    "Status",                    // Field name
    ComparisonTypes.eq,         // Comparison operator
    "Active"                     // Value
);

// Get collection with multiple criteria
var queryTerms = new List<DBQueryItem>
{
    new DBQueryItem 
    { 
        FieldName = "Status", 
        FieldValue = "Active", 
        HowToCompare = ComparisonTypes.eq 
    },
    new DBQueryItem 
    { 
        FieldName = "CreatedDate", 
        FieldValue = DateTime.Today.AddDays(-30).ToString("O"), 
        HowToCompare = ComparisonTypes.gt,
        IsDateTime = true 
    }
};

var results = await dataAccess.GetCollectionAsync(
    queryTerms, 
    QueryCombineStyle.and  // Combine with AND or OR
);
```

### Lambda Expression Queries

```csharp
// Simple equality check
var activeCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active"
);

// Multiple conditions with AND
var recentActiveCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && 
         c.CreatedDate > DateTime.Today.AddDays(-30)
);

// OR conditions
var priorityCustomers = await dataAccess.GetCollectionAsync(
    c => c.IsPremium == true || c.TotalPurchases > 10000
);

// Complex expressions with automatic hybrid filtering
var searchResults = await dataAccess.GetCollectionAsync(
    c => c.Name.ToLower().Contains("tech") ||      // Client-side
         c.Email.EndsWith("@acme.com") ||          // Client-side
         c.Revenue > 1000000                       // Server-side
);

// Negation
var nonActiveCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status != "Active"
);

// Null checks
var customersWithEmail = await dataAccess.GetCollectionAsync(
    c => !string.IsNullOrEmpty(c.Email)
);
```

---

## Entity Models & TableEntityBase

### Creating Strongly-Typed Entities

All entities must inherit from `TableEntityBase` and implement `ITableExtra`:

```csharp
public class Customer : TableEntityBase, ITableExtra
{
    // PartitionKey - Used for logical grouping
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    // RowKey - Unique identifier within partition
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    // Standard properties
    public string Name { get; set; }
    public string Email { get; set; }
    public string Phone { get; set; }
    public bool IsPremium { get; set; }
    public decimal TotalPurchases { get; set; }
    public DateTime CreatedDate { get; set; }
    public string Status { get; set; }
    
    // Large field - automatically chunked if > 32KB
    public string Notes { get; set; }
    public string JsonData { get; set; }  // Can store large JSON
    
    // Complex object - serialize to JSON
    private Address _address;
    public Address CustomerAddress 
    { 
        get => _address;
        set 
        {
            _address = value;
            AddressJson = JsonConvert.SerializeObject(value);
        }
    }
    
    // Stored as JSON string in table
    public string AddressJson { get; set; }
    
    // ITableExtra implementation
    public string TableReference => "Customers";  // Table name
    
    public string GetIDValue() => this.CustomerId;  // Return unique ID
}

public class Address
{
    public string Street { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public string ZipCode { get; set; }
    public string Country { get; set; }
}
```

### Understanding TableEntityBase

`TableEntityBase` provides automatic field chunking for large data:

```csharp
public class Document : TableEntityBase, ITableExtra
{
    public string DocumentId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Category
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    // This field can be any size - automatically chunked if > 32KB
    public string Content { get; set; }
    
    // Metadata
    public string Title { get; set; }
    public string Author { get; set; }
    public DateTime PublishedDate { get; set; }
    
    public string TableReference => "Documents";
    public string GetIDValue() => this.DocumentId;
}

// Usage
var document = new Document
{
    DocumentId = Guid.NewGuid().ToString(),
    Category = "Reports",
    Title = "Annual Report 2025",
    Author = "John Doe",
    PublishedDate = DateTime.UtcNow,
    Content = veryLargeString  // Can be megabytes - automatically handled
};

var dataAccess = new DataAccess<Document>(accountName, accountKey);
await dataAccess.ManageDataAsync(document);
```

### Entity Inheritance Patterns

```csharp
// Base entity for common properties
public abstract class BaseEntity : TableEntityBase, ITableExtra
{
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string CreatedBy { get; set; }
    public string ModifiedBy { get; set; }
    public bool IsDeleted { get; set; }
    
    // Abstract members for derived classes
    public abstract string TableReference { get; }
    public abstract string GetIDValue();
}

// Specific entity implementation
public class Product : BaseEntity
{
    public string ProductId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string CategoryId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Name { get; set; }
    public string Description { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsAvailable { get; set; }
    
    public override string TableReference => "Products";
    public override string GetIDValue() => this.ProductId;
}
```

---

## Dynamic Entities

Dynamic entities allow runtime-defined schemas without compile-time type definitions.

### Creating Dynamic Entities

```csharp
// Basic creation with explicit keys
var entity = new DynamicEntity("DynamicTable", "partition1", "row1");

// Add properties dynamically
entity["FirstName"] = "John";
entity["LastName"] = "Doe";
entity["Age"] = 30;
entity["IsActive"] = true;
entity["Balance"] = 1234.56m;
entity["LastLogin"] = DateTime.UtcNow;
entity["Tags"] = JsonConvert.SerializeObject(new[] { "vip", "premium" });

// Alternative property methods
entity.SetProperty("Email", "john@example.com");
entity.SetProperty("Phone", "+1234567890");
```

### Pattern-Based Key Detection

```csharp
// Define pattern configuration
var patternConfig = new DynamicEntity.KeyPatternConfig
{
    PartitionKeyPatterns = new List<DynamicEntity.PatternRule>
    {
        new() { Pattern = "category|group|type", Priority = 1 },
        new() { Pattern = "tenant.*|company.*", Priority = 2 }
    },
    RowKeyPatterns = new List<DynamicEntity.PatternRule>
    {
        new() { Pattern = ".*id$|.*key$", Priority = 1 },
        new() { Pattern = "code|number", Priority = 2 }
    },
    FuzzyMatchThreshold = 0.7
};

// Create entity with automatic key detection
var data = new Dictionary<string, object>
{
    { "category", "electronics" },      // Detected as PartitionKey
    { "productId", "PROD123" },         // Detected as RowKey
    { "name", "Laptop" },
    { "price", 999.99 }
};

var entity = new DynamicEntity("Products", data, patternConfig);
```

### Working with Dynamic Properties

```csharp
// Type-safe property retrieval
string name = entity.GetProperty<string>("name");
decimal price = entity.GetProperty<decimal>("price");
DateTime? lastUpdated = entity.GetProperty<DateTime?>("lastUpdated");

// Check property existence
if (entity.HasProperty("discount"))
{
    var discount = entity.GetProperty<decimal>("discount");
    var finalPrice = price - (price * discount / 100);
}

// Get all properties
var allProperties = entity.GetAllProperties();
foreach (var prop in allProperties)
{
    Console.WriteLine($"{prop.Key}: {prop.Value}");
}

// Remove properties
entity.RemoveProperty("temporaryField");

// Get diagnostics
var diagnostics = entity.GetDiagnostics();
Console.WriteLine($"Total Properties: {diagnostics["PropertyCount"]}");
Console.WriteLine($"Partition Key: {diagnostics["PartitionKey"]}");
Console.WriteLine($"Row Key: {diagnostics["RowKey"]}");
```

### Dynamic Entity with DataAccess

```csharp
var dataAccess = new DataAccess<DynamicEntity>(accountName, accountKey);

// Create and save
var entity = new DynamicEntity("UserProfiles", "region_us", "user_123");
entity["Username"] = "johndoe";
entity["Email"] = "john@example.com";
entity["Preferences"] = JsonConvert.SerializeObject(new 
{
    Theme = "dark",
    Language = "en-US",
    Notifications = true
});

await dataAccess.ManageDataAsync(entity);

// Query dynamic entities
var usProfiles = await dataAccess.GetCollectionAsync("region_us");

// Lambda queries work with dynamic entities
var activeUsers = await dataAccess.GetCollectionAsync(
    e => e.GetProperty<bool>("IsActive") == true
);
```

---

## Lambda Expressions & Hybrid Filtering

The library automatically optimizes lambda expressions by splitting operations between server-side (Azure Table Storage) and client-side execution.

### Understanding Hybrid Filtering

```csharp
// Server-side operations (executed in Azure):
// - Simple comparisons (==, !=, <, >, <=, >=)
// - AND/OR operations
// - Date comparisons
// - Boolean checks

// Client-side operations (executed locally):
// - String methods (Contains, StartsWith, EndsWith, ToLower, ToUpper)
// - Complex expressions
// - Method calls
// - Computed properties
```

### Examples of Automatic Optimization

```csharp
// Pure server-side (fast)
var results = await dataAccess.GetCollectionAsync(
    x => x.Status == "Active" && 
         x.Priority > 5 &&
         x.CreatedDate > DateTime.Today.AddDays(-7)
);

// Mixed operations (automatic hybrid filtering)
var results = await dataAccess.GetCollectionAsync(
    c => c.Name.ToLower().Contains("tech") ||     // Client-side
         c.Revenue > 1000000                       // Server-side
);
// The library:
// 1. Executes "Revenue > 1000000" on Azure
// 2. Retrieves filtered dataset
// 3. Applies "Name.ToLower().Contains("tech")" locally
// 4. Combines results with OR logic

// Complex hybrid example
var results = await dataAccess.GetCollectionAsync(
    p => (p.Category == "Electronics" ||           // Server-side
          p.Category == "Computers") &&            // Server-side
         p.Price < 1000 &&                         // Server-side
         p.Description.Contains("gaming") &&       // Client-side
         p.Tags.Any(t => t.StartsWith("new"))     // Client-side
);
```

### Optimization Strategies

```csharp
// Strategy 1: Put server-supported operations first
// GOOD - Server filters first, reducing data transfer
var results = await dataAccess.GetCollectionAsync(
    x => x.Status == "Active" &&                   // Server-side first
         x.Name.Contains("Corp")                    // Client-side after
);

// Strategy 2: Use server-side operations when possible
// BETTER
var results = await dataAccess.GetCollectionAsync(
    x => x.NameLowercase == "acme corporation"     // Pre-computed lowercase field
);

// GOOD (but requires client-side processing)
var results = await dataAccess.GetCollectionAsync(
    x => x.Name.ToLower() == "acme corporation"
);

// Strategy 3: Pre-compute complex conditions
public class OptimizedEntity : TableEntityBase, ITableExtra
{
    public string Name { get; set; }
    public string NameLowercase { get; set; }      // Pre-computed
    public bool HasPendingOrders { get; set; }     // Pre-computed flag
    public string SearchText { get; set; }         // Concatenated searchable fields
    
    // Update computed fields on save
    public void UpdateComputedFields()
    {
        NameLowercase = Name?.ToLower();
        SearchText = $"{Name} {Email} {Phone}".ToLower();
    }
}
```

---

## Batch Operations & Pagination

### Batch Operations

Azure Table Storage has a strict limit of 100 operations per batch, and all entities in a batch must share the same PartitionKey.

```csharp
// Prepare large dataset
var customers = new List<Customer>();
for (int i = 0; i < 1000; i++)
{
    customers.Add(new Customer
    {
        CompanyId = "COMP123",  // Same partition for batch
        CustomerId = $"CUST{i:D6}",
        Name = $"Customer {i}",
        Email = $"customer{i}@example.com",
        Status = "Active"
    });
}

// Batch insert with progress tracking
var progress = new Progress<DataAccess<Customer>.BatchUpdateProgress>(p =>
{
    Console.WriteLine($"Progress: {p.PercentComplete:F1}%");
    Console.WriteLine($"Batches: {p.CompletedBatches}/{p.TotalBatches}");
    Console.WriteLine($"Items: {p.ProcessedItems}/{p.TotalItems}");
    Console.WriteLine($"Current Batch Size: {p.CurrentBatchSize}");
});

var result = await dataAccess.BatchUpdateListAsync(
    customers,
    TableOperationType.InsertOrReplace,
    progress
);

Console.WriteLine($"Operation completed: {result.Success}");
Console.WriteLine($"Successful items: {result.SuccessfulItems}");
Console.WriteLine($"Failed items: {result.FailedItems}");
if (result.Errors.Any())
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
}
```

### Handling Mixed Partitions

```csharp
// Mixed partitions - automatically grouped
var mixedCustomers = new List<Customer>
{
    new Customer { CompanyId = "COMP1", CustomerId = "C1", Name = "Customer 1" },
    new Customer { CompanyId = "COMP2", CustomerId = "C2", Name = "Customer 2" },
    new Customer { CompanyId = "COMP1", CustomerId = "C3", Name = "Customer 3" },
    // ... more with different CompanyIds
};

// The library automatically:
// 1. Groups by PartitionKey (CompanyId)
// 2. Splits each group into 100-item batches
// 3. Executes batches sequentially
var result = await dataAccess.BatchUpdateListAsync(mixedCustomers);
```

### Pagination

```csharp
// Basic pagination
var firstPage = await dataAccess.GetPagedCollectionAsync(
    pageSize: 100,
    continuationToken: null  // null for first page
);

Console.WriteLine($"Page has {firstPage.Data.Count} items");
Console.WriteLine($"Has more pages: {firstPage.HasMore}");

// Get next page
if (firstPage.HasMore)
{
    var secondPage = await dataAccess.GetPagedCollectionAsync(
        pageSize: 100,
        continuationToken: firstPage.ContinuationToken
    );
}

// Pagination with filtering
var pagedActive = await dataAccess.GetPagedCollectionAsync(
    predicate: c => c.Status == "Active",
    pageSize: 50
);

// Complete pagination loop
var allItems = new List<Customer>();
string continuationToken = null;
do
{
    var page = await dataAccess.GetPagedCollectionAsync(
        pageSize: 100,
        continuationToken: continuationToken
    );
    
    allItems.AddRange(page.Data);
    continuationToken = page.ContinuationToken;
    
    Console.WriteLine($"Retrieved {allItems.Count} total items...");
    
} while (!string.IsNullOrEmpty(continuationToken));

// Pagination with partition key
var pagedByPartition = await dataAccess.GetPagedCollectionAsync(
    partitionKeyID: "COMP123",
    pageSize: 100
);
```

### Initial Data Load Pattern

```csharp
// Load initial data quickly, then load rest in background
public async Task LoadDataWithProgressiveEnhancement()
{
    // Quick initial load
    var initialData = await dataAccess.GetInitialDataLoadAsync(
        initialLoadSize: 50
    );
    
    // Display initial data immediately
    DisplayData(initialData.Data);
    
    // Load remaining data in background
    if (initialData.HasMore)
    {
        _ = Task.Run(async () =>
        {
            var continuationToken = initialData.ContinuationToken;
            while (!string.IsNullOrEmpty(continuationToken))
            {
                var nextBatch = await dataAccess.GetPagedCollectionAsync(
                    pageSize: 200,
                    continuationToken: continuationToken
                );
                
                // Update UI on UI thread
                await Dispatcher.InvokeAsync(() => 
                    DisplayData(nextBatch.Data));
                
                continuationToken = nextBatch.ContinuationToken;
            }
        });
    }
}
```

---

## StateList - Position-Aware Collections

`StateList<T>` is an enhanced collection that maintains its position through serialization, perfect for resumable operations.

### Core Features

```csharp
// Create and initialize
var tasks = new StateList<ProcessingTask>
{
    new ProcessingTask { Id = 1, Name = "Task 1" },
    new ProcessingTask { Id = 2, Name = "Task 2" },
    new ProcessingTask { Id = 3, Name = "Task 3" }
};

tasks.Description = "Daily Processing Queue";

// Navigation
tasks.First();                  // Move to first item
Console.WriteLine(tasks.Current); // Get current item

tasks.MoveNext();               // Move forward
tasks.MovePrevious();           // Move backward
tasks.Last();                   // Move to last item
tasks.Reset();                  // Reset position (before first)

// Position checking
if (tasks.HasNext)
{
    tasks.MoveNext();
}

// Get current with index
var (item, index) = tasks.Peek.Value;
Console.WriteLine($"Item: {item.Name}, Position: {index}");

// Position is preserved through serialization
tasks.CurrentIndex = 1;
string json = JsonConvert.SerializeObject(tasks);
var restored = JsonConvert.DeserializeObject<StateList<ProcessingTask>>(json);
Console.WriteLine(restored.CurrentIndex); // Still 1
```

### Advanced StateList Operations

```csharp
// Add with position management
var list = new StateList<string>();
list.Add("Item1", setCurrent: true);       // Add and set as current
list.AddRange(new[] { "Item2", "Item3" }, 
    setCurrentToFirst: true);               // Add range and set to first added

// Find operations set current position
var found = list.Find(x => x.Contains("2"));
if (found != null)
{
    Console.WriteLine($"Found and set current: {list.Current}");
}

// String-based indexer for quick search
var item = list["Item2"];  // Case-insensitive search

// LINQ operations return new StateList
var filtered = list.Where(x => x.StartsWith("Item"));
var excluded = list.Except(new[] { "Item1" });

// Sorting maintains current item reference
var current = list.Current;
list.Sort();
// Current item is tracked to new position

// Remove operations adjust position intelligently
list.RemoveAt(0);  // If current was at 0, moves to new 0
list.RemoveAll(x => x.Contains("temp"));

// Batch operations
list.RemoveRange(1, 2);
list.InsertRange(1, new[] { "New1", "New2" });
list.Reverse();
```

### StateList in Practice

```csharp
public class DataProcessor
{
    private StateList<DataItem> _items;
    private readonly ILogger _logger;
    
    public async Task ProcessWithRecovery(StateList<DataItem> items)
    {
        _items = items;
        _items.Description = $"Processing batch {DateTime.Now:yyyy-MM-dd}";
        
        // Start or resume processing
        if (_items.CurrentIndex < 0)
        {
            _items.First();
            _logger.LogInformation("Starting new processing batch");
        }
        else
        {
            _logger.LogInformation($"Resuming from position {_items.CurrentIndex}");
        }
        
        while (_items.HasNext || _items.IsValidIndex)
        {
            var current = _items.Current;
            
            try
            {
                await ProcessItem(current);
                
                // Save progress periodically
                if (_items.CurrentIndex % 10 == 0)
                {
                    await SaveProgress();
                }
                
                if (!_items.MoveNext())
                    break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed at position {_items.CurrentIndex}");
                await SaveProgress();  // Save position for recovery
                throw;
            }
        }
    }
    
    private async Task SaveProgress()
    {
        // StateList position is automatically preserved
        var json = JsonConvert.SerializeObject(_items);
        await File.WriteAllTextAsync("progress.json", json);
    }
    
    private async Task<StateList<DataItem>> LoadProgress()
    {
        if (File.Exists("progress.json"))
        {
            var json = await File.ReadAllTextAsync("progress.json");
            return JsonConvert.DeserializeObject<StateList<DataItem>>(json);
        }
        return null;
    }
}
```

---

## Queue Management with QueueData

`QueueData<T>` provides persistent queue management using `StateList<T>` internally.

### Creating and Saving Queues

```csharp
// Create from StateList
var items = new StateList<WorkItem>(workItems);
items.Description = "Import Queue";

var queue = QueueData<WorkItem>.CreateFromStateList(
    items,
    name: "DailyImport",        // Category (PartitionKey)
    queueId: Guid.NewGuid().ToString()  // Unique queue ID
);

// Save to Azure Table Storage
await queue.SaveQueueAsync(accountName, accountKey);

// Check status
Console.WriteLine($"Queue: {queue.Name}");
Console.WriteLine($"Status: {queue.ProcessingStatus}");
Console.WriteLine($"Progress: {queue.PercentComplete:F1}%");
Console.WriteLine($"Position: {queue.LastProcessedIndex + 1}/{queue.TotalItemCount}");
```

### Processing Queues

```csharp
public class QueueProcessor
{
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly ILogger _logger;
    
    public async Task ProcessQueue(string queueId)
    {
        // Load existing queue
        var queue = await QueueData<WorkItem>.GetQueueAsync(
            queueId, _accountName, _accountKey);
        
        if (queue == null)
        {
            _logger.LogWarning($"Queue {queueId} not found");
            return;
        }
        
        _logger.LogInformation($"Processing queue from {queue.PercentComplete:F1}%");
        
        // Process items
        while (queue.Data.HasNext)
        {
            queue.Data.MoveNext();
            var item = queue.Data.Current;
            
            try
            {
                await ProcessWorkItem(item);
                
                // Update status
                queue.ProcessingStatus = $"Processed {queue.Data.CurrentIndex + 1}/{queue.TotalItemCount}";
                
                // Save progress every 10 items
                if (queue.Data.CurrentIndex % 10 == 0)
                {
                    await queue.SaveQueueAsync(_accountName, _accountKey);
                    _logger.LogInformation($"Progress saved: {queue.PercentComplete:F1}%");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed at item {queue.Data.CurrentIndex}");
                queue.ProcessingStatus = $"Error: {ex.Message}";
                await queue.SaveQueueAsync(_accountName, _accountKey);
                throw;
            }
        }
        
        // Delete completed queue
        await QueueData<WorkItem>.DeleteQueueAsync(
            queueId, _accountName, _accountKey);
        
        _logger.LogInformation("Queue processing completed");
    }
}
```

### Managing Multiple Queues

```csharp
// Get all queues in a category
var importQueues = await QueueData<WorkItem>.GetQueuesAsync(
    "DailyImport", accountName, accountKey);

Console.WriteLine($"Found {importQueues.Count} import queues:");
foreach (var q in importQueues)
{
    Console.WriteLine($"  Queue {q.QueueID}:");
    Console.WriteLine($"    Status: {q.ProcessingStatus}");
    Console.WriteLine($"    Progress: {q.PercentComplete:F1}%");
    Console.WriteLine($"    Items: {q.TotalItemCount}");
    Console.WriteLine($"    Last Updated: {q.Timestamp}");
}

// Delete completed queues
var completedIds = importQueues
    .Where(q => q.PercentComplete >= 100)
    .Select(q => q.QueueID)
    .ToList();

if (completedIds.Any())
{
    await QueueData<WorkItem>.DeleteQueuesAsync(
        completedIds, accountName, accountKey);
}

// Delete old queues
await QueueData<WorkItem>.DeleteQueuesMatchingAsync(
    accountName, accountKey,
    q => q.Timestamp < DateTime.UtcNow.AddDays(-7)
);

// Get queue statistics
var stats = importQueues.GroupBy(q => q.ProcessingStatus)
    .Select(g => new { Status = g.Key, Count = g.Count() });

foreach (var stat in stats)
{
    Console.WriteLine($"{stat.Status}: {stat.Count} queues");
}
```

### Queue Recovery Pattern

```csharp
public class ResilientQueueProcessor
{
    public async Task<bool> ProcessWithRetry(string queueId, int maxRetries = 3)
    {
        int retryCount = 0;
        
        while (retryCount < maxRetries)
        {
            try
            {
                var queue = await QueueData<DataItem>.GetQueueAsync(
                    queueId, accountName, accountKey);
                
                if (queue == null)
                    return false;
                
                // Check if already completed
                if (queue.PercentComplete >= 100)
                {
                    await QueueData<DataItem>.DeleteQueueAsync(
                        queueId, accountName, accountKey);
                    return true;
                }
                
                // Process remaining items
                while (queue.Data.HasNext)
                {
                    queue.Data.MoveNext();
                    await ProcessItem(queue.Data.Current);
                    
                    // Periodic save
                    if (queue.Data.CurrentIndex % 5 == 0)
                    {
                        await queue.SaveQueueAsync(accountName, accountKey);
                    }
                }
                
                // Success - delete queue
                await QueueData<DataItem>.DeleteQueueAsync(
                    queueId, accountName, accountKey);
                return true;
            }
            catch (Exception ex)
            {
                retryCount++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount)); // Exponential backoff
                
                _logger.LogWarning(ex, 
                    $"Queue processing failed. Retry {retryCount}/{maxRetries} after {delay}");
                
                await Task.Delay(delay);
            }
        }
        
        return false;
    }
}
```

---

## Session Management

The library provides distributed-safe session management that works across all application types.

### Initialization

```csharp
// ASP.NET Core
builder.Services.AddAzureTableSessions(options =>
{
    options.AccountName = "myaccount";
    options.AccountKey = "mykey";
    options.SessionTimeout = TimeSpan.FromMinutes(20);
    options.IdStrategy = SessionIdStrategy.HttpContext;
    options.BatchWriteDelay = TimeSpan.FromMilliseconds(500);
});

// Desktop Application
SessionManager.Initialize(accountName, accountKey, options =>
{
    options.IdStrategy = SessionIdStrategy.UserMachine;
    options.ApplicationName = "MyDesktopApp";
    options.AutoCommit = true;
});

// Console/Service
SessionManager.Initialize(accountName, accountKey, options =>
{
    options.IdStrategy = SessionIdStrategy.MachineProcess;
    options.EnableAutoCleanup = true;
    options.CleanupInterval = TimeSpan.FromHours(1);
});
```

### Using Sessions

```csharp
// Set session values
SessionManager.Current["UserId"] = "user123";
SessionManager.Current["UserName"] = "John Doe";
SessionManager.Current["LoginTime"] = DateTime.UtcNow.ToString("O");
SessionManager.Current["Preferences"] = JsonConvert.SerializeObject(new
{
    Theme = "dark",
    Language = "en-US",
    TimeZone = "UTC"
});

// Get session values
var userId = SessionManager.Current["UserId"]?.Value;
var userName = SessionManager.Current["UserName"]?.Value;

if (!string.IsNullOrEmpty(userId))
{
    var prefs = JsonConvert.DeserializeObject<UserPreferences>(
        SessionManager.Current["Preferences"]?.Value);
}

// Check if key exists
if (SessionManager.Current.ContainsKey("CartItems"))
{
    var cartJson = SessionManager.Current["CartItems"]?.Value;
    var cart = JsonConvert.DeserializeObject<List<CartItem>>(cartJson);
}

// Remove items
await SessionManager.Current.RemoveAsync("TempData");

// Clear entire session
await SessionManager.Current.ClearAsync();

// Force flush (usually automatic)
await SessionManager.Current.FlushAsync();
```

### Session Statistics

```csharp
var stats = SessionManager.GetStatistics();

Console.WriteLine("Session Statistics:");
Console.WriteLine($"  Active Sessions: {stats.ActiveSessions}");
Console.WriteLine($"  Total Reads: {stats.TotalReads}");
Console.WriteLine($"  Total Writes: {stats.TotalWrites}");
Console.WriteLine($"  Successful Writes: {stats.SuccessfulWrites}");
Console.WriteLine($"  Failed Writes: {stats.FailedWrites}");
Console.WriteLine($"  Write Success Rate: {stats.WriteSuccessRate:F1}%");
Console.WriteLine($"  Average Write Latency: {stats.AverageWriteLatency.TotalMilliseconds}ms");
Console.WriteLine($"  Uptime: {stats.Uptime}");
Console.WriteLine($"  Last Activity: {stats.LastActivity}");
Console.WriteLine($"  Last Flush: {stats.LastFlush}");
Console.WriteLine($"  Last Cleanup: {stats.LastCleanup}");
```

### Advanced Session Patterns

```csharp
// Shopping cart management
public class ShoppingCartService
{
    public void AddToCart(string productId, int quantity)
    {
        var cartJson = SessionManager.Current["Cart"]?.Value ?? "[]";
        var cart = JsonConvert.DeserializeObject<List<CartItem>>(cartJson);
        
        var existing = cart.FirstOrDefault(i => i.ProductId == productId);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            cart.Add(new CartItem { ProductId = productId, Quantity = quantity });
        }
        
        SessionManager.Current["Cart"] = JsonConvert.SerializeObject(cart);
        SessionManager.Current["CartUpdated"] = DateTime.UtcNow.ToString("O");
    }
    
    public decimal GetCartTotal()
    {
        var cartJson = SessionManager.Current["Cart"]?.Value;
        if (string.IsNullOrEmpty(cartJson))
            return 0;
        
        var cart = JsonConvert.DeserializeObject<List<CartItem>>(cartJson);
        return cart.Sum(i => i.Price * i.Quantity);
    }
}

// User preferences
public class UserPreferencesService
{
    private const string PrefsKey = "UserPreferences";
    
    public UserPreferences GetPreferences()
    {
        var json = SessionManager.Current[PrefsKey]?.Value;
        if (string.IsNullOrEmpty(json))
        {
            return new UserPreferences
            {
                Theme = "light",
                Language = "en-US",
                PageSize = 20
            };
        }
        
        return JsonConvert.DeserializeObject<UserPreferences>(json);
    }
    
    public void SavePreferences(UserPreferences prefs)
    {
        SessionManager.Current[PrefsKey] = JsonConvert.SerializeObject(prefs);
    }
}
```

---

## Universal Logging System

The v3.1 logging system provides a complete ILogger implementation that works across all .NET application types.

### Configuration

```csharp
public class AzureTableLoggerOptions
{
    public string AccountName { get; set; }
    public string AccountKey { get; set; }
    public string TableName { get; set; } = "ApplicationLogs";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public int RetentionDays { get; set; } = 60;
    public bool EnableAutoCleanup { get; set; } = true;
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan? CleanupTimeOfDay { get; set; }  // e.g., "02:00:00"
    public Dictionary<LogLevel, int> RetentionByLevel { get; set; }
    public int BatchSize { get; set; } = 100;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public string ApplicationName { get; set; }
    public string Environment { get; set; }
}
```

### Basic Logging

```csharp
// Get a logger
var logger = AzureTableLogging.CreateLogger<MyClass>();

// Standard logging
logger.LogTrace("Detailed trace information");
logger.LogDebug("Debug information");
logger.LogInformation("Information message");
logger.LogWarning("Warning message");
logger.LogError("Error message");
logger.LogCritical("Critical error message");

// Structured logging
logger.LogInformation("Processing order {OrderId} for customer {CustomerId}", 
    orderId, customerId);

// With exception
try
{
    ProcessOrder(order);
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to process order {OrderId}", order.Id);
}

// With event ID
logger.LogInformation(new EventId(1001, "UserLogin"), 
    "User {UserId} logged in from {IpAddress}", userId, ipAddress);
```

### Scoped Logging

```csharp
// Create scope for correlated logs
using (logger.BeginScope("Transaction {TransactionId}", transactionId))
{
    logger.LogInformation("Starting transaction");
    
    using (logger.BeginScope(new Dictionary<string, object>
    {
        { "UserId", userId },
        { "OrderId", orderId }
    }))
    {
        logger.LogInformation("Processing payment");
        // All logs within scope include transaction, user, and order context
        
        ProcessPayment();
        
        logger.LogInformation("Payment completed");
    }
    
    logger.LogInformation("Transaction completed");
}
```

### Desktop Application Extensions

```csharp
// Log from UI thread safely (WPF)
private void Button_Click(object sender, RoutedEventArgs e)
{
    _logger.LogFromUI(LogLevel.Information, "Button clicked by user");
}

// Log user actions
_logger.LogUserAction("FileOpen", new 
{ 
    FileName = "document.pdf",
    Size = fileInfo.Length,
    Location = fileInfo.DirectoryName
});

// Log application lifecycle
_logger.LogApplicationLifecycle("Startup");
_logger.LogApplicationLifecycle("Shutdown");
_logger.LogApplicationLifecycle("Hibernation");
```

### Console/Service Extensions

```csharp
// Progress tracking
using (_logger.LogProgress("DataImport", totalRecords))
{
    for (int i = 0; i < totalRecords; i++)
    {
        ProcessRecord(records[i]);
        // Progress automatically logged every 10 seconds
    }
}

// Heartbeat monitoring
var heartbeatTimer = new Timer(_ => 
{
    _logger.LogHeartbeat("DataProcessor");
}, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

// Batch results
var stopwatch = Stopwatch.StartNew();
var (processed, failed) = await ProcessBatch(items);
stopwatch.Stop();

_logger.LogBatchResult("Import", 
    processed: processed, 
    failed: failed, 
    duration: stopwatch.Elapsed);
```

### Automatic Cleanup

```csharp
// Configure retention by level
var options = new AzureTableLoggerOptions
{
    RetentionDays = 30,  // Default retention
    EnableAutoCleanup = true,
    CleanupTimeOfDay = TimeSpan.Parse("02:00:00"),  // Run at 2 AM
    RetentionByLevel = new Dictionary<LogLevel, int>
    {
        { LogLevel.Trace, 7 },      // Keep trace logs for 7 days
        { LogLevel.Debug, 14 },      // Keep debug logs for 14 days
        { LogLevel.Information, 30 }, // Keep info logs for 30 days
        { LogLevel.Warning, 60 },     // Keep warnings for 60 days
        { LogLevel.Error, 90 },       // Keep errors for 90 days
        { LogLevel.Critical, 180 }    // Keep critical logs for 180 days
    }
};

// Manual cleanup
await logger.CleanupOldLogsAsync(accountName, accountKey, daysOld: 30);

// Cleanup by level
await logger.CleanupLogsByLevelAsync(
    accountName, accountKey, 
    LogLevel.Trace, 
    daysOld: 7);
```

---

## Error Handling & ErrorLogData

The library provides comprehensive error logging with automatic caller information capture.

### Basic Error Logging

```csharp
try
{
    // Your code
    await ProcessData();
}
catch (Exception ex)
{
    // Create error log with automatic caller info
    var errorLog = ErrorLogData.CreateWithCallerInfo(
        $"Data processing failed: {ex.Message}",
        ErrorCodeTypes.Error,
        customerId: "CUST123"
    );
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}
```

### Advanced Error Logging

```csharp
// With state object
var state = new 
{
    Operation = "DataImport",
    FileName = "import.csv",
    RecordCount = 1000,
    UserId = currentUserId
};

try
{
    await ImportData();
}
catch (Exception ex)
{
    var errorLog = ErrorLogData.CreateWithCallerInfo(
        state,
        ex,
        (s, e) => $"Operation {s.Operation} failed for file {s.FileName}: {e.Message}",
        ErrorCodeTypes.Critical,
        customerId: currentUserId
    );
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}
```

### Error Severity Levels

```csharp
// Information - General logging
var info = ErrorLogData.CreateWithCallerInfo(
    "User logged in successfully",
    ErrorCodeTypes.Information,
    userId
);

// Warning - Non-critical issues
var warning = ErrorLogData.CreateWithCallerInfo(
    "API rate limit approaching: 80% utilized",
    ErrorCodeTypes.Warning
);

// Error - Recoverable errors
var error = ErrorLogData.CreateWithCallerInfo(
    "Email delivery failed, will retry",
    ErrorCodeTypes.Error,
    customerId
);

// Critical - System failures
var critical = ErrorLogData.CreateWithCallerInfo(
    "Database connection lost",
    ErrorCodeTypes.Critical
);

// Unknown - Unclassified issues
var unknown = ErrorLogData.CreateWithCallerInfo(
    "Unexpected state in workflow",
    ErrorCodeTypes.Unknown
);
```

### Error Log Maintenance

```csharp
// Clean up old logs
await ErrorLogData.ClearOldDataAsync(
    accountName, accountKey, 
    daysOld: 60
);

// Clean up by severity
await ErrorLogData.ClearOldDataByType(
    accountName, accountKey,
    ErrorCodeTypes.Information,
    daysOld: 7
);

// Scheduled cleanup
public class ErrorLogMaintenanceService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Clean different levels with different retention
                await ErrorLogData.ClearOldDataByType(
                    _accountName, _accountKey,
                    ErrorCodeTypes.Information, 7);
                    
                await ErrorLogData.ClearOldDataByType(
                    _accountName, _accountKey,
                    ErrorCodeTypes.Warning, 30);
                    
                await ErrorLogData.ClearOldDataByType(
                    _accountName, _accountKey,
                    ErrorCodeTypes.Error, 90);
                    
                // Critical errors kept for 180 days
                await ErrorLogData.ClearOldDataByType(
                    _accountName, _accountKey,
                    ErrorCodeTypes.Critical, 180);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error log cleanup failed");
            }
            
            // Run daily
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
```

---

## Azure Blob Storage Operations

The `AzureBlobs` class provides comprehensive blob storage operations with tag-based indexing.

### Basic Setup

```csharp
// Create blob service for a specific container
var blobs = new AzureBlobs(accountName, accountKey, "documents");

// Configure allowed file types
blobs.AddAllowedFileType(".pdf", "application/pdf");
blobs.AddAllowedFileType(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
blobs.AddAllowedFileType(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

// Or add multiple at once
blobs.AddAllowedFileType(new Dictionary<string, string>
{
    { ".jpg", "image/jpeg" },
    { ".png", "image/png" },
    { ".gif", "image/gif" }
});
```

### Uploading Files

```csharp
// Upload with tags (searchable) and metadata (not searchable)
var uri = await blobs.UploadFileAsync(
    @"C:\Documents\invoice.pdf",
    enforceFileTypeRestriction: true,
    maxFileSizeBytes: 10 * 1024 * 1024,  // 10MB limit
    indexTags: new Dictionary<string, string>
    {
        { "documentType", "invoice" },
        { "year", "2025" },
        { "month", "08" },
        { "status", "pending" },
        { "customerId", "CUST123" }
    },
    metadata: new Dictionary<string, string>
    {
        { "uploadedBy", "john.doe" },
        { "department", "accounting" },
        { "amount", "5432.10" },
        { "dueDate", "2025-09-15" }
    }
);

Console.WriteLine($"File uploaded to: {uri}");
```

### Uploading Streams

```csharp
// Upload from memory stream
using var stream = new MemoryStream(pdfBytes);
var uri = await blobs.UploadStreamAsync(
    stream,
    fileName: "report.pdf",
    contentType: "application/pdf",
    indexTags: new Dictionary<string, string>
    {
        { "reportType", "monthly" },
        { "month", DateTime.Now.ToString("yyyy-MM") }
    }
);
```

### Searching Blobs

```csharp
// Search by tags using lambda expressions
var invoices = await blobs.GetCollectionAsync(
    b => b.Tags["documentType"] == "invoice" &&
         b.Tags["year"] == "2025" &&
         b.Tags["status"] == "pending"
);

// Complex searches with date filtering
var recentLargeFiles = await blobs.GetCollectionAsync(
    b => b.UploadDate > DateTime.Today.AddDays(-7) &&
         b.Size > 1024 * 1024  // Files larger than 1MB
);

// Search with multiple criteria
var results = await blobs.SearchBlobsAsync(
    searchText: "invoice",
    tagFilters: new Dictionary<string, string>
    {
        { "status", "pending" },
        { "customerId", "CUST123" }
    },
    startDate: DateTime.Today.AddMonths(-1),
    endDate: DateTime.Today
);

// Direct tag query
var tagQuery = "documentType = 'invoice' AND year = '2025'";
var queryResults = await blobs.SearchBlobsByTagsAsync(tagQuery);
```

### Downloading Files

```csharp
// Download single file
var success = await blobs.DownloadFileAsync(
    "invoice.pdf",
    @"C:\Downloads\invoice.pdf"
);

// Download to stream
using var stream = new MemoryStream();
await blobs.DownloadToStreamAsync("report.pdf", stream);
var bytes = stream.ToArray();

// Batch download based on criteria
var downloadResults = await blobs.DownloadFilesAsync(
    b => b.Tags["documentType"] == "report" && 
         b.Tags["year"] == "2025",
    @"C:\Downloads\Reports",
    preserveOriginalNames: true
);

foreach (var result in downloadResults)
{
    if (result.Value.Success)
    {
        Console.WriteLine($"Downloaded: {result.Key} to {result.Value.DestinationPath}");
    }
    else
    {
        Console.WriteLine($"Failed: {result.Key} - {result.Value.ErrorMessage}");
    }
}
```

### Managing Blob Tags

```csharp
// Update tags on existing blob
await blobs.UpdateBlobTagsAsync("document.pdf", new Dictionary<string, string>
{
    { "status", "approved" },
    { "approvedBy", "manager" },
    { "approvalDate", DateTime.UtcNow.ToString("O") }
});

// Get tags for a blob
var tags = await blobs.GetBlobTagsAsync("document.pdf");
foreach (var tag in tags)
{
    Console.WriteLine($"{tag.Key}: {tag.Value}");
}
```

### Batch Operations

```csharp
// Upload multiple files
var files = Directory.GetFiles(@"C:\Documents", "*.pdf")
    .Select(f => new BlobUploadInfo
    {
        FilePath = f,
        IndexTags = new Dictionary<string, string>
        {
            { "batch", "import_2025_08" },
            { "source", "scanner" },
            { "date", DateTime.Today.ToString("yyyy-MM-dd") }
        },
        Metadata = new Dictionary<string, string>
        {
            { "originalLocation", Path.GetDirectoryName(f) }
        }
    });

var uploadResults = await blobs.UploadMultipleFilesAsync(
    files,
    enforceFileTypeRestriction: true,
    maxFileSizeBytes: 50 * 1024 * 1024  // 50MB
);

// Process results
var successCount = uploadResults.Count(r => r.Value.Success);
var failureCount = uploadResults.Count(r => !r.Value.Success);
Console.WriteLine($"Uploaded: {successCount}, Failed: {failureCount}");

// Delete multiple blobs
var deleteResults = await blobs.DeleteMultipleBlobsAsync(
    b => b.Tags["batch"] == "import_2025_07" &&  // Last month's batch
         b.Tags["status"] == "processed"
);
```

---

## Performance Optimization

### Caching Strategies

```csharp
// Cache DataAccess instances
public class DataAccessCache
{
    private readonly ConcurrentDictionary<Type, object> _cache = new();
    private readonly string _accountName;
    private readonly string _accountKey;
    
    public DataAccess<T> GetDataAccess<T>() where T : TableEntityBase, ITableExtra, new()
    {
        return (DataAccess<T>)_cache.GetOrAdd(typeof(T), 
            _ => new DataAccess<T>(_accountName, _accountKey));
    }
}

// Usage
var cache = new DataAccessCache(accountName, accountKey);
var customerAccess = cache.GetDataAccess<Customer>();
var orderAccess = cache.GetDataAccess<Order>();
```

### Batch Processing Optimization

```csharp
// Optimal batch processing
public async Task OptimizedBatchInsert<T>(List<T> items) 
    where T : TableEntityBase, ITableExtra, new()
{
    // Group by partition for optimal batching
    var grouped = items.GroupBy(i => i.PartitionKey);
    
    var tasks = new List<Task<BatchUpdateResult>>();
    
    foreach (var group in grouped)
    {
        // Process each partition group in parallel
        tasks.Add(ProcessPartitionGroup(group.ToList()));
    }
    
    var results = await Task.WhenAll(tasks);
    
    // Aggregate results
    var totalSuccess = results.Sum(r => r.SuccessfulItems);
    var totalFailed = results.Sum(r => r.FailedItems);
    
    Console.WriteLine($"Total: {totalSuccess} succeeded, {totalFailed} failed");
}

private async Task<BatchUpdateResult> ProcessPartitionGroup<T>(List<T> items)
    where T : TableEntityBase, ITableExtra, new()
{
    var dataAccess = new DataAccess<T>(_accountName, _accountKey);
    return await dataAccess.BatchUpdateListAsync(items);
}
```

### Query Optimization

```csharp
// Pre-compute fields for better query performance
public class OptimizedEntity : TableEntityBase, ITableExtra
{
    private string _name;
    private string _email;
    
    public string Name 
    { 
        get => _name;
        set
        {
            _name = value;
            NameLowercase = value?.ToLower();
            UpdateSearchText();
        }
    }
    
    public string Email
    {
        get => _email;
        set
        {
            _email = value;
            EmailDomain = value?.Split('@').LastOrDefault();
            UpdateSearchText();
        }
    }
    
    // Pre-computed fields for server-side filtering
    public string NameLowercase { get; set; }
    public string EmailDomain { get; set; }
    public string SearchText { get; set; }
    public int YearMonth { get; set; }  // For date-based partitioning
    
    private void UpdateSearchText()
    {
        SearchText = $"{Name} {Email} {Phone}".ToLower();
    }
    
    public void BeforeSave()
    {
        YearMonth = int.Parse(DateTime.UtcNow.ToString("yyyyMM"));
        UpdateSearchText();
    }
    
    public string TableReference => "OptimizedEntities";
    public string GetIDValue() => this.RowKey;
}
```

### Connection Pooling

```csharp
// Singleton pattern for connection management
public class StorageConnectionManager
{
    private static readonly Lazy<StorageConnectionManager> _instance = 
        new(() => new StorageConnectionManager());
    
    private readonly ConcurrentDictionary<string, CloudTableClient> _clients = new();
    
    public static StorageConnectionManager Instance => _instance.Value;
    
    public CloudTableClient GetClient(string accountName, string accountKey)
    {
        var key = $"{accountName}:{accountKey.GetHashCode()}";
        
        return _clients.GetOrAdd(key, _ =>
        {
            var credentials = new StorageCredentials(accountName, accountKey);
            var account = new CloudStorageAccount(credentials, true);
            return account.CreateCloudTableClient();
        });
    }
}
```

### Memory-Efficient Processing

```csharp
// Stream processing for large datasets
public async IAsyncEnumerable<T> StreamDataAsync<T>(
    Expression<Func<T, bool>> predicate,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
    where T : TableEntityBase, ITableExtra, new()
{
    var dataAccess = new DataAccess<T>(_accountName, _accountKey);
    string continuationToken = null;
    
    do
    {
        var page = await dataAccess.GetPagedCollectionAsync(
            predicate,
            pageSize: 100,
            continuationToken: continuationToken
        );
        
        foreach (var item in page.Data)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;
                
            yield return item;
        }
        
        continuationToken = page.ContinuationToken;
        
    } while (!string.IsNullOrEmpty(continuationToken));
}

// Usage
await foreach (var customer in StreamDataAsync<Customer>(c => c.Status == "Active"))
{
    await ProcessCustomer(customer);
}
```

---

## Migration Guides

### From v2.5 to v3.1

Version 3.1 is backward compatible with v2.5. Key additions include:

1. **Add Universal Logging**
```csharp
// OLD: Manual ErrorLogData
var error = new ErrorLogData(ex, "Error", ErrorCodeTypes.Error);
await error.LogErrorAsync(accountName, accountKey);

// NEW: ILogger with automatic everything
var logger = AzureTableLogging.CreateLogger<MyClass>();
logger.LogError(ex, "Error occurred");
```

2. **Add Session Management**
```csharp
// OLD: Manual Session class
using (var session = new Session(accountName, accountKey, sessionId))
{
    session["key"] = "value";
    session.CommitData();
}

// NEW: Global SessionManager
SessionManager.Initialize(accountName, accountKey);
SessionManager.Current["key"] = "value";
// Auto-commits
```

3. **Automatic Cleanup**
```csharp
// OLD: Manual cleanup
await ErrorLogData.ClearOldDataAsync(accountName, accountKey, 60);

// NEW: Automatic with configuration
builder.Logging.AddAzureTableLogging(options =>
{
    options.EnableAutoCleanup = true;
    options.RetentionDays = 60;
    options.RetentionByLevel = new Dictionary<LogLevel, int>
    {
        { LogLevel.Trace, 7 },
        { LogLevel.Error, 90 }
    };
});
```

### From v1.x to v3.1

Major architectural changes require code updates:

1. **Entity Definition Changes**
```csharp
// v1.x
public class OldEntity : TableEntity
{
    public string Data { get; set; }
}

// v3.1
public class NewEntity : TableEntityBase, ITableExtra
{
    public string Data { get; set; }  // Auto-chunking for large data
    
    public string TableReference => "MyTable";
    public string GetIDValue() => this.RowKey;
}
```

2. **Query Changes**
```csharp
// v1.x
var query = new TableQuery<OldEntity>()
    .Where(TableQuery.GenerateFilterCondition("Status", QueryComparisons.Equal, "Active"));
var results = table.ExecuteQuery(query);

// v3.1
var dataAccess = new DataAccess<NewEntity>(accountName, accountKey);
var results = await dataAccess.GetCollectionAsync(x => x.Status == "Active");
```

---

## Best Practices & Patterns

### Entity Design Best Practices

1. **Choose Appropriate Keys**
```csharp
public class BestPracticeEntity : TableEntityBase, ITableExtra
{
    // GOOD: Use meaningful partition keys for logical grouping
    public string TenantId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    // GOOD: Use GUIDs or sequential IDs for row keys
    public string EntityId
    {
        get => this.RowKey;
        set => this.RowKey = value ?? Guid.NewGuid().ToString();
    }
    
    // Pre-compute for queries
    public string YearMonth { get; set; }  // Format: "202508"
    public string SearchableText { get; set; }  // Lowercase concatenated fields
    
    public string TableReference => "BestPracticeEntities";
    public string GetIDValue() => this.EntityId;
}
```

2. **Handle Large Data**
```csharp
public class DocumentEntity : TableEntityBase, ITableExtra
{
    // Store large JSON in auto-chunked property
    public string DocumentJson { get; set; }
    
    // Store binary data as base64
    private byte[] _binaryData;
    public byte[] BinaryData
    {
        get => _binaryData;
        set
        {
            _binaryData = value;
            BinaryDataBase64 = Convert.ToBase64String(value);
        }
    }
    
    public string BinaryDataBase64 { get; set; }  // Auto-chunked
    
    public string TableReference => "Documents";
    public string GetIDValue() => this.RowKey;
}
```

### Repository Pattern

```csharp
public interface IRepository<T> where T : TableEntityBase, ITableExtra, new()
{
    Task<T> GetByIdAsync(string id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate);
    Task<bool> AddAsync(T entity);
    Task<bool> UpdateAsync(T entity);
    Task<bool> DeleteAsync(string id);
    Task<BatchUpdateResult> AddRangeAsync(IEnumerable<T> entities);
}

public class TableStorageRepository<T> : IRepository<T> 
    where T : TableEntityBase, ITableExtra, new()
{
    private readonly DataAccess<T> _dataAccess;
    private readonly ILogger<TableStorageRepository<T>> _logger;
    
    public TableStorageRepository(string accountName, string accountKey, ILogger<TableStorageRepository<T>> logger)
    {
        _dataAccess = new DataAccess<T>(accountName, accountKey);
        _logger = logger;
    }
    
    public async Task<T> GetByIdAsync(string id)
    {
        try
        {
            return await _dataAccess.GetRowObjectAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get entity {Id}", id);
            throw;
        }
    }
    
    public async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _dataAccess.GetAllTableDataAsync();
    }
    
    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
    {
        return await _dataAccess.GetCollectionAsync(predicate);
    }
    
    public async Task<bool> AddAsync(T entity)
    {
        try
        {
            await _dataAccess.ManageDataAsync(entity, TableOperationType.InsertOrReplace);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add entity");
            return false;
        }
    }
    
    public async Task<bool> UpdateAsync(T entity)
    {
        try
        {
            await _dataAccess.ManageDataAsync(entity, TableOperationType.InsertOrMerge);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update entity");
            return false;
        }
    }
    
    public async Task<bool> DeleteAsync(string id)
    {
        try
        {
            var entity = await GetByIdAsync(id);
            if (entity != null)
            {
                await _dataAccess.ManageDataAsync(entity, TableOperationType.Delete);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete entity {Id}", id);
            return false;
        }
    }
    
    public async Task<BatchUpdateResult> AddRangeAsync(IEnumerable<T> entities)
    {
        return await _dataAccess.BatchUpdateListAsync(entities.ToList());
    }
}
```

### Unit of Work Pattern

```csharp
public interface IUnitOfWork : IDisposable
{
    IRepository<Customer> Customers { get; }
    IRepository<Order> Orders { get; }
    IRepository<Product> Products { get; }
    Task<bool> CommitAsync();
}

public class TableStorageUnitOfWork : IUnitOfWork
{
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly ILogger<TableStorageUnitOfWork> _logger;
    private readonly Dictionary<Type, object> _repositories = new();
    
    public TableStorageUnitOfWork(string accountName, string accountKey, ILogger<TableStorageUnitOfWork> logger)
    {
        _accountName = accountName;
        _accountKey = accountKey;
        _logger = logger;
    }
    
    public IRepository<Customer> Customers => GetRepository<Customer>();
    public IRepository<Order> Orders => GetRepository<Order>();
    public IRepository<Product> Products => GetRepository<Product>();
    
    private IRepository<T> GetRepository<T>() where T : TableEntityBase, ITableExtra, new()
    {
        var type = typeof(T);
        if (!_repositories.ContainsKey(type))
        {
            var repoLogger = _logger as ILogger<TableStorageRepository<T>>;
            _repositories[type] = new TableStorageRepository<T>(_accountName, _accountKey, repoLogger);
        }
        return (IRepository<T>)_repositories[type];
    }
    
    public async Task<bool> CommitAsync()
    {
        // In Table Storage, operations are already committed
        // This is here for pattern compatibility
        return await Task.FromResult(true);
    }
    
    public void Dispose()
    {
        _repositories.Clear();
    }
}
```

---

## Troubleshooting Guide

### Common Issues and Solutions

**Issue**: StateList CurrentIndex not preserved after serialization
- **Cause**: Incorrect serialization settings
- **Solution**: Ensure JsonPropertyName attributes are present
```csharp
// Correct serialization
var json = JsonConvert.SerializeObject(stateList);
var restored = JsonConvert.DeserializeObject<StateList<T>>(json);
```

**Issue**: Batch operations failing with "Request body too large"
- **Cause**: Exceeding Azure's 100-entity or 4MB limit
- **Solution**: Library handles this automatically, but ensure entities aren't too large individually

**Issue**: Lambda queries returning unexpected results
- **Cause**: Mix of server and client operations
- **Solution**: Understand which operations run where
```csharp
// Debug hybrid filtering
var dataAccess = new DataAccess<Entity>(accountName, accountKey);
// Add logging to see the split
```

**Issue**: Session data not persisting
- **Cause**: Batch write delay not expired
- **Solution**: Force flush or wait for automatic flush
```csharp
await SessionManager.Current.FlushAsync();
```

**Issue**: Large field causing "Property value too large" error
- **Cause**: Field exceeds 64KB and entity doesn't inherit from TableEntityBase
- **Solution**: Ensure entity inherits from TableEntityBase for automatic chunking

**Issue**: Blob tag searches not finding results
- **Cause**: Tags are case-sensitive and must match exactly
- **Solution**: Standardize tag values (e.g., always lowercase)

**Issue**: Memory issues with large datasets
- **Cause**: Loading entire dataset into memory
- **Solution**: Use pagination or streaming
```csharp
// Use async enumerable for streaming
await foreach (var item in StreamDataAsync<Entity>(predicate))
{
    await ProcessItem(item);
}
```

### Performance Troubleshooting

**Slow Queries**
1. Check if operations are client-side
2. Add pre-computed fields for common queries
3. Use appropriate partition keys
4. Enable query metrics

**High Latency**
1. Reuse DataAccess instances
2. Use batch operations
3. Consider geographic proximity to storage account
4. Check for throttling (503 errors)

**Memory Issues**
1. Use pagination instead of GetAllTableDataAsync
2. Process in batches
3. Dispose of resources properly
4. Use streaming for large operations

---

## API Reference

### Core Classes

**DataAccess<T>**
- `ManageDataAsync(T entity, TableOperationType operation)`
- `GetAllTableDataAsync()`
- `GetCollectionAsync(Expression<Func<T, bool>> predicate)`
- `GetRowObjectAsync(string rowKey)`
- `BatchUpdateListAsync(List<T> data, TableOperationType operation, IProgress<BatchUpdateProgress> progress)`
- `GetPagedCollectionAsync(int pageSize, string continuationToken)`

**TableEntityBase**
- Automatic field chunking for properties > 32KB
- `PartitionKey` - Logical grouping
- `RowKey` - Unique identifier
- `Timestamp` - Last modified
- `ETag` - Optimistic concurrency

**DynamicEntity**
- Runtime-defined properties
- Pattern-based key detection
- `SetProperty(string name, object value)`
- `GetProperty<T>(string name)`
- `HasProperty(string name)`

**StateList<T>**
- Position-aware collection
- `CurrentIndex` - Preserved through serialization
- `MoveNext()`, `MovePrevious()`, `First()`, `Last()`
- `HasNext`, `HasPrevious` - Navigation checks
- Full IList<T> implementation

**QueueData<T>**
- Persistent queue management
- `ProcessingStatus` - Current state
- `PercentComplete` - Progress tracking
- `SaveQueueAsync()` - Persist to storage
- `GetQueueAsync()` - Retrieve queue

**SessionManager**
- Global session access
- `Current` - Active session
- `Initialize()` - Setup
- `GetStatistics()` - Performance metrics

**AzureBlobs**
- Blob storage operations
- Tag-based indexing (max 10 tags)
- `UploadFileAsync()`, `DownloadFileAsync()`
- `GetCollectionAsync()` - Lambda search
- `UpdateBlobTagsAsync()` - Tag management

**ErrorLogData**
- Comprehensive error logging
- `CreateWithCallerInfo()` - Automatic caller capture
- `LogErrorAsync()` - Persist to storage
- `ClearOldDataAsync()` - Maintenance

---

## Code Examples & Recipes

### Recipe: Multi-Tenant Application

```csharp
public class TenantEntity : TableEntityBase, ITableExtra
{
    public string TenantId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string EntityId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string TableReference => $"Tenant_{TenantId}_Data";
    public string GetIDValue() => this.EntityId;
}

public class MultiTenantService
{
    private readonly ConcurrentDictionary<string, DataAccess<TenantEntity>> _tenantAccess = new();
    
    public DataAccess<TenantEntity> GetTenantAccess(string tenantId)
    {
        return _tenantAccess.GetOrAdd(tenantId, 
            _ => new DataAccess<TenantEntity>(_accountName, _accountKey));
    }
    
    public async Task<TenantEntity> GetEntityAsync(string tenantId, string entityId)
    {
        var access = GetTenantAccess(tenantId);
        return await access.GetRowObjectAsync(entityId);
    }
}
```

### Recipe: Event Sourcing

```csharp
public class Event : TableEntityBase, ITableExtra
{
    public string AggregateId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string EventId
    {
        get => this.RowKey;
        set => this.RowKey = value ?? $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid()}";
    }
    
    public string EventType { get; set; }
    public string EventData { get; set; }  // JSON serialized
    public DateTime OccurredAt { get; set; }
    public string UserId { get; set; }
    
    public string TableReference => "Events";
    public string GetIDValue() => this.EventId;
}

public class EventStore
{
    private readonly DataAccess<Event> _dataAccess;
    
    public async Task AppendEventAsync(string aggregateId, string eventType, object eventData)
    {
        var evt = new Event
        {
            AggregateId = aggregateId,
            EventType = eventType,
            EventData = JsonConvert.SerializeObject(eventData),
            OccurredAt = DateTime.UtcNow,
            UserId = GetCurrentUserId()
        };
        
        await _dataAccess.ManageDataAsync(evt);
    }
    
    public async Task<List<Event>> GetAggregateEventsAsync(string aggregateId)
    {
        var events = await _dataAccess.GetCollectionAsync(aggregateId);
        return events.OrderBy(e => e.OccurredAt).ToList();
    }
    
    public async Task<T> RebuildAggregateAsync<T>(string aggregateId) where T : new()
    {
        var events = await GetAggregateEventsAsync(aggregateId);
        var aggregate = new T();
        
        foreach (var evt in events)
        {
            ApplyEvent(aggregate, evt);
        }
        
        return aggregate;
    }
}
```

### Recipe: Caching Layer

```csharp
public class CachedRepository<T> where T : TableEntityBase, ITableExtra, new()
{
    private readonly DataAccess<T> _dataAccess;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheExpiration;
    
    public CachedRepository(string accountName, string accountKey, IMemoryCache cache)
    {
        _dataAccess = new DataAccess<T>(accountName, accountKey);
        _cache = cache;
        _cacheExpiration = TimeSpan.FromMinutes(5);
    }
    
    public async Task<T> GetByIdAsync(string id)
    {
        var cacheKey = $"{typeof(T).Name}:{id}";
        
        if (_cache.TryGetValue<T>(cacheKey, out var cached))
        {
            return cached;
        }
        
        var entity = await _dataAccess.GetRowObjectAsync(id);
        
        if (entity != null)
        {
            _cache.Set(cacheKey, entity, _cacheExpiration);
        }
        
        return entity;
    }
    
    public async Task<bool> UpdateAsync(T entity)
    {
        await _dataAccess.ManageDataAsync(entity);
        
        // Invalidate cache
        var cacheKey = $"{typeof(T).Name}:{entity.GetIDValue()}";
        _cache.Remove(cacheKey);
        
        return true;
    }
    
    public async Task InvalidatePartitionCacheAsync(string partitionKey)
    {
        // Remove all cached items for this partition
        // Implementation depends on your cache provider
    }
}
```

### Recipe: Audit Trail

```csharp
public abstract class AuditableEntity : TableEntityBase
{
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string ModifiedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string DeletedBy { get; set; }
    
    public void SetCreated(string userId)
    {
        CreatedAt = DateTime.UtcNow;
        CreatedBy = userId;
    }
    
    public void SetModified(string userId)
    {
        ModifiedAt = DateTime.UtcNow;
        ModifiedBy = userId;
    }
    
    public void SetDeleted(string userId)
    {
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = userId;
    }
}

public class AuditService<T> where T : AuditableEntity, ITableExtra, new()
{
    private readonly DataAccess<T> _dataAccess;
    private readonly string _currentUserId;
    
    public async Task<T> CreateAsync(T entity)
    {
        entity.SetCreated(_currentUserId);
        await _dataAccess.ManageDataAsync(entity);
        await LogAuditAsync("Create", entity);
        return entity;
    }
    
    public async Task<T> UpdateAsync(T entity)
    {
        entity.SetModified(_currentUserId);
        await _dataAccess.ManageDataAsync(entity);
        await LogAuditAsync("Update", entity);
        return entity;
    }
    
    public async Task<bool> SoftDeleteAsync(string id)
    {
        var entity = await _dataAccess.GetRowObjectAsync(id);
        if (entity != null)
        {
            entity.SetDeleted(_currentUserId);
            await _dataAccess.ManageDataAsync(entity);
            await LogAuditAsync("Delete", entity);
            return true;
        }
        return false;
    }
    
    private async Task LogAuditAsync(string action, T entity)
    {
        var auditLog = new AuditLog
        {
            EntityType = typeof(T).Name,
            EntityId = entity.GetIDValue(),
            Action = action,
            UserId = _currentUserId,
            Timestamp = DateTime.UtcNow,
            Changes = JsonConvert.SerializeObject(entity)
        };
        
        var auditAccess = new DataAccess<AuditLog>(_accountName, _accountKey);
        await auditAccess.ManageDataAsync(auditLog);
    }
}
```

---

## Summary

ASCDataAccessLibrary v3.1 provides a comprehensive, production-ready solution for working with Azure Table Storage and Blob Storage in .NET applications. The library's design philosophy emphasizes:

- **Developer Productivity**: Intuitive APIs with sensible defaults
- **Performance**: Automatic optimizations and efficient batching
- **Reliability**: Built-in retry logic and error handling
- **Flexibility**: Support for both strongly-typed and dynamic schemas
- **Observability**: Comprehensive logging and monitoring capabilities

Whether you're building a simple CRUD application or a complex distributed system, this library provides the tools and patterns needed for success with Azure Storage services.

For the latest updates, bug reports, and feature requests, visit the [GitHub repository](https://github.com/answersalescalls/ASCDataAccessLibrary).

---

*Version 3.1.0 - August 2025*  
*© 2025 Answer Sales Calls Inc. - All Rights Reserved*  
*Licensed under MIT License*