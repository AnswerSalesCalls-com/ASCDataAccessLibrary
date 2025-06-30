# **ASCDataAccessLibrary: Complete Getting Started Guide**

This guide provides a comprehensive introduction to working with the ASCDataAccessLibrary, a powerful .NET library for managing Azure Table Storage operations with enhanced capabilities for large data fields, session management, error logging, and both synchronous and asynchronous operations.

## **Table of Contents**

1. [Installation](#installation)
2. [Basic Concepts](#basic-concepts)
3. [Working with Data Access](#working-with-data-access)
4. [Session Management](#session-management)
5. [Error Logging](#error-logging)
6. [Advanced Features](#advanced-features)
7. [Examples](#examples)
8. [Best Practices](#best-practices)

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
- **Session**: Advanced session management utility for maintaining stateful web applications with async support
- **ErrorLogData**: Comprehensive error logging system with automatic caller information capture
- **TableEntityBase**: Base class that automatically handles large data fields by chunking
- **ITableExtra**: Interface that your model classes must implement

### **Creating a Model**

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

## **Working with Data Access**

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

### **Basic CRUD Operations**

#### **Create or Update a Single Entity**

```csharp
// Create a new customer
var newCustomer = new Customer
{
    CompanyId = "company123",
    CustomerId = "cust456",
    Name = "Acme Corporation",
    Email = "contact@acme.com"
};

// Synchronous - Insert or replace the entity
customerAccess.ManageData(newCustomer);

// Asynchronous (recommended)
await customerAccess.ManageDataAsync(newCustomer);

// To update with a merge operation instead
await customerAccess.ManageDataAsync(newCustomer, TableOperationType.InsertOrMerge);
```

#### **Retrieve Operations**

```csharp
// Get by RowKey
var customer = await customerAccess.GetRowObjectAsync("cust456");

// Get with a custom query
var customerByEmail = await customerAccess.GetRowObjectAsync("Email", ComparisonTypes.eq, "contact@acme.com");

// Get using lambda expressions (new feature)
var activeCustomer = await customerAccess.GetRowObjectAsync(x => x.Email == "contact@acme.com" && x.Status == "Active");

// Get multiple entities with the same partition key
var companyCustomers = await customerAccess.GetCollectionAsync("company123");

// Get all entities in the table
var allCustomers = await customerAccess.GetAllTableDataAsync();

// Advanced querying with lambda expressions
var recentCustomers = await customerAccess.GetCollectionAsync(x => x.CreatedDate > DateTime.Today.AddDays(-30));
```

#### **Pagination Support**

```csharp
// Get paginated results
var firstPage = await customerAccess.GetPagedCollectionAsync(pageSize: 50);

// Process results
foreach (var customer in firstPage.Items)
{
    Console.WriteLine($"Customer: {customer.Name}");
}

// Get next page using continuation token
if (firstPage.HasMore)
{
    var nextPage = await customerAccess.GetPagedCollectionAsync(
        pageSize: 50, 
        continuationToken: firstPage.ContinuationToken);
}

// Paginated query with lambda expression
var pagedActiveCustomers = await customerAccess.GetPagedCollectionAsync(
    x => x.Status == "Active", 
    pageSize: 100);
```

#### **Batch Operations with Progress Tracking**

```csharp
// Simple batch update
List<Customer> customersToUpdate = GetCustomersForUpdate();
var result = await customerAccess.BatchUpdateListAsync(customersToUpdate);

if (result.Success)
{
    Console.WriteLine($"Successfully updated {result.SuccessfulItems} customers");
}

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

// Handle errors
if (!result.Success)
{
    Console.WriteLine($"Failed items: {result.FailedItems}");
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
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
queue.SaveQueue("your-account", "your-key");

// Later, retrieve and process queued data
var queueProcessor = new QueueData<CustomerProcessingTask>();
var pendingTasks = queueProcessor.GetQueues("CustomerProcessing", "your-account", "your-key");

foreach (var task in pendingTasks)
{
    // Process each task
    Console.WriteLine($"Processing task: {task.TaskId}");
}
```

### **Blob Data Information**

```csharp
// Create blob metadata
var blobData = new BlobData
{
    Name = "customer-document.pdf",
    OriginalFilename = "Customer Contract.pdf",
    ContentType = BlobData.FileTypes[".pdf"], // Uses built-in MIME type mapping
    Size = 1024000,
    UploadDate = DateTime.UtcNow,
    Url = new Uri("https://yourstorage.blob.core.windows.net/container/customer-document.pdf")
};

// Check supported file types
if (BlobData.FileTypes.ContainsKey(".docx"))
{
    string mimeType = BlobData.FileTypes[".docx"];
    Console.WriteLine($"Word document MIME type: {mimeType}");
}
```

## **Examples**

### **Complete Customer Management with Error Handling**

```csharp
public class CustomerService
{
    private readonly DataAccess<Customer> _customerAccess;
    private readonly Session _session;
    
    public CustomerService(string accountName, string accountKey, string sessionId)
    {
        _customerAccess = new DataAccess<Customer>(accountName, accountKey);
        _session = new Session(accountName, accountKey, sessionId);
    }
    
    public async Task<bool> CreateCustomerAsync(Customer customer)
    {
        try
        {
            // Validate customer
            if (string.IsNullOrEmpty(customer.Email))
                throw new ArgumentException("Email is required");
            
            // Check if customer already exists
            var existing = await _customerAccess.GetRowObjectAsync(x => x.Email == customer.Email);
            if (existing != null)
                throw new InvalidOperationException("Customer with this email already exists");
            
            // Save customer
            await _customerAccess.ManageDataAsync(customer);
            
            // Update session
            _session["LastCustomerCreated"] = customer.CustomerId;
            _session["LastCustomerCreatedTime"] = DateTime.UtcNow.ToString();
            await _session.CommitDataAsync();
            
            // Log success
            var successLog = ErrorLogData.CreateWithCallerInfo(
                $"Customer created successfully: {customer.Email}",
                ErrorCodeTypes.Information,
                customer.CustomerId);
            await successLog.LogErrorAsync("account", "key");
            
            return true;
        }
        catch (Exception ex)
        {
            // Log error with automatic caller information
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to create customer: " + ex.Message,
                ErrorCodeTypes.Error,
                customer?.CustomerId ?? "unknown");
            await errorLog.LogErrorAsync("account", "key");
            
            return false;
        }
    }
    
    public async Task<List<Customer>> GetRecentCustomersAsync(int days = 30)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-days);
            
            // Use pagination for large datasets
            var allCustomers = new List<Customer>();
            string continuationToken = null;
            
            do
            {
                var page = await _customerAccess.GetPagedCollectionAsync(
                    x => x.CreatedDate > cutoffDate,
                    pageSize: 100,
                    continuationToken: continuationToken);
                
                allCustomers.AddRange(page.Items);
                continuationToken = page.ContinuationToken;
                
            } while (!string.IsNullOrEmpty(continuationToken));
            
            return allCustomers;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to retrieve recent customers: " + ex.Message,
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync("account", "key");
            
            return new List<Customer>();
        }
    }
    
    public async Task<bool> BulkUpdateCustomersAsync(List<Customer> customers)
    {
        try
        {
            var progress = new Progress<DataAccess<Customer>.BatchUpdateProgress>(p =>
            {
                Console.WriteLine($"Updating customers: {p.PercentComplete:F1}% complete");
            });
            
            var result = await _customerAccess.BatchUpdateListAsync(
                customers,
                TableOperationType.InsertOrReplace,
                progress);
            
            if (result.Success)
            {
                var successLog = ErrorLogData.CreateWithCallerInfo(
                    $"Successfully updated {result.SuccessfulItems} customers",
                    ErrorCodeTypes.Information);
                await successLog.LogErrorAsync("account", "key");
            }
            else
            {
                var errorLog = ErrorLogData.CreateWithCallerInfo(
                    $"Batch update completed with {result.FailedItems} failures. Errors: {string.Join(", ", result.Errors)}",
                    ErrorCodeTypes.Warning);
                await errorLog.LogErrorAsync("account", "key");
            }
            
            return result.Success;
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Bulk update failed: " + ex.Message,
                ErrorCodeTypes.Error);
            await errorLog.LogErrorAsync("account", "key");
            
            return false;
        }
    }
    
    public async Task DisposeAsync()
    {
        await _session?.DisposeAsync();
    }
}
```

### **Session-Based Shopping Cart Example**

```csharp
public class ShoppingCartService
{
    private readonly Session _session;
    
    public ShoppingCartService(string accountName, string accountKey, string sessionId)
    {
        _session = new Session(accountName, accountKey, sessionId);
    }
    
    public static async Task<ShoppingCartService> CreateAsync(string accountName, string accountKey, string sessionId)
    {
        var service = new ShoppingCartService(accountName, accountKey, sessionId);
        await service._session.LoadSessionDataAsync(sessionId);
        return service;
    }
    
    public async Task AddItemAsync(string productId, int quantity, decimal price)
    {
        try
        {
            // Get current cart items
            var cartItems = GetCartItems();
            
            // Add or update item
            var existingItem = cartItems.FirstOrDefault(x => x.ProductId == productId);
            if (existingItem != null)
            {
                existingItem.Quantity += quantity;
            }
            else
            {
                cartItems.Add(new CartItem 
                { 
                    ProductId = productId, 
                    Quantity = quantity, 
                    Price = price 
                });
            }
            
            // Save back to session
            _session["CartItems"] = JsonConvert.SerializeObject(cartItems);
            _session["CartLastUpdated"] = DateTime.UtcNow.ToString();
            _session["CartTotal"] = cartItems.Sum(x => x.Quantity * x.Price).ToString();
            
            await _session.CommitDataAsync();
            
            var log = ErrorLogData.CreateWithCallerInfo(
                $"Item added to cart: {productId} (Qty: {quantity})",
                ErrorCodeTypes.Information,
                _session.SessionID);
            await log.LogErrorAsync("account", "key");
        }
        catch (Exception ex)
        {
            var errorLog = ErrorLogData.CreateWithCallerInfo(
                "Failed to add item to cart: " + ex.Message,
                ErrorCodeTypes.Error,
                _session.SessionID);
            await errorLog.LogErrorAsync("account", "key");
        }
    }
    
    public List<CartItem> GetCartItems()
    {
        var cartItemsJson = _session["CartItems"]?.Value;
        if (string.IsNullOrEmpty(cartItemsJson))
            return new List<CartItem>();
            
        return JsonConvert.DeserializeObject<List<CartItem>>(cartItemsJson) ?? new List<CartItem>();
    }
    
    public async Task ClearCartAsync()
    {
        _session["CartItems"] = "";
        _session["CartTotal"] = "0";
        _session["CartLastUpdated"] = DateTime.UtcNow.ToString();
        
        await _session.CommitDataAsync();
    }
    
    public async Task<List<string>> GetAbandonedCartsAsync()
    {
        return await _session.GetStaleSessionsAsync();
    }
}

public class CartItem
{
    public string ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}
```

## **Best Practices**

### **General Guidelines**

1. **Use Async Methods**: Always prefer async methods (`ManageDataAsync`, `GetRowObjectAsync`, etc.) for better performance and scalability.

2. **Create Separate Instances**: Always create a new DataAccess instance for each table you're working with.

3. **Leverage Batch Operations**: When updating multiple entities, use `BatchUpdateListAsync` with progress tracking for better performance.

4. **Handle Large Data**: Let TableEntityBase handle chunking of large text fields automatically - no need for custom code.

### **Session Management**

5. **Use Using Statements**: Wrap sessions in `using` statements or call `DisposeAsync()` to ensure data is committed:
   ```csharp
   await using var session = await Session.CreateAsync("account", "key", "session-id");
   // Session data is automatically committed on disposal
   ```

6. **Clean Up Regularly**: Implement a periodic job to call `CleanSessionDataAsync()` to remove stale sessions.

7. **Monitor Stale Sessions**: Use `GetStaleSessionsAsync()` to identify and handle abandoned user sessions.

### **Error Logging**

8. **Use Automatic Caller Info**: Prefer `ErrorLogData.CreateWithCallerInfo()` for automatic capturing of method, file, and line number information.

9. **Log Appropriately**: Use appropriate severity levels:
   - `Information`: Normal operations, user actions
   - `Warning`: Recoverable issues, performance concerns
   - `Error`: Exceptions that affect functionality
   - `Critical`: System failures requiring immediate attention

10. **Include Context**: Always include relevant context like customer IDs, session IDs, or operation details in error messages.

### **Performance Optimization**

11. **Use Pagination**: For large datasets, use `GetPagedCollectionAsync()` to avoid memory issues and improve response times.

12. **Optimize Queries**: Use lambda expressions for complex queries instead of building manual filter strings.

13. **Batch Operations**: Group multiple operations using batch methods, and monitor progress for long-running operations.

14. **Partition Key Design**: Design your PartitionKey strategy carefully to ensure good performance with Azure Table Storage.

### **Error Handling**

15. **Implement Retry Logic**: For transient failures, implement retry policies around your data access operations.

16. **Validate Input**: Always validate input data before attempting database operations.

17. **Handle Batch Failures**: Check `BatchUpdateResult` for partial failures and handle them appropriately.

18. **Type Safety**: Leverage the generic nature of `DataAccess<T>` for compile-time type safety.

This library simplifies Azure Table Storage operations while adding powerful features like automatic field chunking for large data, complex querying with lambda expressions, comprehensive session management, detailed error logging with automatic caller information capture, and full async/await support for modern .NET applications.