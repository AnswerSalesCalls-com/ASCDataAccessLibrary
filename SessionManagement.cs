using ASCTableStorage.Common;
using ASCTableStorage.Data;
using ASCTableStorage.Models;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Loader;

namespace ASCTableStorage.Sessions
{
    #region Session Configuration

    /// <summary>
    /// Configuration options for Azure Table-based session management
    /// </summary>
    public class SessionOptions
    {
        /// <summary>
        /// The unique identifier for the session (optional)
        /// </summary>
        public string? SessionId { get; set; }
        /// <summary>
        /// Azure Storage account name (required)
        /// </summary>
        public string? AccountName { get; set; }

        /// <summary>
        /// Azure Storage account key (required)
        /// </summary>
        public string? AccountKey { get; set; }

        /// <summary>
        /// Table name for storing session data (default: AppSessionData)
        /// </summary>
        public string TableName { get; set; } = Constants.DefaultSessionTableName;

        /// <summary>
        /// How old session data should be before cleanup (default: 45 Minutes)
        /// </summary>
        public TimeSpan StaleDataCleanupAge { get; set; } = TimeSpan.FromMinutes(45);

        /// <summary>
        /// Auto-commit session changes (default: true)
        /// </summary>
        public bool AutoCommit { get; set; } = true;

        /// <summary>
        /// Enable automatic cleanup of old sessions (default: true)
        /// </summary>
        public bool EnableAutoCleanup { get; set; } = true;

        /// <summary>
        /// Happens when a session becomes stale (no usage) in this amount of time. 
        /// Then we  mark for cleanup. Interval for cleanup runs (default: 30 Minutes).
        /// Should be less that the StaleDataCleanupAge to make sure data does not linger too long between cycles
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Batch write delay for performance optimization (default: 500ms)
        /// </summary>
        public TimeSpan BatchWriteDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Maximum batch size for write operations (default: 100)
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Strategy for generating session IDs
        /// </summary>
        public SessionIdStrategy IdStrategy { get; set; } = SessionIdStrategy.Auto;

        /// <summary>
        /// Custom session ID provider function (used when IdStrategy = Custom)
        /// </summary>
        public Func<string>? CustomIdProvider { get; set; }

        /// <summary>
        /// Application name for session grouping
        /// </summary>
        public string? ApplicationName { get; set; }

        /// <summary>
        /// Auto-discover credentials from various sources
        /// </summary>
        public bool AutoDiscoverCredentials { get; set; } = true;

        /// <summary>
        /// Enable session activity tracking
        /// </summary>
        public bool TrackActivity { get; set; } = true;

        /// <summary>
        /// Validates that required options are set
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(AccountName) && !string.IsNullOrEmpty(AccountKey);
        }

        /// <summary>
        /// Auto-commit interval (set to TimeSpan.Zero to disable, default is 10 seconds)
        /// </summary>
        public TimeSpan AutoCommitInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Delay before starting the first cleanup run (default: 30 seconds)
        /// This prevents race conditions during initialization
        /// </summary>
        public TimeSpan CleanupStartDelay { get; set; } = TimeSpan.FromSeconds(30);
    } // end class SessionOptions

    /// <summary>
    /// Strategies for generating session IDs
    /// </summary>
    public enum SessionIdStrategy
    {
        /// <summary>
        /// Automatically detect based on application type
        /// </summary>
        Auto,

        /// <summary>
        /// Use HttpContext.Session.Id for web applications
        /// </summary>
        HttpContext,

        /// <summary>
        /// Use username + machine name for desktop applications
        /// </summary>
        UserMachine,

        /// <summary>
        /// Use machine name + process ID for services
        /// </summary>
        MachineProcess,

        /// <summary>
        /// Use custom provider function
        /// </summary>
        Custom,

        /// <summary>
        /// Generate a new GUID for each session
        /// </summary>
        Guid
    } //end enum SessionIdStrategy

    #endregion Session Configuration

    #region Static Session Manager


    /// <summary>
    /// Global session manager providing unified session access across all application types
    /// </summary>
    public static class SessionManager
    {
        private static Session? _currentSession;
        private static string? _accountName;
        private static string? _accountKey;
        private static string? _sessionId;
        private static Func<string>? _customIdProvider;
        private static readonly object _lock = new object();
        private static Timer? _autoCommitTimer;
        private static CancellationTokenSource? _shutdownTokenSource;
        private static SessionBackgroundService? _backgroundService;
        internal static bool _initialized = false;

        /// <summary>
        /// Gets the current session instance (implements ISession)
        /// </summary>
        public static Session Current
        {
            get
            {
                if (!_initialized)
                {
                    throw new InvalidOperationException(
                        "SessionManager has not been initialized. Call Initialize() first.");
                }

                if (_currentSession == null)
                {
                    lock (_lock)
                    {
                        if (_currentSession == null)
                        {
                            var sessionId = _sessionId ?? _customIdProvider?.Invoke() ?? GetDefaultSessionId();
                            _currentSession = new Session(_accountName!, _accountKey!, sessionId);
                        }
                    }
                }

                return _currentSession;
            }
        }

        /// <summary>
        /// Initialize session manager with explicit credentials
        /// </summary>
        /// <param name="accountName">Azure Table Storage Database name</param>
        /// <param name="accountKey">Azure Table Storage Database key</param>
        /// <param name="configure">The options that determine configurations</param>
        public static void Initialize(string accountName, string accountKey, Action<SessionOptions>? configure = null)
        {
            lock (_lock)
            {
                if (_initialized)
                {
                    Shutdown();
                }

                _accountName = accountName;
                _accountKey = accountKey;
                _shutdownTokenSource = new CancellationTokenSource();

                var options = new SessionOptions
                {
                    AccountName = accountName,
                    AccountKey = accountKey
                };
                configure?.Invoke(options);

                if (options.IdStrategy == SessionIdStrategy.Custom && options.CustomIdProvider != null)
                {
                    _customIdProvider = options.CustomIdProvider;
                }
                else if (!string.IsNullOrEmpty(options.SessionId))
                {
                    _sessionId = options.SessionId;
                }

                // Initialize background service if cleanup is enabled
                if (options.EnableAutoCleanup)
                {
                    _backgroundService = new SessionBackgroundService(options);
                    // Start the background service with the cancellation token
                    _backgroundService.StartAsync(_shutdownTokenSource.Token).GetAwaiter().GetResult();
                }

                // Setup auto-commit timer if enabled
                if (options.AutoCommitInterval > TimeSpan.Zero)
                {
                    _autoCommitTimer = new Timer(
                        AutoCommitCallback,
                        null,
                        options.AutoCommitInterval,
                        options.AutoCommitInterval);
                }

                _initialized = true;

                // Register for multiple shutdown scenarios
                RegisterShutdownHandlers();
            }
        }

        /// <summary>
        /// Initialize with specific session ID
        /// </summary>
        /// <param name="accountName">Azure Table Storage Database name</param>
        /// <param name="accountKey">Azure Table Storage Database key</param>
        /// <param name="sessionId">The sessionID this instance will work on</param>
        public static void Initialize(string accountName, string accountKey, string sessionId)
            => Initialize(accountName, accountKey, options => options.SessionId = sessionId);

        /// <summary>
        /// Register all shutdown handlers for different scenarios
        /// </summary>
        private static void RegisterShutdownHandlers()
        {
            // 1. Normal app domain shutdown
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            // 2. Unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // 3. Assembly unload (for .NET Core)
            try
            {
                AssemblyLoadContext.Default.Unloading += OnAssemblyUnloading;
            }
            catch
            {
                // Ignore if not on .NET Core
            }

            // 4. Console cancel (Ctrl+C)
            Console.CancelKeyPress += OnConsoleCancelKeyPress;

            // 5. For ASP.NET Core apps - IHostApplicationLifetime would be better but we don't have DI here
            // Users should call RegisterForGracefulShutdown with IHostApplicationLifetime token
        }

        /// <summary>
        /// Register for graceful shutdown with a cancellation token (for ASP.NET Core apps)
        /// Call this from Program.cs with IHostApplicationLifetime.ApplicationStopping token
        /// </summary>
        /// <param name="cancellationToken">The cancellation token that sparks or requests shutdown</param>
        public static void RegisterForGracefulShutdown(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() =>
            {
                try
                {
                    CommitAndShutdown("Application stopping");
                }
                catch (Exception ex)
                {
                    // Log if possible but don't throw
                    System.Diagnostics.Debug.WriteLine($"Error during graceful shutdown: {ex}");
                }
            });
        }

        /// <summary>
        /// Auto-commit callback for timer
        /// </summary>
        /// <param name="state">The state of the session</param>
        private static void AutoCommitCallback(object? state)
        {
            try
            {
                if (_currentSession != null)
                {
                    // Always commit when timer fires - don't trust DataHasBeenCommitted
                    _currentSession.CommitData();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-commit failed: {ex}");
            }
        }

        /// <summary>
        /// Handle process exit
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnProcessExit(object? sender, EventArgs e) => CommitAndShutdown("Process exit");

        /// <summary>
        /// Handle unhandled exceptions
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            CommitAndShutdown($"Unhandled exception (terminating: {e.IsTerminating})");
        }

        /// <summary>
        /// Handle assembly unloading
        /// </summary>
        /// <param name="context">Assembly context</param>
        private static void OnAssemblyUnloading(AssemblyLoadContext context) => CommitAndShutdown("Assembly unloading");


        /// <summary>
        /// Handle console cancel key press (Ctrl+C)
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            CommitAndShutdown("Console cancel");            
            e.Cancel = false; // Allow the process to terminate
        }

        /// <summary>
        /// Common method to commit and shutdown
        /// </summary>
        private static void CommitAndShutdown(string reason)
        {
            try
            {
                Debug.WriteLine($"SessionManager shutdown initiated: {reason}");

                lock (_lock)
                {
                    if (_currentSession != null)
                    {
                        // ALWAYS commit on shutdown, ignore DataHasBeenCommitted flag
                        // Use synchronous commit to ensure it completes
                        _currentSession.CommitData();
                    }
                }

                Shutdown();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during shutdown commit: {ex}");
            }
        }

        /// <summary>
        /// Shutdown and flush all pending session data
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    _shutdownTokenSource?.Cancel();

                    // Stop background service if running
                    if (_backgroundService != null)
                    {
                        _backgroundService.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                        _backgroundService.Dispose();
                        _backgroundService = null;
                    }

                    _shutdownTokenSource?.Dispose();
                    _shutdownTokenSource = null;

                    _autoCommitTimer?.Dispose();
                    _autoCommitTimer = null;

                    if (_currentSession != null)
                    {
                        // ALWAYS commit on shutdown
                        _currentSession.CommitData();
                        _currentSession.Dispose();
                        _currentSession = null;
                    }
                }
                finally
                {
                    _initialized = false;

                    // Unregister handlers
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                    Console.CancelKeyPress -= OnConsoleCancelKeyPress;

                    try
                    {
                        AssemblyLoadContext.Default.Unloading -= OnAssemblyUnloading;
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        } // end ShutDown()

        /// <summary>
        /// Async shutdown
        /// </summary>
        public static async Task ShutdownAsync()
        {
            try
            {
                _shutdownTokenSource?.Cancel();

                if (_backgroundService != null)
                {
                    await _backgroundService.StopAsync(CancellationToken.None);
                }

                if (_currentSession != null && !_currentSession.DataHasBeenCommitted)
                {
                    await _currentSession.CommitDataAsync();
                }
            }
            finally
            {
                Shutdown();
            }
        }

        /// <summary>
        /// Commits data from the current session
        /// </summary>
        public static bool CommitData()
        {
            if (_currentSession != null)
            {
                return _currentSession.CommitData();
            }
            return false;
        }

        /// <summary>
        /// Async commit data from the current session
        /// </summary>
        public static async Task<bool> CommitDataAsync()
        {
            if (_currentSession != null)
            {
                return await _currentSession.CommitDataAsync();
            }
            return false;
        }

        /// <summary>
        /// Generate a default session ID based on context
        /// </summary>
        private static string GetDefaultSessionId()
        {
            // For desktop apps, use username + machine
            if (Environment.UserInteractive)
            {
                return $"{Environment.UserName}_{Environment.MachineName}".Replace("\\", "_");
            }

            // For services/console apps, use machine + process
            return $"{Environment.MachineName}_{Process.GetCurrentProcess().Id}";
        }

        /// <summary>
        /// Get session statistics from the background service
        /// </summary>
        public static SessionStatistics? GetStatistics()
        {
            return _backgroundService?.Statistics;
        }

    } // end class SessionManager

    #endregion Static Session Manager

    #region Session Interface

    /// <summary>
    /// Session interface for accessing session data
    /// </summary>
    public interface ISession
    {
        /// <summary>
        /// Gets or sets session values by key
        /// </summary>
        AppSessionData? this[string key] { get; set; }

        /// <summary>
        /// Gets the session ID
        /// </summary>
        string SessionId { get; }

        /// <summary>
        /// Checks if a key exists in the session
        /// </summary>
        bool ContainsKey(string key);

        /// <summary>
        /// Removes a key from the session
        /// </summary>
        Task<bool> RemoveAsync(string key);

        /// <summary>
        /// Clears all session data
        /// </summary>
        Task ClearAsync();

        /// <summary>
        /// Forces a flush of pending writes
        /// </summary>
        Task FlushAsync();

        /// <summary>
        /// Gets all session keys
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Gets the number of items in the session
        /// </summary>
        int Count { get; }
    } //end interface iSession

    #endregion Session Interface

    #region Optimized Session Implementation

    /// <summary>
    /// Optimized session implementation with batched writes and direct reads
    /// </summary>
    public class OptimizedSession : ISession, IDisposable
    {
        private readonly SessionOptions _options;
        private readonly string _sessionId;
        private readonly DataAccess<AppSessionData> _dataAccess;
        private readonly ConcurrentDictionary<string, AppSessionData> _pendingWrites;
        private readonly ConcurrentDictionary<string, AppSessionData> _sessionData;
        private readonly SessionBackgroundService _backgroundService;
        private readonly Timer _batchTimer;
        private readonly SemaphoreSlim _flushSemaphore;
        private bool _disposed;

        /// <summary>
        /// The unique identifier for this session
        /// </summary>
        public string SessionId => _sessionId;
        /// <summary>
        /// The keys in the session
        /// </summary>
        public IEnumerable<string> Keys => _sessionData.Keys;
        /// <summary>
        /// The total number of items in the session
        /// </summary>
        public int Count => _sessionData.Count;

        internal OptimizedSession(SessionOptions options, string sessionId, SessionBackgroundService backgroundService)
        {
            _options = options;
            _sessionId = sessionId;
            _backgroundService = backgroundService;
            _dataAccess = new DataAccess<AppSessionData>(options.AccountName!, options.AccountKey!);
            _pendingWrites = new ConcurrentDictionary<string, AppSessionData>();
            _sessionData = new ConcurrentDictionary<string, AppSessionData>();
            _flushSemaphore = new SemaphoreSlim(1, 1);

            // Setup batch timer
            _batchTimer = new Timer(
                async _ => await FlushAsync(),
                null,
                _options.BatchWriteDelay,
                _options.BatchWriteDelay
            );

            // Register with background service
            _backgroundService.RegisterSession(this);
        }

        /// <summary>
        /// Gets or sets session data by key
        /// </summary>
        public AppSessionData? this[string key]
        {
            get
            {
                // Check pending writes first (most recent data)
                if (_pendingWrites.TryGetValue(key, out var pendingData))
                    return pendingData;

                // Check loaded data
                if (_sessionData.TryGetValue(key, out var data))
                    return data;

                // Load from Azure if not in memory (synchronous for indexer)
                var loaded = LoadSingleAsync(key).GetAwaiter().GetResult();
                if (loaded != null)
                {
                    _sessionData.TryAdd(key, loaded);
                }
                return loaded;
            }
            set
            {
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentNullException(nameof(key));

                if (value == null)
                {
                    // Remove the key
                    RemoveAsync(key).GetAwaiter().GetResult();
                    return;
                }

                // Ensure session ID and key are set
                value.SessionID = _sessionId;
                value.Key = key;

                // Add to pending writes
                _pendingWrites.AddOrUpdate(key, value, (k, v) => value);

                // Also update in-memory data
                _sessionData.AddOrUpdate(key, value, (k, v) => value);

                // Track statistics
                _backgroundService.Statistics.TotalWrites++;
            }
        }

        /// <summary>
        /// Load all session data
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                var allData = await _dataAccess.GetCollectionAsync(_sessionId);

                foreach (var item in allData)
                {
                    if (!string.IsNullOrEmpty(item.Key))
                    {
                        _sessionData.TryAdd(item.Key, item);
                    }
                }

                _backgroundService.Statistics.TotalReads += allData.Count;
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                Debug.WriteLine($"Failed to load session data: {ex.Message}");
            }
        }

        /// <summary>
        /// Load a single key from Azure
        /// </summary>
        private async Task<AppSessionData?> LoadSingleAsync(string key)
        {
            try
            {
                var results = await _dataAccess.GetCollectionAsync(
                    s => s.SessionID == _sessionId && s.Key == key
                );

                _backgroundService.Statistics.TotalReads++;
                return results.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Check if key exists
        /// </summary>
        public bool ContainsKey(string key) 
            => _pendingWrites.ContainsKey(key) || _sessionData.ContainsKey(key);
       

        /// <summary>
        /// Remove a key from session
        /// </summary>
        public async Task<bool> RemoveAsync(string key)
        {
            // Remove from collections
            _pendingWrites.TryRemove(key, out _);
            _sessionData.TryRemove(key, out var existing);

            if (existing != null)
            {
                // Delete from Azure
                try
                {
                    await _dataAccess.ManageDataAsync(existing, TableOperationType.Delete);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Clear all session data
        /// </summary>
        public async Task ClearAsync()
        {
            // Clear pending writes
            _pendingWrites.Clear();

            // Get all data to delete
            var toDelete = _sessionData.Values.ToList();
            _sessionData.Clear();

            // Delete from Azure
            if (toDelete.Any())
            {
                try
                {
                    await _dataAccess.BatchUpdateListAsync(toDelete, TableOperationType.Delete);
                }
                catch { }
            }
        }

        /// <summary>
        /// Flush pending writes to Azure
        /// </summary>
        public async Task FlushAsync()
        {
            if (_pendingWrites.IsEmpty)
                return;

            await _flushSemaphore.WaitAsync();
            try
            {
                var toWrite = _pendingWrites.Values.ToList();
                _pendingWrites.Clear();

                if (toWrite.Any())
                {
                    // Batch write to Azure
                    var sw = Stopwatch.StartNew();
                    var result = await _dataAccess.BatchUpdateListAsync(
                        toWrite,
                        TableOperationType.InsertOrReplace
                    );
                    sw.Stop();

                    // Update statistics
                    _backgroundService.Statistics.SuccessfulWrites += result.SuccessfulItems;
                    _backgroundService.Statistics.FailedWrites += result.FailedItems;
                    _backgroundService.Statistics.AverageWriteLatency = sw.Elapsed;
                    _backgroundService.Statistics.LastFlush = DateTime.UtcNow;

                    // Re-queue failed items
                    if (result.FailedItems > 0)
                    {
                        // Could implement retry logic here
                    }
                }
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Flush pending writes
            if (_options.AutoCommit)
            {
                FlushAsync().GetAwaiter().GetResult();
            }

            _batchTimer?.Dispose();
            _flushSemaphore?.Dispose();

            // Unregister from background service
            _backgroundService.UnregisterSession(this);
        }
    } // end class OptimizedSession

    #endregion Optimized Session Implementation

    #region Background Service

    /// <summary>
    /// Background service for session maintenance and cleanup
    /// </summary>
    public class SessionBackgroundService : IHostedService, IDisposable
    {
        private readonly SessionOptions _options;
        private readonly DataAccess<AppSessionData> _dataAccess;
        private readonly ConcurrentDictionary<string, OptimizedSession> _activeSessions;
        private Timer? _cleanupTimer;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;
        private bool _isStarted = false;
        private readonly object _startLock = new object();

        /// <summary>
        /// The statistics for the session manager
        /// </summary>
        public SessionStatistics Statistics { get; } = new SessionStatistics();

        /// <summary>
        /// Initialize the background service with session options
        /// </summary>
        /// <param name="options">Any session options you want configured</param>
        public SessionBackgroundService(SessionOptions options)
        {
            _options = options;
            _dataAccess = new DataAccess<AppSessionData>(options.AccountName!, options.AccountKey!);
            _activeSessions = new ConcurrentDictionary<string, OptimizedSession>();
        }

        /// <summary>
        /// Initialize the background service
        /// </summary>
        /// <param name="cancellationToken">The cancellation token</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
           lock (_startLock)
            {
                if (_isStarted)
                    return Task.CompletedTask;

                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Statistics.StartTime = DateTime.UtcNow;

                // Setup cleanup timer with delayed start to prevent race conditions
                if (_options.EnableAutoCleanup)
                {
                    _cleanupTimer = new Timer(
                        async _ => await RunCleanupAsync(),
                        null,
                        _options.CleanupStartDelay,  // Delay first run
                        _options.CleanupInterval
                    );
                }

                _isStarted = true;
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets the cancellation token for the background service
        /// </summary>
        /// <param name="cancellationToken">The cancellation token</param>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Flush all active sessions
            var flushTasks = _activeSessions.Values.Select(s => s.FlushAsync());
            await Task.WhenAll(flushTasks);

            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Register an active session
        /// </summary>
        internal void RegisterSession(OptimizedSession session)
        {
            _activeSessions.TryAdd(session.SessionId, session);
            Statistics.ActiveSessions = _activeSessions.Count;
        }

        /// <summary>
        /// Unregister a session
        /// </summary>
        internal void UnregisterSession(OptimizedSession session)
        {
            _activeSessions.TryRemove(session.SessionId, out _);
            Statistics.ActiveSessions = _activeSessions.Count;
        }

        /// <summary>
        /// Run cleanup of old session data
        /// </summary>
        private async Task RunCleanupAsync()
        {
            // Prevent concurrent cleanup runs
            if (_cancellationTokenSource?.IsCancellationRequested == true)
                return;

            try
            {
                var cutoffTime = DateTime.UtcNow.Subtract(_options.StaleDataCleanupAge);

                // Get all session data from the table
                var allSessionData = await _dataAccess.GetAllTableDataAsync();

                // Group by SessionID to identify complete sessions
                var sessionGroups = allSessionData.GroupBy(s => s.SessionID);

                var sessionsToDelete = new List<string>();
                var rowsToDelete = new List<AppSessionData>();

                foreach (var sessionGroup in sessionGroups)
                {
                    // Check if ANY part of this session is still fresh
                    var hasFreshData = sessionGroup.Any(s => s.Timestamp >= cutoffTime);

                    if (!hasFreshData)
                    {
                        // All data in this session is stale, mark entire session for deletion
                        sessionsToDelete.Add(sessionGroup.Key!);
                        rowsToDelete.AddRange(sessionGroup);
                    }
                    // If any part is fresh, keep the entire session (do nothing)
                }

                // Batch delete old sessions
                if (rowsToDelete.Any())
                {
                    var result = await _dataAccess.BatchUpdateListAsync(
                        rowsToDelete,
                        TableOperationType.Delete
                    );

                    Statistics.SessionsDeleted += sessionsToDelete.Count;
                    Statistics.LastCleanup = DateTime.UtcNow;

                    Debug.WriteLine($"Cleaned up {sessionsToDelete.Count} complete sessions ({result.SuccessfulItems} rows)");
                }

                // Also check for stale sessions that should be submitted to CRM
                await ProcessStaleSessions();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Session cleanup failed: {ex.Message}");
            }
        } // end RunCleanupAsync()

        /// <summary>
        /// Process stale sessions (for CRM submission etc.)
        /// </summary>
        private async Task ProcessStaleSessions()
        {
            try
            {
                // This replicates the original Session.GetStaleSessions logic
                string filter = @"(Key eq 'SessionSubmittedToCRM' and Value eq 'false') or (Key eq 'prospectChannel' and Value eq 'facebook')";
                var q = new TableQuery<AppSessionData>().Where(filter);

                var coll = await _dataAccess.GetCollectionAsync(q);

                // Convert to UTC for comparison
                var utcNow = DateTime.UtcNow;
                var staleSessions = coll
                    .Where(s => s.Timestamp.IsTimeBetween(utcNow, 5, 60))
                    .GroupBy(s => s.SessionID)
                    .Select(g => g.Key)
                    .ToList();

                Statistics.StaleSessions = staleSessions.Count;

                // Could trigger events or callbacks here for stale session processing
                if (staleSessions.Count > 0)
                {
                    Debug.WriteLine($"Found {staleSessions.Count} stale sessions for CRM processing");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProcessStaleSessions failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Properly dispose of resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cleanupTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    } // end class SessionBackgroundService

    #endregion Background Service

    #region Statistics

    /// <summary>
    /// Session statistics for monitoring
    /// </summary>
    public class SessionStatistics
    {
        /// <summary>
        /// The number of active sessions
        /// </summary>
        public int ActiveSessions { get; set; }
        /// <summary>
        /// The total number of reads and writes performed
        /// </summary>
        public long TotalReads { get; set; }
        /// <summary>
        /// The total number of successful reads
        /// </summary>
        public long TotalWrites { get; set; }
        /// <summary>
        /// The total number of writes that were successful
        /// </summary>
        public long SuccessfulWrites { get; set; }
        /// <summary>
        /// The total number of writes that failed
        /// </summary>
        public long FailedWrites { get; set; }
        /// <summary>
        /// The total number of sessions deleted
        /// </summary>
        public int SessionsDeleted { get; set; }
        /// <summary>
        /// The number of expired sessions
        /// </summary>
        public int ExpiredSessions { get; set; }
        /// <summary>
        /// The number of stale sessions that were found
        /// </summary>
        public int StaleSessions { get; set; }
        /// <summary>
        /// The time when the session manager started
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// The last time the session manager was active
        /// </summary>
        public DateTime LastActivity { get; set; }
        /// <summary>
        /// The last time the session data was flushed to Azure
        /// </summary>
        public DateTime LastFlush { get; set; }
        /// <summary>
        /// The last time the cleanup process ran
        /// </summary>
        public DateTime LastCleanup { get; set; }
        /// <summary>
        /// The average write latency for writes to Azure
        /// </summary>
        public TimeSpan AverageWriteLatency { get; set; }
        /// <summary>
        /// The percentage of successful writes compared to total writes
        /// </summary>
        public double WriteSuccessRate => TotalWrites > 0
            ? (double)SuccessfulWrites / TotalWrites * 100
            : 0;
        /// <summary>
        /// The total uptime of the session manager
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - StartTime;
    } // end class SessionStatistics

    #endregion Statistics

    #region Extension Methods

    /// <summary>
    /// Extension methods for dependency injection
    /// </summary>
    public static class SessionExtensions
    {
        /// <summary>
        /// Add Azure Table sessions to services (ASP.NET Core)
        /// </summary>
        /// <param name="services">The managed service collection</param>
        /// <param name="accountName">Name of the database in Azure Tables</param>
        /// <param name="accountKey">Key credential of the database</param>
        /// <param name="configure">Session Options</param>
        public static IServiceCollection AddAzureTableSessions(this IServiceCollection services, string accountName, string accountKey, Action<SessionOptions>? configure = null)
        {
            var options = new SessionOptions
            {
                AccountName = accountName,
                AccountKey = accountKey,
                IdStrategy = SessionIdStrategy.HttpContext
            };

            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddHostedService<SessionBackgroundService>();

            // Initialize SessionManager
            SessionManager.Initialize(accountName, accountKey, configure);

            return services;
        }

        /// <summary>
        /// Add Azure Table sessions with configuration
        /// </summary>
        /// <param name="services">The managed service collection</param>
        /// <param name="configure">Session Options</param>
        /// <exception cref="ArgumentException"></exception>
        public static IServiceCollection AddAzureTableSessions(this IServiceCollection services, Action<SessionOptions> configure)
        {
            var options = new SessionOptions();
            configure(options);

            if (!options.IsValid())
            {
                throw new ArgumentException("Session options must include AccountName and AccountKey");
            }

            return services.AddAzureTableSessions(options.AccountName!, options.AccountKey!, configure);
        }

        /// <summary>
        /// Add Azure Table sessions to host builder
        /// </summary>
        /// <param name="builder">Builder object from application satup</param>
        /// <param name="accountName">Name of the database in Azure Tables</param>
        /// <param name="accountKey">Key credential of the database</param>
        /// <param name="configure">Session Options</param>
        public static IHostBuilder AddAzureTableSessions(this IHostBuilder builder, string accountName, string accountKey, Action<SessionOptions>? configure = null)        
            => builder.ConfigureServices((context, services) =>
            {
                services.AddAzureTableSessions(accountName, accountKey, configure);
            });

    } // end class SessionExtensions

    #endregion Extension Methods

    #region Backwards Compatibility Bridge

    /// <summary>
    /// Bridge to maintain compatibility with existing Session class usage
    /// </summary>
    public class SessionCompatibilityBridge : Session
    {
        private readonly Session _modernSession;

        /// <summary>
        /// Allows for compatibility with existing Session usage
        /// </summary>
        /// <param name="accountName">The Azure Table Storage account name to access the table store</param>
        /// <param name="accountKey">The Azure Table Storage account key to access the table store</param>
        /// <param name="sessionId">The unique identifier for the session</param>
        public SessionCompatibilityBridge(string accountName, string accountKey, string sessionId)
            : base(accountName, accountKey, sessionId)
        {
            // Initialize modern session if not already done
            if (!SessionManager._initialized)
            {
                SessionManager.Initialize(accountName, accountKey, options =>
                {
                    options.IdStrategy = SessionIdStrategy.Custom;
                    options.CustomIdProvider = () => sessionId;
                });
            }
            _modernSession = SessionManager.Current; // Now this works since both are Session
        }
    }

    #endregion Backwards Compatibility Bridge
}