using ASCTableStorage.Common;
using ASCTableStorage.Data;
using ASCTableStorage.Logging;
using ASCTableStorage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace ASCTableStorage.Sessions
{
    #region Session Configuration

    /// <summary>
    /// Hosted Service to allow for proper initialization of the HttpContextAccessor for web sessions
    /// </summary>
    public class SessionManagerInitializerService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        /// <summary>
        /// Constructs the Service provider for HttpContext
        /// </summary>
        /// <param name="serviceProvider">The service provider</param>
        public SessionManagerInitializerService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Starts up the service
        /// </summary>
        /// <param name="cancellationToken">Allows graceful shutdown</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Resolve dependencies *after* the service provider is built
            var options = _serviceProvider.GetRequiredService<SessionOptions>();
            var httpContextAccessor = _serviceProvider.GetService<IHttpContextAccessor>(); // Gets the real instance

            // Pass the resolved accessor to the options
            options.ContextAccessor = httpContextAccessor!; // Use your property name

            // Now initialize SessionManager with the accessor available in options
            SessionManager.Initialize(options.AccountName!, options.AccountKey!, opt =>
            {
                // Copy properties or pass options directly if signature allows
                opt.ContextAccessor = options.ContextAccessor; // Ensure it's passed through
                                                               // ... other options
            });

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gracefully stops the instance when needed
        /// </summary>
        /// <param name="cancellationToken">The stop token</param>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Configuration options for Azure Table-based session management
    /// </summary>
    public class SessionOptions
    {
        /// <summary>
        /// Provides a HTTP Context for web applications
        /// </summary>
        public IHttpContextAccessor ContextAccessor { get; set; }
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
        /// Auto-commit interval (set to TimeSpan.Zero to disable, default is 500 milliseconds or half a second).
        /// Determines how quickly session will auto update to the data store.
        /// </summary>
        public TimeSpan AutoCommitInterval { get; set; } = TimeSpan.FromMilliseconds(500);

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
        private const string SessionIdName = "SESSION_MANAGER_ID";
        // Fields for cross-platform session ID handling
        private static IHttpContextAccessor? _httpContextAccessor;
        private const string AspNetSessionCookieName = ".AspNetCore.Session"; // Standard ASP.NET Core session cookie name
        private static readonly ConcurrentDictionary<string, Session> _multiSessions = new();
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
        private static readonly Lazy<ILogger> logger = new Lazy<ILogger>(() =>
        {
            // This code inside the lambda ONLY runs the first time _logger?.Value is accessed
            if (RemoteLogger.Logger != null)
                return RemoteLogger.Logger.CreateLogger("SessionManager");

            return null!;
        });
        private static ILogger _logger = logger.Value;

        /// <summary>
        /// Gets the current session instance (implements ISession)
        /// </summary>
        public static Session Current
        {
            get
            {
                if (_logger == null)
                    _logger = RemoteLogger.Logger.CreateLogger("SessionManager"); //instatiation timing issue

                if (!_initialized)
                {
                    _logger?.LogError("SessionManager accessed before initialization. Call Initialize() first.");
                    throw new InvalidOperationException(
                        "SessionManager has not been initialized. Call Initialize() first.");
                }

                if (_currentSession == null)
                {
                    lock (_lock)
                    {
                        if (_currentSession == null)
                        {
                            string sessionId = _sessionId ?? _customIdProvider?.Invoke() ?? LocalSessionID!;
                            if (_currentSession == null && !string.IsNullOrEmpty(sessionId))
                            {
                                _currentSession = new Session(_accountName!, _accountKey!, sessionId);
                                _logger?.LogInformation("New session created successfully. Session ID: {SessionId}", sessionId);
                            }
                            if (_currentSession == null)
                            {
                                string err = "No Session ID was able to be created in the SessionManager.Current property getter.";
                                _logger?.LogError(err);
                                throw new ArgumentNullException("SessionID", err);
                            }
                        }
                    }
                }

                return _currentSession;
            }
        }

        /// <summary>
        /// Gets or creates a session with the specified ID
        /// </summary>
        public static Session GetSession(string sessionId)
        {
            if (!_initialized)
                throw new InvalidOperationException("SessionManager not initialized");

            return _multiSessions.GetOrAdd(sessionId, id =>
            {
                _logger?.LogInformation("Creating new multi-session: {SessionId}", id);
                return new Session(_accountName!, _accountKey!, id);
            });
        }

        /// <summary>
        /// Initialize session manager with explicit credentials
        /// </summary>
        /// <param name="accountName">Azure Table Storage Database name</param>
        /// <param name="accountKey">Azure Table Storage Database key</param>
        /// <param name="configure">The options that determine configurations</param>
        public static void Initialize(string accountName, string accountKey, Action<SessionOptions>? configure = null)
        {
            _logger?.LogInformation("Initializing SessionManager with account: {AccountName}", accountName);

            lock (_lock)
            {
                if (_initialized)
                {
                    _logger?.LogWarning("SessionManager already initialized. Performing shutdown before re-initialization.");
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

                // Capture IHttpContextAccessor from options for web integration
                _httpContextAccessor = options.ContextAccessor;
                if (_httpContextAccessor != null)
                    _logger?.LogInformation("IHttpContextAccessor provided and configured for web integration.");                
                else                
                    _logger?.LogDebug("No IHttpContextAccessor provided. Web-specific features will use fallbacks.");
                

                _logger?.LogDebug("Session options configured. SessionIdStrategy: {Strategy}, CustomIdProvider: {HasCustomProvider}, SessionId: {ExplicitSessionId}",
                    options.IdStrategy,
                    options.CustomIdProvider != null,
                    options.SessionId);

                if (options.IdStrategy == SessionIdStrategy.Custom && options.CustomIdProvider != null)
                {
                    _customIdProvider = options.CustomIdProvider;
                    _logger?.LogInformation("Custom session ID provider configured");
                }
                else if (!string.IsNullOrEmpty(options.SessionId))
                {
                    _sessionId = options.SessionId;
                    _logger?.LogInformation("Explicit session ID configured: {SessionId}", options.SessionId);
                }

                // Initialize background service if cleanup is enabled
                if (options.EnableAutoCleanup)
                {
                    _logger?.LogInformation("Initializing background cleanup service");
                    _backgroundService = new SessionBackgroundService(options);
                    // Start the background service with the cancellation token
                    _backgroundService.StartAsync(_shutdownTokenSource.Token).GetAwaiter().GetResult();
                    _logger?.LogInformation("Background cleanup service started");
                }

                // Setup auto-commit timer if enabled
                if (options.AutoCommitInterval > TimeSpan.Zero)
                {
                    _logger?.LogInformation("Setting up auto-commit timer with interval: {Interval}", options.AutoCommitInterval);
                    _autoCommitTimer = new Timer(
                        AutoCommitCallback,
                        null,
                        options.AutoCommitInterval,
                        options.AutoCommitInterval);
                    _logger?.LogInformation("Auto-commit timer configured successfully");
                }

                _initialized = true;
                _logger?.LogInformation("SessionManager initialization completed successfully");

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
        {
            _logger?.LogInformation("Initializing SessionManager with explicit session ID: {SessionId}", sessionId);
            Initialize(accountName, accountKey, options => options.SessionId = sessionId);
        }

        /// <summary>
        /// Register all shutdown handlers for different scenarios
        /// </summary>
        private static void RegisterShutdownHandlers()
        {
            _logger?.LogInformation("Registering shutdown handlers");

            // 1. Normal app domain shutdown
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            _logger?.LogDebug("ProcessExit handler registered");

            // 2. Unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            _logger?.LogDebug("UnhandledException handler registered");

            // 3. Assembly unload (for .NET Core)
            try
            {
                AssemblyLoadContext.Default.Unloading += OnAssemblyUnloading;
                _logger?.LogDebug("AssemblyUnloading handler registered");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not register AssemblyUnloading handler (likely not .NET Core)");
            }

            // 4. Console cancel (Ctrl+C)
            Console.CancelKeyPress += OnConsoleCancelKeyPress;
            _logger?.LogDebug("ConsoleCancelKeyPress handler registered");

            _logger?.LogInformation("All shutdown handlers registered successfully");
        }

        /// <summary>
        /// Register for graceful shutdown with a cancellation token (for ASP.NET Core apps)
        /// Call this from Program.cs with IHostApplicationLifetime.ApplicationStopping token
        /// </summary>
        /// <param name="cancellationToken">The cancellation token that sparks or requests shutdown</param>
        public static void RegisterForGracefulShutdown(CancellationToken cancellationToken)
        {
            _logger?.LogInformation("Registering for graceful shutdown");

            cancellationToken.Register(() =>
            {
                try
                {
                    _logger?.LogInformation("Graceful shutdown initiated via cancellation token");
                    CommitAndShutdown("Application stopping via cancellation token");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during graceful shutdown registration callback");
                }
            });
        }

        /// <summary>
        /// Auto-commit callback for timer
        /// </summary>
        /// <param name="state">The state of the session</param>
        private static void AutoCommitCallback(object? state)
        {
            _logger?.LogDebug("Auto-commit timer callback triggered");

            try
            {
                var sessionsToCommit = new List<Session>();

                // Check current session
                if (_currentSession != null && !_currentSession.DataHasBeenCommitted)
                {
                    sessionsToCommit.Add(_currentSession);
                    _logger?.LogDebug("Current session needs commit: {SessionId}", _currentSession.SessionID);
                }

                // Check multi-sessions
                foreach (var kvp in _multiSessions)
                {
                    if (!kvp.Value.DataHasBeenCommitted)
                    {
                        sessionsToCommit.Add(kvp.Value);
                        _logger?.LogDebug("Multi-session needs commit: {SessionId}", kvp.Key);
                    }
                }

                // Commit all dirty sessions
                if (sessionsToCommit.Count > 0)
                {
                    _logger?.LogDebug("Auto-committing {Count} dirty sessions", sessionsToCommit.Count);

                    Parallel.ForEach(sessionsToCommit, session =>
                    {
                        try
                        {
                            session.CommitData();
                            _logger?.LogDebug("Auto-committed session: {SessionId}", session.SessionID);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to auto-commit session: {SessionId}", session.SessionID);
                        }
                    });
                }
                else
                {
                    _logger?.LogDebug("No dirty sessions to auto-commit");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in auto-commit callback");
            }
        } // end AutoCommitCallback()

        /// <summary>
        /// Handle process exit
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnProcessExit(object? sender, EventArgs e)
        {
            _logger?.LogInformation("ProcessExit event received");
            CommitAndShutdown("Process exit");
        }

        /// <summary>
        /// Handle unhandled exceptions
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            _logger?.LogWarning("UnhandledException event received. IsTerminating: {IsTerminating}", e.IsTerminating);
            CommitAndShutdown($"Unhandled exception (terminating: {e.IsTerminating})");
        }

        /// <summary>
        /// Handle assembly unloading
        /// </summary>
        /// <param name="context">Assembly context</param>
        private static void OnAssemblyUnloading(AssemblyLoadContext context)
        {
            _logger?.LogInformation("AssemblyUnloading event received");
            CommitAndShutdown("Assembly unloading");
        }


        /// <summary>
        /// Handle console cancel key press (Ctrl+C)
        /// </summary>
        /// <param name="sender">The sender of the process command</param>
        /// <param name="e">Arguments to use if any. Ignored here</param>
        private static void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            _logger?.LogInformation("ConsoleCancelKeyPress event received. Cancel: {Cancel}", e.Cancel);
            CommitAndShutdown("Console cancel");
            e.Cancel = false; // Allow the process to terminate
        }

        /// <summary>
        /// Common method to commit and shutdown
        /// </summary>
        private static void CommitAndShutdown(string reason)
        {
            _logger?.LogInformation("CommitAndShutdown initiated. Reason: {Reason}", reason);

            try
            {
                lock (_lock)
                {
                    // Commit current session
                    if (_currentSession != null && !_currentSession.DataHasBeenCommitted)
                    {
                        var sessionId = _currentSession.SessionID;
                        _logger?.LogInformation("Committing current session during shutdown. Session ID: {SessionId}", sessionId);
                        _currentSession.CommitData();
                        _logger?.LogDebug("Current session committed. Session ID: {SessionId}", sessionId);
                    }

                    // Commit multi-sessions
                    foreach (var kvp in _multiSessions)
                    {
                        if (!kvp.Value.DataHasBeenCommitted)
                        {
                            _logger?.LogInformation("Committing multi-session during shutdown. Session ID: {SessionId}", kvp.Key);
                            kvp.Value.CommitData();
                        }
                    }
                }

                Shutdown();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during shutdown commit process. Reason: {Reason}", reason);
            }
        }

        /// <summary>
        /// Shutdown and flush all pending session data
        /// </summary>
        public static void Shutdown()
        {
            _logger?.LogInformation("SessionManager shutdown initiated");

            lock (_lock)
            {
                try
                {
                    if (_shutdownTokenSource != null)
                    {
                        _logger?.LogDebug("Cancelling shutdown token source");
                        _shutdownTokenSource.Cancel();
                    }

                    // Stop background service if running
                    if (_backgroundService != null)
                    {
                        _logger?.LogInformation("Stopping background service");
                        _backgroundService.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                        _backgroundService.Dispose();
                        _backgroundService = null;
                        _logger?.LogInformation("Background service stopped and disposed");
                    }

                    if (_shutdownTokenSource != null)
                    {
                        _logger?.LogDebug("Disposing shutdown token source");
                        _shutdownTokenSource.Dispose();
                        _shutdownTokenSource = null;
                    }

                    if (_autoCommitTimer != null)
                    {
                        _logger?.LogDebug("Disposing auto-commit timer");
                        _autoCommitTimer.Dispose();
                        _autoCommitTimer = null;
                    }

                    // Dispose current session
                    if (_currentSession != null)
                    {
                        var sessionId = _currentSession.SessionID;
                        _logger?.LogInformation("Committing and disposing current session. Session ID: {SessionId}", sessionId);

                        // ALWAYS commit on shutdown
                        _currentSession.CommitData();
                        _logger?.LogDebug("Session data committed during shutdown. Session ID: {SessionId}", sessionId);

                        _currentSession.Dispose();
                        _logger?.LogInformation("Current session disposed. Session ID: {SessionId}", sessionId);
                        _currentSession = null;
                    }

                    // Dispose multi-sessions
                    foreach (var kvp in _multiSessions)
                    {
                        _logger?.LogInformation("Disposing multi-session. Session ID: {SessionId}", kvp.Key);
                        kvp.Value.CommitData();
                        kvp.Value.Dispose();
                    }
                    _multiSessions.Clear();
                }
                finally
                {
                    _initialized = false;
                    _logger?.LogDebug("SessionManager initialized flag set to false");

                    // Unregister handlers
                    _logger?.LogDebug("Unregistering shutdown handlers");
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                    Console.CancelKeyPress -= OnConsoleCancelKeyPress;

                    try
                    {
                        AssemblyLoadContext.Default.Unloading -= OnAssemblyUnloading;
                        _logger?.LogDebug("AssemblyUnloading handler unregistered");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Could not unregister AssemblyUnloading handler");
                    }

                    _logger?.LogInformation("SessionManager shutdown completed successfully");
                }
            }
        } // end ShutDown()

        /// <summary>
        /// Async shutdown
        /// </summary>
        public static async Task ShutdownAsync()
        {
            _logger?.LogInformation("SessionManager async shutdown initiated");

            try
            {
                if (_shutdownTokenSource != null)
                {
                    _logger?.LogDebug("Cancelling shutdown token source");
                    _shutdownTokenSource.Cancel();
                }

                if (_backgroundService != null)
                {
                    _logger?.LogInformation("Stopping background service asynchronously");
                    await _backgroundService.StopAsync(CancellationToken.None);
                    _logger?.LogInformation("Background service stopped asynchronously");
                }

                if (_currentSession != null && !_currentSession.DataHasBeenCommitted)
                {
                    var sessionId = _currentSession.SessionID;
                    _logger?.LogInformation("Committing session data asynchronously during shutdown. Session ID: {SessionId}", sessionId);
                    await _currentSession.CommitDataAsync();
                    _logger?.LogInformation("Session data committed asynchronously during shutdown. Session ID: {SessionId}", sessionId);
                }

                // Commit multi-sessions
                var tasks = _multiSessions
                    .Where(kvp => !kvp.Value.DataHasBeenCommitted)
                    .Select(kvp => kvp.Value.CommitDataAsync());
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during async shutdown process");
            }
            finally
            {
                Shutdown();
            }
        } // end ShutDownAsync()

        /// <summary>
        /// Commits data from the current session
        /// </summary>
        public static bool CommitData()
        {
            _logger?.LogDebug("CommitData called");

            if (_currentSession != null)
            {
                var sessionId = _currentSession.SessionID;
                _logger?.LogInformation("Committing current session data. Session ID: {SessionId}", sessionId);

                var result = _currentSession.CommitData();

                if (result)
                {
                    _logger?.LogInformation("Session data committed successfully. Session ID: {SessionId}", sessionId);
                }
                else
                {
                    _logger?.LogWarning("Session data commit returned false. Session ID: {SessionId}", sessionId);
                }

                return result;
            }

            _logger?.LogWarning("Attempted to commit data but no current session exists");
            return false;
        }

        /// <summary>
        /// Async commit data from the current session
        /// </summary>
        public static async Task<bool> CommitDataAsync()
        {
            _logger?.LogDebug("CommitDataAsync called");

            if (_currentSession != null)
            {
                var sessionId = _currentSession.SessionID;
                _logger?.LogInformation("Committing current session data asynchronously. Session ID: {SessionId}", sessionId);

                var result = await _currentSession.CommitDataAsync();

                if (result)
                {
                    _logger?.LogInformation("Session data committed asynchronously successfully. Session ID: {SessionId}", sessionId);
                }
                else
                {
                    _logger?.LogWarning("Session data async commit returned false. Session ID: {SessionId}", sessionId);
                }

                return result;
            }

            _logger?.LogWarning("Attempted to commit data asynchronously but no current session exists");
            return false;
        }

        /// <summary>
        /// Generate a default session ID based on context
        /// </summary>
        private static string GetDefaultSessionId()
        {
            _logger?.LogDebug("Retrieving default session ID from environment or generating fallback");

            var stored = Environment.GetEnvironmentVariable(SessionIdName);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                _logger?.LogInformation("Retrieved stored session ID from environment: {SessionId}", stored);
                return stored;
            }

            string sessionId;

            if (Environment.UserInteractive)
            {
                sessionId = $"{Environment.UserName}_{Environment.MachineName}".Replace("\\", "_");
                _logger?.LogInformation("Generated default session ID for desktop app. User: {User}, Machine: {Machine}, Session ID: {SessionId}",
                    Environment.UserName, Environment.MachineName, sessionId);
            }
            else
            {
                sessionId = $"{Environment.MachineName}_{Process.GetCurrentProcess().Id}";
                _logger?.LogInformation("Generated default session ID for service. Machine: {Machine}, Process ID: {ProcessId}, Session ID: {SessionId}",
                    Environment.MachineName, Process.GetCurrentProcess().Id, sessionId);
            }

            // Store it for future retrieval
            SetDefaultSessionID(sessionId);
            return sessionId;
        }

        /// <summary>
        /// Force flush all pending session data
        /// </summary>
        public static async Task FlushAsync()
        {
            var tasks = new List<Task<bool>>();

            if (_currentSession != null && !_currentSession.DataHasBeenCommitted)
                tasks.Add(_currentSession.CommitDataAsync());

            tasks.AddRange(_multiSessions
                .Where(kvp => !kvp.Value.DataHasBeenCommitted)
                .Select(kvp => kvp.Value.CommitDataAsync()));

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Get session statistics from the background service
        /// </summary>
        public static SessionStatistics? GetStatistics()
        {
            _logger?.LogDebug("GetStatistics called");

            if (_backgroundService != null)
            {
                _logger?.LogDebug("Returning statistics from background service");
                return _backgroundService.Statistics;
            }

            _logger?.LogDebug("No background service available for statistics");
            return null;
        }

        /// <summary>
        /// Gets or sets the local session ID using platform-appropriate storage mechanisms.
        /// Automatically detects the application context and uses the appropriate storage method.
        /// </summary>
        /// <remarks>
        /// Storage mechanisms by platform:
        /// - Web Applications: HTTP Cookies
        /// - Desktop Applications: Application Settings/Registry
        /// - Mobile Applications: Platform-specific secure storage
        /// - Console/Service Applications: Environment variables or temporary files
        /// 
        /// Note: This property requires platform-specific implementation and may need 
        /// additional dependencies for full cross-platform support.
        /// </remarks>
        public static string? LocalSessionID
        {
            get
            {
                try
                {
                    _logger?.LogDebug("Getting LocalSessionID via LocalSessionID from platform-appropriate storage and setting up the apps Session data.");
                    var platform = DetectAppPlatformAsync().GetAwaiter().GetResult();
                    string? ret = platform switch
                    {
                        AppPlatform.Web => GetWebSessionID(),
                        AppPlatform.Desktop => GetDesktopSessionID(),
                        AppPlatform.Mobile => GetMobileSessionID(),
                        _ => GetDefaultSessionId()
                    };
                    // Create a temporary session to check if the session exists and is valid
                    if (!string.IsNullOrEmpty(ret))
                    {
                        var tempSession = new Session(_accountName!, _accountKey!, ret);

                        // If we successfully created the session and it has data, use it
                        if (tempSession.SessionData != null)
                        {
                            lock (_lock)
                            {
                                // Dispose of the current session if it exists
                                _currentSession?.Dispose();

                                // Set the current session to the retrieved session
                                _currentSession = tempSession;
                                _sessionId = ret;
                            }
                        }
                    }
                    return ret;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error retrieving LocalSessionID from storage");
                    return null;
                }
            }
            set
            {
                try
                {
                    _logger?.LogInformation("Setting LocalSessionID to: {SessionId}", value);
                    var platform = DetectAppPlatformAsync().GetAwaiter().GetResult();

                    switch (platform)
                    {
                        case AppPlatform.Web:
                            SetWebSessionID(value);
                            break;
                        case AppPlatform.Desktop:
                            SetDesktopSessionID(value);
                            break;
                        case AppPlatform.Mobile:
                            SetMobileSessionID(value);
                            break;
                        default:
                            SetDefaultSessionID(value);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error setting LocalSessionID to storage. Value: {SessionId}", value);
                    throw new InvalidOperationException($"Failed to store LocalSessionID: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Determine the platform being used by the app user
        /// </summary>
        public static async Task<AppPlatform> DetectAppPlatformAsync()
        {
            var checks = new Dictionary<Task<bool>, AppPlatform>
            {
                [Task.Run(() => IsWebApplication())] = AppPlatform.Web,
                [Task.Run(() => IsDesktopApplication())] = AppPlatform.Desktop,
                [Task.Run(() => IsMobileApplication())] = AppPlatform.Mobile
            };

            var completed = await Task.WhenAny(checks.Keys);
            return await completed ? checks[completed] : AppPlatform.Unknown;
        }

        /// <summary>
        /// The platform the user is using the application on
        /// </summary>
        public enum AppPlatform
        {
            /// <summary>
            /// Web app
            /// </summary>
            Web,
            /// <summary>
            /// Desktop App
            /// </summary>
            Desktop,
            /// <summary>
            /// Mobile App
            /// </summary>
            Mobile,
            /// <summary>
            /// Platform could not easily be determined
            /// </summary>
            Unknown
        }

        #region Platform Detection Methods

        /// <summary>
        /// Determines if the current application is a web application
        /// </summary>
        private static bool IsWebApplication()
        {
            try
            {
                // Check for common web application indicators
                var httpContextType = Type.GetType("System.Web.HttpContext, System.Web");
                var httpContextCurrent = httpContextType?.GetProperty("Current")?.GetValue(null);

                // Also check for ASP.NET Core
                var httpContextAccessor = Type.GetType("Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.Abstractions");

                var isWeb = httpContextCurrent != null || httpContextAccessor != null;
                _logger?.LogDebug("Platform detection - IsWebApplication: {IsWeb}", isWeb);
                return isWeb;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error detecting web application");
                return false;
            }
        }

        /// <summary>
        /// Determines if the current application is a desktop application
        /// </summary>
        private static bool IsDesktopApplication()
        {
            var isDesktop = Environment.UserInteractive && !IsWebApplication();
            _logger?.LogDebug("Platform detection - IsDesktopApplication: {IsDesktop}", isDesktop);
            return isDesktop;
        }

        /// <summary>
        /// Determines if the current application is a mobile application
        /// </summary>
        private static bool IsMobileApplication()
        {
            try
            {
                // Check for Xamarin/MAUI indicators
                var xamarinFormsApp = Type.GetType("Xamarin.Forms.Application, Xamarin.Forms.Core");
                var mauiApp = Type.GetType("Microsoft.Maui.Controls.Application, Microsoft.Maui.Controls");

                var isMobile = xamarinFormsApp != null || mauiApp != null;
                _logger?.LogDebug("Platform detection - IsMobileApplication: {IsMobile}", isMobile);
                return isMobile;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error detecting mobile application");
                return false;
            }
        }

        #endregion Platform Detection Methods

        #region Platform-Specific Storage Methods

        /// <summary>
        /// A default fallback SessionID location on the local store if available
        /// </summary>
        public static string GetFallBackSessionID()
            => Environment.GetEnvironmentVariable(SessionIdName) ?? GetDefaultSessionId(); // Fallback cause nothing was ever set        

        /// <summary>
        /// Retrieves session ID from web application cookies
        /// </summary>
        public static string? GetWebSessionID()
        {
            var cookies = _httpContextAccessor?.HttpContext?.Request?.Cookies;
            if (cookies == null)
                return GetFallBackSessionID();

            foreach (var name in new[] { "sessionid", "sessionId", AspNetSessionCookieName })
            {
                if (cookies.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                {
                    _sessionId = value;
                    break;
                }
            }

            if (string.IsNullOrEmpty(_sessionId))
                _sessionId = GetFallBackSessionID();

            SetWebSessionID(_sessionId);
            return _sessionId;
        }

        /// <summary>
        /// Stores session ID to web application cookies
        /// </summary>
        public static void SetWebSessionID(string? sessionId)
        {
            if (_httpContextAccessor?.HttpContext?.Response?.Cookies is not { } cookies || string.IsNullOrEmpty(sessionId))
            {
                SetDefaultSessionID(sessionId);
                return;
            }

            cookies.Append(SessionIdName, sessionId, new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddHours(24),
                HttpOnly = true,
                IsEssential = true,
                Path = "/", // Ensures it's available across the app
                SameSite = SameSiteMode.Lax // Or None if needed
            });
        }


        /// <summary>
        /// Retrieves session ID from desktop application storage
        /// </summary>
        private static string? GetDesktopSessionID()
        {
            // Try file-based storage first, fallback to environment variable
            try
            {
                var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "session.id");
                if (File.Exists(filePath)) 
                    _sessionId = File.ReadAllText(filePath).Trim();
            }
            catch { /* Intentionally ignored */ }

            if (string.IsNullOrEmpty(_sessionId))
                _sessionId = GetFallBackSessionID();

            SetDesktopSessionID(_sessionId);
            return _sessionId;
        }

        /// <summary>
        /// Stores session ID to desktop application storage
        /// </summary>
        private static void SetDesktopSessionID(string? sessionId)
        {
            // Store to both file and environment variable
            try
            {
                var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
                Directory.CreateDirectory(appDir);
                var filePath = Path.Combine(appDir, "session.id");

                if (sessionId != null) File.WriteAllText(filePath, sessionId);
                else if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch { /* File storage failed, continue with environment variable */ }
        }

        /// <summary>
        /// Retrieves session ID from mobile application storage
        /// </summary>
        private static string? GetMobileSessionID()
        {
            // Try Essentials/Preferences via reflection, fallback to environment variable
            try
            {
                var essentials = Type.GetType("Xamarin.Essentials.Preferences, Xamarin.Essentials") ??
                                Type.GetType("CommunityToolkit.Maui.Preferences, CommunityToolkit.Maui");
                var method = essentials?.GetMethod("Get", new[] { typeof(string), typeof(string) });
                var result = method?.Invoke(null, new object[] { "SessionID", "" }) as string;
                if (!string.IsNullOrEmpty(result)) 
                    _sessionId = result;
            }
            catch { /* Intentionally ignored */ }

            if (string.IsNullOrEmpty(_sessionId))
                _sessionId = GetFallBackSessionID();

            SetMobileSessionID(_sessionId);
            return _sessionId;
        }

        /// <summary>
        /// Stores session ID to mobile application storage
        /// </summary>
        private static void SetMobileSessionID(string? sessionId)
        {
            // Try Essentials/Preferences via reflection, fallback to environment variable
            try
            {
                var essentials = Type.GetType("Xamarin.Essentials.Preferences, Xamarin.Essentials") ??
                                Type.GetType("CommunityToolkit.Maui.Preferences, CommunityToolkit.Maui");
                MethodInfo method;

                if (sessionId != null)
                {
                    method = essentials?.GetMethod("Set", new[] { typeof(string), typeof(string) })!;
                    method?.Invoke(null, new object[] { "SessionID", sessionId });
                }
                else
                {
                    method = essentials?.GetMethod("Remove", new[] { typeof(string) })!;
                    method?.Invoke(null, new object[] { "SessionID" });
                }
            }
            catch { /* Intentionally ignored */ }
        }

        /// <summary>
        /// Stores session ID to generic application storage
        /// </summary>
        private static void SetDefaultSessionID(string? sessionId)
        {
            _logger?.LogDebug("Attempting to store session ID to generic storage: {SessionId}", sessionId);

            try
            {
                // Fallback to environment variables for generic applications
                Environment.SetEnvironmentVariable(SessionIdName, sessionId);
                _logger?.LogInformation("Stored generic session ID to environment variable: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error storing generic session ID to environment variable: {SessionId}", sessionId);
                throw;
            }
        }

        #endregion Platform-Specific Storage Methods

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

            // Automatically add IHttpContextAccessor if needed
            if (options.IdStrategy == SessionIdStrategy.HttpContext)
            {
                services.AddHttpContextAccessor();
                // Register a hosted service to perform the actual initialization
                services.AddHostedService<SessionManagerInitializerService>();
            }

            // Initialize SessionManager
            SessionManager.Initialize(accountName, accountKey, configure);

            return services;
        }

        /// <summary>
        /// Add Azure Table sessions with configuration
        /// </summary>
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
        public static IHostBuilder AddAzureTableSessions(this IHostBuilder builder, string accountName, string accountKey, Action<SessionOptions>? configure = null)
            => builder.ConfigureServices((context, services) =>
            {
                services.AddAzureTableSessions(accountName, accountKey, configure);
            });
    } // end SessionExtensions

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

    /// <summary>
    /// Manages session based data into the database with unified async/sync operations
    /// Now implements ISession for compatibility with ASP.NET Core
    /// </summary>
    public class Session : IDisposable, IAsyncDisposable, ISession
    {
        private string? m_sessionID;
        private List<AppSessionData> m_sessionData = new List<AppSessionData>();
        private readonly DataAccess<AppSessionData> m_da;

        #region Constructors

        /// <summary>
        /// Constructor initializes the configuration and data access setup
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure account key for the table store</param>
        public Session(string accountName, string accountKey)
        {
            m_da = new DataAccess<AppSessionData>(accountName, accountKey);
        }

        /// <summary>
        /// Constructor to setup the Session object and table
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure account key for the table store</param>
        /// <param name="sessionID">Session Identifier</param>
        public Session(string accountName, string accountKey, string sessionID) : this(accountName, accountKey)
        {
            m_sessionID = sessionID;
            m_sessionData = LoadSessionDataCore(m_sessionID).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously creates a Session instance
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure account key for the table store</param>
        /// <param name="sessionID">Session Identifier</param>
        /// <returns>A new Session instance with data loaded asynchronously</returns>
        public static async Task<Session> CreateAsync(string accountName, string accountKey, string sessionID)
        {
            var session = new Session(accountName, accountKey);
            session.m_sessionID = sessionID;
            session.m_sessionData = await session.LoadSessionDataCore(sessionID);
            return session;
        }
        
        #endregion Constructors

        #region ISession Implementation

        /// <summary>
        /// ISession - Indicates whether the session is available
        /// </summary>
        public bool IsAvailable => true;

        /// <summary>
        /// ISession - Gets the session ID
        /// </summary>
        public string Id => m_sessionID ?? Guid.NewGuid().ToString();

        /// <summary>
        /// ISession - Gets all keys in the session
        /// </summary>
        public IEnumerable<string> Keys
        {
            get
            {
                if (m_sessionData != null)
                {
                    return m_sessionData.Select(s => s.Key).Where(k => !string.IsNullOrEmpty(k))!;
                }
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// ISession - Clears all session data
        /// </summary>
        public void Clear()
        {
            RestartSession();
            m_sessionData?.Clear();
        }

        /// <summary>
        /// ISession - Commits session changes asynchronously
        /// </summary>
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await CommitDataAsync();
        }

        /// <summary>
        /// ISession - Loads session data asynchronously
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            await RefreshSessionDataAsync();
        }

        /// <summary>
        /// ISession - Removes a key from the session
        /// </summary>
        public void Remove(string key)
        {
            m_sessionData?.RemoveAll(s => s.Key == key);
            DataHasBeenCommitted = false;
        }

        /// <summary>
        /// ISession - Sets a byte array value in the session
        /// </summary>
        public void Set(string key, byte[] value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.", nameof(key));

            DataHasBeenCommitted = false;
            if (value == null)
            {
                // If value is null, remove the key (standard session behavior)
                Remove(key);
                return;
            }

            this[key]!.Value = value;
        }

        /// <summary>
        /// ISession - Tries to get a byte array value from the session.
        /// </summary>
        public bool TryGetValue(string key, out byte[] value)
        {
            value = null!;
            if (string.IsNullOrEmpty(key))
                return false;

            AppSessionData? data = this[key];
            if (data != null && data.Value != null && data.Value is byte[] byteArray)
            {
                value = byteArray;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Stores a strongly typed object in the session state.
        /// </summary>
        public void SetObject<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;
            this[key]!.Value = value;
            DataHasBeenCommitted = false;
        }

        /// <summary>
        /// Retrieves a strongly typed object from the session state.
        /// </summary>
        public T? GetObject<T>(string key)
        {
            if (string.IsNullOrEmpty(key)) return default;

            var data = this[key];
            if (data != null && data.Value != null)
                return System.Text.Json.JsonSerializer.Deserialize<T>(data.Value.ToString()!, ASCTableStorage.Common.Functions.JsonOptions); ;

            return default;
        }

        /// <summary>
        /// Sets an int value in the <see cref="ISession"/>.
        /// </summary>
        public void SetInt32(string key, int value)
        {
            Find(key)!.Value = value;
        }

        /// <summary>
        /// Gets an int value from <see cref="ISession"/>.
        /// </summary>
        public int? GetInt32(string key)
        {
            var data = Find(key);
            string val = data?.Value?.ToString()!;
            Int32.TryParse(val, out var val2);
            return val2;
        }

        /// <summary>
        /// Sets a <see cref="string"/> value in the <see cref="ISession"/>.
        /// </summary>
        public void SetString(string key, string value)
            => Find(key)!.Value = value;

        /// <summary>
        /// Gets a string value from <see cref="ISession"/>.
        /// </summary>
        public string? GetString(string key)
        {
            var data = Find(key);
            if (data == null)
            {
                return null;
            }
            return data.ToString();
        }

        /// <summary>
        /// Retrieves a session value by key and attempts to parse it as a local DateTime.
        /// </summary>
        public DateTime GetDateTime(string key)
        {
            var value = GetString(key);
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result)
                ? result
                : DateTime.MinValue;
        }

        /// <summary>
        /// Retrieves a session value by key and attempts to parse it as a UTC DateTime.
        /// </summary>
        public DateTime GetUtcDateTime(string key)
        {
            var value = GetString(key);
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var result)
                ? result
                : DateTime.MinValue;
        }

        /// <summary>
        /// Retrieves a session value by key and formats it as a currency string.
        /// </summary>
        public string GetCurrencyString(string key, string cultureName = "en-US")
        {
            var raw = GetString(key);
            if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return "$0.00"; // fallback

            var culture = new System.Globalization.CultureInfo(cultureName);
            return value.ToString("C", culture);
        }

        /// <summary>
        /// Retrieves a session value by key and attempts to parse it as a boolean.
        /// </summary>
        public bool GetBool(string key)
        {
            var raw = GetString(key);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Trim().ToLowerInvariant();

            return raw switch
            {
                "true" => true,
                "1" => true,
                "yes" => true,
                "false" => false,
                "0" => false,
                "no" => false,
                _ => bool.TryParse(raw, out var result) && result
            };
        }

        /// <summary>
        /// Gets a byte-array value from <see cref="ISession"/>.
        /// </summary>
        public byte[]? Get(string key)
        {
            TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Checks if a key exists in the session.
        /// </summary>
        public bool ContainsKey(string key)
        {
            return m_sessionData.Any(s => s.Key == key);
        }

        /// <summary>
        /// Asynchronously removes an item from the session and persists the change.
        /// </summary>
        public async Task<bool> RemoveAsync(string key)
        {
            var itemsToRemove = m_sessionData.Where(s => s.Key == key).ToList();
            if (itemsToRemove.Any())
            {
                m_sessionData.RemoveAll(s => s.Key == key);
                var result = await m_da.BatchUpdateListAsync(itemsToRemove, Microsoft.Azure.Cosmos.Table.TableOperationType.Delete);
                return result.Success;
            }
            return false;
        }

        /// <summary>
        /// Asynchronously clears all data from the session and persists the change.
        /// </summary>
        public Task ClearAsync()
        {
            return RestartSessionCore();
        }

        /// <summary>
        /// Asynchronously persists all pending changes to the underlying data store.
        /// </summary>
        public Task FlushAsync()
        {
            return CommitDataAsync();
        }

        #endregion ISession Implementation

        #region Core Implementation Methods (Async-First)

        /// <summary>
        /// Core implementation for loading session data
        /// </summary>
        private async Task<List<AppSessionData>> LoadSessionDataCore(string sessionID) => await m_da.GetCollectionAsync(sessionID);

        /// <summary>
        /// Core implementation for getting stale sessions
        /// </summary>
        private async Task<List<string>> GetStaleSessionsCore()
        {
            string filter = @"(Key eq 'SessionSubmittedToCRM' and Value eq 'false') or (Key eq 'prospectChannel' and Value eq 'facebook')";
            TableQuery<AppSessionData> q = new TableQuery<AppSessionData>().Where(filter);
            List<AppSessionData> coll = await m_da.GetCollectionAsync(q);
            coll = coll.FindAll(s => s.Timestamp.IsTimeBetween(DateTime.Now, 5, 60));
            return coll.DistinctBy(s => s.SessionID).Select(s => s.SessionID).ToList()!;
        }

        /// <summary>
        /// Core implementation for cleaning session data
        /// </summary>
        private async Task CleanSessionDataCore()
        {
            string queryString = TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.LessThan, DateTime.Now.AddHours(-2));
            TableQuery<AppSessionData> q = new TableQuery<AppSessionData>().Where(queryString);
            List<AppSessionData> data = await m_da.GetCollectionAsync(q);
            await m_da.BatchUpdateListAsync(data, TableOperationType.Delete);
        }

        /// <summary>
        /// Core implementation for committing data
        /// </summary>
        private async Task<bool> CommitDataCore(List<AppSessionData> data, TableOperationType direction)
        {
            await m_da.BatchUpdateListAsync(data, direction);
            DataHasBeenCommitted = direction != TableOperationType.Delete;
            return DataHasBeenCommitted;
        }

        /// <summary>
        /// Core implementation for restarting session
        /// </summary>
        private async Task RestartSessionCore()
        {
            await m_da.BatchUpdateListAsync(SessionData!, TableOperationType.Delete);
            m_sessionData = new List<AppSessionData>();
        }

        #endregion

        #region Public Sync Methods (Wrap Async Core)

        /// <summary>
        /// Finds the abandoned or stale conversations in session (Synchronous)
        /// </summary>
        public List<string> GetStaleSessions() => GetStaleSessionsCore().GetAwaiter().GetResult();

        /// <summary>
        /// Removes legacy session data (Synchronous)
        /// </summary>
        public void CleanSessionData() => CleanSessionDataCore().GetAwaiter().GetResult();

        /// <summary>
        /// Uploads all session data to the dB (Synchronous)
        /// </summary>
        public bool CommitData()
        {
            CleanSessionData();
            return CommitData(m_sessionData, TableOperationType.InsertOrReplace);
        }

        /// <summary>
        /// Commits all of the data stored in a collection (Synchronous)
        /// </summary>
        public bool CommitData(List<AppSessionData> data, TableOperationType direction) => CommitDataCore(data, direction).GetAwaiter().GetResult();

        /// <summary>
        /// Restarts the session by removing previous session data (Synchronous)
        /// </summary>
        public void RestartSession() => RestartSessionCore().GetAwaiter().GetResult();

        /// <summary>
        /// Loads session data from the database (Synchronous)
        /// </summary>
        public void LoadSessionData(string sessionID)
        {
            m_sessionID = sessionID;
            m_sessionData = LoadSessionDataCore(m_sessionID).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Refreshes the current session data from the database (Synchronous)
        /// </summary>
        public void RefreshSessionData()
        {
            if (!string.IsNullOrEmpty(m_sessionID))
                m_sessionData = LoadSessionDataCore(m_sessionID).GetAwaiter().GetResult();
        }

        #endregion

        #region Public Async Methods (Call Core Directly)

        /// <summary>
        /// Finds the abandoned or stale conversations in session (Asynchronous)
        /// </summary>
        public Task<List<string>> GetStaleSessionsAsync() => GetStaleSessionsCore();

        /// <summary>
        /// Removes legacy session data (Asynchronous)
        /// </summary>
        public Task CleanSessionDataAsync() => CleanSessionDataCore();

        /// <summary>
        /// Uploads all session data to the dB (Asynchronous)
        /// </summary>
        public async Task<bool> CommitDataAsync()
        {
            await CleanSessionDataAsync();
            return await CommitDataAsync(m_sessionData, TableOperationType.InsertOrReplace);
        }

        /// <summary>
        /// Commits all of the data stored in a collection (Asynchronous)
        /// </summary>
        public Task<bool> CommitDataAsync(List<AppSessionData> data, TableOperationType direction) => CommitDataCore(data, direction);

        /// <summary>
        /// Restarts the session by removing previous session data (Asynchronous)
        /// </summary>
        public Task RestartSessionAsync() => RestartSessionCore();

        /// <summary>
        /// Loads session data from the database (Asynchronous)
        /// </summary>
        public async Task LoadSessionDataAsync(string sessionID)
        {
            m_sessionID = sessionID;
            m_sessionData = await LoadSessionDataCore(m_sessionID);
        }

        /// <summary>
        /// Refreshes the current session data from the database (Asynchronous)
        /// </summary>
        public async Task RefreshSessionDataAsync()
        {
            if (!string.IsNullOrEmpty(m_sessionID))
                m_sessionData = await LoadSessionDataCore(m_sessionID);
        }

        #endregion Public Async Methods

        #region Helper Methods and Properties

        /// <summary>
        /// Checks to see if an object already exists within the collection
        /// </summary>
        private AppSessionData? Find(string key)
        {
            AppSessionData? b = SessionData!.Find(s => s.Key == key);
            if (b == null)
            {
                b = new AppSessionData() { SessionID = m_sessionID, Key = key };
                SessionData!.Add(b);
                DataHasBeenCommitted = false;
            }
            return b;
        }

        /// <summary>
        /// Locates the object within the collection matching the specified key
        /// </summary>
        public AppSessionData? this[string key]
        {
            get { return Find(key); }
            set { Find(key)!.Value = value; DataHasBeenCommitted = false; }
        }

        /// <summary>
        /// Readonly reference to the data stored 
        /// </summary>
        public List<AppSessionData>? SessionData => m_sessionData;

        /// <summary>
        /// Gets the current session ID
        /// </summary>
        public string? SessionID => m_sessionID;

        /// <summary>
        /// Determines whether the data has successfully been committed to the database
        /// </summary>
        public bool DataHasBeenCommitted { get; set; }

        public string SessionId => throw new NotImplementedException();

        public int Count => throw new NotImplementedException();

        #endregion Helper Methods and Properties

        #region Disposal Pattern

        /// <summary>
        /// Destructor makes sure the data is committed to the DB
        /// </summary>
        ~Session()
        {
            if (!DataHasBeenCommitted)
                CommitData();
        }

        /// <summary>
        /// Implements IDisposable pattern for proper resource cleanup
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Asynchronously implements disposal pattern
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !DataHasBeenCommitted)
                CommitData();
        }

        /// <summary>
        /// Protected async dispose core method
        /// </summary>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (!DataHasBeenCommitted)
                await CommitDataCore(m_sessionData, TableOperationType.InsertOrReplace);
        }

        #endregion Disposal Pattern

    } // end class Session

    /// <summary>
    /// Provides asynchronous enumeration with progress tracking functionality for a collection of items.
    /// </summary>
    /// <typeparam name="T">The type of elements in the collection.</typeparam>
    public class AsyncProcessingState<T>
    {
        private readonly List<T> m_items;
        private int m_currentIndex = -1;
        private readonly object m_lock = new();

        /// <summary>
        /// Initializes a new instance of the AsyncProcessingState class with the specified collection.
        /// </summary>
        /// <param name="items">The collection of items to process.</param>
        public AsyncProcessingState(List<T> items)
        {
            m_items = items;
        }

        public int TotalCount => m_items.Count;
        public int ProcessedCount => m_currentIndex + 1;
        public int RemainingCount => TotalCount - ProcessedCount;

        /// <summary>
        /// Lazily retrieves and yields one item at a time along with its index.
        /// This allows efficient asynchronous iteration without loading all items into memory at once.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An asynchronous enumerable that yields one (item, index) tuple at a time.</returns>
        public async IAsyncEnumerable<(T item, int index)> GetItemAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                int index;
                T item;

                // Lock to ensure only one thread modifies m_currentIndex at a time
                lock (m_lock)
                {
                    if (this.ProcessedCount >= m_items.Count) yield break;

                    cancellationToken.ThrowIfCancellationRequested();

                    m_currentIndex++;
                    item = m_items[m_currentIndex];
                    index = m_currentIndex;
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return (item, index);
                if (index % 10 == 0) await Task.Yield(); // Yield every 10 iterations, for efficiency
            }
        }

        /// <summary>
        /// Returns a list of all items that have not yet been processed
        /// </summary>
        /// <returns>A new list containing the remaining items</returns>
        public List<T> GetRemainingItems()
        {
            return this.ProcessedCount < m_items.Count
                ? m_items.GetRange(this.ProcessedCount, this.RemainingCount)
                : new List<T>();
        }
    } // end class AsyncProcessingState
}