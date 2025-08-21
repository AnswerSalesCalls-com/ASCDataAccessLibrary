using ASCTableStorage.Data;
using ASCTableStorage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;


namespace ASCTableStorage.Logging
{
    
    #region Core Configuration

    /// <summary>
    /// Configuration options for Azure Table logging with account-based authentication
    /// </summary>
    public class AzureTableLoggerOptions
    {
        /// <summary>
        /// If true, keeps the Debug logger in addition to Azure Table logging
        /// </summary>
        public bool KeepDebug { get; set; }
        /// <summary>
        /// If true, keeps the console logger in addition to Azure Table logging
        /// </summary>
        public bool KeepConsole { get; set; }
        /// <summary>
        /// If true, clears existing providers before adding this logger
        /// </summary>
        public bool ClearProviders { get; set; }
        /// <summary>
        /// Azure Storage account name (required)
        /// </summary>
        public string? AccountName { get; set; }

        /// <summary>
        /// Azure Storage account key (required)
        /// </summary>
        public string? AccountKey { get; set; }

        /// <summary>
        /// Table name for storing logs (default: ApplicationLogs)
        /// </summary>
        public string TableName { get; set; } = Constants.DefaultLogTableName;

        /// <summary>
        /// Minimum log level to record (default: Information in Production, Trace in Development)
        /// </summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// Maximum batch size for Azure Table operations (max 100)
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Interval for flushing logs to Azure (default: 2 seconds)
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Include scopes in log messages
        /// </summary>
        public bool IncludeScopes { get; set; } = true;

        /// <summary>
        /// Auto-discover credentials from various sources
        /// </summary>
        public bool AutoDiscoverCredentials { get; set; } = true;

        /// <summary>
        /// Maximum queue size before blocking (0 = unlimited)
        /// </summary>
        public int MaxQueueSize { get; set; } = 10000;

        /// <summary>
        /// Enable fallback logging when Azure is unavailable
        /// </summary>
        public bool EnableFallback { get; set; } = true;

        /// <summary>
        /// Application name for grouping logs
        /// </summary>
        public string? ApplicationName { get; set; }

        /// <summary>
        /// Environment name (Development, Production, etc.)
        /// </summary>
        public string? Environment { get; set; }

        /// <summary>
        /// Validates that required options are set
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(AccountName) && !string.IsNullOrEmpty(AccountKey);
        }

        #region Additional Properties for Cleanup

        /// <summary>
        /// Number of days to retain logs before automatic cleanup (default: 60 days)
        /// </summary>
        public int RetentionDays { get; set; } = 60;

        /// <summary>
        /// Enable automatic cleanup of old logs (default: true)
        /// </summary>
        public bool EnableAutoCleanup { get; set; } = true;

        /// <summary>
        /// Time interval between cleanup runs (default: 1 day)
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromDays(1);

        /// <summary>
        /// Specific time of day to run cleanup (optional, null = use interval from startup)
        /// Format: "HH:mm:ss" e.g., "02:00:00" for 2 AM
        /// </summary>
        public TimeSpan? CleanupTimeOfDay { get; set; }

        /// <summary>
        /// Clean up logs by severity level with different retention periods
        /// Key = LogLevel, Value = Retention days for that level
        /// </summary>
        public Dictionary<LogLevel, int>? RetentionByLevel { get; set; }

        #endregion Additional Properties for Cleanup
    } // end class AzureTableLoggerOptions

    #endregion Core Configuration

    #region Static Initialization Helper

    /// <summary>
    /// Static helper for non-DI scenarios (Console, Desktop applications)
    /// </summary>
    public static class AzureTableLogging
    {
        private static AzureTableLoggerProvider? _provider;
        private static readonly object _lock = new object();
        private static string? _accountName;
        private static string? _accountKey;
        private static ILoggerFactory? _factory;

        /// <summary>
        /// Initializes the Azure Table logger with explicit credentials and an ambient <see cref="ILoggerFactory"/>.
        /// </summary>
        /// <param name="factory">The <see cref="ILoggerFactory"/> used to create scoped loggers.</param>
        /// <param name="accountName">Azure Storage account name.</param>
        /// <param name="accountKey">Azure Storage account key.</param>
        /// <param name="configure">Optional delegate to configure <see cref="AzureTableLoggerOptions"/>.</param>
        public static void Initialize(ILoggerFactory factory, string accountName, string accountKey, Action<AzureTableLoggerOptions>? configure = null)
        {
            var options = new AzureTableLoggerOptions
            {
                AccountName = accountName,
                AccountKey = accountKey
            };
            configure?.Invoke(options);
            InitializeInternal(factory, options);
        }

        /// <summary>
        /// Initializes the Azure Table logger using auto-discovered credentials and an ambient <see cref="ILoggerFactory"/>.
        /// </summary>
        /// <param name="factory">The <see cref="ILoggerFactory"/> used to create scoped loggers.</param>
        /// <param name="configure">Optional delegate to configure <see cref="AzureTableLoggerOptions"/>.</param>
        /// <exception cref="InvalidOperationException">Thrown if credentials could not be discovered or validated.</exception>
        public static void Initialize(ILoggerFactory factory, Action<AzureTableLoggerOptions>? configure = null)
        {
            var options = new AzureTableLoggerOptions { AutoDiscoverCredentials = true };
            configure?.Invoke(options);

            DiscoverCredentials(options);

            if (!options.IsValid())
            {
                throw new InvalidOperationException("Could not discover Azure credentials. Please provide AccountName and AccountKey explicitly or through configuration.");
            }

            InitializeInternal(factory, options);
        }

        /// <summary>
        /// Internal helper to safely initialize the logger provider and factory.
        /// Disposes any existing provider before reinitializing.
        /// </summary>
        /// <param name="factory">Ambient logger factory for scoped logger creation.</param>
        /// <param name="options">Configured options for Azure Table logging.</param>
        private static void InitializeInternal(ILoggerFactory factory, AzureTableLoggerOptions options)
        {
            lock (_lock)
            {
                _provider?.Dispose();
                _provider = new AzureTableLoggerProvider(Options.Create(options));
                _factory = factory;
            }
        }

        /// <summary>
        /// Retrieves a scoped <see cref="ILogger"/> for the specified category.
        /// </summary>
        /// <param name="category">The logging category name (e.g., "Security.Suspicion").</param>
        /// <returns>An <see cref="ILogger"/> instance scoped to the given category.</returns>
        /// <exception cref="InvalidOperationException">Thrown if <see cref="Initialize"/> has not been called.</exception>
        public static ILogger GetLogger(string category)
        {
            if (_factory == null)
            {
                throw new InvalidOperationException("AzureTableLogger not initialized. Call Initialize(...) during app startup.");
            }

            return _factory.CreateLogger(category);
        }

        /// <summary>
        /// Create a logger for a specific type without DI
        /// </summary>
        public static ILogger<T> CreateLogger<T>()
        {
            if (_provider == null)
            {
                throw new InvalidOperationException(
                    "AzureTableLogging has not been initialized. Call Initialize() first.");
            }

            return new TypedLogger<T>(_provider.CreateLogger(typeof(T).FullName ?? typeof(T).Name));
        }

        /// <summary>
        /// Create a logger with a category name
        /// </summary>
        public static ILogger CreateLogger(string categoryName)
        {
            if (_provider == null)
            {
                throw new InvalidOperationException(
                    "AzureTableLogging has not been initialized. Call Initialize() first.");
            }

            return _provider.CreateLogger(categoryName);
        }

        /// <summary>
        /// Flush all pending logs and shutdown
        /// </summary>
        public static async Task ShutdownAsync()
        {
            if (_provider != null)
            {
                await _provider.FlushAsync();
                _provider.Dispose();
                _provider = null;
            }
        }


        /// <summary>
        /// Registers the Azure Table logger with ASP.NET Core DI
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="accountName">The Azure Table Storage Account Name</param>
        /// <param name="accountKey">The Azure Table Storage Account Key</param>
        public static IServiceCollection AddErrorLoggingBridge(this IServiceCollection services, string? accountName = null, string? accountKey = null)
        {
            // Try to get credentials from multiple sources
            var accName = accountName ?? _accountName;
            var accKey = accountKey ?? _accountKey;

            if (string.IsNullOrEmpty(accName) || string.IsNullOrEmpty(accKey))
            {
                // Try to get from registered AzureTableLoggerOptions
                services.AddScoped(typeof(IErrorLoggingService), provider =>
                {
                    var options = provider.GetService<IOptions<AzureTableLoggerOptions>>();
                    if (options?.Value == null || !options.Value.IsValid())
                    {
                        throw new InvalidOperationException(
                            "Azure credentials not found. Either pass them to AddErrorLoggingBridge or configure AzureTableLogging first.");
                    }

                    return new ErrorLoggingServiceBridge(
                        provider.GetRequiredService<ILogger<ErrorLoggingServiceBridge>>(),
                        options.Value.AccountName!,
                        options.Value.AccountKey!
                    );
                });
            }
            else
            {
                // Use provided credentials
                services.AddScoped(typeof(IErrorLoggingService), provider =>
                    new ErrorLoggingServiceBridge(
                        provider.GetRequiredService<ILogger<ErrorLoggingServiceBridge>>(),
                        accName,
                        accKey
                    )
                );
            }

            return services;
        }

        /// <summary>
        /// Auto-discover credentials from various sources
        /// </summary>
        private static void DiscoverCredentials(AzureTableLoggerOptions options)
        {
            // 1. Check environment variables
            if (string.IsNullOrEmpty(options.AccountName))
            {
                options.AccountName = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT");
            }
            if (string.IsNullOrEmpty(options.AccountKey))
            {
                options.AccountKey = Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY");
            }

            // 2. Check app.config/web.config
            if (string.IsNullOrEmpty(options.AccountName) || string.IsNullOrEmpty(options.AccountKey))
            {
                TryLoadFromAppConfig(options);
            }

            // 3. Check appsettings.json (if exists)
            if (string.IsNullOrEmpty(options.AccountName) || string.IsNullOrEmpty(options.AccountKey))
            {
                TryLoadFromAppSettings(options);
            }
        }

        private static void TryLoadFromAppConfig(AzureTableLoggerOptions options)
        {
            try
            {
                var config = System.Configuration.ConfigurationManager.AppSettings;
                if (config != null)
                {
                    options.AccountName ??= config["Azure:TableStorageName"] ?? config["AzureTableStorageKey"];
                    options.AccountKey ??= config["Azure:TableStorageKey"] ?? config["AzureTableStorageKey"];
                }
            }
            catch
            {
                // ConfigurationManager not available
            }
        }

        private static void TryLoadFromAppSettings(AzureTableLoggerOptions options)
        {
            try
            {
                var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true)
                    .AddJsonFile($"appsettings.{options.Environment ?? "Production"}.json", optional: true);

                var configuration = builder.Build();
                options.AccountName ??= configuration["Azure:TableStorageName"];
                options.AccountKey ??= configuration["Azure:TableStorageKey"];
            }
            catch
            {
                // Configuration files not available
            }
        }
    } // end class AzureTableLogging

    #endregion Static Initialization Helper

    #region Logger Provider

    /// <summary>
    /// Azure Table logger provider that manages logger instances and background processing
    /// </summary>
    public class AzureTableLoggerProvider : ILoggerProvider, IDisposable, ISupportExternalScope
    {
        private readonly AzureTableLoggerOptions _options;
        private readonly ConcurrentDictionary<string, AzureTableLogger> _loggers = new();
        private readonly LoggingBackgroundService _backgroundService;
        private IExternalScopeProvider? _scopeProvider;
        private static ILoggerFactory? _factory;
        private bool _disposed;

        /// <summary>
        /// Constructor that initializes the logger provider with options
        /// </summary>
        /// <param name="options">Table options to include</param>
        public AzureTableLoggerProvider(IOptions<AzureTableLoggerOptions> options)
        {
            _options = options.Value;

            if (_options.AutoDiscoverCredentials && !_options.IsValid())
            {
                DiscoverCredentials();
            }

            if (!_options.IsValid())
            {
                throw new ArgumentException("Azure Table Logger requires AccountName and AccountKey");
            }

            _backgroundService = new LoggingBackgroundService(_options);
            _ = _backgroundService.StartAsync(CancellationToken.None);
        }
        /// <summary>
        /// Sets up or retrieves a logger for the specified category
        /// </summary>
        /// <param name="categoryName">Desired category name</param>
        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new AzureTableLogger(name, _options, _backgroundService, _scopeProvider));
        }

        /// <summary>
        /// Initializer to allow for category logger setups outside of immediate classes, like referenced APIs you control.
        /// </summary>
        /// <param name="factory">The factory to append</param>
        public static void Initialize(ILoggerFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// Retrieves the Logger instance that writes to this custom logger
        /// </summary>
        /// <param name="category">The category or class the logs will belong to.</param>
        public static ILogger GetLogger(string category)
        {
            if (_factory == null)
                throw new InvalidOperationException("LoggerFactory not initialized. Call Initialize() first.");

            return _factory.CreateLogger(category);
        }

        /// <summary>
        /// Retrieves a strongly typed <see cref="ILogger{T}"/> for the specified type.
        /// Requires that <see cref="Initialize(ILoggerFactory)"/> has been called.
        /// </summary>
        /// <typeparam name="T">The type whose name will be used as the logging category.</typeparam>
        /// <returns>An <see cref="ILogger{T}"/> instance scoped to type <typeparamref name="T"/>.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the logger factory has not been initialized.
        /// </exception>
        public static ILogger<T> GetLogger<T>()
        {
            if (_factory == null)
                throw new InvalidOperationException("LoggerFactory not initialized. Call Initialize() first.");

            return _factory.CreateLogger<T>();
        }


        /// <summary>
        /// Properly disposes of resources used by the logger provider
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _backgroundService?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _backgroundService?.Dispose();
        }

        /// <summary>
        /// Properly flushes all pending logs to Azure Table Storage
        /// </summary>
        public async Task FlushAsync()
        {
            await _backgroundService.FlushAsync();
        }
        /// <summary>
        /// Sets the scope provider for including scopes in log messages
        /// </summary>
        /// <param name="scopeProvider">The scope provider as required by the app setup</param>
        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
            foreach (var logger in _loggers.Values)
            {
                logger.ScopeProvider = scopeProvider;
            }
        }
        /// <summary>
        /// Helps respect ScopeProvider during log emissions
        /// </summary>
        /// <typeparam name="TState">Manages the type that manages state</typeparam>
        /// <param name="state">The object managing state</param>
        public IDisposable BeginScope<TState>(TState state) =>
            _scopeProvider?.Push(state) ?? NullScope.Instance;

        /// <summary>
        /// Discovers Azure Storage credentials and reinitializes the Azure Table logger infrastructure.
        /// Uses the static logger factory previously set via <see cref="Initialize(ILoggerFactory)"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the logger factory has not been initialized.
        /// </exception>
        private void DiscoverCredentials()
        {
            if (_factory == null)
            {
                throw new InvalidOperationException("LoggerFactory not initialized. Call AzureTableLoggerProvider.Initialize(...) before using credential discovery.");
            }

            AzureTableLogging.Initialize(_factory, opt =>
            {
                opt.AccountName = _options.AccountName;
                opt.AccountKey = _options.AccountKey;
                opt.AutoDiscoverCredentials = true;
            });
        }

    } // end class AzureTableLoggerProvider

    #endregion Logger Provider

    #region Logger Implementation

    /// <summary>
    /// Azure Table logger implementation
    /// </summary>
    public class AzureTableLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly AzureTableLoggerOptions _options;
        private readonly LoggingBackgroundService _backgroundService;

        internal IExternalScopeProvider? ScopeProvider { get; set; }

        /// <summary>
        /// Constructor that initializes the logger with category and options
        /// </summary>
        /// <param name="categoryName">The name of the category for this logger</param>
        /// <param name="options">The options for configuring the logger</param>
        /// <param name="backgroundService">The background service for processing logs</param>
        /// <param name="scopeProvider">The scope provider for managing log scopes</param>
        public AzureTableLogger(string categoryName, AzureTableLoggerOptions options,
            LoggingBackgroundService backgroundService, IExternalScopeProvider? scopeProvider)
        {
            _categoryName = categoryName;
            _options = options;
            _backgroundService = backgroundService;
            ScopeProvider = scopeProvider;
        }
        /// <summary>
        /// True if the specified log level is enabled
        /// </summary>
        /// <param name="logLevel">The desired log level</param>
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= _options.MinimumLevel;
        }
        /// <summary>
        /// Logs a message with the specified level, event ID, state, exception, and formatter
        /// </summary>
        /// <typeparam name="TState">The type of the state object</typeparam>
        /// <param name="logLevel">The desired log level</param>
        /// <param name="eventId">The event ID</param>
        /// <param name="state">The state object</param>
        /// <param name="exception">The exception object</param>
        /// <param name="formatter">The formatter function</param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null) return;

            var logEntry = new ApplicationLogEntry
            {
                ApplicationName = _options.ApplicationName ?? GetApplicationName(),
                Environment = _options.Environment ?? GetEnvironment(),
                LogLevel = logLevel.ToString(),
                Category = _categoryName,
                EventId = eventId.Id,
                EventName = eventId.Name,
                Message = message,
                Exception = exception?.ToString(),
                Timestamp = DateTime.UtcNow,
                CorrelationId = Activity.Current?.Id,
                MachineName = Environment.MachineName,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            // Extract structured data
            if (state is IEnumerable<KeyValuePair<string, object>> values)
            {
                logEntry.Properties = values.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString());
            }

            // Include scopes
            if (_options.IncludeScopes && ScopeProvider != null)
            {
                var scopes = new List<string>();
                ScopeProvider.ForEachScope((scope, state) =>
                {
                    scopes.Add(scope?.ToString() ?? "");
                }, logEntry);

                if (scopes.Any())
                {
                    logEntry.Scopes = string.Join(" => ", scopes);
                }
            }

            // Enhanced caller info for errors
            if (logLevel >= LogLevel.Error)
            {
                var callerInfo = CallerContext.GetCallerInfo();
                logEntry.CallerMemberName = callerInfo.MemberName;
                logEntry.CallerFilePath = callerInfo.FilePath;
                logEntry.CallerLineNumber = callerInfo.LineNumber;
            }

            // Queue for background processing
            if (!_backgroundService.TryEnqueue(logEntry))
            {
                // Queue is full, use fallback if enabled
                if (_options.EnableFallback)
                {
                    FallbackLogger.Log(logEntry);
                }
            }
        }
        /// <summary>
        /// The scope provider for managing log scopes
        /// </summary>
        /// <typeparam name="TState">The type of the state object</typeparam>
        /// <param name="state">The state object</param>
        public IDisposable BeginScope<TState>(TState state)
        {
            return ScopeProvider?.Push(state) ?? NullScope.Instance;
        }

        private string GetApplicationName() 
            => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown";        

        private string GetEnvironment()
            => Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                   Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                   "Production";        
    }

    #endregion Logger Implementation

    #region Background Service

    /// <summary>
    /// Background service that batches and writes logs to Azure Table Storage
    /// </summary>
    public class LoggingBackgroundService : IHostedService, IDisposable
    {
        private readonly AzureTableLoggerOptions _options;
        private readonly Channel<ApplicationLogEntry> _queue;
        private readonly Timer _flushTimer;
        private readonly List<ApplicationLogEntry> _buffer = new();
        private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
        private readonly DataAccess<ApplicationLogEntry> _dataAccess;
        private readonly CircuitBreaker _circuitBreaker;
        private Task? _processTask;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// The current logging statistics
        /// </summary>
        public LoggingStatistics Statistics { get; } = new();

        /// <summary>
        /// Constructor that initializes the logging background service
        /// </summary>
        /// <param name="options">The Azure Table Logger options</param>
        public LoggingBackgroundService(AzureTableLoggerOptions options)
        {
            _options = options;

            _queue = _options.MaxQueueSize > 0
                ? Channel.CreateBounded<ApplicationLogEntry>(new BoundedChannelOptions(_options.MaxQueueSize)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                })
                : Channel.CreateUnbounded<ApplicationLogEntry>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

            _dataAccess = new DataAccess<ApplicationLogEntry>(
                options.AccountName!,
                options.AccountKey!
            );

            _circuitBreaker = new CircuitBreaker();

            _flushTimer = new Timer(
                async _ => await FlushAsync(),
                null,
                _options.FlushInterval,
                _options.FlushInterval
            );
        }

        /// <summary>
        /// Starts the background service to begin processing logs
        /// </summary>
        /// <param name="cancellationToken">Allows graceful closure of current processing</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _processTask = ProcessLogsAsync(_cancellationTokenSource.Token);

            // Initialize cleanup timer
            InitializeCleanupTimer();  // ADD THIS LINE
            return Task.CompletedTask;
        }

        /// <summary>
        /// Processes logs in the background, flushing them to Azure Table Storage
        /// </summary>
        /// <param name="cancellationToken">Allows graceful closure of current processing</param>
        /// <returns></returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _flushTimer?.Change(Timeout.Infinite, 0);
            _queue.Writer.TryComplete();

            // Give time to flush
            try
            {
                await FlushAsync();
                await (_processTask ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch
            {
                // Best effort
            }

            _cancellationTokenSource?.Cancel();
        }
        /// <summary>
        /// Attempts to enqueue a log entry for processing
        /// </summary>
        /// <param name="entry">The log entry to enqueue</param>
        public bool TryEnqueue(ApplicationLogEntry entry)
        {
            if (_queue.Writer.TryWrite(entry))
            {
                Interlocked.Increment(ref Statistics.TotalLogsQueued);
                return true;
            }
            return false;
        }
        /// <summary>
        /// attempts to flush the current buffer to Azure Table Storage
        /// </summary>
        public async Task FlushAsync()
        {
            await _flushSemaphore.WaitAsync();
            try
            {
                if (_buffer.Count > 0)
                {
                    await WriteBatchAsync(_buffer.ToList());
                    _buffer.Clear();
                }
                Statistics.LastFlush = DateTime.UtcNow;
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }
        /// <summary>
        /// Commits logs from the queue to the buffer and processes them asynchronously
        /// </summary>
        /// <param name="cancellationToken">Allows graceful closure of current processing</param>
        private async Task ProcessLogsAsync(CancellationToken cancellationToken)
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                _buffer.Add(entry);

                if (_buffer.Count >= _options.BatchSize)
                {
                    await FlushAsync();
                }
            }
        }
        /// <summary>
        /// Attempts to write a batch of logs to Azure Table Storage
        /// </summary>
        /// <param name="logs">The data to write</param>
        private async Task WriteBatchAsync(List<ApplicationLogEntry> logs)
        {
            if (!logs.Any()) return;

            // Group by partition key (hour-based for query efficiency)
            var groups = logs.GroupBy(l => GetPartitionKey(l.Timestamp.DateTime));

            foreach (var group in groups)
            {
                var batch = group.ToList();

                if (_circuitBreaker.IsOpen)
                {
                    // Circuit is open, use fallback
                    if (_options.EnableFallback)
                    {
                        batch.ForEach(FallbackLogger.Log);
                    }
                    continue;
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = await _dataAccess.BatchUpdateListAsync(batch, TableOperationType.InsertOrReplace);
                    sw.Stop();

                    if (result.Success)
                    {
                        _circuitBreaker.RecordSuccess();
                        Interlocked.Add(ref Statistics.TotalLogsWritten, batch.Count);
                        Statistics.AverageWriteLatency = sw.Elapsed;
                    }
                    else
                    {
                        _circuitBreaker.RecordFailure();
                        Interlocked.Add(ref Statistics.TotalLogsFailed, result.FailedItems);

                        if (_options.EnableFallback)
                        {
                            batch.ForEach(FallbackLogger.Log);
                        }
                    }
                }
                catch (Exception)
                {
                    _circuitBreaker.RecordFailure();
                    Interlocked.Add(ref Statistics.TotalLogsFailed, batch.Count);

                    if (_options.EnableFallback)
                    {
                        batch.ForEach(FallbackLogger.Log);
                    }
                }
            }
        } // end WriteBatchAsync

        /// <summary>
        /// Retrives the partition key based on the timestamp
        /// </summary>
        /// <param name="timestamp">The timestamp entry</param>
        private string GetPartitionKey(DateTime timestamp)
        {
            // Hour-based partitions for balance between query performance and distribution
            return $"{_options.ApplicationName ?? "App"}_{timestamp:yyyyMMddHH}";
        }
        /// <summary>
        /// Properly disposes of resources used by the logger provider
        /// </summary>
        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _flushTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
            _flushSemaphore?.Dispose();
        }

        #region Cleanup Timer and Methods

        private Timer? _cleanupTimer;
        private DateTime _lastCleanupRun = DateTime.MinValue;

        private void InitializeCleanupTimer()
        {
            if (!_options.EnableAutoCleanup)
                return;

            if (_options.CleanupTimeOfDay.HasValue)
            {
                // Schedule for specific time of day
                var now = DateTime.Now;
                var scheduledTime = now.Date.Add(_options.CleanupTimeOfDay.Value);

                if (scheduledTime <= now)
                    scheduledTime = scheduledTime.AddDays(1);

                var initialDelay = scheduledTime - now;

                _cleanupTimer = new Timer(
                    async _ => await RunScheduledCleanupAsync(),
                    null,
                    initialDelay,
                    TimeSpan.FromDays(1)
                );
            }
            else
            {
                // Run at regular intervals
                _cleanupTimer = new Timer(
                    async _ => await RunScheduledCleanupAsync(),
                    null,
                    _options.CleanupInterval,
                    _options.CleanupInterval
                );
            }
        }

        /// <summary>
        /// Runs the scheduled cleanup of old logs
        /// </summary>
        private async Task RunScheduledCleanupAsync()
        {
            try
            {
                // Prevent multiple simultaneous cleanups
                if (DateTime.UtcNow - _lastCleanupRun < TimeSpan.FromHours(1))
                    return;

                _lastCleanupRun = DateTime.UtcNow;

                if (_options.RetentionByLevel != null && _options.RetentionByLevel.Any())
                {
                    // Clean by log level with different retention periods
                    await CleanupByLevelAsync();
                }
                else
                {
                    // Simple cleanup based on RetentionDays
                    await CleanupOldLogsAsync(_options.RetentionDays);
                }

                // Log the cleanup operation (meta!)
                var cleanupLog = new ApplicationLogEntry
                {
                    ApplicationName = _options.ApplicationName ?? "LogCleanup",
                    Environment = _options.Environment,
                    LogLevel = LogLevel.Information.ToString(),
                    Category = "System.Cleanup",
                    Message = $"Completed scheduled log cleanup. Retention: {_options.RetentionDays} days",
                    Timestamp = DateTime.UtcNow,
                    MachineName = Environment.MachineName
                };

                TryEnqueue(cleanupLog);
            }
            catch (Exception ex)
            {
                // Log cleanup failure
                var errorLog = new ApplicationLogEntry
                {
                    ApplicationName = _options.ApplicationName ?? "LogCleanup",
                    Environment = _options.Environment,
                    LogLevel = LogLevel.Error.ToString(),
                    Category = "System.Cleanup",
                    Message = "Failed to run scheduled log cleanup",
                    Exception = ex.ToString(),
                    Timestamp = DateTime.UtcNow,
                    MachineName = Environment.MachineName
                };

                TryEnqueue(errorLog);
            }
        }

        /// <summary>
        /// Cleans up logs older than the specified number of days
        /// </summary>
        public async Task CleanupOldLogsAsync(int daysToRetain)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysToRetain);

            // Use efficient partition key strategy for deletion
            // Since partition keys are formatted as App_yyyyMMddHH, we can target old partitions
            var oldPartitions = GetOldPartitionKeys(cutoffDate);

            foreach (var partitionKey in oldPartitions)
            {
                try
                {
                    await DeletePartitionAsync(partitionKey);
                }
                catch
                {
                    // Continue with next partition even if one fails
                }
            }

            // Also clean up any remaining old logs that might be in current partitions
            await CleanupOldLogsInCurrentPartitionsAsync(cutoffDate);
        }

        /// <summary>
        /// Cleans up logs by severity level with different retention periods
        /// </summary>
        private async Task CleanupByLevelAsync()
        {
            if (_options.RetentionByLevel == null)
                return;

            foreach (var kvp in _options.RetentionByLevel)
            {
                var logLevel = kvp.Key;
                var retentionDays = kvp.Value;
                var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                // Clean logs of this level older than retention period
                await CleanupLogsByLevelAsync(logLevel.ToString(), cutoffDate);
            }
        }

        /// <summary>
        /// Cleans up logs of a specific level older than the cutoff date
        /// </summary>
        private async Task CleanupLogsByLevelAsync(string logLevel, DateTime cutoffDate)
        {
            try
            {
                var logsToDelete = await _dataAccess.GetCollectionAsync(
                    log => log.LogLevel == logLevel && log.Timestamp < cutoffDate
                );

                if (logsToDelete.Any())
                {
                    var result = await _dataAccess.BatchUpdateListAsync(
                        logsToDelete,
                        TableOperationType.Delete
                    );

                    // Log cleanup stats
                    if (result.Success)
                    {
                        Statistics.TotalLogsDeleted += result.SuccessfulItems;
                    }
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }

        /// <summary>
        /// Gets partition keys that are older than the cutoff date
        /// </summary>
        private List<string> GetOldPartitionKeys(DateTime cutoffDate)
        {
            var partitions = new List<string>();
            var appName = _options.ApplicationName ?? "App";

            // Generate partition keys for dates before cutoff
            // Go back maximum of RetentionDays + 30 days to catch old data
            var startDate = DateTime.UtcNow.AddDays(-(_options.RetentionDays + 30));

            for (var date = startDate; date < cutoffDate; date = date.AddHours(1))
            {
                partitions.Add($"{appName}_{date:yyyyMMddHH}");
            }

            return partitions;
        }

        /// <summary>
        /// Deletes all logs in a specific partition
        /// </summary>
        private async Task DeletePartitionAsync(string partitionKey)
        {
            var logsInPartition = await _dataAccess.GetCollectionAsync(partitionKey);

            if (logsInPartition.Any())
            {
                // Batch delete in chunks of 100 (Azure limit)
                var chunks = logsInPartition
                    .Select((log, index) => new { log, index })
                    .GroupBy(x => x.index / 100)
                    .Select(g => g.Select(x => x.log).ToList());

                foreach (var chunk in chunks)
                {
                    await _dataAccess.BatchUpdateListAsync(chunk, TableOperationType.Delete);
                    Statistics.TotalLogsDeleted += chunk.Count;
                }
            }
        }

        /// <summary>
        /// Cleans up old logs that might be in current/recent partitions
        /// </summary>
        private async Task CleanupOldLogsInCurrentPartitionsAsync(DateTime cutoffDate)
        {
            try
            {
                // Query for old logs regardless of partition
                var oldLogs = await _dataAccess.GetCollectionAsync(
                    log => log.Timestamp < cutoffDate
                );

                if (oldLogs.Any())
                {
                    var result = await _dataAccess.BatchUpdateListAsync(
                        oldLogs,
                        TableOperationType.Delete
                    );

                    Statistics.TotalLogsDeleted += result.SuccessfulItems;
                }
            }
            catch
            {
                // Best effort
            }
        }

        #endregion Cleanup Timer and Methods
    } // end class LoggingBackgroundService

    #endregion Background Service

    #region Log Entry Model

    /// <summary>
    /// Application log entry that extends TableEntityBase for Azure Table Storage
    /// </summary>
    public class ApplicationLogEntry : TableEntityBase, ITableExtra
    {
        public string LogID
        {
            get
            {
                if (string.IsNullOrEmpty(RowKey))
                    RowKey = Guid.NewGuid().ToString("N");
                return RowKey;
            }
            set => RowKey = value;

        }
        /// <summary>
        /// The category name (usually the class or namespace)
        /// </summary>
        public string? Category
        {
            get => PartitionKey;
            set => PartitionKey = value;
        }
        /// <summary>
        /// Name of the application generating the log
        /// </summary>
        public string? ApplicationName { get; set; }
        /// <summary>
        /// The environment (Development, Production, etc.)
        /// </summary>
        public string? Environment { get; set; }
        /// <summary>
        /// The log level (Trace, Debug, Information, Warning, Error, Critical)
        /// </summary>
        public string? LogLevel { get; set; }
        /// <summary>
        /// The event ID associated with the log
        /// </summary>
        public int EventId { get; set; }
        /// <summary>
        /// The event name associated with the log
        /// </summary>
        public string? EventName { get; set; }
        /// <summary>
        /// The message to log
        /// </summary>
        public string? Message { get; set; }
        /// <summary>
        /// The exception details as a string if applicable
        /// </summary>
        public string? Exception { get; set; }
        /// <summary>
        /// The correlation ID for tracing across services.
        /// Helps link related log entries.
        /// </summary>
        public string? CorrelationId { get; set; }
        /// <summary>
        /// The machine name where the log was generated
        /// </summary>
        public string? MachineName { get; set; }
        /// <summary>
        /// The thread ID where the log was generated
        /// </summary>
        public int ThreadId { get; set; }
        /// <summary>
        /// The scopes associated with the log entry, if any
        /// </summary>
        public string? Scopes { get; set; }
        /// <summary>
        /// The custom properties associated with the log entry
        /// </summary>
        public Dictionary<string, string?>? Properties { get; set; }

        /// <summary>
        /// Caller information for enhanced diagnostics
        /// </summary>
        public string? CallerMemberName { get; set; }
        /// <summary>
        /// The file path of the caller
        /// </summary>
        public string? CallerFilePath { get; set; }
        /// <summary>
        /// The line number in the caller file
        /// </summary>
        public int? CallerLineNumber { get; set; }
        /// <summary>
        /// The table the data will be logged to
        /// </summary>
        public string TableReference => Constants.DefaultLogTableName;
        /// <summary>
        /// The unique ID for the log entry
        /// </summary>
        public string GetIDValue() => LogID;
    } // end class ApplicationLogEntry

    #endregion Log Entry Model

    #region Supporting Classes

    /// <summary>
    /// Caller context extraction without expensive stack walking
    /// </summary>
    public static class CallerContext
    {
        private static readonly ConcurrentDictionary<string, CallerInfo> _callerCache = new();

        /// <summary>
        /// Attempts to get caller info using Caller Info attributes
        /// </summary>
        /// <param name="memberName">The name of the member (method/property) being called</param>
        /// <param name="filePath">The file path of the caller</param>
        /// <param name="lineNumber">The line number in the caller file</param>
        public static CallerInfo GetCallerInfo([CallerMemberName] string memberName = "",
            [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            var key = $"{memberName}:{filePath}:{lineNumber}";
            return _callerCache.GetOrAdd(key, _ => new CallerInfo
            {
                MemberName = memberName,
                FilePath = filePath,
                LineNumber = lineNumber
            });
        }
        /// <summary>
        /// The caller information structure
        /// </summary>
        public class CallerInfo
        {
            /// <summary>
            /// The name of the member (method/property) being called
            /// </summary>
            public string MemberName { get; set; } = "";
            /// <summary>
            /// The file path of the caller
            /// </summary>
            public string FilePath { get; set; } = "";
            /// <summary>
            /// The line number in the caller file
            /// </summary>
            public int LineNumber { get; set; }
            /// <summary>
            /// The empty caller info instance.
            /// Helps to avoid null checks.
            /// </summary>
            public static CallerInfo Empty => new();
        }
    } // end class CallerContext

    /// <summary>
    /// Circuit breaker for Azure failures.
    /// Helps avoid overwhelming Azure when it's down.
    /// </summary>
    public class CircuitBreaker
    {
        private int _failureCount;
        private DateTime _lastFailureTime;
        private readonly int _threshold = 5;
        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(1);

        /// <summary>
        /// True if the circuit is open (Azure is unavailable)
        /// </summary>
        public bool IsOpen
        {
            get
            {
                if (_failureCount >= _threshold)
                {
                    if (DateTime.UtcNow - _lastFailureTime > _timeout)
                    {
                        Reset();
                        return false;
                    }
                    return true;
                }
                return false;
            }
        }
        /// <summary>
        /// Records a successful operation, resetting the failure count
        /// </summary>
        public void RecordSuccess()
        {
            Reset();
        }
        /// <summary>
        /// Records a failed operation, incrementing the failure count
        /// </summary>
        public void RecordFailure()
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;
        }
        /// <summary>
        /// Resets the failure count
        /// </summary>
        private void Reset()
        {
            _failureCount = 0;
        }
    } //end class CircuitBreaker

    /// <summary>
    /// Fallback logger for when Azure is unavailable
    /// </summary>
    public static class FallbackLogger
    {
        private static readonly object _lock = new();

        public static void Log(ApplicationLogEntry entry)
        {
            try
            {
                // Try Event Log (Windows only)
                TryEventLog(entry);
            }
            catch
            {
                // Try file
                TryFileLog(entry);
            }
        }

        private static void TryEventLog(ApplicationLogEntry entry)
        {
            if (OperatingSystem.IsWindows())
            {
                using var eventLog = new EventLog("Application");
                eventLog.Source = entry.ApplicationName ?? "Application";

                var eventType = entry.LogLevel switch
                {
                    "Critical" or "Error" => EventLogEntryType.Error,
                    "Warning" => EventLogEntryType.Warning,
                    _ => EventLogEntryType.Information
                };

                eventLog.WriteEntry($"{entry.Category}: {entry.Message}", eventType);
            }
        }

        private static void TryFileLog(ApplicationLogEntry entry)
        {
            lock (_lock)
            {
                var logFile = Path.Combine(Path.GetTempPath(), $"azuretablelog_{DateTime.Today:yyyyMMdd}.txt");
                var logLine = $"{entry.Timestamp:O} [{entry.LogLevel}] {entry.Category}: {entry.Message}{Environment.NewLine}";
                File.AppendAllText(logFile, logLine);
            }
        }
    }

    /// <summary>
    /// Logging statistics for monitoring
    /// </summary>
    public class LoggingStatistics
    {
        /// <summary>
        /// The total number of logs queued
        /// </summary>
        public long TotalLogsQueued;
        /// <summary>
        /// The total number of logs successfully written
        /// </summary>
        public long TotalLogsWritten;
        /// <summary>
        /// The total number of logs that failed to write
        /// </summary>
        public long TotalLogsFailed;
        /// <summary>
        /// The timestamp of the last successful flush
        /// </summary>
        public DateTime LastFlush { get; set; }
        /// <summary>
        /// The average latency of writes to Azure Table Storage
        /// </summary>
        public TimeSpan AverageWriteLatency { get; set; }
        /// <summary>
        /// The current depth of the log queue
        /// Helps monitor backlog
        /// </summary>
        public int CurrentQueueDepth => (int)(TotalLogsQueued - TotalLogsWritten - TotalLogsFailed);
        /// <summary>
        /// The success rate of log writes
        /// </summary>
        public double SuccessRate => TotalLogsQueued > 0 ? (double)TotalLogsWritten / TotalLogsQueued : 0;

        /// <summary>
        /// Total number of logs deleted during cleanup operations
        /// </summary>
        public long TotalLogsDeleted { get; set; }

        /// <summary>
        /// Last time cleanup was run
        /// </summary>
        public DateTime LastCleanupRun { get; set; }

        /// <summary>
        /// Next scheduled cleanup time
        /// </summary>
        public DateTime NextCleanupRun { get; set; }
    } // end class LoggingStatistics

    /// <summary>
    /// Null scope for when scopes are not supported
    /// </summary>
    internal class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }

    /// <summary>
    /// Typed logger wrapper for static scenarios
    /// </summary>
    internal class TypedLogger<T> : ILogger<T>
    {
        private readonly ILogger _logger;

        public TypedLogger(ILogger logger)
        {
            _logger = logger;
        }

        public IDisposable BeginScope<TState>(TState state) => _logger.BeginScope(state)!;
        public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _logger.Log(logLevel, eventId, state, exception, formatter);
    }

    #endregion Supporting Classes

    #region Extension Methods

    /// <summary>
    /// Extension methods for easy integration
    /// </summary>
    public static class AzureTableLoggingExtensions
    {
        /// <summary>
        /// Manually trigger cleanup of old logs
        /// </summary>
        public static async Task CleanupOldLogsAsync(this ILogger logger,
            string accountName, string accountKey, int retentionDays = 60)
        {
            var dataAccess = new DataAccess<ApplicationLogEntry>(accountName, accountKey);
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            var oldLogs = await dataAccess.GetCollectionAsync(
                log => log.Timestamp < cutoffDate
            );

            if (oldLogs.Any())
            {
                await dataAccess.BatchUpdateListAsync(oldLogs, TableOperationType.Delete);
            }
        }

        /// <summary>
        /// Clean up logs by severity level
        /// </summary>
        public static async Task CleanupLogsByLevelAsync(this ILogger logger,
            string accountName, string accountKey, LogLevel level, int retentionDays)
        {
            var dataAccess = new DataAccess<ApplicationLogEntry>(accountName, accountKey);
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            var oldLogs = await dataAccess.GetCollectionAsync(
                log => log.LogLevel == level.ToString() && log.Timestamp < cutoffDate
            );

            if (oldLogs.Any())
            {
                await dataAccess.BatchUpdateListAsync(oldLogs, TableOperationType.Delete);
            }
        }

        /// <summary>
        /// Add Azure Table logging to ASP.NET Core
        /// </summary>
        public static ILoggingBuilder AddAzureTableLogging(this ILoggingBuilder builder)
        {
            builder.Services.AddSingleton<ILoggerProvider, AzureTableLoggerProvider>();
            return builder;
        }

        /// <summary>
        /// Adds Azure Table logging with configuration options
        /// </summary>
        /// <param name="builder">The logging builder</param>
        /// <param name="options">Configuration options for Azure Table logging</param>
        /// <returns>The logging builder for chaining</returns>
        public static ILoggingBuilder AddAzureTableLogging(this ILoggingBuilder builder, AzureTableLoggerOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            // Validate required options
            if (string.IsNullOrEmpty(options.AccountName))
                throw new ArgumentException("AccountName is required", nameof(options));
            if (string.IsNullOrEmpty(options.AccountKey))
                throw new ArgumentException("AccountKey is required", nameof(options));

            // Register the options as singleton so the provider can access them
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<ILoggerProvider, AzureTableLoggerProvider>();
            return builder;
        }

        /// <summary>
        /// Adds Azure Table logging with configuration action
        /// </summary>
        /// <param name="builder">The logging builder</param>
        /// <param name="configure">Action to configure the options</param>
        /// <returns>The logging builder for chaining</returns>
        public static ILoggingBuilder AddAzureTableLogging(this ILoggingBuilder builder, Action<AzureTableLoggerOptions> configure)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            // Validate that we can create valid options
            var options = new AzureTableLoggerOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.AccountName))
                throw new ArgumentException("AccountName is required");
            if (string.IsNullOrEmpty(options.AccountKey))
                throw new ArgumentException("AccountKey is required");

            // Use Configure to register with IOptions pattern
            builder.Services.Configure<AzureTableLoggerOptions>(configure);

            // Register the provider - DI will inject IOptions<AzureTableLoggerOptions>
            builder.Services.AddSingleton<ILoggerProvider, AzureTableLoggerProvider>();

            return builder;
        }

        /// <summary>
        /// Add Azure Table logging with explicit credentials
        /// </summary>
        public static ILoggingBuilder AddAzureTableLogging(this ILoggingBuilder builder,
            string accountName, string accountKey)
        {
            builder.Services.Configure<AzureTableLoggerOptions>(options =>
            {
                options.AccountName = accountName;
                options.AccountKey = accountKey;
            });

            builder.Services.AddSingleton<ILoggerProvider, AzureTableLoggerProvider>();
            return builder;
        }

        /// <summary>
        /// Add Azure Table logging to host builder
        /// </summary>
        public static IHostBuilder AddAzureTableLogging(this IHostBuilder builder,
            string accountName, string accountKey)
        {
            return builder.ConfigureLogging(logging =>
            {
                logging.AddAzureTableLogging(accountName, accountKey);
            });
        }

        /// <summary>
        /// Add Azure Table logging to service collection (for desktop apps with DI)
        /// </summary>
        public static IServiceCollection AddAzureTableLogging(this IServiceCollection services,
            string accountName, string accountKey)
        {
            services.Configure<AzureTableLoggerOptions>(options =>
            {
                options.AccountName = accountName;
                options.AccountKey = accountKey;
            });

            services.AddSingleton<ILoggerProvider, AzureTableLoggerProvider>();
            services.AddLogging();
            return services;
        }
    }

    #endregion Extension Methods

    #region Desktop Application Extensions

    /// <summary>
    /// Extensions specifically for desktop applications
    /// </summary>
    public static class DesktopLoggingExtensions
    {
        /// <summary>
        /// Log from UI thread safely (WPF)
        /// </summary>
        public static void LogFromUI(this ILogger logger, LogLevel level, string message, params object[] args)
        {
            logger.Log(level, message, args);
        }

        /// <summary>
        /// Log user action with context
        /// </summary>
        public static void LogUserAction(this ILogger logger, string action, object? details = null)
        {
            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["UserAction"] = action,
                ["User"] = Environment.UserName
            }))
            {
                logger.LogInformation("User action: {Action}", new { Action = action, Details = details });
            }
        }

        /// <summary>
        /// Log application lifecycle events
        /// </summary>
        public static void LogApplicationLifecycle(this ILogger logger, string eventName)
        {
            logger.LogInformation("Application lifecycle: {Event}", new
            {
                Event = eventName,
                Time = DateTime.UtcNow,
                User = Environment.UserName,
                Machine = Environment.MachineName
            });
        }
    }

    #endregion Desktop Application Extensions

    #region Console Application Extensions

    /// <summary>
    /// Extensions for console and service applications
    /// </summary>
    public static class ConsoleLoggingExtensions
    {
        /// <summary>
        /// Log progress of long-running operations
        /// </summary>
        public static IDisposable LogProgress(this ILogger logger, string operation, int total)
        {
            return new ProgressLogger(logger, operation, total);
        }

        /// <summary>
        /// Log heartbeat for monitoring
        /// </summary>
        public static void LogHeartbeat(this ILogger logger, string service)
        {
            logger.LogTrace("Heartbeat: {Service} at {Time}", service, DateTime.UtcNow);
        }

        /// <summary>
        /// Log batch operation results
        /// </summary>
        public static void LogBatchResult(this ILogger logger, string operation, int processed, int failed, TimeSpan duration)
        {
            var level = failed > 0 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "Batch {Operation} completed: {Processed} processed, {Failed} failed in {Duration}ms",
                operation, processed, failed, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Progress logger for tracking long operations
    /// </summary>
    public class ProgressLogger : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operation;
        private readonly int _total;
        private readonly Stopwatch _stopwatch;
        private int _current;
        private readonly Timer _timer;
        /// <summary>
        /// Constructor that initializes the progress logger
        /// </summary>
        /// <param name="logger">The logger instance</param>
        /// <param name="operation">The name of the operation</param>
        /// <param name="total">The total number of items to process</param>
        public ProgressLogger(ILogger logger, string operation, int total)
        {
            _logger = logger;
            _operation = operation;
            _total = total;
            _stopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Starting {Operation} with {Total} items", operation, total);

            // Log progress every 10 seconds
            _timer = new Timer(_ => LogProgress(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        /// <summary>
        /// Increments the current progress count.
        /// Helps track how many items have been processed.
        /// </summary>
        /// <param name="count">The number of items to increment</param>
        public void Increment(int count = 1)
        {
            Interlocked.Add(ref _current, count);
        }
        /// <summary>
        /// The current progress count
        /// </summary>
        private void LogProgress()
        {
            var percent = _total > 0 ? (_current * 100.0 / _total) : 0;
            var rate = _current / _stopwatch.Elapsed.TotalSeconds;

            _logger.LogInformation("{Operation} progress: {Current}/{Total} ({Percent:F1}%) at {Rate:F0} items/sec",
                _operation, _current, _total, percent, rate);
        }
        /// <summary>
        /// Properly disposes of resources used by the progress logger
        /// </summary>
        public void Dispose()
        {
            _timer?.Dispose();
            _stopwatch.Stop();

            _logger.LogInformation("Completed {Operation}: {Current}/{Total} items in {Duration}",
                _operation, _current, _total, _stopwatch.Elapsed);
        }
    } // end class ProgressLogger

    #endregion Console Application Extensions

    #region Backwards Compatibility

    /// <summary>
    /// Interface for compatibility
    /// </summary>
    public interface IErrorLoggingService
    {
        /// <summary>
        /// The method to log an error
        /// </summary>
        /// <param name="ex">The exception data to log</param>
        /// <param name="description">The description of the error</param>
        /// <param name="severity">The severity level of the error</param>
        /// <param name="customerId">The ID of the customer associated with the error</param>
        Task LogErrorAsync(Exception? ex, string description, ErrorCodeTypes severity, string customerId = "undefined");
    }

    #endregion Backwards Compatibility

    #region Client Side Setup
    /// <summary>
    /// Centralized Azure Table Storage logging configuration for all application types
    /// </summary>
    public static class AzureTableLoggerConfiguration
    {
        private static string? _accountName;
        private static string? _accountKey;
        private static bool _isInitialized = false;

        /// <summary>
        /// Configure and add Azure Table logging to the application
        /// Works for Web, Desktop, Console, and Service applications
        /// </summary>
        public static ILoggingBuilder ConfigureAzureTableLogging(
            this ILoggingBuilder logging,
            string accountName,
            string accountKey,
            Action<AzureTableLoggerOptions>? configure = null)
        {
            _accountName = accountName;
            _accountKey = accountKey;

            var options = new AzureTableLoggerOptions
            {
                AccountName = accountName,
                AccountKey = accountKey
            };

            configure?.Invoke(options);

            // Clear default providers if requested
            if (options.ClearProviders)
            {
                logging.ClearProviders();
            }

            // Keep console and debug for development if requested
            if (options.KeepConsole)
            {
                logging.AddConsole();
            }

            if (options.KeepDebug)
            {
                logging.AddDebug();
            }

            // Add Azure Table Logging
            logging.AddAzureTableLogging(options);

            _isInitialized = true;
            return logging;
        }

        /// <summary>
        /// Configure with auto-discovery of credentials
        /// </summary>
        public static ILoggingBuilder ConfigureAzureTableLogging(
            this ILoggingBuilder logging,
            IConfiguration configuration,
            IHostEnvironment? environment = null,
            Action<AzureTableLoggerOptions>? configure = null)
        {
            // Try multiple sources for credentials
            _accountName = configuration["Azure:TableStorageName"]
                ?? configuration["AZURE_STORAGE_ACCOUNT"]
                ?? configuration["TableStorageName"]
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT");

            _accountKey = configuration["Azure:TableStorageKey"]
                ?? configuration["AZURE_STORAGE_KEY"]
                ?? configuration["TableStorageKey"]
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY");

            if (string.IsNullOrEmpty(_accountName) || string.IsNullOrEmpty(_accountKey))
            {
                throw new InvalidOperationException(
                    "Azure Storage credentials not found. Please configure Azure:TableStorageName/TableStorageKey or set AZURE_STORAGE_ACCOUNT/AZURE_STORAGE_KEY environment variables.");
            }

            var options = new AzureTableLoggerOptions
            {
                AccountName = _accountName,
                AccountKey = _accountKey,
                TableName = GetTableName(environment),
                MinimumLevel = GetMinimumLogLevel(environment),
                RetentionDays = GetRetentionDays(configuration),
                EnableAutoCleanup = GetAutoCleanupSetting(configuration),
                RetentionByLevel = GetRetentionByLevel(configuration)
            };

            configure?.Invoke(options);

            return logging.ConfigureAzureTableLogging(_accountName, _accountKey, opt =>
            {
                opt.TableName = options.TableName;
                opt.MinimumLevel = options.MinimumLevel;
                opt.RetentionDays = options.RetentionDays;
                opt.EnableAutoCleanup = options.EnableAutoCleanup;
                opt.RetentionByLevel = options.RetentionByLevel;
                opt.ClearProviders = options.ClearProviders;
                opt.KeepConsole = options.KeepConsole;
                opt.KeepDebug = options.KeepDebug;
            });
        }

        /// <summary>
        /// Log application startup event (generic for all app types)
        /// </summary>
        public static async Task LogApplicationStartupAsync(
            IServiceProvider serviceProvider,
            ILogger logger,
            string applicationName,
            string? environment = null)
        {
            environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? "Production";

            // Log with structured logging
            logger.LogInformation("{ApplicationName} started successfully at {StartTime} in {Environment} mode",
                applicationName,
                DateTime.UtcNow,
                environment);

            // Try to log through any registered IErrorLoggingService for backward compatibility
            try
            {
                var errorLoggerType = serviceProvider.GetType().Assembly
                    .GetTypes()
                    .FirstOrDefault(t => t.Name == "IErrorLoggingService");

                if (errorLoggerType != null)
                {
                    var errorLogger = serviceProvider.GetService(errorLoggerType);
                    if (errorLogger != null)
                    {
                        var logMethod = errorLoggerType.GetMethod("LogErrorAsync");
                        if (logMethod != null)
                        {
                            await (Task)logMethod.Invoke(errorLogger, new object[]
                            {
                                null!,
                                "Application started",
                                ErrorCodeTypes.Information,
                                "SYSTEM"
                            })!;
                        }
                    }
                }
            }
            catch
            {
                // Ignore if IErrorLoggingService doesn't exist or fails
            }
        }

        /// <summary>
        /// Create a global exception handler middleware (for web apps)
        /// </summary>
        public static Func<HttpContext, Func<Task>, Task> CreateGlobalExceptionHandler(string? customerIdClaimName = "CompanyId")
        {
            return async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GlobalExceptionHandler");

                    var claimName = customerIdClaimName ?? "CompanyId";
                    var customerId = context.User?.FindFirst(claimName)?.Value ?? "UNKNOWN";
                    var requestPath = context.Request.Path.ToString();
                    var requestMethod = context.Request.Method;

                    logger.LogError(ex,
                        "Unhandled exception for customer {CustomerId} on {Method} {Path}",
                        customerId, requestMethod, requestPath);

                    var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                    if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
                    {
                        var headers = context.Request.Headers
                            .Where(h => !h.Key.Contains("Authorization", StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(h => h.Key, h => h.Value.ToString());

                        logger.LogError("Request Headers: {Headers}", headers);
                    }

                    throw;
                }
            };
        }


        /// <summary>
        /// Get stored credentials for external use
        /// </summary>
        public static (string AccountName, string AccountKey) GetStoredCredentials()
        {
            if (!_isInitialized || string.IsNullOrEmpty(_accountName) || string.IsNullOrEmpty(_accountKey))
            {
                throw new InvalidOperationException(
                    "AzureTableLogging must be configured before getting credentials. Call ConfigureAzureTableLogging first.");
            }
            return (_accountName, _accountKey);
        }

        /// <summary>
        /// Check if logging has been initialized
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        #region Private Helper Methods

        private static string GetTableName(IHostEnvironment? environment)
        {
            if (environment == null)
                return "ApplicationLogs";

            return environment.IsDevelopment()
                ? "ApplicationLogsDev"
                : environment.IsStaging()
                    ? "ApplicationLogsStaging"
                    : "ApplicationLogs";
        }

        private static LogLevel GetMinimumLogLevel(IHostEnvironment? environment)
        {
            if (environment == null)
                return LogLevel.Information;

            return environment.IsDevelopment()
                ? LogLevel.Debug
                : environment.IsStaging()
                    ? LogLevel.Information
                    : LogLevel.Warning;
        }

        private static int GetRetentionDays(IConfiguration configuration)
        {
            if (int.TryParse(configuration["Logging:AzureTable:RetentionDays"], out int days))
            {
                return days;
            }
            return 60;
        }

        private static bool GetAutoCleanupSetting(IConfiguration configuration)
        {
            if (bool.TryParse(configuration["Logging:AzureTable:EnableAutoCleanup"], out bool enabled))
            {
                return enabled;
            }
            return true;
        }

        private static Dictionary<LogLevel, int> GetRetentionByLevel(IConfiguration configuration)
        {
            var retentionByLevel = new Dictionary<LogLevel, int>();
            var section = configuration.GetSection("Logging:AzureTable:RetentionByLevel");

            if (section.Exists())
            {
                if (int.TryParse(section["Trace"], out int trace))
                    retentionByLevel[LogLevel.Trace] = trace;
                if (int.TryParse(section["Debug"], out int debug))
                    retentionByLevel[LogLevel.Debug] = debug;
                if (int.TryParse(section["Information"], out int info))
                    retentionByLevel[LogLevel.Information] = info;
                if (int.TryParse(section["Warning"], out int warning))
                    retentionByLevel[LogLevel.Warning] = warning;
                if (int.TryParse(section["Error"], out int error))
                    retentionByLevel[LogLevel.Error] = error;
                if (int.TryParse(section["Critical"], out int critical))
                    retentionByLevel[LogLevel.Critical] = critical;
            }

            return new Dictionary<LogLevel, int>
            {
                { LogLevel.Trace, retentionByLevel.GetValueOrDefault(LogLevel.Trace, 7) },
                { LogLevel.Debug, retentionByLevel.GetValueOrDefault(LogLevel.Debug, 14) },
                { LogLevel.Information, retentionByLevel.GetValueOrDefault(LogLevel.Information, 30) },
                { LogLevel.Warning, retentionByLevel.GetValueOrDefault(LogLevel.Warning, 60) },
                { LogLevel.Error, retentionByLevel.GetValueOrDefault(LogLevel.Error, 90) },
                { LogLevel.Critical, retentionByLevel.GetValueOrDefault(LogLevel.Critical, 180) }
            };
        }

        #endregion
    }

    /// <summary>
    /// Extended options for Azure Table Logger configuration
    /// </summary>
    public class AzureTableLoggerOptionsExtended : AzureTableLoggerOptions
    {
        /// <summary>
        /// Whether to clear existing logging providers (default: true)
        /// </summary>
        public bool ClearProviders { get; set; } = true;

        /// <summary>
        /// Whether to keep console logging (default: true)
        /// </summary>
        public bool KeepConsole { get; set; } = true;

        /// <summary>
        /// Whether to keep debug logging (default: true)
        /// </summary>
        public bool KeepDebug { get; set; } = true;
    }

    /// <summary>
    /// Generic bridge class for backward compatibility with IErrorLoggingService
    /// Works with any project that has an IErrorLoggingService interface
    /// </summary>
    public class ErrorLoggingServiceBridge
    {
        private readonly ILogger<ErrorLoggingServiceBridge> _logger;
        private readonly string _accountName;
        private readonly string _accountKey;

        /// <summary>
        /// Constructor for the error logging service bridge
        /// </summary>
        /// <param name="logger">The logger instance</param>
        /// <param name="accountName">The azure table storage account name to access your table storage instance</param>
        /// <param name="accountKey">The created key credential for access</param>
        public ErrorLoggingServiceBridge(ILogger<ErrorLoggingServiceBridge> logger, string accountName, string accountKey)
        {
            _logger = logger;
            _accountName = accountName;
            _accountKey = accountKey;
        }

        /// <summary>
        /// Generic error logging method that matches most IErrorLoggingService interfaces
        /// </summary>
        /// <param name="ex">The exception to log</param>
        /// <param name="description">A description of the error</param>
        /// <param name="severity">The severity level of the error</param>
        /// <param name="customerId">The ID of the customer associated with the error</param>
        public async Task LogErrorAsync(Exception? ex, string description, ErrorCodeTypes severity, string customerId)
        {
            var logLevel = MapErrorCodeTypeToLogLevel(severity);
            _logger.Log(logLevel, ex, "{Description} | Customer: {CustomerId}", description, customerId);

            try
            {
                var errorLog = new ErrorLogData(ex, description, severity, customerId);
                await errorLog.LogErrorAsync(_accountName, _accountKey);
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "Failed to log error through ErrorLogData legacy system");
            }
        }

        /// <summary>
        /// Helps map error code types to log levels
        /// </summary>
        /// <param name="errorType">The available error code types</param>
        private static LogLevel MapErrorCodeTypeToLogLevel(ErrorCodeTypes errorType)
        {
            return errorType switch
            {
                ErrorCodeTypes.Critical => LogLevel.Critical,
                ErrorCodeTypes.Error => LogLevel.Error,
                ErrorCodeTypes.Warning => LogLevel.Warning,
                ErrorCodeTypes.Information => LogLevel.Information,
                _ => LogLevel.Information
            };
        }
    } // end class ErrorLoggingServiceBridge
    #endregion Client Side Setup
}