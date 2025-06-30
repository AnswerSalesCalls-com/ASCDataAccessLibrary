using ASCTableStorage.Common;
using ASCTableStorage.Data;
using Microsoft.Azure.Cosmos.Table;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace ASCTableStorage.Models
{
    /// <summary>
    /// Required by all TableEntities to help with DataAccess
    /// </summary>
    public interface ITableExtra
    {
        /// <summary>
        /// Reference to the table represented by the object
        /// </summary>
        string TableReference { get; }
        /// <summary>
        /// Ensures all ID values are generically accessible
        /// </summary>
        string GetIDValue();
    }

    /// <summary>
    /// Allows for Table Entity Objects to manage data overflow into Table Storage
    /// </summary> 
    public abstract class TableEntityBase : ITableEntity
    {
        public string? PartitionKey { get; set; }
        public string? RowKey { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? ETag { get; set; }

        private const int maxFieldSize = 31999; // Azure Table Storage field size limit of characters == 64Kb or 32K chars
        private const string fieldExtendedName = "_pt_";

        /// <summary>
        /// Deserialize the data back into the object reference keeping in mind any chunked fields
        /// </summary>
        /// <param name="props">The properties within the entity to consider</param>
        /// <param name="ctx">The data context</param>
        public virtual void ReadEntity(IDictionary<string, EntityProperty> props, OperationContext ctx)
        {
            PropertyInfo[] pInfo = this.GetType().GetProperties();
            PropertyInfo p;

            //Find string overflow fields within the dB. Discover which Object Prop the field reps and append the data to that Prop
            var overflowFields = props.Where(p => p.Value.PropertyType == EdmType.String && p.Key.Contains(fieldExtendedName)).ToList(); //Coming from the dB
            if (overflowFields.Any())
            {
                //Find and add the original Field Names as well.
                List<string> origFieldNames = overflowFields.Select(p => p.Key.Substring(0, p.Key.IndexOf(fieldExtendedName))).Distinct().ToList();
                foreach (string origField in origFieldNames) overflowFields.Insert(0, new KeyValuePair<string, EntityProperty>(origField, props[origField]));
                List<(string Key, string Value, bool IsLastTrip)> propData = new();
                string pBaseName;
                int extNameIndex = -1;
                int pdIndex = -1;
                foreach (var eP in overflowFields) //extract the name of the Object Field and combine/append the data into a Dictionary to go into the object property
                {
                    extNameIndex = eP.Key.IndexOf(fieldExtendedName);
                    pBaseName = (extNameIndex > 0) ? eP.Key.Substring(0, extNameIndex) : eP.Key;
                    pdIndex = propData.FindIndex(v => v.Key == pBaseName);
                    if (pdIndex != -1)
                    {
                        var pD = propData[pdIndex];
                        if (!pD.IsLastTrip) pD = (pD.Key, pD.Value + eP.Value.StringValue, pD.IsLastTrip); //considers contracting data sizes. Stop adding data when its noticed the field size is below threshold
                        pD.IsLastTrip = eP.Value.StringValue.Length < maxFieldSize;
                        propData[pdIndex] = pD;
                    }
                    else
                    {
                        propData.Add((pBaseName, eP.Value.StringValue, eP.Value.StringValue.Length < maxFieldSize));
                    }
                }

                foreach (var data in propData)
                {
                    p = pInfo.FirstOrDefault(p => p.CanWrite && p.Name == data.Key)!;
                    if (p != null) p.SetValue(this, data.Value); //put combined data into the object property
                }
            }

            //otherwise take all other fields and append them to the output. Everything still needs to get processed
            var nonOverflowFields = props.Where(p => !overflowFields.Select(of => of.Key).Contains(p.Key));
            foreach (var f in nonOverflowFields)
            {
                p = pInfo.FirstOrDefault(p => p.CanWrite && p.Name == f.Key)!;
                if (p != null)
                {
                    try
                    {
                        //fill the property with the appropriate datatype
                        //TODO: This may cause issues later as we find out datatypes not considered
                        if (p.PropertyType.AssemblyQualifiedName!.Contains("DateTime"))
                            p.SetValue(this, Convert.ToDateTime(f.Value.PropertyAsObject));
                        else if (p.PropertyType.IsEnum)
                            p.SetValue(this, Enum.Parse(p.PropertyType, f.Value.ToString()));
                        else
                            p.SetValue(this, Convert.ChangeType(f.Value.PropertyAsObject, p.PropertyType));
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Serialize the data to the database chunking any large data blocks into separated DB fields
        /// </summary>
        /// <returns>Data for Table Storage that considers correct sized chunks</returns>
        public virtual IDictionary<string, EntityProperty> WriteEntity(OperationContext ctx)
        {
            Dictionary<string, EntityProperty> ret = new();
            string currValue;
            int howManyChunks;
            int cursor;
            string fieldName;
            int charsToGrab;
            foreach (PropertyInfo pI in this.GetType().GetProperties())
            {
                if (pI.PropertyType == typeof(string))
                {
                    currValue = (string)pI.GetValue(this)!;
                    if (!string.IsNullOrEmpty(currValue) && currValue.Length > maxFieldSize)
                    {
                        cursor = 0;
                        howManyChunks = (int)Math.Ceiling((double)currValue.Length / maxFieldSize);
                        for (int i = 0; i < howManyChunks; i++)
                        {
                            charsToGrab = Math.Min(maxFieldSize, (currValue.Length - cursor));//NOTE: the Substring cannot exceed the loaction of the remaining characters
                            fieldName = (i == 0) ? pI.Name : pI.Name + fieldExtendedName + i.ToString(); //Always define overflow fields with the extension so start with pt_1, but also fill the original field to now have orphaned fields
                            ret.Add(fieldName, new EntityProperty(currValue.Substring(cursor, charsToGrab))); //Keep appending new fields with the extension name to the dB.
                            cursor += charsToGrab;
                        }
                    }
                    else
                        ret.Add(pI.Name, new EntityProperty(currValue));//value may be null or the right size
                }
                else
                {
                    //Everything else also needs to get processed into the DB AS IS
                    ret.Add(pI.Name, EntityProperty.CreateEntityPropertyFromObject(pI.GetValue(this)));
                }
            }

            return ret;
        }
    }//End Class TableEntityBase

    /// <summary>
    /// Represents a row of data for the Session Table
    /// </summary>
    public class AppSessionData : TableEntityBase, ITableExtra
    {
        /// <summary>
        /// Placeholder ID for all data pertaining to the session
        /// </summary>
        public string? SessionID
        {
            get { return this.PartitionKey; }
            set { this.PartitionKey = value; }
        }
        /// <summary>
        /// Unique Name of the object for retrieval
        /// </summary>
        public string? Key
        {
            get { return base.RowKey; }
            set { base.RowKey = value; }
        }
        /// <summary>
        /// Value of the object for retrieval
        /// </summary>
        public string? Value { get; set; }
        [XmlIgnore]
        public string TableReference => "AppSessionData";
        public string GetIDValue() => this.SessionID!;
    } //end class BOTSessionData

    /// <summary>
    /// Represents blob information with metadata
    /// </summary>
    public class BlobData
    {
        /// <summary>
        /// The name of the blob in storage
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Original filename before upload
        /// </summary>
        public string? OriginalFilename { get; set; }

        /// <summary>
        /// Content type
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Size in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Upload date
        /// </summary>
        public DateTime UploadDate { get; set; }

        /// <summary>
        /// Full URL to the blob
        /// </summary>
        public Uri? Url { get; set; }

        /// <summary>
        /// Standard file types and their corresponding MIME types
        /// </summary>
        public static Dictionary<string, string> FileTypes
        {
            get => new()
            { 
                // Images
                { ".jpg", "image/jpeg" },
                { ".jpeg", "image/jpeg" },
                { ".png", "image/png" },
                { ".gif", "image/gif" },
                { ".bmp", "image/bmp" },
                { ".svg", "image/svg+xml" },
                { ".tiff", "image/tiff" },
                { ".tif", "image/tiff" },
                { ".webp", "image/webp" },
            
                // Documents
                { ".pdf", "application/pdf" },
                { ".txt", "text/plain" },
            
                // Microsoft Office Documents
                // Word
                { ".doc", "application/msword" },
                { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                { ".docm", "application/vnd.ms-word.document.macroEnabled.12" },
                { ".dot", "application/msword" },
                { ".dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template" },
                { ".dotm", "application/vnd.ms-word.template.macroEnabled.12" },
            
                // Excel
                { ".xls", "application/vnd.ms-excel" },
                { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                { ".xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12" },
                { ".xlt", "application/vnd.ms-excel" },
                { ".xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template" },
                { ".xltm", "application/vnd.ms-excel.template.macroEnabled.12" },
                { ".xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12" },
            
                // PowerPoint
                { ".ppt", "application/vnd.ms-powerpoint" },
                { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
                { ".pptm", "application/vnd.ms-powerpoint.presentation.macroEnabled.12" },
                { ".pot", "application/vnd.ms-powerpoint" },
                { ".potx", "application/vnd.openxmlformats-officedocument.presentationml.template" },
                { ".potm", "application/vnd.ms-powerpoint.template.macroEnabled.12" },
                { ".pps", "application/vnd.ms-powerpoint" },
                { ".ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow" },
                { ".ppsm", "application/vnd.ms-powerpoint.slideshow.macroEnabled.12" },
            
                // Access
                { ".accdb", "application/vnd.ms-access" },
                { ".accde", "application/vnd.ms-access" },
                { ".accdt", "application/vnd.ms-access" },
                { ".mdb", "application/vnd.ms-access" },
            
                // Publisher
                { ".pub", "application/vnd.ms-publisher" },
            
                // OneNote
                { ".one", "application/onenote" },
            
                // Visio
                { ".vsd", "application/vnd.visio" },
                { ".vsdx", "application/vnd.ms-visio.drawing" },
                { ".vsdm", "application/vnd.ms-visio.drawing.macroEnabled.12" },
                { ".vst", "application/vnd.visio" },
                { ".vstx", "application/vnd.ms-visio.template" },
                { ".vstm", "application/vnd.ms-visio.template.macroEnabled.12" },
            
                // Project
                { ".mpp", "application/vnd.ms-project" },
            
                // OpenDocument Formats
                { ".odt", "application/vnd.oasis.opendocument.text" },
                { ".ods", "application/vnd.oasis.opendocument.spreadsheet" },
                { ".odp", "application/vnd.oasis.opendocument.presentation" },
            
                // Audio Files
                { ".mp3", "audio/mpeg" },
                { ".wav", "audio/wav" },
                { ".ogg", "audio/ogg" },
                { ".flac", "audio/flac" },
                { ".aac", "audio/aac" },
                { ".m4a", "audio/mp4" },
                { ".wma", "audio/x-ms-wma" },
                { ".aiff", "audio/aiff" },
                { ".alac", "audio/alac" },
                { ".mid", "audio/midi" },
                { ".midi", "audio/midi" },
                { ".oga", "audio/ogg" },
                { ".opus", "audio/opus" },
                { ".ra", "audio/x-realaudio" },
                { ".webm", "audio/webm" },
            
                // Video Files
                { ".mp4", "video/mp4" },
                { ".avi", "video/x-msvideo" },
                { ".mov", "video/quicktime" },
                { ".wmv", "video/x-ms-wmv" },
                { ".mkv", "video/x-matroska" },
                { ".flv", "video/x-flv" },
                { ".webm", "video/webm" },
            
                // Data Files
                { ".csv", "text/csv" },
                { ".json", "application/json" },
                { ".xml", "application/xml" },
                { ".yaml", "application/yaml" },
                { ".yml", "application/yaml" },
            
                // Web Files
                { ".html", "text/html" },
                { ".htm", "text/html" },
                { ".css", "text/css" },
                { ".js", "application/javascript" },
            
                // Compressed Files
                { ".zip", "application/zip" },
                { ".rar", "application/x-rar-compressed" },
                { ".7z", "application/x-7z-compressed" },
                { ".tar", "application/x-tar" },
                { ".gz", "application/gzip" }
            };
        }
    } //end class BlobData

    /// <summary>
    /// Result of batch update operation
    /// </summary>
    public class BatchUpdateResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the operation was successful.
        /// </summary>
        public bool Success { get; set; }
        /// <summary>
        /// Gets or sets the number of items that were successfully processed.
        /// </summary>
        public int SuccessfulItems { get; set; }
        /// <summary>
        /// Gets or sets the number of items that failed during the operation.
        /// </summary>
        public int FailedItems { get; set; }
        /// <summary>
        /// Gets or sets the collection of error messages.
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Progress information for batch operations
    /// </summary>
    public class BatchUpdateProgress
    {
        /// <summary>
        /// Gets or sets the number of batches that have been successfully completed.
        /// </summary>
        public int CompletedBatches { get; set; }
        /// <summary>
        /// Gets or sets the total number of batches processed or to be processed.
        /// </summary>
        public int TotalBatches { get; set; }
        /// <summary>
        /// Gets or sets the number of items that have been successfully processed.
        /// </summary>
        public int ProcessedItems { get; set; }
        /// <summary>
        /// Gets or sets the total number of items.
        /// </summary>
        public int TotalItems { get; set; }
        /// <summary>
        /// Gets or sets the size of the current batch being processed.
        /// </summary>
        public int CurrentBatchSize { get; set; }
        /// <summary>
        /// Gets the percentage of items that have been processed.
        /// </summary>
        public double PercentComplete => TotalItems > 0 ? (double)ProcessedItems / TotalItems * 100 : 0;
    }

    /// <summary>
    /// Allows for collections of data to be stored in the DB for State Management
    /// </summary>
    public class QueueData<T> : TableEntityBase, ITableExtra
    {
        /// <summary>
        /// Gets or sets the unique identifier for the queue.
        /// </summary>
        public string? QueueID
        {
            get => this.RowKey;
            set => this.RowKey = value;
        }
        /// <summary>
        /// Name the type of data that's going to be stored.
        /// </summary>
        public string? Name
        {
            get => this.PartitionKey;
            set => this.PartitionKey = value;
        }
        /// <summary>
        /// The Actual Serialized Collection of Type T Data being Stored
        /// </summary>
        public string? Value { get; set; }
        /// <summary>
        /// Explodes the data into usable form
        /// </summary>
        public List<T> GetData()
        {
            return JsonConvert.DeserializeObject<List<T>>(Value!)!;
        }
        /// <summary>
        /// Shrinks the data to a string to store
        /// </summary>
        /// <param name="data">The collection</param>
        public void PutData(List<T> data)
        {
            Value = JsonConvert.SerializeObject(data, Functions.NewtonSoftRemoveNulls());
        }
        /// <summary>
        /// Preserves the queued data to the DB
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure Account key for Table Storage</param>
        public void SaveQueue(string accountName, string accountKey)
        {
            new DataAccess<QueueData<T>>(accountName, accountKey).ManageData(this);
        }
        /// <summary>
        /// Preserves the queued data to the DB asynchronously
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure Account key for Table Storage</param>
        public async Task SaveQueueAsync(string accountName, string accountKey)
        {
            await new DataAccess<QueueData<T>>(accountName, accountKey).ManageDataAsync(this);
        }
        /// <summary>
        /// Returns a list of Queued data that is ready to be worked on again. Removes the data once complete
        /// </summary>
        /// <param name="name">The name stored to identify the type of data</param>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure Account key for Table Storage</param>
        public List<T> GetQueues(string name, string accountName, string accountKey)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            List<QueueData<T>> d = da.GetCollection(name).OrderBy(x => x.Timestamp).ToList();//Make sure to handle the data in the correct order of importance
            da.BatchUpdateListAsync(d, TableOperationType.Delete);

            //Convert the data back to an enumerable that can be used in code
            List<T> allData = new();
            foreach (QueueData<T> q in d)
                allData.AddRange(q.GetData());
            return allData;
        }
        /// <summary>
        /// Asynchronously returns a list of Queued data that is ready to be worked on again. Removes the data once complete
        /// </summary>
        /// <param name="name">The name stored to identify the type of data</param>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure Account key for Table Storage</param>
        public Task<List<T>> GetQueuesAsync(string name, string accountName, string accountKey)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            return da.GetCollectionAsync(name).ContinueWith(t =>
            {
                List<QueueData<T>> d = t.Result.OrderBy(x => x.Timestamp).ToList(); // Make sure to handle the data in the correct order of importance
                _ = Task.Run(async () => { await da.BatchUpdateListAsync(d, TableOperationType.Delete); }); // This is in a thread so it does not delay the return
                // Convert the data back to an enumerable that can be used in code
                List<T> allData = new();
                foreach (QueueData<T> q in d)
                    allData.AddRange(q.GetData());
                return allData;
            });
        }

        /// <summary>
        /// Gets the reference name of the table associated with application queue data.
        /// </summary>
        [XmlIgnore]
        public string TableReference => "AppQueueData";
        /// <summary>
        /// Retrieves the unique identifier associated with the current queue.
        /// </summary>
        /// <returns>A string representing the unique identifier of the queue. This value is never null.</returns>
        public string GetIDValue() => this.QueueID!;
    }// end class QueueData

    /// <summary>
    /// The various acceptable error codes to report
    /// </summary>
    public enum ErrorCodeTypes
    {
        /// <summary>
        /// Represents a message or log entry that is an Error in nature.
        /// </summary>
        Error,
        /// <summary>
        /// Represents an unknown or unspecified value.
        /// </summary>
        /// <remarks>This type or member is used as a placeholder for cases where the value or state is not
        /// defined. It may be used in scenarios where a default or fallback value is required.</remarks>
        Unknown,
        /// <summary>
        /// Represents a warning message or notification within the system.
        /// </summary>
        /// <remarks>This class can be used to encapsulate details about a warning, such as its message,
        /// severity, or associated metadata. It is typically used in logging, user notifications, or system monitoring
        /// scenarios.</remarks>
        Warning,
        /// <summary>
        /// Represents a critical log level used to indicate severe issues that require immediate attention.
        /// </summary>
        /// <remarks>The <see cref="Critical"/> log level is typically used for errors or conditions that
        /// cause the application  to fail or require urgent intervention. Use this level sparingly and only for the most
        /// serious issues.</remarks>
        Critical,
        /// <summary>
        /// Represents general information or metadata.
        /// </summary>
        /// <remarks>This class or member is intended to encapsulate or provide access to informational data.
        /// Use it to store or retrieve descriptive details relevant to the application or domain.</remarks>
        Information
    } //end enum ErrorCodeTypes

    /// <summary>
    /// Represents detailed error information for logging and tracking purposes.
    /// </summary>
    /// <remarks>The <see cref="ErrorLogData"/> class is designed to capture and store information about errors
    /// that occur within an application. It includes details such as the application name, error severity, error
    /// message, and the function where the error occurred. This class also supports logging errors asynchronously and
    /// associating errors with specific customers or subscriptions.</remarks>
    public class ErrorLogData : TableEntityBase, ITableExtra
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ErrorLogData"/> class.
        /// </summary>
        /// <remarks>This constructor creates a default instance of the <see cref="ErrorLogData"/> class. Use
        /// this constructor when no initial data needs to be provided.</remarks>
        public ErrorLogData() { } //Default Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="ErrorLogData"/> class, representing detailed error information.
        /// </summary>
        /// <remarks>This constructor combines information from the provided exception and custom error
        /// description to populate the error log data. The application name and calling function are dynamically
        /// determined from the call stack.</remarks>
        /// <param name="e">The exception that occurred. Must not be <see langword="null"/>.</param>
        /// <param name="errDescription">A custom description of the error. This is typically additional context or details about the error.</param>
        /// <param name="severity">The severity level of the error, represented as an <see cref="ErrorCodeTypes"/> value.</param>
        /// <param name="cID">The customer identifier associated with the error. Defaults to "undefined" if not provided.</param>
        public ErrorLogData(Exception e, string errDescription, ErrorCodeTypes severity, string cID = "undefined")
        {
            var callStackInfo = GetCallStackInfo();

            this.ApplicationName = callStackInfo.ApplicationName;
            this.ErrorSeverity = severity.ToString();
            this.ErrorMessage = errDescription + " " + e.Message;
            this.FunctionName = callStackInfo.CallingFunction;
            this.CustomerID = cID;
        }

        /// <summary>
        /// Creates an ErrorLogData instance with caller information automatically captured
        /// </summary>
        /// <param name="errDescription">A custom description of the error</param>
        /// <param name="severity">The severity level of the error</param>
        /// <param name="cID">The customer identifier associated with the error</param>
        /// <param name="callerMemberName">Automatically captured caller member name</param>
        /// <param name="callerFilePath">Automatically captured caller file path</param>
        /// <param name="callerLineNumber">Automatically captured caller line number</param>
        /// <returns>A new ErrorLogData instance</returns>
        public static ErrorLogData CreateWithCallerInfo(string errDescription, ErrorCodeTypes severity, string cID = "undefined", 
            [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
        {
            var errorLog = new ErrorLogData();
            var callStackInfo = GetCallStackInfo();

            errorLog.ApplicationName = callStackInfo.ApplicationName;
            errorLog.ErrorSeverity = severity.ToString();
            errorLog.ErrorMessage = errDescription;
            errorLog.FunctionName = $"{callerMemberName} (Line: {callerLineNumber})";
            errorLog.CustomerID = cID;

            return errorLog;
        }

        /// <summary>
        /// Gets detailed information from the current call stack
        /// </summary>
        /// <returns>Call stack information including application name and calling function</returns>
        private static (string ApplicationName, string CallingFunction) GetCallStackInfo()
        {
            try
            {
                var stackTrace = new StackTrace(true);
                var frames = stackTrace.GetFrames();

                // Skip the current method and constructor frames to find the actual caller
                MethodBase? callingMethod = null;
                string applicationName = Assembly.GetExecutingAssembly().GetName().Name ?? "Unknown";

                for (int i = 2; i < frames.Length; i++) // Start at 2 to skip current method and constructor
                {
                    var frame = frames[i];
                    var method = frame.GetMethod();

                    if (method != null &&
                        method.DeclaringType != typeof(ErrorLogData) &&
                        !method.DeclaringType?.Name.Contains("Exception") == true)
                    {
                        callingMethod = method;

                        // Get the assembly name from the calling method's declaring type
                        if (method.DeclaringType?.Assembly != null)
                        {
                            applicationName = method.DeclaringType.Assembly.GetName().Name ?? applicationName;
                        }
                        break;
                    }
                }

                string functionInfo = "Unknown";
                if (callingMethod != null)
                {
                    var className = callingMethod.DeclaringType?.Name ?? "Unknown";
                    var methodName = callingMethod.Name;

                    // Find the frame with file info for line number
                    var frameWithFileInfo = Array.Find(frames, f =>
                        f.GetMethod() == callingMethod && f.GetFileName() != null);

                    if (frameWithFileInfo != null)
                    {
                        var lineNumber = frameWithFileInfo.GetFileLineNumber();
                        functionInfo = $"{className}.{methodName} (Line: {lineNumber})";
                    }
                    else
                    {
                        functionInfo = $"{className}.{methodName}";
                    }
                }

                return (applicationName, functionInfo);
            }
            catch
            {
                // Fallback in case of any issues with stack trace analysis
                return (Assembly.GetExecutingAssembly().GetName().Name ?? "Unknown", "Unknown");
            }
        }

        /// <summary>
        /// Unique ID for the Error
        /// </summary>
        public string ErrorID
        {
            get { return string.IsNullOrEmpty(this.RowKey) ? this.RowKey = Guid.NewGuid().ToString() : this.RowKey; }
            set { this.RowKey = value; }
        }

        /// <summary>
        /// Name of the app
        /// </summary>
        public string ApplicationName
        {
            get { return this.PartitionKey!; }
            set { this.PartitionKey = value; }
        }

        /// <summary>
        /// When included allows for errors to be tracked by the Company / Customer / Subscription
        /// </summary>
        public string? CustomerID { get; set; }

        /// <summary>
        /// The message from the code
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Severity of the Error
        /// </summary>
        /// <example>Information | Critical | Message</example>
        public string? ErrorSeverity { get; set; }

        /// <summary>
        /// Name of the function where the error occurred if known
        /// </summary>
        public string? FunctionName { get; set; }

        /// <summary>
        /// Gets the reference name of the table used for storing application error logs.
        /// </summary>
        [XmlIgnore]
        public string TableReference => "AppErrorLogs";
        /// <summary>
        /// Retrieves the value of the error identifier.
        /// </summary>
        /// <returns>A <see cref="string"/> representing the error identifier. Returns an empty string if no error identifier is
        /// set.</returns>
        public string GetIDValue() => this.ErrorID;

        /// <summary>
        /// Asynchronously logs an error to the data store using the specified account credentials.
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure Account key for Table Storage</param>
        public async Task LogErrorAsync(string accountName, string accountKey)
        {
            await new DataAccess<ErrorLogData>(accountName, accountKey).ManageDataAsync(this); //InsertUpdates the Data
        }
        /// <summary>
        /// Logs an error to the data store using the specified account credentials.
        /// </summary>
        /// <remarks>This method uses the provided account credentials to log error information. Ensure
        /// that the account credentials are valid and have the necessary permissions to perform the
        /// operation.</remarks>
        /// <param name="accountName">The name of the account used to authenticate the data access operation. Cannot be null or empty.</param>
        /// <param name="accountKey">The key associated with the account used to authenticate the data access operation. Cannot be null or empty.</param>
        public void LogError(string accountName, string accountKey)
        {
            new DataAccess<ErrorLogData>(accountName, accountKey).ManageData(this); //InsertUpdates the Data
        }
    } //end class ErrorLogData


} // end namespace ASCTableStorage.Models
