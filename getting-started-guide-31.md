# ASCDataAccessLibrary v3.1 Complete Documentation

## Enterprise Azure Storage Solution for .NET Applications

**Version**: 3.1.0  
**Last Updated**: January 2025  
**Authors**: O. Brown | M. Chukwuemeka  
**Company**: Answer Sales Calls Inc.  
**License**: MIT

---

## Table of Contents

1. [Introduction & What's New](#introduction--whats-new)
2. [Quick Start Guide](#quick-start-guide)
3. [Installation & Setup](#installation--setup)
4. [Program.cs Configuration Examples](#programcs-configuration-examples)
5. [Core Architecture](#core-architecture)
6. [Azure Table Storage - DataAccess](#azure-table-storage---dataaccess)
7. [Entity Models & TableEntityBase](#entity-models--tableentitybase)
8. [Dynamic Entities](#dynamic-entities)
9. [Lambda Expressions & Hybrid Filtering](#lambda-expressions--hybrid-filtering)
10. [Batch Operations & Pagination](#batch-operations--pagination)
11. [StateList - Position-Aware Collections](#statelist---position-aware-collections)
12. [Queue Management with QueueData](#queue-management-with-queuedata)
13. [Session Management](#session-management)
14. [Universal Logging System](#universal-logging-system)
15. [Error Handling & ErrorLogData](#error-handling--errorlogdata)
16. [Azure Blob Storage Operations](#azure-blob-storage-operations)
17. [Performance Optimization](#performance-optimization)
18. [Migration Guides](#migration-guides)
19. [Best Practices & Patterns](#best-practices--patterns)
20. [Troubleshooting Guide](#troubleshooting-guide)
21. [Complete Working Examples](#complete-working-examples)

---

## Introduction & What's New

ASCDataAccessLibrary v3.1 represents a major evolution in Azure Storage integration for .NET applications. This version introduces significant architectural improvements, new features, and enhanced performance optimizations while maintaining backward compatibility.

### Major Updates in v3.1

#### 🚀 **Universal ILogger Implementation**
- Full `Microsoft.Extensions.Logging.ILogger` implementation
- Works seamlessly across Web, Desktop, Console, and Service applications
- Automatic caller information capture without stack walking
- Circuit breaker pattern for Azure failures with automatic fallback
- Structured logging with scopes and correlation IDs

#### 🔄 **Enhanced Session Management**
- Global `SessionManager` with thread-safe singleton pattern
- Optimized batched writes with configurable delays
- Multiple session ID strategies (HttpContext, UserMachine, MachineProcess, Custom)
- Automatic session cleanup with configurable retention
- Full `ISession` interface compatibility for ASP.NET Core

#### 🎯 **Advanced Dynamic Entities**
- Pattern-based automatic key detection using regex and fuzzy matching
- Runtime schema definition without compile-time types
- Automatic PartitionKey/RowKey inference from data patterns
- Support for complex object serialization
- Diagnostic capabilities for debugging

#### ⚡ **Performance Enhancements**
- Lightweight type cache for serialization (50% faster)
- Optimized batch processing with progress tracking
- Memory-efficient streaming for large datasets
- Connection pooling and reuse patterns
- Hybrid server/client filtering optimization

#### 🛡️ **Reliability Improvements**
- Circuit breaker pattern for Azure failures
- Automatic retry with exponential backoff
- Graceful degradation with fallback logging
- Comprehensive error recovery mechanisms
- Thread-safe operations throughout

### Key Capabilities Summary

| Feature | Description | New in v3.1 |
|---------|-------------|-------------|
| **Table Storage** | Complete CRUD with strongly-typed and dynamic entities | Enhanced |
| **Field Chunking** | Automatic handling of fields > 64KB | Optimized |
| **Lambda Queries** | Intuitive LINQ-style queries with auto-optimization | Enhanced |
| **Hybrid Filtering** | Automatic server/client operation splitting | New Algorithm |
| **Universal Logging** | ILogger across all .NET app types | ✅ New |
| **Session Management** | Distributed-safe sessions without Redis | ✅ New |
| **Blob Storage** | Tag-based indexing with lambda search | Enhanced |
| **State Management** | Position-aware collections | Enhanced |
| **Queue Processing** | Resumable operations with progress | Improved |
| **Batch Operations** | Efficient bulk ops with auto-chunking | Optimized |

---

## Quick Start Guide

### 1. Install the Package

```bash
dotnet add package ASCDataAccessLibrary --version 3.1.0
```

### 2. Configure in Program.cs (ASP.NET Core)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add Azure Table Logging
builder.Logging.AddAzureTableLogging(options =>
{
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    options.MinimumLevel = LogLevel.Information;
    options.EnableAutoCleanup = true;
});

// Add Azure Table Sessions
builder.Services.AddAzureTableSessions(options =>
{
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    options.SessionTimeout = TimeSpan.FromMinutes(20);
});

var app = builder.Build();
```

### 3. Create Your First Entity

```csharp
public class Customer : TableEntityBase, ITableExtra
{
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Name { get; set; }
    public string Email { get; set; }
    public bool IsActive { get; set; }
    
    public string TableReference => "Customers";
    public string GetIDValue() => this.CustomerId;
}
```

### 4. Perform CRUD Operations

```csharp
// Create DataAccess instance
var dataAccess = new DataAccess<Customer>(accountName, accountKey);

// Insert
var customer = new Customer
{
    CustomerId = Guid.NewGuid().ToString(),
    CompanyId = "COMP001",
    Name = "Acme Corporation",
    Email = "contact@acme.com",
    IsActive = true
};
await dataAccess.ManageDataAsync(customer);

// Query with Lambda
var activeCustomers = await dataAccess.GetCollectionAsync(
    c => c.IsActive == true && c.CompanyId == "COMP001"
);

// Update
customer.Email = "newemail@acme.com";
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrMerge);

// Delete
await dataAccess.ManageDataAsync(customer, TableOperationType.Delete);
```

---

## Installation & Setup

### Prerequisites

- **.NET Version**: 6.0, 7.0, 8.0, or 9.0
- **Azure Storage Account**: With Table Storage and Blob Storage enabled
- **Credentials**: Account Name and Account Key (connection strings are NOT used)

### Installation Methods

#### Via Package Manager Console
```powershell
Install-Package ASCDataAccessLibrary -Version 3.1.0
```

#### Via .NET CLI
```bash
dotnet add package ASCDataAccessLibrary --version 3.1.0
```

#### Via PackageReference in .csproj
```xml
<ItemGroup>
  <PackageReference Include="ASCDataAccessLibrary" Version="3.1.0" />
</ItemGroup>
```

### Configuration Storage

Store your Azure credentials securely:

#### appsettings.json (Web Applications)
```json
{
  "Azure": {
    "StorageAccountName": "your-account-name",
    "StorageAccountKey": "your-account-key"
  }
}
```

#### Environment Variables (Console/Services)
```bash
export AZURE_STORAGE_ACCOUNT="your-account-name"
export AZURE_STORAGE_KEY="your-account-key"
```

#### User Secrets (Development)
```bash
dotnet user-secrets init
dotnet user-secrets set "Azure:StorageAccountName" "your-account-name"
dotnet user-secrets set "Azure:StorageAccountKey" "your-account-key"
```

---

## Program.cs Configuration Examples

### ASP.NET Core Web Application (Complete Setup)

```csharp
using ASCTableStorage.Logging;
using ASCTableStorage.Sessions;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure Azure Table Logging with all options
builder.Logging.ClearProviders(); // Optional: Remove default providers
builder.Logging.AddAzureTableLogging(options =>
{
    // Required credentials
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    
    // Logging configuration
    options.TableName = "ApplicationLogs";
    options.MinimumLevel = LogLevel.Information;
    options.ApplicationName = "MyWebApp";
    options.Environment = builder.Environment.EnvironmentName;
    
    // Batching configuration
    options.BatchSize = 100;
    options.FlushInterval = TimeSpan.FromSeconds(2);
    options.MaxQueueSize = 10000;
    
    // Retention configuration
    options.RetentionDays = 30;
    options.EnableAutoCleanup = true;
    options.CleanupInterval = TimeSpan.FromDays(1);
    options.CleanupTimeOfDay = TimeSpan.Parse("02:00:00"); // 2 AM
    
    // Retention by log level
    options.RetentionByLevel = new Dictionary<LogLevel, int>
    {
        { LogLevel.Trace, 7 },
        { LogLevel.Debug, 14 },
        { LogLevel.Information, 30 },
        { LogLevel.Warning, 60 },
        { LogLevel.Error, 90 },
        { LogLevel.Critical, 180 }
    };
    
    // Advanced options
    options.IncludeScopes = true;
    options.EnableFallback = true;
    options.AutoDiscoverCredentials = false;
});

// Keep console logging for development
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

// Configure Azure Table Sessions
builder.Services.AddAzureTableSessions(options =>
{
    // Credentials
    options.AccountName = builder.Configuration["Azure:StorageAccountName"];
    options.AccountKey = builder.Configuration["Azure:StorageAccountKey"];
    
    // Session configuration
    options.TableName = "AppSessionData";
    options.SessionTimeout = TimeSpan.FromMinutes(20);
    options.IdStrategy = SessionIdStrategy.HttpContext;
    options.ApplicationName = "MyWebApp";
    
    // Performance options
    options.BatchWriteDelay = TimeSpan.FromMilliseconds(500);
    options.MaxBatchSize = 100;
    options.AutoCommit = true;
    options.AutoCommitInterval = TimeSpan.FromSeconds(10);
    
    // Cleanup options
    options.EnableAutoCleanup = true;
    options.CleanupInterval = TimeSpan.FromMinutes(30);
    options.StaleDataCleanupAge = TimeSpan.FromMinutes(45);
    
    // Tracking
    options.TrackActivity = true;
});

// Add session middleware support
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Register DataAccess as services
builder.Services.AddScoped(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return new DataAccess<Customer>(
        config["Azure:StorageAccountName"],
        config["Azure:StorageAccountKey"]
    );
});

var app = builder.Build();

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseSession(); // Enable session middleware
app.UseAuthorization();
app.MapControllers();

// Log application startup
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Application started successfully at {StartTime}", DateTime.UtcNow);

// Graceful shutdown handling
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Application is shutting down...");
    SessionManager.Shutdown();
    AzureTableLogging.ShutdownAsync().Wait();
});

app.Run();
```

### Desktop Application (WPF) Setup

```csharp
// App.xaml.cs
using System.Windows;
using ASCTableStorage.Logging;
using ASCTableStorage.Sessions;
using Microsoft.Extensions.Logging;

namespace MyWpfApp
{
    public partial class App : Application
    {
        private ILogger<App> _logger;
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Initialize Azure Table Logging
            AzureTableLogging.Initialize(options =>
            {
                // Load from app.config or settings
                options.AccountName = Properties.Settings.Default.AzureStorageAccountName;
                options.AccountKey = Properties.Settings.Default.AzureStorageAccountKey;
                
                options.ApplicationName = "MyWpfApp";
                options.Environment = "Production";
                options.MinimumLevel = LogLevel.Information;
                options.EnableAutoCleanup = true;
                options.RetentionDays = 60;
                
                // Desktop-specific options
                options.IncludeScopes = true;
                options.EnableFallback = true;
            });
            
            // Initialize Session Management
            SessionManager.Initialize(
                Properties.Settings.Default.AzureStorageAccountName,
                Properties.Settings.Default.AzureStorageAccountKey,
                options =>
                {
                    options.IdStrategy = SessionIdStrategy.UserMachine;
                    options.ApplicationName = "MyWpfApp";
                    options.AutoCommit = true;
                    options.SessionTimeout = TimeSpan.FromMinutes(30);
                    options.EnableAutoCleanup = true;
                });
            
            // Create logger
            _logger = AzureTableLogging.CreateLogger<App>();
            
            // Log application start
            _logger.LogInformation("WPF Application started by {User} on {Machine}", 
                Environment.UserName, 
                Environment.MachineName);
            
            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            
            // Create and show main window
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
        
        private void OnDispatcherUnhandledException(object sender, 
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "Unhandled dispatcher exception");
            
            MessageBox.Show(
                "An unexpected error occurred. The error has been logged.", 
                "Error", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
            
            e.Handled = true;
        }
        
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            _logger.LogCritical(exception, "Unhandled application exception");
            
            if (e.IsTerminating)
            {
                _logger.LogCritical("Application is terminating due to unhandled exception");
            }
        }
        
        protected override void OnExit(ExitEventArgs e)
        {
            _logger.LogInformation("Application shutting down with exit code {ExitCode}", e.ApplicationExitCode);
            
            // Ensure all logs and sessions are flushed
            SessionManager.Shutdown();
            AzureTableLogging.ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
            
            base.OnExit(e);
        }
    }
}

// MainWindow.xaml.cs - Example usage
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly DataAccess<Customer> _dataAccess;
    
    public MainWindow()
    {
        InitializeComponent();
        
        _logger = AzureTableLogging.CreateLogger<MainWindow>();
        _dataAccess = new DataAccess<Customer>(
            Properties.Settings.Default.AzureStorageAccountName,
            Properties.Settings.Default.AzureStorageAccountKey);
        
        _logger.LogInformation("Main window initialized");
    }
    
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("User clicked Save button");
            
            // Store in session
            SessionManager.Current["LastAction"] = "Save";
            SessionManager.Current["LastActionTime"] = DateTime.UtcNow.ToString("O");
            
            var customer = new Customer
            {
                CustomerId = Guid.NewGuid().ToString(),
                CompanyId = "COMP001",
                Name = NameTextBox.Text,
                Email = EmailTextBox.Text,
                IsActive = ActiveCheckBox.IsChecked ?? false
            };
            
            await _dataAccess.ManageDataAsync(customer);
            
            _logger.LogInformation("Customer {CustomerId} saved successfully", customer.CustomerId);
            MessageBox.Show("Customer saved successfully!", "Success", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save customer");
            MessageBox.Show($"Error saving customer: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
```

### Console Application Setup

```csharp
// Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ASCTableStorage.Logging;
using ASCTableStorage.Sessions;
using ASCTableStorage.Data;

// Create host builder
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Register services
        services.AddSingleton<DataProcessor>();
        
        // Register DataAccess instances
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            return new DataAccess<WorkItem>(
                config["Azure:StorageAccountName"],
                config["Azure:StorageAccountKey"]
            );
        });
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        
        // Add Azure Table Logging
        logging.AddAzureTableLogging(options =>
        {
            options.AccountName = context.Configuration["Azure:StorageAccountName"];
            options.AccountKey = context.Configuration["Azure:StorageAccountKey"];
            options.ApplicationName = "DataProcessor";
            options.Environment = context.HostingEnvironment.EnvironmentName;
            options.MinimumLevel = LogLevel.Debug;
            options.EnableAutoCleanup = true;
        });
        
        // Keep console for immediate feedback
        logging.AddConsole();
    })
    .Build();

// Initialize Session Manager
SessionManager.Initialize(
    host.Services.GetRequiredService<IConfiguration>()["Azure:StorageAccountName"],
    host.Services.GetRequiredService<IConfiguration>()["Azure:StorageAccountKey"],
    options =>
    {
        options.IdStrategy = SessionIdStrategy.MachineProcess;
        options.ApplicationName = "DataProcessor";
        options.AutoCommit = true;
    });

// Get logger
var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Set up graceful shutdown
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (sender, e) =>
{
    logger.LogInformation("Shutdown requested via Ctrl+C");
    e.Cancel = true;
    cts.Cancel();
};

lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Application stopping, flushing logs and sessions...");
    SessionManager.Shutdown();
    AzureTableLogging.ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
});

// Run application
try
{
    logger.LogInformation("Console application started at {Time}", DateTime.UtcNow);
    
    var processor = host.Services.GetRequiredService<DataProcessor>();
    await processor.RunAsync(cts.Token);
    
    logger.LogInformation("Processing completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("Processing cancelled by user");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Application failed with critical error");
    Environment.ExitCode = 1;
}
finally
{
    await host.StopAsync();
    host.Dispose();
}

// DataProcessor class example
public class DataProcessor
{
    private readonly ILogger<DataProcessor> _logger;
    private readonly DataAccess<WorkItem> _dataAccess;
    
    public DataProcessor(ILogger<DataProcessor> logger, DataAccess<WorkItem> dataAccess)
    {
        _logger = logger;
        _dataAccess = dataAccess;
    }
    
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting data processing");
        
        // Use session to track progress
        SessionManager.Current["ProcessingStartTime"] = DateTime.UtcNow.ToString("O");
        SessionManager.Current["ProcessingStatus"] = "Running";
        
        var items = await _dataAccess.GetAllTableDataAsync();
        var processed = 0;
        var total = items.Count();
        
        using (_logger.LogProgress("DataProcessing", total))
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Processing cancelled at item {Processed}/{Total}", 
                        processed, total);
                    break;
                }
                
                await ProcessItemAsync(item);
                processed++;
                
                // Update session periodically
                if (processed % 10 == 0)
                {
                    SessionManager.Current["LastProcessedId"] = item.GetIDValue();
                    SessionManager.Current["ProcessedCount"] = processed.ToString();
                    await SessionManager.CommitDataAsync();
                }
            }
        }
        
        SessionManager.Current["ProcessingStatus"] = "Completed";
        SessionManager.Current["ProcessingEndTime"] = DateTime.UtcNow.ToString("O");
        
        _logger.LogInformation("Processed {Count} items successfully", processed);
    }
    
    private async Task ProcessItemAsync(WorkItem item)
    {
        try
        {
            // Process item logic
            await Task.Delay(100); // Simulate work
            
            item.ProcessedDate = DateTime.UtcNow;
            item.Status = "Completed";
            
            await _dataAccess.ManageDataAsync(item, TableOperationType.InsertOrMerge);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process item {ItemId}", item.GetIDValue());
            throw;
        }
    }
}
```

### Windows Service / Background Service Setup

```csharp
// Program.cs for Windows Service
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ASCTableStorage.Logging;
using ASCTableStorage.Sessions;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService() // For Windows Service
            .ConfigureServices((hostContext, services) =>
            {
                // Add the worker service
                services.AddHostedService<Worker>();
                
                // Configure Azure Table Sessions as a hosted service
                services.AddSingleton<SessionBackgroundService>();
                services.AddHostedService(provider => 
                    provider.GetRequiredService<SessionBackgroundService>());
            })
            .ConfigureLogging((hostContext, logging) =>
            {
                logging.ClearProviders();
                
                // Configure Azure Table Logging
                logging.AddAzureTableLogging(options =>
                {
                    var config = hostContext.Configuration;
                    options.AccountName = config["Azure:StorageAccountName"];
                    options.AccountKey = config["Azure:StorageAccountKey"];
                    options.ApplicationName = "MyWindowsService";
                    options.Environment = hostContext.HostingEnvironment.EnvironmentName;
                    options.MinimumLevel = LogLevel.Information;
                    
                    // Service-specific settings
                    options.BatchSize = 200;
                    options.FlushInterval = TimeSpan.FromSeconds(5);
                    options.EnableAutoCleanup = true;
                    options.RetentionDays = 90;
                });
                
                // Add Event Log for Windows Service
                logging.AddEventLog(settings =>
                {
                    settings.SourceName = "MyWindowsService";
                    settings.LogName = "Application";
                });
            });
}

// Worker.cs
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private DataAccess<ServiceTask> _dataAccess;
    private Timer _heartbeatTimer;

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialize components
        _dataAccess = new DataAccess<ServiceTask>(
            _configuration["Azure:StorageAccountName"],
            _configuration["Azure:StorageAccountKey"]
        );
        
        // Initialize Session Manager
        SessionManager.Initialize(
            _configuration["Azure:StorageAccountName"],
            _configuration["Azure:StorageAccountKey"],
            options =>
            {
                options.IdStrategy = SessionIdStrategy.MachineProcess;
                options.ApplicationName = "MyWindowsService";
                options.AutoCommit = true;
                options.SessionTimeout = TimeSpan.FromHours(1);
            });
        
        _logger.LogInformation("Windows Service started at: {time}", DateTimeOffset.Now);
        
        // Set up heartbeat logging
        _heartbeatTimer = new Timer(
            callback: _ => _logger.LogHeartbeat("ServiceWorker"),
            state: null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromMinutes(5)
        );
        
        // Main service loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTasksAsync(stoppingToken);
                
                // Wait before next iteration
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in service loop");
                
                // Wait before retry
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        
        _logger.LogInformation("Windows Service stopping at: {time}", DateTimeOffset.Now);
    }

    private async Task ProcessTasksAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Checking for pending tasks");
        
        // Get pending tasks
        var pendingTasks = await _dataAccess.GetCollectionAsync(
            t => t.Status == "Pending" && t.ScheduledTime <= DateTime.UtcNow
        );
        
        if (!pendingTasks.Any())
        {
            _logger.LogDebug("No pending tasks found");
            return;
        }
        
        _logger.LogInformation("Processing {Count} pending tasks", pendingTasks.Count());
        
        // Store processing state in session
        SessionManager.Current["LastProcessingRun"] = DateTime.UtcNow.ToString("O");
        SessionManager.Current["TasksInProgress"] = pendingTasks.Count().ToString();
        
        // Process tasks with batch result logging
        var processed = 0;
        var failed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        foreach (var task in pendingTasks)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            
            try
            {
                await ExecuteTaskAsync(task);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process task {TaskId}", task.TaskId);
                failed++;
            }
        }
        
        stopwatch.Stop();
        
        // Log batch results
        _logger.LogBatchResult("TaskProcessing", processed, failed, stopwatch.Elapsed);
        
        // Update session
        SessionManager.Current["LastProcessingCompleted"] = DateTime.UtcNow.ToString("O");
        SessionManager.Current["LastProcessedCount"] = processed.ToString();
        SessionManager.Current["LastFailedCount"] = failed.ToString();
        await SessionManager.CommitDataAsync();
    }

    private async Task ExecuteTaskAsync(ServiceTask task)
    {
        _logger.LogDebug("Executing task {TaskId}: {TaskName}", task.TaskId, task.Name);
        
        task.StartTime = DateTime.UtcNow;
        task.Status = "Processing";
        await _dataAccess.ManageDataAsync(task, TableOperationType.InsertOrMerge);
        
        try
        {
            // Simulate task execution
            await Task.Delay(TimeSpan.FromSeconds(5));
            
            task.EndTime = DateTime.UtcNow;
            task.Status = "Completed";
            task.Result = "Success";
        }
        catch (Exception ex)
        {
            task.EndTime = DateTime.UtcNow;
            task.Status = "Failed";
            task.Result = ex.Message;
            throw;
        }
        finally
        {
            await _dataAccess.ManageDataAsync(task, TableOperationType.InsertOrMerge);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Service is stopping");
        
        _heartbeatTimer?.Dispose();
        
        // Ensure all logs and sessions are saved
        await SessionManager.ShutdownAsync();
        await AzureTableLogging.ShutdownAsync();
        
        await base.StopAsync(cancellationToken);
    }
}

// ServiceTask entity
public class ServiceTask : TableEntityBase, ITableExtra
{
    public string TaskId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Category
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Name { get; set; }
    public string Status { get; set; }
    public DateTime ScheduledTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Result { get; set; }
    
    public string TableReference => "ServiceTasks";
    public string GetIDValue() => this.TaskId;
}
```

---

## Azure Table Storage - DataAccess

The `DataAccess<T>` class is the core component for all table storage operations. It provides comprehensive CRUD operations, batch processing, pagination, and advanced querying capabilities.

### Creating DataAccess Instances

```csharp
// Standard creation
var dataAccess = new DataAccess<Customer>(accountName, accountKey);

// With dependency injection
services.AddScoped(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return new DataAccess<Customer>(
        config["Azure:StorageAccountName"],
        config["Azure:StorageAccountKey"]
    );
});

// With custom table name (for non-ITableExtra types)
var dataAccess = new DataAccess<CustomType>(new TableOptions
{
    TableStorageName = accountName,
    TableStorageKey = accountKey,
    TableName = "CustomTable",
    PartitionKeyPropertyName = "CategoryId"
});
```

### CRUD Operations

#### Insert Operations

```csharp
// Insert or Replace (full replacement)
var customer = new Customer
{
    CustomerId = Guid.NewGuid().ToString(),
    CompanyId = "COMP123",
    Name = "Acme Corporation",
    Email = "contact@acme.com",
    Phone = "+1-234-567-8900",
    IsActive = true,
    CreatedDate = DateTime.UtcNow
};

// Synchronous
dataAccess.ManageData(customer, TableOperationType.InsertOrReplace);

// Asynchronous (recommended)
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrReplace);

// Insert only (fails if exists)
try
{
    await dataAccess.ManageDataAsync(customer, TableOperationType.Insert);
}
catch (StorageException ex) when (ex.RequestInformation.HttpStatusCode == 409)
{
    // Entity already exists
    Console.WriteLine("Customer already exists");
}
```

#### Update Operations

```csharp
// Merge update (only updates provided properties)
customer.Email = "newemail@acme.com";
customer.Phone = null; // Won't delete existing phone
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrMerge);

// Replace update (replaces entire entity)
customer.Email = "newemail@acme.com";
customer.Phone = null; // Will delete existing phone
await dataAccess.ManageDataAsync(customer, TableOperationType.Replace);

// Conditional update with ETag
try
{
    await dataAccess.ManageDataAsync(customer, TableOperationType.Replace);
}
catch (StorageException ex) when (ex.RequestInformation.HttpStatusCode == 412)
{
    // Precondition failed - entity was modified by another process
    Console.WriteLine("Entity was modified by another process");
}
```

#### Delete Operations

```csharp
// Delete by entity
await dataAccess.ManageDataAsync(customer, TableOperationType.Delete);

// Delete by key
var customerToDelete = await dataAccess.GetRowObjectAsync("CUST456");
if (customerToDelete != null)
{
    await dataAccess.ManageDataAsync(customerToDelete, TableOperationType.Delete);
}

// Soft delete pattern
customer.IsDeleted = true;
customer.DeletedDate = DateTime.UtcNow;
await dataAccess.ManageDataAsync(customer, TableOperationType.InsertOrMerge);
```

### Query Operations

#### Basic Queries

```csharp
// Get all data from table
var allCustomers = await dataAccess.GetAllTableDataAsync();

// Get by RowKey (primary key)
var customer = await dataAccess.GetRowObjectAsync("CUST456");

// Get by PartitionKey (all entities in partition)
var companyCustomers = await dataAccess.GetCollectionAsync("COMP123");

// Get single entity by field value
var customer = await dataAccess.GetRowObjectAsync(
    "Email",                     // Field name
    ComparisonTypes.eq,         // Comparison operator
    "contact@acme.com"           // Value
);
```

#### Advanced Queries with DBQueryItem

```csharp
// Build complex queries with multiple conditions
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
        FieldName = "Revenue", 
        FieldValue = "1000000", 
        HowToCompare = ComparisonTypes.gt 
    },
    new DBQueryItem 
    { 
        FieldName = "CreatedDate", 
        FieldValue = DateTime.Today.AddDays(-30).ToString("O"), 
        HowToCompare = ComparisonTypes.ge,
        IsDateTime = true 
    }
};

// Combine with AND
var highValueActive = await dataAccess.GetCollectionAsync(
    queryTerms, 
    QueryCombineStyle.and
);

// Combine with OR
var priorityCustomers = await dataAccess.GetCollectionAsync(
    queryTerms, 
    QueryCombineStyle.or
);
```

### Lambda Expression Queries

Lambda expressions provide the most intuitive way to query data. The library automatically optimizes these into hybrid server/client operations.

#### Simple Queries

```csharp
// Equality check
var activeCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active"
);

// Inequality
var nonActiveCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status != "Active"
);

// Comparison operators
var highValueCustomers = await dataAccess.GetCollectionAsync(
    c => c.Revenue > 1000000
);

var recentCustomers = await dataAccess.GetCollectionAsync(
    c => c.CreatedDate >= DateTime.Today.AddDays(-7)
);
```

#### Complex Queries

```csharp
// Multiple conditions with AND
var targetCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && 
         c.Revenue > 500000 && 
         c.CreatedDate > DateTime.Today.AddMonths(-6) &&
         c.IsPremium == true
);

// OR conditions
var priorityCustomers = await dataAccess.GetCollectionAsync(
    c => c.IsPremium == true || 
         c.Revenue > 1000000 || 
         c.CustomerType == "Enterprise"
);

// Mixed AND/OR with grouping
var complexQuery = await dataAccess.GetCollectionAsync(
    c => (c.Status == "Active" || c.Status == "Trial") &&
         c.Revenue > 100000 &&
         (c.Region == "North" || c.Region == "South")
);
```

#### String Operations (Client-Side)

```csharp
// Contains (case-sensitive)
var techCompanies = await dataAccess.GetCollectionAsync(
    c => c.Name.Contains("Tech")
);

// Case-insensitive contains
var techCompaniesIgnoreCase = await dataAccess.GetCollectionAsync(
    c => c.Name.ToLower().Contains("tech")
);

// StartsWith / EndsWith
var emailDomain = await dataAccess.GetCollectionAsync(
    c => c.Email.EndsWith("@acme.com")
);

var prefixMatch = await dataAccess.GetCollectionAsync(
    c => c.ProductCode.StartsWith("PROD-")
);

// String length
var longNames = await dataAccess.GetCollectionAsync(
    c => c.Name.Length > 50
);
```

#### Null Checks

```csharp
// Check for null or empty
var customersWithEmail = await dataAccess.GetCollectionAsync(
    c => !string.IsNullOrEmpty(c.Email)
);

// Check for null values
var customersWithoutPhone = await dataAccess.GetCollectionAsync(
    c => c.Phone == null
);

// Combine with other conditions
var incompleteProfiles = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && 
         (string.IsNullOrEmpty(c.Email) || string.IsNullOrEmpty(c.Phone))
);
```

#### Working with Collections

```csharp
// Entity with collection property
public class Order : TableEntityBase, ITableExtra
{
    public string OrderId { get; set; }
    public List<string> ProductIds { get; set; }
    public string ProductIdsJson 
    { 
        get => JsonConvert.SerializeObject(ProductIds);
        set => ProductIds = JsonConvert.DeserializeObject<List<string>>(value);
    }
    
    public string TableReference => "Orders";
    public string GetIDValue() => this.OrderId;
}

// Query with collection operations (client-side)
var ordersWithProduct = await dataAccess.GetCollectionAsync(
    o => o.ProductIds.Contains("PROD123")
);

var largeOrders = await dataAccess.GetCollectionAsync(
    o => o.ProductIds.Count > 10
);
```

---

## Entity Models & TableEntityBase

### Understanding TableEntityBase

`TableEntityBase` is the foundation for all entities stored in Azure Table Storage. It provides automatic field chunking for large data and implements the required Azure Table Storage properties.

#### Core Properties

```csharp
public abstract class TableEntityBase : ITableEntity
{
    // Azure Table Storage required properties
    public string PartitionKey { get; set; }  // Logical grouping
    public string RowKey { get; set; }        // Unique within partition
    public DateTimeOffset Timestamp { get; set; } // Last modified
    public string ETag { get; set; }          // Optimistic concurrency
    
    // Automatic chunking for fields > 32KB
    // Serialization/deserialization handled automatically
}
```

### Creating Basic Entities

```csharp
public class Customer : TableEntityBase, ITableExtra
{
    // Map PartitionKey and RowKey to meaningful names
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value ?? Guid.NewGuid().ToString();
    }
    
    // Simple properties (stored directly)
    public string Name { get; set; }
    public string Email { get; set; }
    public string Phone { get; set; }
    public bool IsActive { get; set; }
    public decimal Revenue { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? LastOrderDate { get; set; }
    
    // ITableExtra implementation
    public string TableReference => "Customers";
    public string GetIDValue() => this.CustomerId;
}
```

### Handling Large Data

Fields are automatically chunked if they exceed 32KB:

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
    
    // These can be any size - automatically chunked
    public string Content { get; set; }        // Can be megabytes
    public string JsonData { get; set; }       // Large JSON objects
    public string XmlContent { get; set; }     // Large XML documents
    public string Base64Image { get; set; }    // Base64 encoded images
    
    // Metadata (normal size)
    public string Title { get; set; }
    public string Author { get; set; }
    public long SizeInBytes { get; set; }
    
    public string TableReference => "Documents";
    public string GetIDValue() => this.DocumentId;
}

// Usage example
var document = new Document
{
    DocumentId = Guid.NewGuid().ToString(),
    Category = "Reports",
    Title = "Annual Report 2025",
    Author = "John Doe",
    Content = File.ReadAllText("large-report.html"), // 5MB file - no problem!
    JsonData = JsonConvert.SerializeObject(complexObject),
    SizeInBytes = new FileInfo("large-report.html").Length
};

await dataAccess.ManageDataAsync(document);
```

### Complex Types and Serialization

```csharp
public class Product : TableEntityBase, ITableExtra
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
    
    // Simple properties
    public string Name { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    
    // Complex object - serialize to JSON
    private ProductDetails _details;
    public ProductDetails Details
    {
        get 
        { 
            if (_details == null && !string.IsNullOrEmpty(DetailsJson))
                _details = JsonConvert.DeserializeObject<ProductDetails>(DetailsJson);
            return _details;
        }
        set 
        { 
            _details = value;
            DetailsJson = value != null ? 
                JsonConvert.SerializeObject(value) : null;
        }
    }
    
    // JSON storage field
    public string DetailsJson { get; set; }
    
    // Collection property
    private List<string> _tags;
    public List<string> Tags
    {
        get 
        { 
            if (_tags == null && !string.IsNullOrEmpty(TagsJson))
                _tags = JsonConvert.DeserializeObject<List<string>>(TagsJson);
            return _tags ?? (_tags = new List<string>());
        }
        set 
        { 
            _tags = value;
            TagsJson = value != null ? 
                JsonConvert.SerializeObject(value) : null;
        }
    }
    
    public string TagsJson { get; set; }
    
    // Computed property for searching
    public string SearchText { get; set; }
    
    // Update search text before saving
    public void UpdateSearchText()
    {
        SearchText = $"{Name} {string.Join(" ", Tags ?? new List<string>())}".ToLower();
    }
    
    public string TableReference => "Products";
    public string GetIDValue() => this.ProductId;
}

public class ProductDetails
{
    public string Description { get; set; }
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public Dictionary<string, string> Specifications { get; set; }
    public List<string> Features { get; set; }
    public Dimensions Dimensions { get; set; }
}

public class Dimensions
{
    public decimal Length { get; set; }
    public decimal Width { get; set; }
    public decimal Height { get; set; }
    public decimal Weight { get; set; }
    public string Unit { get; set; }
}
```

### Entity Inheritance Patterns

```csharp
// Base entity with common properties
public abstract class BaseEntity : TableEntityBase, ITableExtra
{
    // Audit fields
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string ModifiedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedDate { get; set; }
    public string DeletedBy { get; set; }
    
    // Version control
    public int Version { get; set; }
    
    // Abstract members for derived classes
    public abstract string TableReference { get; }
    public abstract string GetIDValue();
    
    // Helper methods
    public void SetCreated(string userId)
    {
        CreatedDate = DateTime.UtcNow;
        CreatedBy = userId;
        Version = 1;
    }
    
    public void SetModified(string userId)
    {
        ModifiedDate = DateTime.UtcNow;
        ModifiedBy = userId;
        Version++;
    }
    
    public void SetDeleted(string userId)
    {
        IsDeleted = true;
        DeletedDate = DateTime.UtcNow;
        DeletedBy = userId;
    }
}

// Specific implementation
public class Employee : BaseEntity
{
    public string EmployeeId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string DepartmentId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string Position { get; set; }
    public decimal Salary { get; set; }
    public DateTime HireDate { get; set; }
    
    // Computed properties
    public string FullName => $"{FirstName} {LastName}";
    
    public override string TableReference => "Employees";
    public override string GetIDValue() => this.EmployeeId;
}

// Usage with inheritance
public class EmployeeService
{
    private readonly DataAccess<Employee> _dataAccess;
    private readonly string _currentUserId;
    
    public async Task<Employee> CreateEmployeeAsync(Employee employee)
    {
        employee.SetCreated(_currentUserId);
        await _dataAccess.ManageDataAsync(employee);
        return employee;
    }
    
    public async Task<Employee> UpdateEmployeeAsync(Employee employee)
    {
        employee.SetModified(_currentUserId);
        await _dataAccess.ManageDataAsync(employee, TableOperationType.InsertOrMerge);
        return employee;
    }
    
    public async Task SoftDeleteEmployeeAsync(string employeeId)
    {
        var employee = await _dataAccess.GetRowObjectAsync(employeeId);
        if (employee != null && !employee.IsDeleted)
        {
            employee.SetDeleted(_currentUserId);
            await _dataAccess.ManageDataAsync(employee, TableOperationType.InsertOrMerge);
        }
    }
}
```

### Optimized Entity Patterns

```csharp
public class OptimizedCustomer : TableEntityBase, ITableExtra
{
    private string _name;
    private string _email;
    private List<string> _tags;
    
    public string CustomerId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string CompanyId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    // Auto-update computed fields
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
            EmailDomain = value?.Split('@').LastOrDefault()?.ToLower();
            UpdateSearchText();
        }
    }
    
    // Pre-computed fields for server-side filtering
    public string NameLowercase { get; set; }
    public string EmailDomain { get; set; }
    public string SearchText { get; set; }
    public int YearMonth { get; set; }  // Format: 202501
    
    // Tags with JSON storage
    public List<string> Tags
    {
        get => _tags ?? (_tags = new List<string>());
        set
        {
            _tags = value;
            TagsJson = value != null ? JsonConvert.SerializeObject(value) : null;
            UpdateSearchText();
        }
    }
    
    public string TagsJson { get; set; }
    
    // Update all computed fields
    private void UpdateSearchText()
    {
        var tagString = _tags != null ? string.Join(" ", _tags) : "";
        SearchText = $"{Name} {Email} {tagString}".ToLower();
    }
    
    // Call before saving
    public void PrepareForSave()
    {
        YearMonth = int.Parse(DateTime.UtcNow.ToString("yyyyMM"));
        UpdateSearchText();
    }
    
    public string TableReference => "OptimizedCustomers";
    public string GetIDValue() => this.CustomerId;
}

// Usage
var customer = new OptimizedCustomer
{
    CustomerId = Guid.NewGuid().ToString(),
    CompanyId = "COMP001",
    Name = "Acme Corporation",
    Email = "contact@acme.com",
    Tags = new List<string> { "premium", "technology", "enterprise" }
};

customer.PrepareForSave();
await dataAccess.ManageDataAsync(customer);

// Efficient server-side queries using pre-computed fields
var acmeEmails = await dataAccess.GetCollectionAsync(
    c => c.EmailDomain == "acme.com"
);

var currentMonthCustomers = await dataAccess.GetCollectionAsync(
    c => c.YearMonth == 202501
);
```

---

## Dynamic Entities

Dynamic entities allow you to work with table storage without defining compile-time types. Perfect for scenarios with variable schemas or runtime-defined data structures.

### Basic Dynamic Entity Usage

```csharp
// Create with explicit keys
var entity = new DynamicEntity("Products", "Electronics", "PROD-001");

// Add properties dynamically
entity["Name"] = "Laptop";
entity["Brand"] = "Dell";
entity["Price"] = 999.99m;
entity["InStock"] = true;
entity["LastUpdated"] = DateTime.UtcNow;
entity["Specifications"] = JsonConvert.SerializeObject(new
{
    CPU = "Intel i7",
    RAM = "16GB",
    Storage = "512GB SSD"
});

// Alternative property methods
entity.SetProperty("Category", "Computers");
entity.SetProperty("WarrantyYears", 3);

// Save to Azure
var dataAccess = new DataAccess<DynamicEntity>(accountName, accountKey);
await dataAccess.ManageDataAsync(entity);
```

### Pattern-Based Automatic Key Detection

The library can automatically detect which fields should be used as PartitionKey and RowKey based on patterns:

```csharp
// Define pattern configuration
var patternConfig = new DynamicEntity.KeyPatternConfig
{
    PartitionKeyPatterns = new List<DynamicEntity.PatternRule>
    {
        new() 
        { 
            Pattern = "category|type|group", 
            Priority = 1,
            KeyType = KeyGenerationType.DirectValue 
        },
        new() 
        { 
            Pattern = ".*date$", 
            Priority = 2,
            KeyType = KeyGenerationType.DateBased  // Converts to YYYY-MM
        },
        new() 
        { 
            Pattern = "tenant.*|company.*|customer.*", 
            Priority = 3,
            KeyType = KeyGenerationType.DirectValue 
        }
    },
    RowKeyPatterns = new List<DynamicEntity.PatternRule>
    {
        new() 
        { 
            Pattern = ".*id$|.*key$|.*code$", 
            Priority = 1,
            KeyType = KeyGenerationType.DirectValue 
        },
        new() 
        { 
            Pattern = "timestamp|created.*", 
            Priority = 2,
            KeyType = KeyGenerationType.ReverseTimestamp 
        },
        new() 
        { 
            Pattern = "name|title", 
            Priority = 3,
            KeyType = KeyGenerationType.Generated,
            ValueTransform = value => $"{value}-{Guid.NewGuid():N}"
        }
    },
    FuzzyMatchThreshold = 0.7  // 70% similarity required
};

// Create entity with automatic key detection
var data = new Dictionary<string, object>
{
    { "category", "Electronics" },      // Detected as PartitionKey
    { "productId", "PROD-123" },        // Detected as RowKey
    { "name", "Wireless Mouse" },
    { "price", 29.99 },
    { "createdDate", DateTime.UtcNow }
};

var entity = new DynamicEntity("Products", data, patternConfig);

Console.WriteLine($"PartitionKey: {entity.PartitionKey}"); // "Electronics"
Console.WriteLine($"RowKey: {entity.RowKey}");             // "PROD-123"
```

### Working with Dynamic Properties

```csharp
// Type-safe property retrieval
string name = entity.GetProperty<string>("name");
decimal price = entity.GetProperty<decimal>("price");
bool inStock = entity.GetProperty<bool>("inStock");
DateTime? lastUpdated = entity.GetProperty<DateTime?>("lastUpdated");

// Handle missing properties
var discount = entity.GetProperty<decimal?>("discount") ?? 0m;

// Check property existence
if (entity.HasProperty("specialOffer"))
{
    var offer = entity.GetProperty<string>("specialOffer");
    Console.WriteLine($"Special offer: {offer}");
}

// Get all properties
var allProperties = entity.GetAllProperties();
foreach (var prop in allProperties)
{
    Console.WriteLine($"{prop.Key}: {prop.Value} ({prop.Value?.GetType().Name})");
}

// Remove properties
entity.RemoveProperty("temporaryField");

// Property indexer
var value = entity["price"];  // Get
entity["price"] = 24.99m;     // Set
```

### Creating from JSON

```csharp
// JSON string
var json = @"{
    'orderId': 'ORD-2025-001',
    'customerId': 'CUST-123',
    'orderDate': '2025-01-15T10:30:00Z',
    'items': [
        { 'productId': 'PROD-1', 'quantity': 2, 'price': 29.99 },
        { 'productId': 'PROD-2', 'quantity': 1, 'price': 49.99 }
    ],
    'total': 109.97,
    'status': 'Pending'
}";

// Create with automatic key detection
var entity = DynamicEntity.CreateFromJson("Orders", json, new KeyPatternConfig
{
    PartitionKeyPatterns = new List<PatternRule>
    {
        new() { Pattern = "customer.*", Priority = 1 }
    },
    RowKeyPatterns = new List<PatternRule>
    {
        new() { Pattern = "order.*", Priority = 1 }
    }
});

// Entity will have:
// PartitionKey: "CUST-123" (from customerId)
// RowKey: "ORD-2025-001" (from orderId)
```

### Converting Objects to Dynamic Entities

```csharp
// Any object can be converted
var order = new
{
    OrderId = "ORD-123",
    CustomerId = "CUST-456",
    OrderDate = DateTime.UtcNow,
    Items = new[]
    {
        new { ProductId = "PROD-1", Quantity = 2 },
        new { ProductId = "PROD-2", Quantity = 1 }
    },
    Total = 150.00m
};

// Convert to DynamicEntity
var entity = DynamicEntity.CreateFromObject(
    order, 
    "Orders",
    partitionKeyPropertyName: "CustomerId"
);

// Save
await dataAccess.ManageDataAsync(entity);
```

### Advanced Pattern Configuration

```csharp
// Load pattern configuration from JSON
var jsonConfig = @"{
    'partitionKeyPatterns': [
        { 
            'pattern': '.*date$', 
            'priority': 1, 
            'keyType': 'DateBased'
        },
        { 
            'pattern': 'region|location|zone', 
            'priority': 2, 
            'keyType': 'DirectValue'
        }
    ],
    'rowKeyPatterns': [
        { 
            'pattern': 'id|identifier', 
            'priority': 1, 
            'keyType': 'DirectValue'
        },
        { 
            'pattern': '.*', 
            'priority': 99, 
            'keyType': 'Generated'
        }
    ],
    'fuzzyMatchThreshold': 0.8
}";

var config = DynamicEntity.LoadPatternConfig(jsonConfig);

// Use for multiple entities
var entities = jsonDataArray.Select(json => 
    DynamicEntity.CreateFromJson("DynamicData", json, config)
).ToList();

// Batch save
await dataAccess.BatchUpdateListAsync(entities);
```

### Querying Dynamic Entities

```csharp
var dataAccess = new DataAccess<DynamicEntity>(accountName, accountKey);

// Query by partition
var categoryEntities = await dataAccess.GetCollectionAsync("Electronics");

// Lambda queries with dynamic properties
var expensiveItems = await dataAccess.GetCollectionAsync(
    e => e.GetProperty<decimal>("price") > 100
);

var activeProducts = await dataAccess.GetCollectionAsync(
    e => e.GetProperty<bool>("isActive") == true &&
         e.GetProperty<int>("stock") > 0
);

// Complex queries
var results = await dataAccess.GetCollectionAsync(
    e => e.GetProperty<string>("category") == "Electronics" &&
         e.GetProperty<decimal>("price") < 500 &&
         e.GetProperty<DateTime>("lastUpdated") > DateTime.Today.AddDays(-7)
);
```

### Dynamic Entity Diagnostics

```csharp
// Get diagnostic information
var diagnostics = entity.GetDiagnostics();

Console.WriteLine("Entity Diagnostics:");
Console.WriteLine($"  Table: {diagnostics["TableName"]}");
Console.WriteLine($"  PartitionKey: {diagnostics["PartitionKey"]}");
Console.WriteLine($"  RowKey: {diagnostics["RowKey"]}");
Console.WriteLine($"  PropertyCount: {diagnostics["PropertyCount"]}");
Console.WriteLine($"  EstimatedSize: {diagnostics["EstimatedSizeBytes"]} bytes");
Console.WriteLine($"  HasLargeProperties: {diagnostics["HasLargeProperties"]}");

// Property types breakdown
var propertyTypes = diagnostics["PropertyTypes"] as Dictionary<string, int>;
foreach (var type in propertyTypes)
{
    Console.WriteLine($"  {type.Key}: {type.Value} properties");
}
```

### Real-World Dynamic Entity Example

```csharp
public class DynamicFormProcessor
{
    private readonly DataAccess<DynamicEntity> _dataAccess;
    private readonly ILogger<DynamicFormProcessor> _logger;
    
    public DynamicFormProcessor(string accountName, string accountKey)
    {
        _dataAccess = new DataAccess<DynamicEntity>(accountName, accountKey);
        _logger = AzureTableLogging.CreateLogger<DynamicFormProcessor>();
    }
    
    public async Task<string> ProcessFormSubmissionAsync(Dictionary<string, object> formData)
    {
        try
        {
            // Add metadata
            formData["submittedAt"] = DateTime.UtcNow;
            formData["submissionId"] = Guid.NewGuid().ToString();
            formData["ipAddress"] = GetClientIpAddress();
            
            // Create entity with pattern detection
            var entity = new DynamicEntity("FormSubmissions", formData, new KeyPatternConfig
            {
                PartitionKeyPatterns = new List<PatternRule>
                {
                    new() { Pattern = "formType|formName", Priority = 1 },
                    new() { Pattern = ".*date", Priority = 2, KeyType = KeyGenerationType.DateBased }
                },
                RowKeyPatterns = new List<PatternRule>
                {
                    new() { Pattern = "submissionId", Priority = 1 }
                }
            });
            
            // Validate required fields
            var requiredFields = new[] { "email", "name", "formType" };
            foreach (var field in requiredFields)
            {
                if (!entity.HasProperty(field))
                {
                    throw new ValidationException($"Required field '{field}' is missing");
                }
            }
            
            // Save to Azure
            await _dataAccess.ManageDataAsync(entity);
            
            _logger.LogInformation("Form submission {SubmissionId} processed successfully", 
                entity.GetProperty<string>("submissionId"));
            
            return entity.GetProperty<string>("submissionId");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process form submission");
            throw;
        }
    }
    
    public async Task<IEnumerable<DynamicEntity>> GetSubmissionsByTypeAsync(string formType)
    {
        return await _dataAccess.GetCollectionAsync(
            e => e.GetProperty<string>("formType") == formType
        );
    }
    
    private string GetClientIpAddress()
    {
        // Implementation depends on your environment
        return "127.0.0.1";
    }
}
```

---

## Lambda Expressions & Hybrid Filtering

The library automatically optimizes lambda expressions by splitting operations between server-side (Azure Table Storage) and client-side execution. This provides intuitive query syntax while maintaining optimal performance.

### Understanding Hybrid Filtering

```csharp
// Server-side operations (executed in Azure):
// ✅ Simple comparisons (==, !=, <, >, <=, >=)
// ✅ AND/OR logical operations
// ✅ Date/DateTime comparisons
// ✅ Boolean checks
// ✅ Null comparisons
// ✅ Numeric operations

// Client-side operations (executed locally):
// ❌ String methods (Contains, StartsWith, EndsWith, ToLower, ToUpper)
// ❌ Complex expressions and calculations
// ❌ Method calls (except ToString in specific cases)
// ❌ LINQ operations (Any, All, Count, etc.)
// ❌ Property navigation through complex objects
```

### Automatic Optimization Examples

```csharp
// Pure server-side query (fastest)
var results = await dataAccess.GetCollectionAsync(
    x => x.Status == "Active" && 
         x.Priority > 5 &&
         x.CreatedDate > DateTime.Today.AddDays(-7) &&
         x.IsEnabled == true
);
// Generated OData: Status eq 'Active' and Priority gt 5 and CreatedDate gt datetime'2025-01-08T00:00:00Z' and IsEnabled eq true

// Mixed server/client query (automatic hybrid filtering)
var results = await dataAccess.GetCollectionAsync(
    c => c.Revenue > 1000000 &&                    // Server-side
         c.Name.ToLower().Contains("tech")          // Client-side
);
// The library:
// 1. Executes "Revenue > 1000000" on Azure (server-side filter)
// 2. Retrieves filtered dataset
// 3. Applies "Name.ToLower().Contains('tech')" locally
// 4. Returns final results

// Complex hybrid example
var results = await dataAccess.GetCollectionAsync(
    p => (p.Category == "Electronics" ||           // Server-side
          p.Category == "Computers") &&            // Server-side
         p.Price < 1000 &&                         // Server-side
         p.InStock == true &&                      // Server-side
         p.Description.Contains("gaming") &&       // Client-side
         p.Tags.Any(t => t.StartsWith("new"))     // Client-side
);
```

### Optimization Strategies

#### Strategy 1: Pre-compute Searchable Fields

```csharp
public class OptimizedProduct : TableEntityBase, ITableExtra
{
    private string _name;
    private string _description;
    
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            NameLowercase = value?.ToLower();
            UpdateSearchField();
        }
    }
    
    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            UpdateSearchField();
        }
    }
    
    // Pre-computed fields for server-side filtering
    public string NameLowercase { get; set; }
    public string SearchText { get; set; }
    public bool HasGamingTag { get; set; }
    
    private void UpdateSearchField()
    {
        SearchText = $"{Name} {Description}".ToLower();
        HasGamingTag = Description?.Contains("gaming", StringComparison.OrdinalIgnoreCase) ?? false;
    }
    
    public string TableReference => "Products";
    public string GetIDValue() => this.RowKey;
}

// Now you can use server-side queries
var gamingProducts = await dataAccess.GetCollectionAsync(
    p => p.HasGamingTag == true  // Server-side instead of Description.Contains("gaming")
);
```

#### Strategy 2: Use Multiple Queries for Complex Logic

```csharp
// Instead of one complex query with many client-side operations
var inefficient = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" &&
         (c.Name.Contains("Tech") || c.Description.Contains("Technology")) &&
         c.Tags.Any(t => t == "Premium")
);

// Use multiple optimized queries
var techCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && c.NameLowercase.Contains("tech")
);

var technologyCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && c.DescriptionLowercase.Contains("technology")
);

var premiumCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == "Active" && c.IsPremium == true  // Pre-computed flag
);

// Combine results
var results = techCustomers
    .Union(technologyCustomers)
    .Intersect(premiumCustomers)
    .Distinct();
```

#### Strategy 3: Partition Strategy for Time-Based Data

```csharp
public class TimeBasedEntity : TableEntityBase, ITableExtra
{
    public string EntityId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    // Use YYYYMM as partition key for efficient date queries
    public string YearMonth
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public DateTime EventDate { get; set; }
    
    public void SetEventDate(DateTime date)
    {
        EventDate = date;
        YearMonth = date.ToString("yyyyMM");
    }
    
    public string TableReference => "Events";
    public string GetIDValue() => this.EntityId;
}

// Efficient query for date range
var currentMonthEvents = await dataAccess.GetCollectionAsync("202501");

// Or with additional filters
var januaryActiveEvents = await dataAccess.GetCollectionAsync(
    e => e.YearMonth == "202501" && e.Status == "Active"
);
```

### Advanced Query Patterns

#### Closure Variables in Queries

```csharp
// The library handles closure variables automatically
string targetStatus = "Active";
decimal minRevenue = 100000;
DateTime cutoffDate = DateTime.Today.AddDays(-30);

var results = await dataAccess.GetCollectionAsync(
    c => c.Status == targetStatus &&          // Closure variable
         c.Revenue > minRevenue &&            // Closure variable
         c.LastActivityDate > cutoffDate      // Closure variable
);

// Dynamic query building
public async Task<IEnumerable<Customer>> SearchCustomersAsync(
    string status = null,
    decimal? minRevenue = null,
    DateTime? afterDate = null)
{
    return await dataAccess.GetCollectionAsync(c =>
        (status == null || c.Status == status) &&
        (minRevenue == null || c.Revenue >= minRevenue.Value) &&
        (afterDate == null || c.CreatedDate > afterDate.Value)
    );
}
```

#### Working with Enums

```csharp
public enum CustomerStatus
{
    Prospect,
    Active,
    Inactive,
    Suspended
}

public class CustomerWithEnum : TableEntityBase, ITableExtra
{
    public CustomerStatus Status { get; set; }
    
    // Store as string for querying
    public string StatusString
    {
        get => Status.ToString();
        set => Status = Enum.Parse<CustomerStatus>(value);
    }
    
    public string TableReference => "Customers";
    public string GetIDValue() => this.RowKey;
}

// Query with enum
var activeCustomers = await dataAccess.GetCollectionAsync(
    c => c.Status == CustomerStatus.Active
);
```

#### Combining Server and Client Filters Efficiently

```csharp
public class SmartQueryService
{
    private readonly DataAccess<Product> _dataAccess;
    
    public async Task<IEnumerable<Product>> SmartSearchAsync(string searchTerm)
    {
        // First, get a reasonable dataset with server-side filters
        var serverFiltered = await _dataAccess.GetCollectionAsync(
            p => p.IsActive == true && 
                 p.Price < 10000 &&
                 p.StockQuantity > 0
        );
        
        // Then apply complex client-side filters on the smaller dataset
        var results = serverFiltered.Where(p =>
            p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            p.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            p.Tags.Any(t => t.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
        );
        
        return results;
    }
}
```

---

## Batch Operations & Pagination

### Batch Operations

Azure Table Storage limits batch operations to 100 entities per batch, and all entities must share the same PartitionKey.

#### Basic Batch Insert

```csharp
// Create test data
var customers = new List<Customer>();
for (int i = 0; i < 500; i++)
{
    customers.Add(new Customer
    {
        CustomerId = $"CUST{i:D6}",
        CompanyId = "COMP001",  // Same partition for batch
        Name = $"Customer {i}",
        Email = $"customer{i}@example.com",
        IsActive = i % 2 == 0,
        Revenue = Random.Shared.Next(10000, 1000000)
    });
}

// Simple batch insert
var result = await dataAccess.BatchUpdateListAsync(
    customers, 
    TableOperationType.InsertOrReplace
);

Console.WriteLine($"Success: {result.Success}");
Console.WriteLine($"Successful: {result.SuccessfulItems}");
Console.WriteLine($"Failed: {result.FailedItems}");

if (!result.Success && result.Errors.Any())
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
}
```

#### Batch Operations with Progress Tracking

```csharp
// Create progress handler
var progress = new Progress<DataAccess<Customer>.BatchUpdateProgress>(p =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Progress Report:");
    Console.WriteLine($"  - Percent Complete: {p.PercentComplete:F1}%");
    Console.WriteLine($"  - Batches: {p.CompletedBatches}/{p.TotalBatches}");
    Console.WriteLine($"  - Items: {p.ProcessedItems}/{p.TotalItems}");
    Console.WriteLine($"  - Current Batch Size: {p.CurrentBatchSize}");
    Console.WriteLine();
});

// Batch update with progress
var result = await dataAccess.BatchUpdateListAsync(
    customers,
    TableOperationType.InsertOrReplace,
    progress
);
```

#### Handling Mixed Partitions

```csharp
// Mixed partitions - automatically grouped and processed
var mixedCustomers = new List<Customer>();

// Different companies (partitions)
for (int company = 1; company <= 5; company++)
{
    for (int customer = 1; customer <= 250; customer++)
    {
        mixedCustomers.Add(new Customer
        {
            CustomerId = $"CUST{company:D2}{customer:D4}",
            CompanyId = $"COMP{company:D3}",
            Name = $"Customer {customer} of Company {company}",
            Email = $"c{customer}@company{company}.com"
        });
    }
}

// The library automatically:
// 1. Groups by PartitionKey (CompanyId)
// 2. Splits each group into 100-item batches
// 3. Executes batches in parallel per partition
var result = await dataAccess.BatchUpdateListAsync(mixedCustomers);

Console.WriteLine($"Processed {mixedCustomers.Count} customers across 5 companies");
Console.WriteLine($"Total batches executed: ~{Math.Ceiling(250.0 / 100) * 5}");
```

#### Batch Delete Operations

```csharp
// Get entities to delete
var inactiveCustomers = await dataAccess.GetCollectionAsync(
    c => c.IsActive == false && 
         c.LastActivityDate < DateTime.Today.AddYears(-1)
);

// Batch delete
var deleteResult = await dataAccess.BatchUpdateListAsync(
    inactiveCustomers.ToList(),
    TableOperationType.Delete
);

Console.WriteLine($"Deleted {deleteResult.SuccessfulItems} inactive customers");
```

### Pagination

#### Basic Pagination

```csharp
// First page
var firstPage = await dataAccess.GetPagedCollectionAsync(
    pageSize: 50,
    continuationToken: null
);

Console.WriteLine($"Page 1: {firstPage.Data.Count} items");
Console.WriteLine($"Has more pages: {firstPage.HasMore}");

// Get next page
if (firstPage.HasMore)
{
    var secondPage = await dataAccess.GetPagedCollectionAsync(
        pageSize: 50,
        continuationToken: firstPage.ContinuationToken
    );
    
    Console.WriteLine($"Page 2: {secondPage.Data.Count} items");
}
```

#### Pagination with Filtering

```csharp
// Paginate filtered results
var pagedResults = await dataAccess.GetPagedCollectionAsync(
    predicate: c => c.Status == "Active" && c.Revenue > 100000,
    pageSize: 25
);

// Process all pages
var allResults = new List<Customer>();
var pageNumber = 1;
string continuationToken = null;

do
{
    var page = await dataAccess.GetPagedCollectionAsync(
        predicate: c => c.Status == "Active",
        pageSize: 100,
        continuationToken: continuationToken
    );
    
    allResults.AddRange(page.Data);
    continuationToken = page.ContinuationToken;
    
    Console.WriteLine($"Page {pageNumber++}: Retrieved {page.Data.Count} items");
    
} while (!string.IsNullOrEmpty(continuationToken));

Console.WriteLine($"Total items retrieved: {allResults.Count}");
```

#### Pagination by Partition

```csharp
// Paginate within a specific partition
var companyCustomers = await dataAccess.GetPagedCollectionAsync(
    partitionKeyID: "COMP001",
    pageSize: 50
);

// Process all customers for a company
string continuationToken = null;
int totalProcessed = 0;

do
{
    var page = await dataAccess.GetPagedCollectionAsync(
        partitionKeyID: "COMP001",
        pageSize: 100,
        continuationToken: continuationToken
    );
    
    foreach (var customer in page.Data)
    {
        await ProcessCustomerAsync(customer);
        totalProcessed++;
    }
    
    continuationToken = page.ContinuationToken;
    
} while (!string.IsNullOrEmpty(continuationToken));

Console.WriteLine($"Processed {totalProcessed} customers for COMP001");
```

#### Progressive Data Loading Pattern

```csharp
public class DataGridService
{
    private readonly DataAccess<Customer> _dataAccess;
    private readonly List<Customer> _loadedData = new();
    private string _continuationToken;
    private bool _isLoading;
    
    public async Task<List<Customer>> LoadInitialDataAsync()
    {
        var initialLoad = await _dataAccess.GetInitialDataLoadAsync(
            initialLoadSize: 50
        );
        
        _loadedData.AddRange(initialLoad.Data);
        _continuationToken = initialLoad.ContinuationToken;
        
        // Start background loading
        if (initialLoad.HasMore)
        {
            _ = LoadRemainingDataAsync();
        }
        
        return _loadedData;
    }
    
    private async Task LoadRemainingDataAsync()
    {
        _isLoading = true;
        
        while (!string.IsNullOrEmpty(_continuationToken))
        {
            var page = await _dataAccess.GetPagedCollectionAsync(
                pageSize: 200,
                continuationToken: _continuationToken
            );
            
            _loadedData.AddRange(page.Data);
            _continuationToken = page.ContinuationToken;
            
            // Notify UI of new data
            OnDataLoaded?.Invoke(_loadedData.Count);
            
            // Small delay to prevent UI freezing
            await Task.Delay(100);
        }
        
        _isLoading = false;
        OnLoadingComplete?.Invoke();
    }
    
    public event Action<int> OnDataLoaded;
    public event Action OnLoadingComplete;
}
```

#### Efficient Large Dataset Processing

```csharp
public class BatchProcessor
{
    private readonly DataAccess<WorkItem> _dataAccess;
    private readonly ILogger<BatchProcessor> _logger;
    
    public async Task ProcessLargeDatasetAsync(
        Expression<Func<WorkItem, bool>> filter,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        string continuationToken = null;
        int totalProcessed = 0;
        int batchNumber = 0;
        
        _logger.LogInformation("Starting large dataset processing");
        
        do
        {
            // Get next batch
            var page = await _dataAccess.GetPagedCollectionAsync(
                predicate: filter,
                pageSize: batchSize,
                continuationToken: continuationToken
            );
            
            if (!page.Data.Any())
                break;
            
            batchNumber++;
            _logger.LogInformation("Processing batch {Batch} with {Count} items", 
                batchNumber, page.Data.Count);
            
            // Process batch in parallel
            var tasks = page.Data.Select(item => ProcessItemAsync(item, cancellationToken));
            await Task.WhenAll(tasks);
            
            totalProcessed += page.Data.Count;
            continuationToken = page.ContinuationToken;
            
            // Check for cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Processing cancelled after {Count} items", totalProcessed);
                break;
            }
            
            // Update session with progress
            SessionManager.Current["LastProcessedBatch"] = batchNumber.ToString();
            SessionManager.Current["TotalProcessed"] = totalProcessed.ToString();
            
            // Log progress
            _logger.LogInformation("Processed {Total} items so far", totalProcessed);
            
        } while (!string.IsNullOrEmpty(continuationToken));
        
        _logger.LogInformation("Processing complete. Total items: {Total}", totalProcessed);
    }
    
    private async Task ProcessItemAsync(WorkItem item, CancellationToken cancellationToken)
    {
        // Process individual item
        await Task.Delay(10, cancellationToken);  // Simulate work
        
        item.ProcessedDate = DateTime.UtcNow;
        item.Status = "Processed";
    }
}
```

---

## Complete Working Examples

### Example 1: Multi-Tenant SaaS Application

```csharp
// Entities
public class Tenant : TableEntityBase, ITableExtra
{
    public string TenantId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string Region
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string CompanyName { get; set; }
    public string AdminEmail { get; set; }
    public string Plan { get; set; }
    public DateTime CreatedDate { get; set; }
    public bool IsActive { get; set; }
    
    public string TableReference => "Tenants";
    public string GetIDValue() => this.TenantId;
}

public class TenantUser : TableEntityBase, ITableExtra
{
    public string UserId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string TenantId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string Email { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }
    public DateTime LastLogin { get; set; }
    
    public string TableReference => "TenantUsers";
    public string GetIDValue() => this.UserId;
}

// Service Implementation
public class TenantService
{
    private readonly DataAccess<Tenant> _tenantAccess;
    private readonly DataAccess<TenantUser> _userAccess;
    private readonly ILogger<TenantService> _logger;
    
    public TenantService(string accountName, string accountKey)
    {
        _tenantAccess = new DataAccess<Tenant>(accountName, accountKey);
        _userAccess = new DataAccess<TenantUser>(accountName, accountKey);
        _logger = AzureTableLogging.CreateLogger<TenantService>();
    }
    
    public async Task<Tenant> CreateTenantAsync(string companyName, string adminEmail, string region)
    {
        var tenant = new Tenant
        {
            TenantId = Guid.NewGuid().ToString("N"),
            Region = region,
            CompanyName = companyName,
            AdminEmail = adminEmail,
            Plan = "Trial",
            CreatedDate = DateTime.UtcNow,
            IsActive = true
        };
        
        await _tenantAccess.ManageDataAsync(tenant);
        
        // Create admin user
        var adminUser = new TenantUser
        {
            UserId = Guid.NewGuid().ToString("N"),
            TenantId = tenant.TenantId,
            Email = adminEmail,
            Name = "Administrator",
            Role = "Admin",
            LastLogin = DateTime.UtcNow
        };
        
        await _userAccess.ManageDataAsync(adminUser);
        
        _logger.LogInformation("Created tenant {TenantId} for {Company}", 
            tenant.TenantId, companyName);
        
        return tenant;
    }
    
    public async Task<IEnumerable<TenantUser>> GetTenantUsersAsync(string tenantId)
    {
        return await _userAccess.GetCollectionAsync(tenantId);
    }
    
    public async Task<bool> AuthenticateUserAsync(string email, string password)
    {
        // Find user across all tenants
        var user = await _userAccess.GetRowObjectAsync(
            "Email", ComparisonTypes.eq, email
        );
        
        if (user != null)
        {
            user.LastLogin = DateTime.UtcNow;
            await _userAccess.ManageDataAsync(user, TableOperationType.InsertOrMerge);
            
            // Store in session
            SessionManager.Current["UserId"] = user.UserId;
            SessionManager.Current["TenantId"] = user.TenantId;
            SessionManager.Current["UserRole"] = user.Role;
            
            return true;
        }
        
        return false;
    }
}
```

### Example 2: IoT Device Monitoring System

```csharp
// Entities
public class Device : TableEntityBase, ITableExtra
{
    public string DeviceId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string LocationId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public string DeviceType { get; set; }
    public string Status { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public string FirmwareVersion { get; set; }
    public string ConfigJson { get; set; }
    
    public string TableReference => "Devices";
    public string GetIDValue() => this.DeviceId;
}

public class DeviceTelemetry : TableEntityBase, ITableExtra
{
    public string TelemetryId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string DeviceId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public DateTime Timestamp { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public double Pressure { get; set; }
    public string MetricsJson { get; set; }
    
    public string TableReference => "DeviceTelemetry";
    public string GetIDValue() => this.TelemetryId;
}

// Monitoring Service
public class DeviceMonitoringService : BackgroundService
{
    private readonly DataAccess<Device> _deviceAccess;
    private readonly DataAccess<DeviceTelemetry> _telemetryAccess;
    private readonly ILogger<DeviceMonitoringService> _logger;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await MonitorDevicesAsync();
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
    
    private async Task MonitorDevicesAsync()
    {
        // Get all devices that haven't sent heartbeat recently
        var threshold = DateTime.UtcNow.AddMinutes(-5);
        var allDevices = await _deviceAccess.GetAllTableDataAsync();
        
        var offlineDevices = allDevices.Where(d => 
            d.Status == "Online" && d.LastHeartbeat < threshold
        ).ToList();
        
        foreach (var device in offlineDevices)
        {
            device.Status = "Offline";
            await _deviceAccess.ManageDataAsync(device, TableOperationType.InsertOrMerge);
            
            _logger.LogWarning("Device {DeviceId} is offline", device.DeviceId);
            
            await SendAlertAsync(device);
        }
    }
    
    public async Task RecordTelemetryAsync(string deviceId, Dictionary<string, object> metrics)
    {
        var telemetry = new DeviceTelemetry
        {
            TelemetryId = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}",
            DeviceId = deviceId,
            Timestamp = DateTime.UtcNow,
            Temperature = Convert.ToDouble(metrics.GetValueOrDefault("temperature", 0)),
            Humidity = Convert.ToDouble(metrics.GetValueOrDefault("humidity", 0)),
            Pressure = Convert.ToDouble(metrics.GetValueOrDefault("pressure", 0)),
            MetricsJson = JsonConvert.SerializeObject(metrics)
        };
        
        await _telemetryAccess.ManageDataAsync(telemetry);
        
        // Update device heartbeat
        var device = await _deviceAccess.GetRowObjectAsync(deviceId);
        if (device != null)
        {
            device.LastHeartbeat = DateTime.UtcNow;
            device.Status = "Online";
            await _deviceAccess.ManageDataAsync(device, TableOperationType.InsertOrMerge);
        }
    }
    
    public async Task<IEnumerable<DeviceTelemetry>> GetDeviceTelemetryAsync(
        string deviceId, 
        DateTime startDate, 
        DateTime endDate)
    {
        var telemetry = await _telemetryAccess.GetCollectionAsync(deviceId);
        
        return telemetry.Where(t => 
            t.Timestamp >= startDate && t.Timestamp <= endDate
        ).OrderBy(t => t.Timestamp);
    }
    
    private async Task SendAlertAsync(Device device)
    {
        // Send email/SMS alert
        _logger.LogInformation("Alert sent for device {DeviceId}", device.DeviceId);
        await Task.CompletedTask;
    }
}
```

### Example 3: E-Commerce Order Processing

```csharp
// Entities
public class Order : TableEntityBase, ITableExtra
{
    public string OrderId
    {
        get => this.RowKey;
        set => this.RowKey = value;
    }
    
    public string CustomerId
    {
        get => this.PartitionKey;
        set => this.PartitionKey = value;
    }
    
    public DateTime OrderDate { get; set; }
    public string Status { get; set; }
    public decimal Total { get; set; }
    public string ItemsJson { get; set; }
    public string ShippingAddress { get; set; }
    public string PaymentMethod { get; set; }
    
    private List<OrderItem> _items;
    public List<OrderItem> Items
    {
        get
        {
            if (_items == null && !string.IsNullOrEmpty(ItemsJson))
                _items = JsonConvert.DeserializeObject<List<OrderItem>>(ItemsJson);
            return _items ?? (_items = new List<OrderItem>());
        }
        set
        {
            _items = value;
            ItemsJson = JsonConvert.SerializeObject(value);
        }
    }
    
    public string TableReference => "Orders";
    public string GetIDValue() => this.OrderId;
}

public class OrderItem
{
    public string ProductId { get; set; }
    public string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total => Quantity * UnitPrice;
}

// Order Service
public class OrderService
{
    private readonly DataAccess<Order> _orderAccess;
    private readonly DataAccess<Product> _productAccess;
    private readonly ILogger<OrderService> _logger;
    
    public async Task<Order> CreateOrderAsync(
        string customerId, 
        List<OrderItem> items, 
        string shippingAddress)
    {
        // Validate inventory
        foreach (var item in items)
        {
            var product = await _productAccess.GetRowObjectAsync(item.ProductId);
            if (product == null || product.StockQuantity < item.Quantity)
            {
                throw new InvalidOperationException(
                    $"Insufficient stock for product {item.ProductId}");
            }
        }
        
        // Create order
        var order = new Order
        {
            OrderId = $"ORD{DateTime.UtcNow:yyyyMMdd}{Guid.NewGuid():N}".Substring(0, 20),
            CustomerId = customerId,
            OrderDate = DateTime.UtcNow,
            Status = "Pending",
            Items = items,
            Total = items.Sum(i => i.Total),
            ShippingAddress = shippingAddress,
            PaymentMethod = "CreditCard"
        };
        
        await _orderAccess.ManageDataAsync(order);
        
        // Update inventory
        foreach (var item in items)
        {
            var product = await _productAccess.GetRowObjectAsync(item.ProductId);
            product.StockQuantity -= item.Quantity;
            await _productAccess.ManageDataAsync(product, TableOperationType.InsertOrMerge);
        }
        
        // Store in session
        SessionManager.Current["LastOrderId"] = order.OrderId;
        SessionManager.Current["LastOrderTotal"] = order.Total.ToString();
        
        _logger.LogInformation("Order {OrderId} created for customer {CustomerId}", 
            order.OrderId, customerId);
        
        return order;
    }
    
    public async Task<IEnumerable<Order>> GetCustomerOrdersAsync(string customerId)
    {
        var orders = await _orderAccess.GetCollectionAsync(customerId);
        return orders.OrderByDescending(o => o.OrderDate);
    }
    
    public async Task ProcessPendingOrdersAsync()
    {
        var pendingOrders = await _orderAccess.GetCollectionAsync(
            o => o.Status == "Pending"
        );
        
        var tasks = pendingOrders.Select(ProcessOrderAsync);
        await Task.WhenAll(tasks);
    }
    
    private async Task ProcessOrderAsync(Order order)
    {
        try
        {
            // Process payment
            await ProcessPaymentAsync(order);
            
            order.Status = "Processing";
            await _orderAccess.ManageDataAsync(order, TableOperationType.InsertOrMerge);
            
            // Generate shipping label
            await GenerateShippingLabelAsync(order);
            
            order.Status = "Shipped";
            await _orderAccess.ManageDataAsync(order, TableOperationType.InsertOrMerge);
            
            _logger.LogInformation("Order {OrderId} processed successfully", order.OrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process order {OrderId}", order.OrderId);
            
            order.Status = "Failed";
            await _orderAccess.ManageDataAsync(order, TableOperationType.InsertOrMerge);
        }
    }
    
    private async Task ProcessPaymentAsync(Order order)
    {
        // Payment processing logic
        await Task.Delay(1000); // Simulate API call
    }
    
    private async Task GenerateShippingLabelAsync(Order order)
    {
        // Shipping label generation
        await Task.Delay(500); // Simulate API call
    }
}
```

---

## Troubleshooting Guide

### Common Issues and Solutions

#### Issue: "Request body too large" error
**Cause**: Individual entity exceeds 1MB or batch exceeds 4MB  
**Solution**: 
- Ensure entities inherit from `TableEntityBase` for automatic chunking
- Split large batches into smaller groups
- Store large data in Blob Storage and reference in table

#### Issue: Lambda queries returning unexpected results
**Cause**: Mix of server and client operations not working as expected  
**Solution**:
- Review which operations run server vs client side
- Add pre-computed fields for complex queries
- Enable logging to see the generated OData filter

#### Issue: Session data not persisting
**Cause**: Session not flushing or batch write delay not expired  
**Solution**:
```csharp
// Force immediate flush
await SessionManager.Current.FlushAsync();

// Or wait for auto-commit
await Task.Delay(TimeSpan.FromSeconds(1));
```

#### Issue: Slow query performance
**Cause**: Too much client-side filtering  
**Solution**:
- Add computed fields to enable server-side filtering
- Use appropriate partition keys for common queries
- Consider using multiple queries instead of complex client-side operations

#### Issue: "The specified entity already exists" error
**Cause**: Using `Insert` operation on existing entity  
**Solution**:
- Use `InsertOrReplace` for upsert behavior
- Use `InsertOrMerge` for partial updates
- Check existence first if needed

#### Issue: Memory issues with large datasets
**Cause**: Loading entire table into memory  
**Solution**:
```csharp
// Use pagination
string continuationToken = null;
do
{
    var page = await dataAccess.GetPagedCollectionAsync(100, continuationToken);
    ProcessPage(page.Data);
    continuationToken = page.ContinuationToken;
} while (!string.IsNullOrEmpty(continuationToken));
```

---

## Summary

ASCDataAccessLibrary v3.1 provides a comprehensive, production-ready solution for Azure Storage integration in .NET applications. The library emphasizes:

- **Developer Productivity**: Intuitive APIs with lambda expressions and automatic optimizations
- **Performance**: Hybrid filtering, batching, and connection pooling
- **Reliability**: Circuit breakers, retry logic, and comprehensive error handling
- **Flexibility**: Strongly-typed and dynamic entities, multiple application types support
- **Observability**: Universal logging with ILogger, session tracking, and diagnostics

The library handles the complexities of Azure Storage while providing a simple, intuitive interface that works across all .NET application types.

For support and updates, visit the [GitHub repository](https://github.com/answersalescalls/ASCDataAccessLibrary).

---

*Version 3.1.0 - January 2025*  
*© 2025 Answer Sales Calls Inc. - All Rights Reserved*  
*Licensed under MIT License*