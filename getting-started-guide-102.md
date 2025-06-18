# ASC Table Storage Library - Complete Documentation
## Version 1.0.2 - DOCX Format Guide

---

**Document Information:**
- **Title:** ASC Table Storage Library - Getting Started Guide
- **Version:** 1.0.2
- **Date:** June 2025
- **Format:** Microsoft Word Document
- **Target Audience:** .NET Developers

---

## Executive Summary

The ASC Table Storage Library is a comprehensive .NET library designed to simplify Azure Table Storage and Blob Storage operations. This library provides modern async/await patterns, LINQ-style lambda expressions, intelligent pagination, and robust queuing capabilities for enterprise applications.

**Key Features:**
- Dual sync/async method support
- Lambda expression querying
- Automatic data pagination
- Batch processing with progress tracking
- File type validation and management
- Process interruption resilience
- Comprehensive error logging

---

## Chapter 1: Installation and Setup

### 1.1 Package Installation

The library is available through NuGet Package Manager:

**Package Manager Console:**
```
Install-Package ASCTableStorage
```

**Package Manager UI:**
1. Right-click on your project in Visual Studio
2. Select "Manage NuGet Packages"
3. Search for "ASCTableStorage"
4. Click "Install"

**.NET CLI:**
```bash
dotnet add package ASCTableStorage
```

### 1.2 Prerequisites

- **.NET Framework:** 4.6.1 or higher, or .NET Core 3.1+
- **Azure Storage Account:** Valid Azure Storage account with Table and Blob services enabled
- **Dependencies:** 
  - Microsoft.Azure.Cosmos.Table (≥ 1.0.8)
  - Newtonsoft.Json (≥ 12.0.3)

### 1.3 Configuration

**App.config/appsettings.json:**
```json
{
  "AzureStorage": {
    "AccountName": "your-storage-account-name",
    "AccountKey": "your-account-key",
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
  }
}
```

---

## Chapter 2: Data Access Fundamentals

### 2.1 Creating Your First Entity

All entities must inherit from `TableEntityBase` and implement `ITableExtra`:

```csharp
using ASCTableStorage.Models;
using Microsoft.Azure.Cosmos.Table;

public class Product : TableEntityBase, ITableExtra
{
    // PartitionKey wrapper - groups related entities
    public async Task CleanOldQueuesAsync(TimeSpan maxAge)
    {
        var cutoffDate = DateTimeOffset.UtcNow.Subtract(maxAge);
        var oldQueues = await _dataAccess.GetCollectionAsync(q => q.Timestamp < cutoffDate);
        
        if (oldQueues.Any())
        {
            await _dataAccess.BatchUpdateListAsync(oldQueues, TableOperationType.Delete);
            Console.WriteLine($"Cleaned up {oldQueues.Count} old queue entries");
        }
    }
}

public class QueueInfo
{
    public string QueueName { get; set; }
    public int TotalEntries { get; set; }
    public int TotalItems { get; set; }
    public DateTimeOffset OldestEntry { get; set; }
    public DateTimeOffset NewestEntry { get; set; }
}
```

---

## Chapter 7: Session Management

### 7.1 Session Overview

The session management system provides persistent storage for user session data across requests, particularly useful for web applications, chatbots, and multi-step processes.

### 7.2 Basic Session Operations

#### 7.2.1 Session Initialization

```csharp
// Create new session
var session = new Session(accountName, accountKey);

// Load existing session
string sessionId = "user-12345-session";
var existingSession = new Session(accountName, accountKey, sessionId);
```

#### 7.2.2 Storing Session Data

```csharp
// Store various types of session data
session["UserID"] = new AppSessionData { Value = "user-12345" };
session["UserEmail"] = new AppSessionData { Value = "john.doe@example.com" };
session["LastActivity"] = new AppSessionData { Value = DateTime.UtcNow.ToString("o") };
session["ShoppingCartItems"] = new AppSessionData { Value = "3" };
session["CurrentStep"] = new AppSessionData { Value = "checkout" };
session["PreferredLanguage"] = new AppSessionData { Value = "en-US" };
session["IsAuthenticated"] = new AppSessionData { Value = "true" };

// Store complex data as JSON
var userPreferences = new
{
    Theme = "dark",
    NotificationsEnabled = true,
    TimeZone = "EST"
};
session["UserPreferences"] = new AppSessionData 
{ 
    Value = JsonConvert.SerializeObject(userPreferences) 
};
```

#### 7.2.3 Retrieving Session Data

```csharp
// Retrieve simple values
string userEmail = session["UserEmail"]?.Value;
string currentStep = session["CurrentStep"]?.Value ?? "start";

// Retrieve and parse complex data
if (DateTime.TryParse(session["LastActivity"]?.Value, out DateTime lastActivity))
{
    TimeSpan timeSinceLastActivity = DateTime.UtcNow - lastActivity;
    if (timeSinceLastActivity > TimeSpan.FromMinutes(30))
    {
        // Session expired
        session.RestartSession();
    }
}

// Retrieve and deserialize JSON data
string preferencesJson = session["UserPreferences"]?.Value;
if (!string.IsNullOrEmpty(preferencesJson))
{
    var preferences = JsonConvert.DeserializeObject(preferencesJson);
    // Use preferences...
}
```

### 7.3 Advanced Session Management

#### 7.3.1 Session Lifecycle Management

```csharp
public class UserSessionManager
{
    private readonly Session _session;
    private readonly string _sessionId;
    
    public UserSessionManager(string accountName, string accountKey, string userId)
    {
        _sessionId = $"user-{userId}-{DateTime.Today:yyyyMMdd}";
        _session = new Session(accountName, accountKey, _sessionId);
    }
    
    public void UpdateLastActivity()
    {
        _session["LastActivity"] = new AppSessionData 
        { 
            Value = DateTime.UtcNow.ToString("o") 
        };
        _session.CommitData();
    }
    
    public bool IsSessionExpired(TimeSpan timeout)
    {
        var lastActivityStr = _session["LastActivity"]?.Value;
        if (string.IsNullOrEmpty(lastActivityStr)) return true;
        
        if (DateTime.TryParse(lastActivityStr, out DateTime lastActivity))
        {
            return DateTime.UtcNow - lastActivity > timeout;
        }
        
        return true;
    }
    
    public void ExtendSession()
    {
        UpdateLastActivity();
        Console.WriteLine($"Session {_sessionId} extended");
    }
    
    public void EndSession()
    {
        _session["SessionEnded"] = new AppSessionData 
        { 
            Value = DateTime.UtcNow.ToString("o") 
        };
        _session.CommitData();
        Console.WriteLine($"Session {_sessionId} ended");
    }
}
```

#### 7.3.2 Multi-Step Process Tracking

```csharp
public class CheckoutSessionManager
{
    private readonly Session _session;
    
    public CheckoutSessionManager(string accountName, string accountKey, string sessionId)
    {
        _session = new Session(accountName, accountKey, sessionId);
    }
    
    public void StartCheckout(string userId, List<string> cartItems)
    {
        _session["UserID"] = new AppSessionData { Value = userId };
        _session["CheckoutStarted"] = new AppSessionData { Value = DateTime.UtcNow.ToString("o") };
        _session["CartItems"] = new AppSessionData { Value = JsonConvert.SerializeObject(cartItems) };
        _session["CurrentStep"] = new AppSessionData { Value = "shipping" };
        _session.CommitData();
    }
    
    public void UpdateShippingInfo(object shippingAddress)
    {
        _session["ShippingAddress"] = new AppSessionData 
        { 
            Value = JsonConvert.SerializeObject(shippingAddress) 
        };
        _session["CurrentStep"] = new AppSessionData { Value = "payment" };
        _session.CommitData();
    }
    
    public void UpdatePaymentInfo(string paymentMethod, string last4Digits)
    {
        _session["PaymentMethod"] = new AppSessionData { Value = paymentMethod };
        _session["CardLast4"] = new AppSessionData { Value = last4Digits };
        _session["CurrentStep"] = new AppSessionData { Value = "review" };
        _session.CommitData();
    }
    
    public void CompleteCheckout(string orderId)
    {
        _session["OrderID"] = new AppSessionData { Value = orderId };
        _session["CheckoutCompleted"] = new AppSessionData { Value = DateTime.UtcNow.ToString("o") };
        _session["CurrentStep"] = new AppSessionData { Value = "completed" };
        _session.CommitData();
    }
    
    public string GetCurrentStep()
    {
        return _session["CurrentStep"]?.Value ?? "start";
    }
    
    public bool CanResumeCheckout()
    {
        var currentStep = GetCurrentStep();
        var checkoutStartedStr = _session["CheckoutStarted"]?.Value;
        
        if (string.IsNullOrEmpty(checkoutStartedStr) || currentStep == "completed")
            return false;
            
        if (DateTime.TryParse(checkoutStartedStr, out DateTime startTime))
        {
            // Allow resume within 1 hour
            return DateTime.UtcNow - startTime < TimeSpan.FromHours(1);
        }
        
        return false;
    }
}
```

### 7.4 Session Analytics and Monitoring

#### 7.4.1 Stale Session Detection

```csharp
public async Task<List<string>> FindStaleSessionsAsync()
{
    var session = new Session(accountName, accountKey);
    var staleSessions = session.GetStaleSessions();
    
    Console.WriteLine($"Found {staleSessions.Count} stale sessions");
    
    // Process stale sessions - e.g., send reminder emails, cleanup carts, etc.
    foreach (var sessionId in staleSessions)
    {
        await HandleStaleSessionAsync(sessionId);
    }
    
    return staleSessions;
}

private async Task HandleStaleSessionAsync(string sessionId)
{
    var staleSession = new Session(accountName, accountKey, sessionId);
    
    // Extract user information
    var userId = staleSession["UserID"]?.Value;
    var userEmail = staleSession["UserEmail"]?.Value;
    var currentStep = staleSession["CurrentStep"]?.Value;
    
    // Handle based on context
    if (currentStep == "checkout" && !string.IsNullOrEmpty(userEmail))
    {
        // Send abandoned cart email
        await SendAbandonedCartEmailAsync(userEmail, sessionId);
    }
    
    // Mark session as processed
    staleSession["StaleProcessed"] = new AppSessionData 
    { 
        Value = DateTime.UtcNow.ToString("o") 
    };
    staleSession.CommitData();
}
```

#### 7.4.2 Session Cleanup

```csharp
public class SessionCleanupService
{
    private readonly string _accountName;
    private readonly string _accountKey;
    
    public SessionCleanupService(string accountName, string accountKey)
    {
        _accountName = accountName;
        _accountKey = accountKey;
    }
    
    public async Task CleanupExpiredSessionsAsync()
    {
        var session = new Session(_accountName, _accountKey);
        
        // Clean sessions older than 2 hours (configurable)
        await session.CleanSessionData();
        
        Console.WriteLine("Expired session cleanup completed");
    }
    
    public async Task ArchiveCompletedSessionsAsync()
    {
        var dataAccess = new DataAccess<AppSessionData>(_accountName, _accountKey);
        
        // Find completed checkout sessions
        var completedSessions = await dataAccess.GetCollectionAsync(x => 
            x.Key == "CurrentStep" && 
            x.Value == "completed" &&
            x.Timestamp < DateTimeOffset.UtcNow.AddDays(-7)); // Older than 7 days
        
        if (completedSessions.Any())
        {
            // Archive to separate storage or delete
            await dataAccess.BatchUpdateListAsync(completedSessions, TableOperationType.Delete);
            Console.WriteLine($"Archived {completedSessions.Count} completed sessions");
        }
    }
}
```

---

## Chapter 8: Error Handling and Logging

### 8.1 Error Logging System

#### 8.1.1 Basic Error Logging

```csharp
try
{
    // Your application logic
    await ProcessCustomerDataAsync(customerId);
}
catch (ArgumentException ex)
{
    // Log validation errors
    var errorLog = new ErrorLogData(
        ex, 
        $"Invalid customer data for ID: {customerId}", 
        ErrorCodeTypes.Warning, 
        customerId);
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}
catch (UnauthorizedAccessException ex)
{
    // Log security errors
    var errorLog = new ErrorLogData(
        ex, 
        "Unauthorized access attempt", 
        ErrorCodeTypes.Critical, 
        customerId);
    
    await errorLog.LogErrorAsync(accountName, accountKey);
}
catch (Exception ex)
{
    // Log unexpected errors
    var errorLog = new ErrorLogData(
        ex, 
        "Unexpected error during customer processing", 
        ErrorCodeTypes.Critical, 
        customerId);
    
    await errorLog.LogErrorAsync(accountName, accountKey);
    
    // Re-throw if needed
    throw;
}
```

#### 8.1.2 Structured Error Logging

```csharp
public class ApplicationLogger
{
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly string _applicationName;
    
    public ApplicationLogger(string accountName, string accountKey, string applicationName)
    {
        _accountName = accountName;
        _accountKey = accountKey;
        _applicationName = applicationName;
    }
    
    public async Task LogErrorAsync(Exception ex, string context, string customerId = null)
    {
        var errorLog = new ErrorLogData
        {
            ApplicationName = _applicationName,
            ErrorID = Guid.NewGuid().ToString(),
            CustomerID = customerId ?? "system",
            ErrorMessage = $"{context}: {ex.Message}",
            ErrorSeverity = DetermineErrorSeverity(ex).ToString(),
            FunctionName = ex.StackTrace
        };
        
        await errorLog.LogErrorAsync(_accountName, _accountKey);
    }
    
    public async Task LogWarningAsync(string message, string customerId = null)
    {
        var errorLog = new ErrorLogData
        {
            ApplicationName = _applicationName,
            ErrorID = Guid.NewGuid().ToString(),
            CustomerID = customerId ?? "system",
            ErrorMessage = message,
            ErrorSeverity = ErrorCodeTypes.Warning.ToString(),
            FunctionName = Environment.StackTrace
        };
        
        await errorLog.LogErrorAsync(_accountName, _accountKey);
    }
    
    public async Task LogInformationAsync(string message, string customerId = null)
    {
        var errorLog = new ErrorLogData
        {
            ApplicationName = _applicationName,
            ErrorID = Guid.NewGuid().ToString(),
            CustomerID = customerId ?? "system",
            ErrorMessage = message,
            ErrorSeverity = ErrorCodeTypes.Information.ToString(),
            FunctionName = "Information Log"
        };
        
        await errorLog.LogErrorAsync(_accountName, _accountKey);
    }
    
    private ErrorCodeTypes DetermineErrorSeverity(Exception ex)
    {
        return ex switch
        {
            ArgumentException => ErrorCodeTypes.Warning,
            UnauthorizedAccessException => ErrorCodeTypes.Critical,
            SecurityException => ErrorCodeTypes.Critical,
            OutOfMemoryException => ErrorCodeTypes.Critical,
            _ => ErrorCodeTypes.Critical
        };
    }
}
```

### 8.2 Error Analysis and Reporting

#### 8.2.1 Error Querying and Analysis

```csharp
public class ErrorAnalyzer
{
    private readonly DataAccess<ErrorLogData> _dataAccess;
    
    public ErrorAnalyzer(string accountName, string accountKey)
    {
        _dataAccess = new DataAccess<ErrorLogData>(accountName, accountKey);
    }
    
    public async Task<List<ErrorLogData>> GetRecentCriticalErrorsAsync(int hours = 24)
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(-hours);
        
        return await _dataAccess.GetCollectionAsync(x => 
            x.ErrorSeverity == "Critical" && 
            x.Timestamp >= cutoffTime);
    }
    
    public async Task<List<ErrorLogData>> GetErrorsForCustomerAsync(string customerId)
    {
        return await _dataAccess.GetCollectionAsync(x => x.CustomerID == customerId);
    }
    
    public async Task<Dictionary<string, int>> GetErrorCountByApplicationAsync(int days = 7)
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddDays(-days);
        var errors = await _dataAccess.GetCollectionAsync(x => x.Timestamp >= cutoffTime);
        
        return errors
            .GroupBy(e => e.ApplicationName)
            .ToDictionary(g => g.Key, g => g.Count());
    }
    
    public async Task<List<ErrorTrend>> GetErrorTrendsAsync(int days = 30)
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddDays(-days);
        var errors = await _dataAccess.GetCollectionAsync(x => x.Timestamp >= cutoffTime);
        
        return errors
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new ErrorTrend
            {
                Date = g.Key,
                TotalErrors = g.Count(),
                CriticalErrors = g.Count(e => e.ErrorSeverity == "Critical"),
                WarningErrors = g.Count(e => e.ErrorSeverity == "Warning")
            })
            .OrderBy(t => t.Date)
            .ToList();
    }
}

public class ErrorTrend
{
    public DateTime Date { get; set; }
    public int TotalErrors { get; set; }
    public int CriticalErrors { get; set; }
    public int WarningErrors { get; set; }
}
```

#### 8.2.2 Error Reporting

```csharp
public class ErrorReportGenerator
{
    private readonly ErrorAnalyzer _analyzer;
    private readonly AzureBlobs _blobService;
    
    public ErrorReportGenerator(string accountName, string accountKey, string connectionString)
    {
        _analyzer = new ErrorAnalyzer(accountName, accountKey);
        _blobService = new AzureBlobs(connectionString);
    }
    
    public async Task<string> GenerateDailyErrorReportAsync()
    {
        var criticalErrors = await _analyzer.GetRecentCriticalErrorsAsync(24);
        var errorCounts = await _analyzer.GetErrorCountByApplicationAsync(1);
        
        var report = new StringBuilder();
        report.AppendLine("# Daily Error Report");
        report.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        report.AppendLine();
        
        report.AppendLine("## Critical Errors (Last 24 Hours)");
        report.AppendLine($"Total: {criticalErrors.Count}");
        report.AppendLine();
        
        foreach (var error in criticalErrors.Take(10)) // Top 10
        {
            report.AppendLine($"- **{error.ApplicationName}**: {error.ErrorMessage}");
            report.AppendLine($"  - Time: {error.Timestamp:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"  - Customer: {error.CustomerID}");
            report.AppendLine();
        }
        
        report.AppendLine("## Errors by Application");
        foreach (var app in errorCounts.OrderByDescending(kvp => kvp.Value))
        {
            report.AppendLine($"- {app.Key}: {app.Value} errors");
        }
        
        // Save report to blob storage
        var reportContent = report.ToString();
        var fileName = $"error-reports/daily-{DateTime.UtcNow:yyyyMMdd}.md";
        
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(reportContent));
        await _blobService.UploadStreamAsync("reports", stream, fileName);
        
        return fileName;
    }
}
```

---

## Chapter 9: Best Practices and Performance

### 9.1 Performance Optimization

#### 9.1.1 Efficient Query Design

```csharp
// ✅ Good - Use PartitionKey for efficient queries
var customerOrders = await dataAccess.GetCollectionAsync("CUSTOMER-12345");

// ✅ Good - Combine PartitionKey and RowKey for fastest lookup
var specificOrder = await dataAccess.GetRowObjectAsync("ORDER-789");

// ❌ Avoid - Full table scans are slow and expensive
var expensiveQuery = await dataAccess.GetCollectionAsync(x => 
    x.SomeProperty == "value"); // No PartitionKey filter

// ✅ Better - Include PartitionKey when possible
var betterQuery = await dataAccess.GetCollectionAsync(x => 
    x.PartitionKey == "CUSTOMER-12345" && x.SomeProperty == "value");
```

#### 9.1.2 Pagination Best Practices

```csharp
// ✅ Good - Use pagination for large datasets
public async Task<List<T>> ProcessLargeDatasetAsync<T>() 
    where T : TableEntityBase, ITableExtra, new()
{
    var allItems = new List<T>();
    string continuationToken = null;
    
    do
    {
        var page = await dataAccess.GetPagedCollectionAsync(
            pageSize: 1000, // Reasonable page size
            continuationToken: continuationToken);
            
        // Process page immediately
        await ProcessPageAsync(page.Items);
        
        continuationToken = page.ContinuationToken;
        
    } while (!string.IsNullOrEmpty(continuationToken));
    
    return allItems;
}

// ❌ Avoid - Loading all data at once
var allData = await dataAccess.GetAllTableDataAsync(); // Could be millions of records
```

#### 9.1.3 Batch Operation Optimization

```csharp
// ✅ Good - Process batches with proper error handling
public async Task<BatchProcessingResult> ProcessLargeBatchAsync<T>(List<T> items) 
    where T : TableEntityBase, ITableExtra, new()
{
    const int maxBatchSize = 100;
    var results = new BatchProcessingResult();
    
    // Group by partition key first
    var groupedItems = items.GroupBy(i => i.PartitionKey);
    
    foreach (var group in groupedItems)
    {
        var groupItems = group.ToList();
        
        // Process in chunks of 100 (Azure limit)
        for (int i = 0; i < groupItems.Count; i += maxBatchSize)
        {
            var chunk = groupItems.Skip(i).Take(maxBatchSize).ToList();
            
            try
            {
                var result = await dataAccess.BatchUpdateListAsync(chunk);
                results.SuccessfulBatches++;
                results.TotalProcessed += result.SuccessfulItems;
            }
            catch (Exception ex)
            {
                results.FailedBatches++;
                results.Errors.Add($"Batch {i / maxBatchSize + 1}: {ex.Message}");
                
                // Optionally retry individual items
                await RetryIndividualItemsAsync(chunk);
            }
            
            // Add small delay to avoid throttling
            if (i + maxBatchSize < groupItems.Count)
            {
                await Task.Delay(50);
            }
        }
    }
    
    return results;
}

public class BatchProcessingResult
{
    public int SuccessfulBatches { get; set; }
    public int FailedBatches { get; set; }
    public int TotalProcessed { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
}
```

### 9.2 Connection and Resource Management

#### 9.2.1 Dependency Injection Setup

```csharp
// Startup.cs or Program.cs
public void ConfigureServices(IServiceCollection services)
{
    // Register as singleton for connection reuse
    services.AddSingleton<IDataAccessFactory, DataAccessFactory>();
    
    // Register specific services
    services.AddScoped<ICustomerService, CustomerService>();
    services.AddScoped<IOrderService, OrderService>();
    
    // Register blob service
    services.AddSingleton<IAzureBlobService, AzureBlobService>();
}

public interface IDataAccessFactory
{
    DataAccess<T> Create<T>() where T : TableEntityBase, ITableExtra, new();
}

public class DataAccessFactory : IDataAccessFactory
{
    private readonly string _accountName;
    private readonly string _accountKey;
    
    public DataAccessFactory(IConfiguration configuration)
    {
        _accountName = configuration["AzureStorage:AccountName"];
        _accountKey = configuration["AzureStorage:AccountKey"];
    }
    
    public DataAccess<T> Create<T>() where T : TableEntityBase, ITableExtra, new()
    {
        return new DataAccess<T>(_accountName, _accountKey);
    }
}
```

#### 9.2.2 Service Layer Implementation

```csharp
public interface ICustomerService
{
    Task<Customer> GetCustomerAsync(string customerId);
    Task<List<Customer>> GetActiveCustomersAsync();
    Task<bool> CreateCustomerAsync(Customer customer);
    Task<bool> UpdateCustomerAsync(Customer customer);
}

public class CustomerService : ICustomerService
{
    private readonly DataAccess<Customer> _dataAccess;
    private readonly ApplicationLogger _logger;
    
    public CustomerService(IDataAccessFactory factory, ApplicationLogger logger)
    {
        _dataAccess = factory.Create<Customer>();
        _logger = logger;
    }
    
    public async Task<Customer> GetCustomerAsync(string customerId)
    {
        try
        {
            return await _dataAccess.GetRowObjectAsync(customerId);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, $"Failed to retrieve customer {customerId}", customerId);
            throw;
        }
    }
    
    public async Task<List<Customer>> GetActiveCustomersAsync()
    {
        try
        {
            return await _dataAccess.GetCollectionAsync(x => x.IsActive);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, "Failed to retrieve active customers");
            throw;
        }
    }
    
    public async Task<bool> ProcessBulkOrderUpdateAsync(List<Order> orders)
    {
        try
        {
            var progress = new Progress<BatchUpdateProgress>(p =>
            {
                Console.WriteLine($"Processing orders: {p.PercentComplete:F1}% complete");
            });
            
            var result = await _orderAccess.BatchUpdateListAsync(orders, 
                TableOperationType.InsertOrMerge, progress);
            
            if (result.Success)
            {
                await _logger.LogInformationAsync($"Successfully updated {result.SuccessfulItems} orders");
                return true;
            }
            else
            {
                await _logger.LogErrorAsync(new Exception(string.Join("; ", result.Errors)), 
                    "Bulk order update failed");
                return false;
            }
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, "Bulk order update exception");
            return false;
        }
    }
}
```

### 10.2 Document Management System

```csharp
public class Document : TableEntityBase, ITableExtra
{
    public string DocumentID
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Category
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Title { get; set; }
    public string Description { get; set; }
    public string BlobUrl { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public string ContentType { get; set; }
    public DateTime UploadDate { get; set; }
    public string UploadedBy { get; set; }
    public List<string> Tags { get; set; } = new List<string>();
    public string TagsJson 
    { 
        get => JsonConvert.SerializeObject(Tags);
        set => Tags = JsonConvert.DeserializeObject<List<string>>(value ?? "[]");
    }
    
    public string TableReference => "Documents";
    public string GetIDValue() => this.DocumentID;
}

public class DocumentManagementService
{
    private readonly DataAccess<Document> _documentAccess;
    private readonly AzureBlobs _blobService;
    private readonly ApplicationLogger _logger;
    
    public DocumentManagementService(
        IDataAccessFactory factory, 
        AzureBlobs blobService, 
        ApplicationLogger logger)
    {
        _documentAccess = factory.Create<Document>();
        _blobService = blobService;
        _logger = logger;
    }
    
    public async Task<Document> UploadDocumentAsync(
        Stream fileStream, 
        string fileName, 
        string category,
        string title,
        string description,
        string uploadedBy,
        List<string> tags = null)
    {
        try
        {
            var documentId = Guid.NewGuid().ToString();
            var blobName = $"{category}/{documentId}/{fileName}";
            
            // Upload file to blob storage
            var blobUri = await _blobService.UploadStreamAsync(
                containerName: "documents",
                stream: fileStream,
                blobName: blobName,
                originalFileName: fileName);
            
            // Create document metadata
            var document = new Document
            {
                DocumentID = documentId,
                Category = category,
                Title = title,
                Description = description,
                FileName = fileName,
                BlobUrl = blobUri.ToString(),
                ContentType = _blobService.GetContentType(fileName),
                FileSize = fileStream.Length,
                UploadDate = DateTime.UtcNow,
                UploadedBy = uploadedBy,
                Tags = tags ?? new List<string>()
            };
            
            // Save metadata to table storage
            await _documentAccess.ManageDataAsync(document);
            
            await _logger.LogInformationAsync($"Document uploaded: {title}", uploadedBy);
            return document;
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, $"Failed to upload document: {fileName}", uploadedBy);
            throw;
        }
    }
    
    public async Task<List<Document>> SearchDocumentsAsync(
        string searchTerm = null,
        string category = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        List<string> tags = null)
    {
        var documents = new List<Document>();
        
        if (!string.IsNullOrEmpty(category))
        {
            // Search within specific category (efficient)
            documents = await _documentAccess.GetCollectionAsync(category);
        }
        else
        {
            // Search all documents (less efficient)
            documents = await _documentAccess.GetAllTableDataAsync();
        }
        
        // Apply additional filters
        var filteredDocs = documents.AsEnumerable();
        
        if (!string.IsNullOrEmpty(searchTerm))
        {
            filteredDocs = filteredDocs.Where(d => 
                d.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                d.FileName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
        }
        
        if (fromDate.HasValue)
        {
            filteredDocs = filteredDocs.Where(d => d.UploadDate >= fromDate.Value);
        }
        
        if (toDate.HasValue)
        {
            filteredDocs = filteredDocs.Where(d => d.UploadDate <= toDate.Value);
        }
        
        if (tags?.Any() == true)
        {
            filteredDocs = filteredDocs.Where(d => 
                tags.Any(tag => d.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }
        
        return filteredDocs.ToList();
    }
    
    public async Task<Stream> DownloadDocumentAsync(string documentId)
    {
        var document = await _documentAccess.GetRowObjectAsync(documentId);
        if (document == null)
        {
            throw new FileNotFoundException($"Document {documentId} not found");
        }
        
        var stream = new MemoryStream();
        var blobName = ExtractBlobNameFromUrl(document.BlobUrl);
        
        var success = await _blobService.DownloadToStreamAsync("documents", blobName, stream);
        if (!success)
        {
            throw new FileNotFoundException($"Document file not found in blob storage");
        }
        
        stream.Position = 0;
        return stream;
    }
    
    public async Task<bool> DeleteDocumentAsync(string documentId, string deletedBy)
    {
        try
        {
            var document = await _documentAccess.GetRowObjectAsync(documentId);
            if (document == null) return false;
            
            // Delete from blob storage
            var blobName = ExtractBlobNameFromUrl(document.BlobUrl);
            await _blobService.DeleteBlobAsync("documents", blobName);
            
            // Delete from table storage
            await _documentAccess.ManageDataAsync(document, TableOperationType.Delete);
            
            await _logger.LogInformationAsync($"Document deleted: {document.Title}", deletedBy);
            return true;
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, $"Failed to delete document: {documentId}", deletedBy);
            return false;
        }
    }
    
    private string ExtractBlobNameFromUrl(string blobUrl)
    {
        var uri = new Uri(blobUrl);
        return uri.AbsolutePath.TrimStart('/').Substring("documents/".Length);
    }
}
```

### 10.3 Background Job Processing System

```csharp
public class BackgroundJob : TableEntityBase, ITableExtra
{
    public string JobID
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string JobType
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Status { get; set; } // Pending, Running, Completed, Failed
    public DateTime CreatedDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string Parameters { get; set; } // JSON serialized parameters
    public string Result { get; set; } // JSON serialized result
    public string ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public int Priority { get; set; } // 1 = Highest, 5 = Lowest
    
    public string TableReference => "BackgroundJobs";
    public string GetIDValue() => this.JobID;
}

public class BackgroundJobProcessor
{
    private readonly DataAccess<BackgroundJob> _jobAccess;
    private readonly ApplicationLogger _logger;
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _jobHandlers;
    
    public BackgroundJobProcessor(IDataAccessFactory factory, ApplicationLogger logger)
    {
        _jobAccess = factory.Create<BackgroundJob>();
        _logger = logger;
        _jobHandlers = new Dictionary<string, Func<string, CancellationToken, Task<string>>>();
        
        RegisterJobHandlers();
    }
    
    private void RegisterJobHandlers()
    {
        _jobHandlers["EmailCampaign"] = ProcessEmailCampaignAsync;
        _jobHandlers["DataExport"] = ProcessDataExportAsync;
        _jobHandlers["FileProcessing"] = ProcessFileAsync;
        _jobHandlers["ReportGeneration"] = ProcessReportGenerationAsync;
    }
    
    public async Task<string> QueueJobAsync(string jobType, object parameters, int priority = 3)
    {
        var job = new BackgroundJob
        {
            JobID = Guid.NewGuid().ToString(),
            JobType = jobType,
            Status = "Pending",
            CreatedDate = DateTime.UtcNow,
            Parameters = JsonConvert.SerializeObject(parameters),
            Priority = priority,
            RetryCount = 0
        };
        
        await _jobAccess.ManageDataAsync(job);
        await _logger.LogInformationAsync($"Job queued: {jobType} ({job.JobID})");
        
        return job.JobID;
    }
    
    public async Task ProcessJobsAsync(CancellationToken cancellationToken)
    {
        const string queueName = "background-job-processing";
        List<BackgroundJob> workingJobs = null;
        
        try
        {
            // Get queued jobs from previous runs
            var queueManager = new QueueData<BackgroundJob>();
            var queuedJobs = queueManager.GetQueues(queueName, _accountName, _accountKey);
            
            // Get new pending jobs, prioritized
            var pendingJobs = await _jobAccess.GetCollectionAsync(x => 
                x.Status == "Pending" || x.Status == "Failed");
            
            var newJobs = pendingJobs
                .Where(j => j.RetryCount < 3) // Max 3 retries
                .OrderBy(j => j.Priority)
                .ThenBy(j => j.CreatedDate)
                .ToList();
            
            // Combine and deduplicate
            workingJobs = queuedJobs.Concat(newJobs)
                .DistinctBy(j => j.JobID)
                .ToList();
            
            foreach (var job in workingJobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                await ProcessSingleJobAsync(job, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Save unprocessed jobs to queue
            if (workingJobs?.Any() == true)
            {
                var unprocessedJobs = workingJobs
                    .Where(j => j.Status == "Pending" || j.Status == "Running")
                    .ToList();
                
                if (unprocessedJobs.Any())
                {
                    unprocessedJobs.CreateQueue(queueName).SaveQueue(_accountName, _accountKey);
                    await _logger.LogInformationAsync($"Saved {unprocessedJobs.Count} unprocessed jobs to queue");
                }
            }
        }
    }
    
    private async Task ProcessSingleJobAsync(BackgroundJob job, CancellationToken cancellationToken)
    {
        try
        {
            // Update job status to running
            job.Status = "Running";
            job.StartedDate = DateTime.UtcNow;
            await _jobAccess.ManageDataAsync(job);
            
            // Execute job based on type
            if (_jobHandlers.TryGetValue(job.JobType, out var handler))
            {
                var result = await handler(job.Parameters, cancellationToken);
                
                // Mark as completed
                job.Status = "Completed";
                job.CompletedDate = DateTime.UtcNow;
                job.Result = result;
                job.ErrorMessage = null;
            }
            else
            {
                throw new NotSupportedException($"Job type '{job.JobType}' is not supported");
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = "Pending"; // Reset to pending for retry
            throw;
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.ErrorMessage = ex.Message;
            job.RetryCount++;
            
            await _logger.LogErrorAsync(ex, $"Job failed: {job.JobType} ({job.JobID})");
        }
        finally
        {
            await _jobAccess.ManageDataAsync(job);
        }
    }
    
    private async Task<string> ProcessEmailCampaignAsync(string parameters, CancellationToken cancellationToken)
    {
        var campaignParams = JsonConvert.DeserializeObject<EmailCampaignParameters>(parameters);
        
        // Simulate email sending with cancellation support
        var sentCount = 0;
        foreach (var recipient in campaignParams.Recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Send email logic here
            await Task.Delay(100, cancellationToken); // Simulate processing
            sentCount++;
        }
        
        return JsonConvert.SerializeObject(new { SentCount = sentCount, Status = "Success" });
    }
    
    private async Task<string> ProcessDataExportAsync(string parameters, CancellationToken cancellationToken)
    {
        var exportParams = JsonConvert.DeserializeObject<DataExportParameters>(parameters);
        
        // Simulate data export
        await Task.Delay(5000, cancellationToken);
        
        var exportResult = new
        {
            ExportType = exportParams.ExportType,
            RecordCount = 1000,
            FilePath = $"exports/export-{Guid.NewGuid()}.csv",
            CompletedAt = DateTime.UtcNow
        };
        
        return JsonConvert.SerializeObject(exportResult);
    }
    
    private async Task<string> ProcessFileAsync(string parameters, CancellationToken cancellationToken)
    {
        var fileParams = JsonConvert.DeserializeObject<FileProcessingParameters>(parameters);
        
        // Simulate file processing
        await Task.Delay(2000, cancellationToken);
        
        return JsonConvert.SerializeObject(new { Status = "Processed", FilePath = fileParams.FilePath });
    }
    
    private async Task<string> ProcessReportGenerationAsync(string parameters, CancellationToken cancellationToken)
    {
        var reportParams = JsonConvert.DeserializeObject<ReportParameters>(parameters);
        
        // Simulate report generation
        await Task.Delay(10000, cancellationToken);
        
        return JsonConvert.SerializeObject(new { ReportPath = $"reports/{reportParams.ReportType}.pdf" });
    }
}

// Parameter classes
public class EmailCampaignParameters
{
    public string CampaignId { get; set; }
    public List<string> Recipients { get; set; }
    public string Subject { get; set; }
    public string Template { get; set; }
}

public class DataExportParameters
{
    public string ExportType { get; set; }
    public Dictionary<string, object> Filters { get; set; }
    public string Format { get; set; }
}

public class FileProcessingParameters
{
    public string FilePath { get; set; }
    public string ProcessingType { get; set; }
}

public class ReportParameters
{
    public string ReportType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}
```

---

## Chapter 11: Conclusion and Support

### 11.1 Key Takeaways

The ASC Table Storage Library provides a comprehensive solution for Azure Storage operations with the following key benefits:

#### **Modern Development Patterns**
- Full async/await support for optimal performance
- LINQ-style lambda expressions for intuitive querying
- Comprehensive error handling and logging

#### **Scalability Features**
- Intelligent pagination for large datasets
- Efficient batch processing with progress tracking
- Process interruption resilience through queuing

#### **Developer Productivity**
- Simplified API reducing boilerplate code
- Automatic handling of Azure Table Storage limitations
- Rich blob storage management with file type validation

#### **Enterprise Readiness**
- Robust error logging and monitoring
- Session management for stateful applications
- Security best practices and validation

### 11.2 Performance Considerations

When using the library in production environments:

1. **Always use async methods** in web applications and services
2. **Implement pagination** for datasets larger than 1,000 records
3. **Use PartitionKey efficiently** for optimal query performance
4. **Monitor batch operation sizes** to stay within Azure limits
5. **Implement proper error handling** and retry logic

### 11.3 Troubleshooting Common Issues

#### **Connection Issues**
```csharp
// Test connection
try
{
    var testAccess = new DataAccess<AppSessionData>(accountName, accountKey);
    var testData = await testAccess.GetAllTableDataAsync();
    Console.WriteLine("Connection successful");
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
    // Check account name, key, and network connectivity
}
```

#### **Query Performance Issues**
```csharp
// ✅ Efficient - Uses PartitionKey
var results = await dataAccess.GetCollectionAsync("PARTITION_VALUE");

// ❌ Inefficient - Full table scan
var results = await dataAccess.GetCollectionAsync(x => x.SomeProperty == "value");

// ✅ Better - Include PartitionKey in filter
var results = await dataAccess.GetCollectionAsync(x => 
    x.PartitionKey == "PARTITION_VALUE" && x.SomeProperty == "value");
```

#### **Batch Operation Failures**
```csharp
var result = await dataAccess.BatchUpdateListAsync(items);
if (!result.Success)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Batch error: {error}");
        // Common causes:
        // - Items not in same partition
        // - Batch size > 100 items
        // - Network timeouts
        // - Invalid entity data
    }
}
```

### 11.4 Version History

#### **Version 1.0.2 (Current)**
- Added lambda expression support for queries
- Enhanced pagination with continuation tokens
- Improved batch processing with strict 100-item validation
- Added comprehensive blob storage management
- Implemented process interruption resilience
- Enhanced error logging and session management

#### **Previous Versions**
- 1.0.1: Basic CRUD operations and batch processing
- 1.0.0: Initial release with table storage support

### 11.5 Support and Resources

#### **Documentation**
- API Reference: Available in package XML documentation
- Code Examples: Comprehensive examples in this guide
- Best Practices: Performance and security guidelines included

#### **Community Support**
- GitHub Repository: [Contact development team for repository link]
- Issue Tracking: Report bugs and feature requests
- Discussions: Community Q&A and best practices sharing

#### **Enterprise Support**
For enterprise customers requiring:
- Custom feature development
- Performance optimization consulting
- Migration assistance
- Training and workshops

Contact the development team for enterprise support options.

### 11.6 Contributing

The ASC Table Storage Library welcomes contributions from the community:

1. **Bug Reports**: Use GitHub issues with detailed reproduction steps
2. **Feature Requests**: Describe use cases and expected behavior
3. **Code Contributions**: Follow coding standards and include unit tests
4. **Documentation**: Help improve examples and clarify usage

### 11.7 License

This library is released under the MIT License, allowing for both commercial and non-commercial use with proper attribution.

---

**Thank you for using the ASC Table Storage Library!**

We hope this library significantly improves your Azure Storage development experience. The combination of modern async patterns, intuitive querying, and enterprise-ready features makes it an ideal choice for applications of any scale.

For the latest updates and announcements, please monitor the NuGet package page and GitHub repository.

---

**Package Information:**
- **Version:** 1.0.2
- **NuGet Package:** ASCTableStorage
- **Target Framework:** .NET Core 3.1+, .NET 5+, .NET 6+
- **Dependencies:** 
  - Microsoft.Azure.Cosmos.Table (≥ 1.0.8)
  - Newtonsoft.Json (≥ 12.0.3)
- **License:** MIT
- **Documentation:** Complete XML documentation included CreateCustomerAsync(Customer customer)
    {
        try
        {
            if (string.IsNullOrEmpty(customer.CustomerID))
            {
                customer.CustomerID = Guid.NewGuid().ToString();
            }
            
            customer.CreatedDate = DateTime.UtcNow;
            await _dataAccess.ManageDataAsync(customer);
            
            await _logger.LogInformationAsync($"Customer created: {customer.CustomerID}", customer.CustomerID);
            return true;
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, "Failed to create customer", customer?.CustomerID);
            return false;
        }
    }
    
    public async Task<bool> UpdateCustomerAsync(Customer customer)
    {
        try
        {
            await _dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrMerge);
            await _logger.LogInformationAsync($"Customer updated: {customer.CustomerID}", customer.CustomerID);
            return true;
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ex, "Failed to update customer", customer?.CustomerID);
            return false;
        }
    }
}
```

### 9.3 Security Best Practices

#### 9.3.1 Connection String Security

```csharp
// ✅ Good - Use Azure Key Vault or secure configuration
public class SecureConfigurationService
{
    public async Task<string> GetStorageConnectionStringAsync()
    {
        // Option 1: Azure Key Vault
        var keyVaultClient = new SecretClient(new Uri("https://vault.vault.azure.net/"), new DefaultAzureCredential());
        var secret = await keyVaultClient.GetSecretAsync("storage-connection-string");
        return secret.Value.Value;
        
        // Option 2: Environment variables (better than hardcoding)
        return Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
    }
}

// ❌ Avoid - Hardcoded connection strings
var connectionString = "DefaultEndpointsProtocol=https;AccountName=..."; // Never do this
```

#### 9.3.2 Data Validation and Sanitization

```csharp
public class SecureCustomerService : ICustomerService
{
    private readonly DataAccess<Customer> _dataAccess;
    private readonly IDataValidator _validator;
    
    public SecureCustomerService(IDataAccessFactory factory, IDataValidator validator)
    {
        _dataAccess = factory.Create<Customer>();
        _validator = validator;
    }
    
    public async Task<bool> CreateCustomerAsync(Customer customer)
    {
        // Validate input
        var validationResult = await _validator.ValidateCustomerAsync(customer);
        if (!validationResult.IsValid)
        {
            throw new ArgumentException($"Invalid customer data: {string.Join(", ", validationResult.Errors)}");
        }
        
        // Sanitize input
        customer.Email = customer.Email?.Trim().ToLowerInvariant();
        customer.Name = SanitizeString(customer.Name);
        
        // Ensure secure defaults
        customer.CustomerID = customer.CustomerID ?? Guid.NewGuid().ToString();
        customer.CreatedDate = DateTime.UtcNow;
        customer.IsActive = true;
        
        await _dataAccess.ManageDataAsync(customer);
        return true;
    }
    
    private string SanitizeString(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // Remove potentially harmful characters
        return Regex.Replace(input.Trim(), @"[<>""';\\]", "");
    }
}
```

---

## Chapter 10: Advanced Examples and Use Cases

### 10.1 E-commerce Order Management System

```csharp
public class Order : TableEntityBase, ITableExtra
{
    public string OrderID
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string CustomerID
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; }
    public string ShippingAddress { get; set; }
    public string OrderItems { get; set; } // JSON serialized
    
    public string TableReference => "Orders";
    public string GetIDValue() => this.OrderID;
}

public class OrderManagementService
{
    private readonly DataAccess<Order> _orderAccess;
    private readonly DataAccess<Customer> _customerAccess;
    private readonly ApplicationLogger _logger;
    
    public OrderManagementService(IDataAccessFactory factory, ApplicationLogger logger)
    {
        _orderAccess = factory.Create<Order>();
        _customerAccess = factory.Create<Customer>();
        _logger = logger;
    }
    
    public async Task<PagedResult<Order>> GetCustomerOrdersAsync(string customerId, 
        int pageSize = 50, string continuationToken = null)
    {
        return await _orderAccess.GetPagedCollectionAsync(
            customerId, pageSize, continuationToken);
    }
    
    public async Task<List<Order>> GetRecentOrdersAsync(int days = 30)
    {
        var cutoffDate = DateTime.Today.AddDays(-days);
        return await _orderAccess.GetCollectionAsync(x => 
            x.OrderDate >= cutoffDate && 
            x.Status != "Cancelled");
    }
    
    public async Task<List<Order>> GetPendingOrdersAsync()
    {
        return await _orderAccess.GetCollectionAsync(x => 
            x.Status == "Pending" || 
            x.Status == "Processing");
    }
    
    public async Task<decimal> GetCustomerTotalSpendingAsync(string customerId)
    {
        var customerOrders = await _orderAccess.GetCollectionAsync(customerId);
        return customerOrders
            .Where(o => o.Status == "Completed")
            .Sum(o => o.TotalAmount);
    }
    
    public async Task<bool> string Category
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }

    // RowKey wrapper - unique identifier within partition
    public string ProductID
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }

    // Business properties
    public string Name { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public string Description { get; set; } // Can be > 64KB (auto-chunked)

    // Required implementations
    public string TableReference => "Products";
    public string GetIDValue() => this.ProductID;
}
```

### 2.2 DataAccess Initialization

```csharp
// Initialize with explicit credentials
var dataAccess = new DataAccess<Product>("storage-account", "account-key");

// Dependency injection example
public class ProductService
{
    private readonly DataAccess<Product> _dataAccess;
    
    public ProductService(IConfiguration config)
    {
        var accountName = config["AzureStorage:AccountName"];
        var accountKey = config["AzureStorage:AccountKey"];
        _dataAccess = new DataAccess<Product>(accountName, accountKey);
    }
}
```

### 2.3 Basic CRUD Operations

#### 2.3.1 Create/Update Operations

```csharp
var product = new Product
{
    ProductID = Guid.NewGuid().ToString(),
    Category = "Electronics",
    Name = "Smartphone X1",
    Price = 699.99m,
    StockQuantity = 50,
    IsActive = true,
    CreatedDate = DateTime.UtcNow,
    Description = "Latest smartphone with advanced features..."
};

// Insert or replace (default behavior)
await dataAccess.ManageDataAsync(product);

// Insert or merge (preserves existing data)
await dataAccess.ManageDataAsync(product, TableOperationType.InsertOrMerge);

// Synchronous version (not recommended for web applications)
dataAccess.ManageData(product);
```

#### 2.3.2 Read Operations

**Single Entity Retrieval:**
```csharp
// By RowKey (most efficient)
var product = await dataAccess.GetRowObjectAsync("product-id-123");

// By custom field
var product = await dataAccess.GetRowObjectAsync("Name", ComparisonTypes.eq, "Smartphone X1");

// Using lambda expression
var product = await dataAccess.GetRowObjectAsync(x => 
    x.Name == "Smartphone X1" && x.IsActive);
```

**Collection Retrieval:**
```csharp
// All products in Electronics category (by PartitionKey)
var electronics = await dataAccess.GetCollectionAsync("Electronics");

// All active products
var activeProducts = await dataAccess.GetCollectionAsync(x => x.IsActive);

// Complex filtering with multiple conditions
var premiumProducts = await dataAccess.GetCollectionAsync(x => 
    x.IsActive && 
    x.Price > 500 && 
    x.StockQuantity > 10 && 
    x.CreatedDate > DateTime.Today.AddMonths(-6));

// Text-based searching
var smartphoneProducts = await dataAccess.GetCollectionAsync(x => 
    x.Name.Contains("Smartphone") && 
    x.Category == "Electronics");
```

#### 2.3.3 Delete Operations

```csharp
// Delete single entity
await dataAccess.ManageDataAsync(product, TableOperationType.Delete);

// Batch delete (covered in Chapter 3)
var productsToDelete = await dataAccess.GetCollectionAsync(x => !x.IsActive);
await dataAccess.BatchUpdateListAsync(productsToDelete, TableOperationType.Delete);
```

---

## Chapter 3: Advanced Querying and Pagination

### 3.1 Lambda Expression Queries

The library supports comprehensive LINQ-style querying:

#### 3.1.1 Comparison Operators

```csharp
// Equality
var exactMatch = await dataAccess.GetCollectionAsync(x => x.Price == 699.99m);

// Inequality
var notExpensive = await dataAccess.GetCollectionAsync(x => x.Price != 1000m);

// Relational operators
var expensiveProducts = await dataAccess.GetCollectionAsync(x => x.Price >= 500m);
var cheapProducts = await dataAccess.GetCollectionAsync(x => x.Price < 100m);
```

#### 3.1.2 Logical Operators

```csharp
// AND conditions
var premiumElectronics = await dataAccess.GetCollectionAsync(x => 
    x.Category == "Electronics" && x.Price > 1000m && x.IsActive);

// OR conditions
var popularCategories = await dataAccess.GetCollectionAsync(x => 
    x.Category == "Electronics" || x.Category == "Computers" || x.Category == "Gaming");

// NOT conditions
var nonElectronics = await dataAccess.GetCollectionAsync(x => 
    x.Category != "Electronics" && x.IsActive);
```

#### 3.1.3 String Operations

```csharp
// Contains
var searchResults = await dataAccess.GetCollectionAsync(x => 
    x.Name.Contains("Pro") && x.Description.Contains("wireless"));

// StartsWith
var appleProducts = await dataAccess.GetCollectionAsync(x => 
    x.Name.StartsWith("iPhone") || x.Name.StartsWith("iPad"));

// EndsWith
var modelVariants = await dataAccess.GetCollectionAsync(x => 
    x.Name.EndsWith("Pro Max") && x.IsActive);
```

#### 3.1.4 DateTime Operations

```csharp
// Recent products (last 30 days)
var recentProducts = await dataAccess.GetCollectionAsync(x => 
    x.CreatedDate > DateTime.Today.AddDays(-30));

// Products created in specific date range
var q1Products = await dataAccess.GetCollectionAsync(x => 
    x.CreatedDate >= new DateTime(2025, 1, 1) && 
    x.CreatedDate < new DateTime(2025, 4, 1));

// Dynamic date calculations
var lastQuarterProducts = await dataAccess.GetCollectionAsync(x => 
    x.CreatedDate > DateTime.Today.AddMonths(-3));
```

### 3.2 Custom Query Building

For complex scenarios where lambda expressions aren't sufficient:

```csharp
var queryItems = new List<DBQueryItem>
{
    new DBQueryItem 
    { 
        FieldName = "Category", 
        FieldValue = "Electronics", 
        HowToCompare = ComparisonTypes.eq 
    },
    new DBQueryItem 
    { 
        FieldName = "Price", 
        FieldValue = "500", 
        HowToCompare = ComparisonTypes.ge 
    },
    new DBQueryItem 
    { 
        FieldName = "CreatedDate", 
        FieldValue = DateTime.Today.AddDays(-30).ToString("o"), 
        HowToCompare = ComparisonTypes.gt,
        IsDateTime = true
    }
};

// Combine with AND
var products = await dataAccess.GetCollectionAsync(queryItems, QueryCombineStyle.and);

// Combine with OR
var alternativeProducts = await dataAccess.GetCollectionAsync(queryItems, QueryCombineStyle.or);
```

### 3.3 Pagination System

#### 3.3.1 Basic Pagination

```csharp
// First page - 50 items
var firstPage = await dataAccess.GetPagedCollectionAsync(pageSize: 50);

Console.WriteLine($"Items: {firstPage.Items.Count}");
Console.WriteLine($"Has more: {firstPage.HasMore}");
Console.WriteLine($"Total in page: {firstPage.Count}");

// Navigate to next page
if (firstPage.HasMore)
{
    var secondPage = await dataAccess.GetPagedCollectionAsync(
        pageSize: 50, 
        continuationToken: firstPage.ContinuationToken);
}
```

#### 3.3.2 Filtered Pagination

```csharp
// Paginated results with filtering
var activePage = await dataAccess.GetPagedCollectionAsync(
    x => x.IsActive && x.StockQuantity > 0, 
    pageSize: 25);

// Paginated by partition key
var electronicsPage = await dataAccess.GetPagedCollectionAsync(
    "Electronics", 
    pageSize: 100);
```

#### 3.3.3 Fast Initial Loading

Optimized for UI responsiveness:

```csharp
// Quick initial load for immediate UI display
var initialData = await dataAccess.GetInitialDataLoadAsync(initialLoadSize: 20);

// Display initial 20 items immediately
DisplayProducts(initialData.Items);

// Continue loading in background
if (initialData.HasMore)
{
    var backgroundTask = LoadMoreDataAsync(initialData.ContinuationToken);
    // Don't await - let it run in background
}

private async Task LoadMoreDataAsync(string continuationToken)
{
    var additionalData = await dataAccess.GetPagedCollectionAsync(
        pageSize: 100, 
        continuationToken: continuationToken);
    
    // Update UI with additional data
    AppendProducts(additionalData.Items);
}
```

#### 3.3.4 Complete Dataset Iteration

```csharp
public async Task<List<Product>> GetAllProductsSafelyAsync()
{
    var allProducts = new List<Product>();
    string continuationToken = null;
    
    do
    {
        var page = await dataAccess.GetPagedCollectionAsync(
            pageSize: 1000, 
            continuationToken: continuationToken);
            
        allProducts.AddRange(page.Items);
        continuationToken = page.ContinuationToken;
        
        // Optional: Report progress
        Console.WriteLine($"Loaded {allProducts.Count} products so far...");
        
    } while (!string.IsNullOrEmpty(continuationToken));
    
    return allProducts;
}
```

---

## Chapter 4: Batch Operations and Performance

### 4.1 Batch Processing Overview

Azure Table Storage has a strict limit of 100 operations per batch, all within the same partition. The library handles this automatically.

#### 4.1.1 Basic Batch Operations

```csharp
var products = new List<Product>();
// ... populate with up to thousands of products

// Simple batch update
var result = await dataAccess.BatchUpdateListAsync(products);

if (result.Success)
{
    Console.WriteLine($"Successfully processed {result.SuccessfulItems} items");
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

#### 4.1.2 Batch Operations with Progress Tracking

```csharp
var progress = new Progress<BatchUpdateProgress>(p => 
{
    Console.WriteLine($"Progress: {p.PercentComplete:F1}%");
    Console.WriteLine($"Batches: {p.CompletedBatches}/{p.TotalBatches}");
    Console.WriteLine($"Items: {p.ProcessedItems}/{p.TotalItems}");
    Console.WriteLine($"Current batch size: {p.CurrentBatchSize}");
    Console.WriteLine("---");
});

var result = await dataAccess.BatchUpdateListAsync(
    products, 
    TableOperationType.InsertOrReplace, 
    progress);
```

#### 4.1.3 Different Batch Operation Types

```csharp
// Insert or Replace (default)
await dataAccess.BatchUpdateListAsync(newProducts, TableOperationType.InsertOrReplace);

// Insert or Merge (preserves existing data)
await dataAccess.BatchUpdateListAsync(updatedProducts, TableOperationType.InsertOrMerge);

// Delete operations
var inactiveProducts = await dataAccess.GetCollectionAsync(x => !x.IsActive);
await dataAccess.BatchUpdateListAsync(inactiveProducts, TableOperationType.Delete);
```

### 4.2 Performance Optimization

#### 4.2.1 Partition Key Strategy

```csharp
// ✅ Good - Distribute across partitions
product.Category = "Electronics"; // Many different categories
product.ProductID = Guid.NewGuid().ToString();

// ❌ Poor - All in same partition
product.Category = "AllProducts"; // Single partition = bottleneck
```

#### 4.2.2 Batch Size Considerations

```csharp
// Process very large datasets in chunks
public async Task ProcessLargeDatasetAsync(List<Product> allProducts)
{
    const int chunkSize = 1000; // Process 1000 at a time
    
    for (int i = 0; i < allProducts.Count; i += chunkSize)
    {
        var chunk = allProducts.Skip(i).Take(chunkSize).ToList();
        
        var result = await dataAccess.BatchUpdateListAsync(chunk);
        
        if (!result.Success)
        {
            // Handle failures for this chunk
            await LogFailures(result.Errors);
        }
        
        // Optional: Add delay to avoid throttling
        if (i + chunkSize < allProducts.Count)
        {
            await Task.Delay(100); // Small delay between chunks
        }
    }
}
```

---

## Chapter 5: Blob Storage Operations

### 5.1 Blob Service Initialization

```csharp
using ASCTableStorage.Blobs;

// Initialize with connection string
string connectionString = "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...";
var blobService = new AzureBlobs(connectionString);

// Initialize with custom file size limit (default: 5MB)
var blobService = new AzureBlobs(connectionString, maxFileSizeBytes: 10 * 1024 * 1024); // 10MB
```

### 5.2 File Type Management

#### 5.2.1 Default Supported File Types

The library comes with extensive file type support including:
- **Images:** .jpg, .png, .gif, .bmp, .svg, .tiff, .webp
- **Documents:** .pdf, .txt, .doc, .docx, .xls, .xlsx, .ppt, .pptx
- **Audio:** .mp3, .wav, .ogg, .flac, .aac, .m4a
- **Video:** .mp4, .avi, .mov, .wmv, .mkv, .flv
- **Archives:** .zip, .rar, .7z, .tar, .gz

#### 5.2.2 Custom File Type Management

```csharp
// Add custom file types
blobService.AddAllowedFileType(".dwg", "application/acad");
blobService.AddAllowedFileType(".psd", "application/photoshop");

// Remove file types for security
blobService.RemoveAllowedFileType(".exe");
blobService.RemoveAllowedFileType(".bat");

// Check if file type is allowed
if (blobService.IsFileTypeAllowed("document.pdf"))
{
    // Proceed with upload
}

// Get content type for file
string contentType = blobService.GetContentType("image.jpg"); // Returns "image/jpeg"
```

### 5.3 File Upload Operations

#### 5.3.1 Single File Upload

```csharp
string containerName = "documents";
string localFilePath = @"C:\files\important-document.pdf";

try
{
    Uri blobUri = await blobService.UploadFileAsync(
        containerName: containerName,
        filePath: localFilePath,
        blobName: "legal/contracts/contract-2025.pdf", // Optional custom name
        overwrite: true,
        enforceFileTypeRestriction: true,
        maxFileSizeBytes: 25 * 1024 * 1024); // 25MB limit for this file

    Console.WriteLine($"File uploaded successfully: {blobUri}");
    
    // File automatically includes metadata:
    // - UploadedOn: Current timestamp
    // - OriginalFilename: Original file name
    // - FileSize: File size in bytes
    // - ContentType: Automatically detected MIME type
}
catch (ArgumentException ex)
{
    Console.WriteLine($"Upload failed: {ex.Message}");
    // Possible reasons:
    // - File too large
    // - File type not allowed
    // - File not found
}
```

#### 5.3.2 Stream Upload

```csharp
// Upload from memory stream
using var fileStream = File.OpenRead(@"C:\uploads\user-avatar.jpg");

Uri blobUri = await blobService.UploadStreamAsync(
    containerName: "user-content",
    stream: fileStream,
    blobName: "avatars/user-12345.jpg",
    originalFileName: "profile-picture.jpg",
    contentType: "image/jpeg", // Optional - auto-detected if not provided
    overwrite: true);

// Upload byte array
byte[] imageData = Convert.FromBase64String(base64ImageString);
using var memoryStream = new MemoryStream(imageData);

await blobService.UploadStreamAsync(
    "images", 
    memoryStream, 
    "generated/chart-12345.png",
    "chart.png",
    "image/png");
```

#### 5.3.3 Multiple File Upload

```csharp
var filePaths = new[]
{
    @"C:\documents\report-q1.pdf",
    @"C:\documents\report-q2.pdf",
    @"C:\documents\budget-2025.xlsx",
    @"C:\images\chart1.png",
    @"C:\images\chart2.png"
};

try
{
    var results = await blobService.UploadMultipleFilesAsync(
        containerName: "company-files",
        filePaths: filePaths,
        overwrite: true,
        enforceFileTypeRestriction: true,
        maxFileSizeBytes: 50 * 1024 * 1024); // 50MB per file

    foreach (var result in results)
    {
        Console.WriteLine($"✅ {result.Key}: {result.Value}");
    }
}
catch (AggregateException ex)
{
    Console.WriteLine("❌ Some files failed to upload:");
    foreach (var innerEx in ex.InnerExceptions)
    {
        Console.WriteLine($"  - {innerEx.Message}");
    }
}
```

### 5.4 File Download Operations

#### 5.4.1 Download to File

```csharp
bool success = await blobService.DownloadFileAsync(
    containerName: "documents",
    blobName: "reports/annual-report-2024.pdf",
    destinationPath: @"C:\downloads\annual-report.pdf");

if (success)
{
    Console.WriteLine("File downloaded successfully");
    // File is now available at C:\downloads\annual-report.pdf
}
else
{
    Console.WriteLine("Download failed - file may not exist");
}
```

#### 5.4.2 Download to Stream

```csharp
using var memoryStream = new MemoryStream();

bool downloaded = await blobService.DownloadToStreamAsync(
    containerName: "images",
    blobName: "products/product-123.jpg",
    targetStream: memoryStream);

if (downloaded)
{
    byte[] imageData = memoryStream.ToArray();
    
    // Use the data - e.g., return as web response
    return File(imageData, "image/jpeg", "product-image.jpg");
}
```

### 5.5 File Management and Search

#### 5.5.1 Listing Files

```csharp
// List all files in container
var allFiles = await blobService.ListBlobsAsync("documents");

foreach (var file in allFiles)
{
    Console.WriteLine($"📄 {file.Name}");
    Console.WriteLine($"   Original: {file.OriginalFilename}");
    Console.WriteLine($"   Size: {file.Size:N0} bytes ({file.Size / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"   Uploaded: {file.UploadDate:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"   Type: {file.ContentType}");
    Console.WriteLine($"   URL: {file.Url}");
    Console.WriteLine();
}

// List files with prefix filter
var reportFiles = await blobService.ListBlobsAsync("documents", "reports/");
var userAvatars = await blobService.ListBlobsAsync("user-content", "avatars/");
```

#### 5.5.2 Search Operations

```csharp
// Search by filename (case-insensitive)
var searchResults = await blobService.SearchBlobsAsync(
    containerName: "documents",
    searchText: "annual report");

// Search by date range
var recentFiles = await blobService.SearchBlobsAsync(
    containerName: "uploads",
    startDate: DateTime.Today.AddDays(-7),
    endDate: DateTime.Today);

// Combined search - filename and date
var quarterlyReports = await blobService.SearchBlobsAsync(
    containerName: "financial",
    searchText: "quarterly",
    startDate: new DateTime(2025, 1, 1),
    endDate: new DateTime(2025, 12, 31));

// Search for specific file types
var pdfFiles = await blobService.SearchBlobsAsync(
    containerName: "documents",
    searchText: ".pdf");
```

#### 5.5.3 File Deletion

```csharp
// Delete single file
bool deleted = await blobService.DeleteBlobAsync("temp-files", "old-cache.tmp");

if (deleted)
{
    Console.WriteLine("File deleted successfully");
}

// Delete multiple files
var filesToDelete = new[] 
{ 
    "old-report-1.pdf", 
    "outdated-chart.png", 
    "temp-data.xlsx" 
};

var deleteResults = await blobService.DeleteMultipleBlobsAsync("cleanup", filesToDelete);

foreach (var result in deleteResults)
{
    string status = result.Value ? "✅ Deleted" : "❌ Failed";
    Console.WriteLine($"{result.Key}: {status}");
}

// Cleanup old files
var oldFiles = await blobService.SearchBlobsAsync(
    "temp-storage",
    startDate: DateTime.MinValue,
    endDate: DateTime.Today.AddDays(-30)); // Older than 30 days

var oldFileNames = oldFiles.Select(f => f.Name).ToArray();
await blobService.DeleteMultipleBlobsAsync("temp-storage", oldFileNames);
```

---

## Chapter 6: Queuing System for Process Resilience

### 6.1 Queue System Overview

The queuing system provides robust data persistence during process interruptions, cancellations, and unexpected shutdowns. This is crucial for long-running operations that need to resume from where they left off.

### 6.2 Basic Queue Operations

#### 6.2.1 Defining Queue Data Models

```csharp
// Example: Email campaign processing
public class EmailCampaign
{
    public string CampaignID { get; set; }
    public string Name { get; set; }
    public List<string> Recipients { get; set; }
    public DateTime ScheduledDate { get; set; }
    public string Status { get; set; }
    public string Priority { get; set; }
}

// Example: File processing task
public class FileProcessingTask
{
    public string TaskID { get; set; }
    public string FilePath { get; set; }
    public string ProcessingType { get; set; }
    public DateTime QueuedAt { get; set; }
    public int RetryCount { get; set; }
}
```

#### 6.2.2 Creating and Saving Queues

```csharp
var campaigns = new List<EmailCampaign>
{
    new EmailCampaign 
    { 
        CampaignID = "camp-001", 
        Name = "Summer Sale", 
        Status = "Pending",
        Priority = "High"
    },
    new EmailCampaign 
    { 
        CampaignID = "camp-002", 
        Name = "Product Launch", 
        Status = "Pending",
        Priority = "Medium"
    }
};

// Create queue entry
var queueData = new QueueData<EmailCampaign>
{
    Name = "email-campaigns", // Queue identifier
    QueueID = Guid.NewGuid().ToString()
};

// Serialize and save data
queueData.PutData(campaigns);
queueData.SaveQueue(accountName, accountKey);

Console.WriteLine($"Queued {campaigns.Count} campaigns for processing");
```

### 6.3 Process Cancellation Handling

#### 6.3.1 Complete Example: Campaign Processing

```csharp
public class CampaignProcessor
{
    private readonly string _accountName;
    private readonly string _accountKey;
    private const string QUEUE_NAME = "campaign-processing";
    
    public CampaignProcessor(string accountName, string accountKey)
    {
        _accountName = accountName;
        _accountKey = accountKey;
    }

    public async Task ProcessCampaignsAsync(CancellationToken cancellationToken)
    {
        List<EmailCampaign> workingCampaigns = null;
        
        try
        {
            // 1. Retrieve any previously queued campaigns from interruptions
            var queueManager = new QueueData<EmailCampaign>();
            List<EmailCampaign> queuedCampaigns = queueManager.GetQueues(
                QUEUE_NAME, _accountName, _accountKey);
            
            Console.WriteLine($"Retrieved {queuedCampaigns.Count} queued campaigns");
            
            // 2. Get new campaigns from database that need processing
            var dataAccess = new DataAccess<EmailCampaign>(_accountName, _accountKey);
            var newCampaigns = await dataAccess.GetCollectionAsync(x => 
                x.Status == "Pending" && 
                x.ScheduledDate <= DateTime.UtcNow);
            
            Console.WriteLine($"Found {newCampaigns.Count} new campaigns to process");
            
            // 3. Combine queued and new campaigns, removing duplicates
            var allCampaigns = queuedCampaigns.Concat(newCampaigns).ToList();
            if (allCampaigns.Count > 0)
            {
                workingCampaigns = allCampaigns
                    .DistinctBy(x => x.CampaignID)
                    .OrderBy(x => x.Priority == "High" ? 0 : x.Priority == "Medium" ? 1 : 2)
                    .ThenBy(x => x.ScheduledDate)
                    .ToList();
                
                Console.WriteLine($"Processing {workingCampaigns.Count} total campaigns");
                
                // 4. Execute the long-running operation
                await ExecuteCampaignOperationAsync(workingCampaigns, cancellationToken);
                
                // 5. Mark completed campaigns as processed
                foreach (var campaign in workingCampaigns.Where(c => c.Status == "Completed"))
                {
                    campaign.Status = "Sent";
                }
                
                // 6. Update database with final results
                var completedCampaigns = workingCampaigns.Where(c => c.Status == "Sent").ToList();
                if (completedCampaigns.Any())
                {
                    await dataAccess.BatchUpdateListAsync(completedCampaigns);
                    Console.WriteLine($"Updated {completedCampaigns.Count} completed campaigns");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("⚠️ Operation was cancelled - saving progress to queue");
            
            // Save unprocessed work to queue for next run
            if (workingCampaigns?.Any() == true)
            {
                var unprocessedCampaigns = workingCampaigns
                    .Where(c => c.Status != "Completed" && c.Status != "Sent")
                    .ToList();
                
                if (unprocessedCampaigns.Any())
                {
                    var recoveryQueue = new QueueData<EmailCampaign>
                    {
                        Name = QUEUE_NAME,
                        QueueID = Guid.NewGuid().ToString()
                    };
                    
                    recoveryQueue.PutData(unprocessedCampaigns);
                    recoveryQueue.SaveQueue(_accountName, _accountKey);
                    
                    Console.WriteLine($"💾 Saved {unprocessedCampaigns.Count} unprocessed campaigns to queue");
                }
            }
        }
        catch (Exception ex)
        {
            // Log error and potentially queue for retry
            var errorLog = new ErrorLogData(ex, 
                "Campaign processing failed", 
                ErrorCodeTypes.Critical);
            await errorLog.LogErrorAsync(_accountName, _accountKey);
            
            // Could also save partial progress here
            throw;
        }
    }

    private async Task ExecuteCampaignOperationAsync(
        List<EmailCampaign> campaigns, 
        CancellationToken cancellationToken)
    {
        var progress = 0;
        foreach (var campaign in campaigns)
        {
            // Check for cancellation before each campaign
            cancellationToken.ThrowIfCancellationRequested();
            
            Console.WriteLine($"Processing campaign {++progress}/{campaigns.Count}: {campaign.Name}");
            
            try
            {
                // Simulate campaign processing (email sending, etc.)
                await ProcessSingleCampaignAsync(campaign, cancellationToken);
                
                campaign.Status = "Completed";
                Console.WriteLine($"✅ Completed: {campaign.Name}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"⚠️ Cancelled during: {campaign.Name}");
                throw; // Re-throw to trigger queue saving
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed: {campaign.Name} - {ex.Message}");
                campaign.Status = "Failed";
                // Continue with other campaigns
            }
        }
    }

    private async Task ProcessSingleCampaignAsync(
        EmailCampaign campaign, 
        CancellationToken cancellationToken)
    {
        // Simulate processing time with cancellation support
        for (int i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken); // 5 seconds total per campaign
        }
        
        // Your actual campaign processing logic here:
        // - Send emails
        // - Update analytics
        // - Generate reports
        // etc.
    }
}
```

#### 6.3.2 Usage Example

```csharp
public async Task RunCampaignProcessingService()
{
    using var cts = new CancellationTokenSource();
    
    // Cancel after 30 minutes (configurable)
    cts.CancelAfter(TimeSpan.FromMinutes(30));
    
    // Also listen for Ctrl+C
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("Cancellation requested...");
    };
    
    var processor = new CampaignProcessor(accountName, accountKey);
    
    try
    {
        await processor.ProcessCampaignsAsync(cts.Token);
        Console.WriteLine("✅ All campaigns processed successfully");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("⚠️ Processing was cancelled but progress was saved");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Processing failed: {ex.Message}");
    }
}
```

### 6.4 Queue Management Utilities

#### 6.4.1 Extension Methods for Simplified Usage

```csharp
public static class QueueExtensions
{
    public static QueueData<T> CreateQueue<T>(this List<T> data, string queueName)
    {
        var queue = new QueueData<T>
        {
            Name = queueName,
            QueueID = Guid.NewGuid().ToString()
        };
        queue.PutData(data);
        return queue;
    }
    
    public static async Task<bool> SaveQueueAsync<T>(this QueueData<T> queue, 
        string accountName, string accountKey)
    {
        return await Task.Run(() =>
        {
            try
            {
                queue.SaveQueue(accountName, accountKey);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}

// Usage
var highPriorityCampaigns = campaigns
    .Where(c => c.Priority == "High")
    .ToList();

await highPriorityCampaigns
    .CreateQueue("high-priority-campaigns")
    .SaveQueueAsync(accountName, accountKey);
```

#### 6.4.2 Queue Monitoring

```csharp
public class QueueMonitor<T> where T : class
{
    private readonly DataAccess<QueueData<T>> _dataAccess;
    
    public QueueMonitor(string accountName, string accountKey)
    {
        _dataAccess = new DataAccess<QueueData<T>>(accountName, accountKey);
    }
    
    public async Task<List<QueueInfo>> GetQueueStatusAsync()
    {
        var allQueues = await _dataAccess.GetAllTableDataAsync();
        
        return allQueues
            .GroupBy(q => q.Name)
            .Select(g => new QueueInfo
            {
                QueueName = g.Key,
                TotalEntries = g.Count(),
                TotalItems = g.Sum(q => q.GetData().Count),
                OldestEntry = g.Min(q => q.Timestamp),
                NewestEntry = g.Max(q => q.Timestamp)
            })
            .ToList();
    }
    
    public