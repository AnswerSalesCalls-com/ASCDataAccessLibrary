using ASCTableStorage.Common;
using ASCTableStorage.Data;
using Microsoft.Azure.Cosmos.Table;
using Newtonsoft.Json;
using System.Collections.Concurrent;
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
    /// Interface for entities that support dynamic properties
    /// </summary>
    public interface IDynamicProperties
    {
        /// <summary>
        /// Gets the dynamic properties dictionary
        /// </summary>
        IDictionary<string, object> DynamicProperties { get; }
    }

    /// <summary>
    /// Allows for the creation of dynamic table entities with flexible properties.
    /// Useful for scenarios where the schema may vary or is not known at compile time.
    /// </summary>
    public class DynamicEntity : TableEntityBase, ITableExtra, IDynamicProperties
    {
        private readonly ConcurrentDictionary<string, object> _properties = new();
        private string _tableName = "DynamicEntities";

        /// <summary>
        /// Creates a new dynamic table entity
        /// </summary>
        public DynamicEntity(string tableName, string partitionKey, string rowKey)
        {
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            PartitionKey = partitionKey ?? throw new ArgumentNullException(nameof(partitionKey));
            RowKey = rowKey ?? throw new ArgumentNullException(nameof(rowKey));
        }

        /// <summary>
        /// Parameterless constructor for deserialization
        /// </summary>
        public DynamicEntity(){}

        #region ITableExtra Implementation

        /// <summary>
        /// The table that will get created in your Table Storage account for managing this data.
        /// </summary>
        public string TableReference
        {
            get => _tableName;
            set => _tableName = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The ID value is always the RowKey for dynamic entities
        /// </summary>
        /// <returns></returns>
        public string GetIDValue() => RowKey ?? Guid.NewGuid().ToString();

        #endregion ITableExtra Implementation

        #region IDynamicProperties Implementation

        /// <summary>
        /// Maintains the dynamic properties for the entity
        /// </summary>
        public IDictionary<string, object> DynamicProperties => _properties;

        #endregion IDynamicProperties Implementation

        #region Property Management Methods

        /// <summary>
        /// Attempts to set a dynamic property by name
        /// </summary>
        /// <param name="name">The name of the property to set</param>
        /// <param name="value">The value of the property to set</param>
        public void SetProperty(string name, object value)
        {
            ValidatePropertyName(name);
            if (value == null)
                _properties.TryRemove(name, out _);
            else
                _properties[name] = value;
        }

        /// <summary>
        /// Attempts to retrieve a dynamic property by name and convert it to the specified type
        /// </summary>
        /// <typeparam name="T">The datatype to convert to</typeparam>
        /// <param name="name">The name of the property to retrieve</param>
        public T GetProperty<T>(string name)
        {
            if (_properties.TryGetValue(name, out var value))
            {
                if (value is T typedValue)
                    return typedValue;
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return default!;
                }
            }
            return default!;
        }

        /// <summary>
        /// Retrieves a dynamic property by name
        /// </summary>
        /// <param name="name">The name of the property to retrieve</param>
        public object GetProperty(string name)
        {
            return _properties.TryGetValue(name, out var value) ? value : null!;
        }

        /// <summary>
        /// True if the named property exists
        /// </summary>
        /// <param name="name">The name of the property to check</param>
        public bool HasProperty(string name) => _properties.ContainsKey(name);

        /// <summary>
        /// Removes a dynamic property by name
        /// </summary>
        /// <param name="name">The name of the property to remove</param>
        public void RemoveProperty(string name) => _properties.TryRemove(name, out _);

        /// <summary>
        /// Returns a copy of all dynamic properties managed by this entity
        /// </summary>
        public Dictionary<string, object> GetAllProperties() => new(_properties);

        /// <summary>
        /// Provides an indexer for dynamic property access
        /// </summary>
        /// <param name="propertyName">The name of the property to access</param>
        public object this[string propertyName]
        {
            get => GetProperty(propertyName);
            set => SetProperty(propertyName, value);
        }

        private void ValidatePropertyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Property name cannot be null or empty");

            var reserved = new[] { "PartitionKey", "RowKey", "Timestamp", "ETag" };
            if (reserved.Contains(name))
                throw new ArgumentException($"'{name}' is a reserved property name");

            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException($"Property name '{name}' contains invalid characters");
        }

        #endregion Property Management Methods

        /// <summary>
        /// Provides a string representation of the dynamic entity for debugging purposes
        /// </summary>
        public override string ToString()
            => $"DynamicEntity[Table={TableReference}, PK={PartitionKey}, RK={RowKey}, Properties={_properties.Count}]";       
    }

    /// <summary>
    /// Lightweight type cache specifically for TableEntityBase serialization performance
    /// </summary>
    internal static class TableEntityTypeCache
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _writablePropertiesCache = new();
        private static readonly ConcurrentDictionary<Type, bool> _isDateTimeTypeCache = new();
        private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> _propertyLookupCache = new();

        /// <summary>
        /// Gets cached writable properties for a type
        /// </summary>
        public static PropertyInfo[] GetWritableProperties(Type type)
        {
            return _writablePropertiesCache.GetOrAdd(type, t =>
                t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .Where(p => p.CanWrite)
                 .ToArray());
        }

        /// <summary>
        /// Gets a dictionary for fast property lookup by name
        /// </summary>
        public static Dictionary<string, PropertyInfo> GetPropertyLookup(Type type)
        {
            return _propertyLookupCache.GetOrAdd(type, t =>
            {
                var props = GetWritableProperties(t);
                return props.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// Checks if a type is DateTime or DateTime?
        /// </summary>
        public static bool IsDateTimeType(Type type)
        {
            return _isDateTimeTypeCache.GetOrAdd(type, t =>
                t == typeof(DateTime) ||
                t == typeof(DateTime?) ||
                t.AssemblyQualifiedName?.Contains("DateTime") == true);
        }
    }

    /// <summary>
    /// Allows for Table Entity Objects to manage data overflow into Table Storage
    /// </summary> 
    public abstract class TableEntityBase : ITableEntity
    {
        /// <summary>
        /// Represents the partition value of table storage. Acts like a Foreign Key relationship
        /// </summary>
        public string? PartitionKey { get; set; }
        /// <summary>
        /// Must be unique within the table. Acts like a Primary Key relationship
        /// </summary>
        public string? RowKey { get; set; }
        /// <summary>
        /// The datetime the data was managed into the table. Is not constant. Updates on each change to the row.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>
        /// Immutable Tag applied by table storage that helps it identify this unique row of data.
        /// </summary>
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
            // Get cached property lookup for this type
            var propertyLookup = TableEntityTypeCache.GetPropertyLookup(this.GetType());

            // Track which properties we've processed
            var processedProps = new HashSet<string>();

            // Find string overflow fields
            var overflowFields = props.Where(p => p.Value.PropertyType == EdmType.String && p.Key.Contains(fieldExtendedName)).ToList();
            if (overflowFields.Any())
            {
                // Process chunked fields
                List<string> origFieldNames = overflowFields.Select(p => p.Key.Substring(0, p.Key.IndexOf(fieldExtendedName))).Distinct().ToList();
                foreach (string origField in origFieldNames)
                {
                    overflowFields.Insert(0, new KeyValuePair<string, EntityProperty>(origField, props[origField]));
                    processedProps.Add(origField);
                }

                List<(string Key, string Value, bool IsLastTrip)> propData = new();
                string pBaseName;
                int extNameIndex = -1;
                int pdIndex = -1;

                foreach (var eP in overflowFields)
                {
                    processedProps.Add(eP.Key);
                    extNameIndex = eP.Key.IndexOf(fieldExtendedName);
                    pBaseName = (extNameIndex > 0) ? eP.Key.Substring(0, extNameIndex) : eP.Key;
                    pdIndex = propData.FindIndex(v => v.Key == pBaseName);

                    if (pdIndex != -1)
                    {
                        var pD = propData[pdIndex];
                        if (!pD.IsLastTrip) pD = (pD.Key, pD.Value + eP.Value.StringValue, pD.IsLastTrip);
                        pD.IsLastTrip = eP.Value.StringValue.Length < maxFieldSize;
                        propData[pdIndex] = pD;
                    }
                    else
                    {
                        propData.Add((pBaseName, eP.Value.StringValue, eP.Value.StringValue.Length < maxFieldSize));
                    }
                }

                // Use cached property lookup instead of repeated LINQ searches
                foreach (var data in propData)
                {
                    if (propertyLookup.TryGetValue(data.Key, out var prop))
                    {
                        prop.SetValue(this, data.Value);
                    }
                    else if (this is IDynamicProperties dynamic)
                    {
                        dynamic.DynamicProperties[data.Key] = data.Value;
                    }
                }
            }

            // Skip system properties
            var systemProps = new HashSet<string> { "PartitionKey", "RowKey", "Timestamp", "ETag", "odata.etag" };

            // Process all other fields
            var nonOverflowFields = props.Where(p => !processedProps.Contains(p.Key) && !systemProps.Contains(p.Key) && !p.Key.StartsWith("odata."));
            foreach (var f in nonOverflowFields)
            {
                processedProps.Add(f.Key);

                if (propertyLookup.TryGetValue(f.Key, out var prop))
                {
                    try
                    {
                        // Use cached type check for DateTime
                        if (TableEntityTypeCache.IsDateTimeType(prop.PropertyType))
                            prop.SetValue(this, Convert.ToDateTime(f.Value.PropertyAsObject));
                        else if (prop.PropertyType.IsEnum)
                            prop.SetValue(this, Enum.Parse(prop.PropertyType, f.Value.ToString()));
                        else
                            prop.SetValue(this, Convert.ChangeType(f.Value.PropertyAsObject, prop.PropertyType));
                    }
                    catch (Exception) { }
                }
                else if (this is IDynamicProperties dynamic)
                {
                    dynamic.DynamicProperties[f.Key] = ConvertFromEntityProperty(f.Value);
                }
            }
        } //end ReadEntity

        /// <summary>
        /// Serialize the data to the database chunking any large data blocks into separated DB fields
        /// </summary>
        /// <returns>Data for Table Storage that considers correct sized chunks</returns>
        public virtual IDictionary<string, EntityProperty> WriteEntity(OperationContext ctx)
        {
            Dictionary<string, EntityProperty> ret = new();

            // Get cached properties once instead of using reflection each time
            var properties = TableEntityTypeCache.GetWritableProperties(this.GetType());

            foreach (PropertyInfo pI in properties)
            {
                if (pI.PropertyType == typeof(string))
                {
                    var currValue = (string)pI.GetValue(this)!;
                    if (!string.IsNullOrEmpty(currValue) && currValue.Length > maxFieldSize)
                    {
                        // Chunk large strings
                        int cursor = 0;
                        int howManyChunks = (int)Math.Ceiling((double)currValue.Length / maxFieldSize);
                        for (int i = 0; i < howManyChunks; i++)
                        {
                            int charsToGrab = Math.Min(maxFieldSize, (currValue.Length - cursor));
                            string fieldName = (i == 0) ? pI.Name : pI.Name + fieldExtendedName + i.ToString();
                            ret.Add(fieldName, new EntityProperty(currValue.Substring(cursor, charsToGrab)));
                            cursor += charsToGrab;
                        }
                    }
                    else
                    {
                        ret.Add(pI.Name, new EntityProperty(currValue));
                    }
                }
                else
                {
                    ret.Add(pI.Name, EntityProperty.CreateEntityPropertyFromObject(pI.GetValue(this)));
                }
            }

            // Process dynamic properties if entity implements IDynamicProperties
            if (this is IDynamicProperties dynamic)
            {
                foreach (var prop in dynamic.DynamicProperties)
                {
                    if (ret.ContainsKey(prop.Key))
                        continue;

                    // Handle string chunking for dynamic properties
                    if (prop.Value is string strValue && !string.IsNullOrEmpty(strValue) && strValue.Length > maxFieldSize)
                    {
                        int cursor = 0;
                        int howManyChunks = (int)Math.Ceiling((double)strValue.Length / maxFieldSize);
                        for (int i = 0; i < howManyChunks; i++)
                        {
                            int charsToGrab = Math.Min(maxFieldSize, (strValue.Length - cursor));
                            string fieldName = (i == 0) ? prop.Key : prop.Key + fieldExtendedName + i.ToString();
                            ret.Add(fieldName, new EntityProperty(strValue.Substring(cursor, charsToGrab)));
                            cursor += charsToGrab;
                        }
                    }
                    else
                    {
                        var entityProp = ConvertToEntityProperty(prop.Value);
                        if (entityProp != null)
                        {
                            ret.Add(prop.Key, entityProp);
                        }
                    }
                }
            }

            return ret;
        } // end WriteEntity

        private static EntityProperty? ConvertToEntityProperty(object? value) =>
            value switch
            {
                null => null,
                string s => new EntityProperty(s),
                bool b => new EntityProperty(b),
                int i => new EntityProperty(i),
                long l => new EntityProperty(l),
                double d => new EntityProperty(d),
                DateTime dt => new EntityProperty(dt),
                DateTimeOffset dto => new EntityProperty(dto),
                Guid g => new EntityProperty(g),
                byte[] bytes => new EntityProperty(bytes),
                float f => new EntityProperty((double)f),
                decimal dec => new EntityProperty(Convert.ToDouble(dec)),
                _ => new EntityProperty(value.ToString())
            };

        private static object? ConvertFromEntityProperty(EntityProperty? prop) =>
            prop is null ? null : prop.PropertyType switch
            {
                EdmType.String => prop.StringValue,
                EdmType.Binary => prop.BinaryValue,
                EdmType.Boolean => prop.BooleanValue,
                EdmType.DateTime => prop.DateTime,
                EdmType.Double => prop.DoubleValue,
                EdmType.Guid => prop.GuidValue,
                EdmType.Int32 => prop.Int32Value,
                EdmType.Int64 => prop.Int64Value,
                _ => prop.PropertyAsObject
            };
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
        /// <summary>
        /// The table that will get created in your Table Storage account for managing session data.
        /// </summary>
        [XmlIgnore]
        public string TableReference => "AppSessionData";
        /// <summary>
        /// The Session ID
        /// </summary>
        public string GetIDValue() => this.SessionID!;
    } //end class BOTSessionData

    /// <summary>
    /// Represents blob information with metadata
    /// </summary>
    public class BlobData
    {
        /// <summary>
        /// The blob name in Azure Storage
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// The original filename when uploaded
        /// </summary>
        public string? OriginalFilename { get; set; }

        /// <summary>
        /// The MIME content type of the blob
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Size of the blob in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// When the blob was uploaded
        /// </summary>
        public DateTime UploadDate { get; set; }

        /// <summary>
        /// Full URL to access the blob
        /// </summary>
        public Uri? Url { get; set; }

        /// <summary>
        /// Container name where the blob is stored
        /// </summary>
        public string? ContainerName { get; set; }

        /// <summary>
        /// Index tags for fast searching (max 10 tags supported by Azure)
        /// These are searchable using tag queries
        /// </summary>
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Metadata associated with the blob (not searchable but accessible)
        /// Use for additional information that doesn't need to be searchable
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Static dictionary of allowed file types and their MIME types
        /// Can be modified using AzureBlobs.AddAllowedFileType() and RemoveAllowedFileType()
        /// </summary>
        public static Dictionary<string, string> FileTypes { get; set; } = new Dictionary<string, string>
        {
            // Documents
            { ".pdf", "application/pdf" },
            { ".doc", "application/msword" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".xls", "application/vnd.ms-excel" },
            { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { ".ppt", "application/vnd.ms-powerpoint" },
            { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
            { ".txt", "text/plain" },
            { ".rtf", "application/rtf" },
            { ".csv", "text/csv" },

            // Images
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".png", "image/png" },
            { ".gif", "image/gif" },
            { ".bmp", "image/bmp" },
            { ".tiff", "image/tiff" },
            { ".tif", "image/tiff" },
            { ".svg", "image/svg+xml" },
            { ".webp", "image/webp" },
            { ".ico", "image/x-icon" },

            // Audio
            { ".mp3", "audio/mpeg" },
            { ".wav", "audio/wav" },
            { ".ogg", "audio/ogg" },
            { ".m4a", "audio/mp4" },
            { ".aac", "audio/aac" },
            { ".flac", "audio/flac" },

            // Video
            { ".mp4", "video/mp4" },
            { ".avi", "video/x-msvideo" },
            { ".mov", "video/quicktime" },
            { ".wmv", "video/x-ms-wmv" },
            { ".flv", "video/x-flv" },
            { ".webm", "video/webm" },
            { ".mkv", "video/x-matroska" },

            // Archives
            { ".zip", "application/zip" },
            { ".rar", "application/vnd.rar" },
            { ".7z", "application/x-7z-compressed" },
            { ".tar", "application/x-tar" },
            { ".gz", "application/gzip" },

            // Web
            { ".html", "text/html" },
            { ".htm", "text/html" },
            { ".css", "text/css" },
            { ".js", "application/javascript" },
            { ".json", "application/json" },
            { ".xml", "application/xml" },

            // Programming
            { ".cs", "text/plain" },
            { ".java", "text/plain" },
            { ".cpp", "text/plain" },
            { ".c", "text/plain" },
            { ".h", "text/plain" },
            { ".py", "text/plain" },
            { ".php", "text/plain" },
            { ".rb", "text/plain" },
            { ".go", "text/plain" },
            { ".sql", "text/plain" }
        };

        /// <summary>
        /// Gets a tag value by key, returns null if not found
        /// </summary>
        /// <param name="tagKey">The tag key to lookup</param>
        /// <returns>Tag value or null if not found</returns>
        public string? GetTag(string tagKey)
        {
            return Tags.TryGetValue(tagKey, out var value) ? value : null;
        }

        /// <summary>
        /// Sets a tag value (adds or updates)
        /// </summary>
        /// <param name="tagKey">The tag key</param>
        /// <param name="tagValue">The tag value</param>
        /// <exception cref="InvalidOperationException">Thrown if trying to add more than 10 tags</exception>
        public void SetTag(string tagKey, string tagValue)
        {
            if (!Tags.ContainsKey(tagKey) && Tags.Count >= 10)
            {
                throw new InvalidOperationException("Azure Blob Storage supports a maximum of 10 index tags per blob");
            }
            Tags[tagKey] = tagValue;
        }

        /// <summary>
        /// Removes a tag by key
        /// </summary>
        /// <param name="tagKey">The tag key to remove</param>
        /// <returns>True if the tag was removed, false if it didn't exist</returns>
        public bool RemoveTag(string tagKey)
        {
            return Tags.Remove(tagKey);
        }

        /// <summary>
        /// Gets metadata value by key, returns null if not found
        /// </summary>
        /// <param name="metadataKey">The metadata key to lookup</param>
        /// <returns>Metadata value or null if not found</returns>
        public string? GetMetadata(string metadataKey)
        {
            return Metadata.TryGetValue(metadataKey, out var value) ? value : null;
        }

        /// <summary>
        /// Sets metadata value (adds or updates)
        /// </summary>
        /// <param name="metadataKey">The metadata key</param>
        /// <param name="metadataValue">The metadata value</param>
        public void SetMetadata(string metadataKey, string metadataValue)
        {
            Metadata[metadataKey] = metadataValue;
        }

        /// <summary>
        /// Removes metadata by key
        /// </summary>
        /// <param name="metadataKey">The metadata key to remove</param>
        /// <returns>True if the metadata was removed, false if it didn't exist</returns>
        public bool RemoveMetadata(string metadataKey)
        {
            return Metadata.Remove(metadataKey);
        }

        /// <summary>
        /// Checks if the blob has a specific tag
        /// </summary>
        /// <param name="tagKey">The tag key to check</param>
        /// <param name="tagValue">Optional specific value to check for</param>
        /// <returns>True if tag exists (and matches value if provided)</returns>
        public bool HasTag(string tagKey, string? tagValue = null)
        {
            if (!Tags.TryGetValue(tagKey, out var existingValue))
                return false;

            return tagValue == null || existingValue == tagValue;
        }

        /// <summary>
        /// Gets a human-readable file size string
        /// </summary>
        /// <returns>Formatted file size (e.g., "1.5 MB", "532 KB")</returns>
        public string GetFormattedSize()
        {
            string[] sizeUnits = { "B", "KB", "MB", "GB", "TB" };
            double size = Size;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < sizeUnits.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:F1} {sizeUnits[unitIndex]}";
        }

        /// <summary>
        /// Gets the file extension from the blob name
        /// </summary>
        /// <returns>File extension including the dot, or empty string if no extension</returns>
        public string GetFileExtension()
        {
            return !string.IsNullOrEmpty(Name) ? Path.GetExtension(Name) : string.Empty;
        }

        /// <summary>
        /// Checks if the blob is an image based on its content type
        /// </summary>
        /// <returns>True if the blob is an image</returns>
        public bool IsImage()
        {
            return !string.IsNullOrEmpty(ContentType) && ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the blob is a document based on its content type
        /// </summary>
        /// <returns>True if the blob is a document</returns>
        public bool IsDocument()
        {
            if (string.IsNullOrEmpty(ContentType)) return false;

            return ContentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) ||
                   ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the blob is a video based on its content type
        /// </summary>
        /// <returns>True if the blob is a video</returns>
        public bool IsVideo()
        {
            return !string.IsNullOrEmpty(ContentType) && ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the blob is audio based on its content type
        /// </summary>
        /// <returns>True if the blob is audio</returns>
        public bool IsAudio()
        {
            return !string.IsNullOrEmpty(ContentType) && ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns a string representation of the blob data
        /// </summary>
        /// <returns>String containing blob name, size, and upload date</returns>
        public override string ToString()
        {
            return $"{OriginalFilename ?? Name} ({GetFormattedSize()}) - {UploadDate:yyyy-MM-dd HH:mm:ss}";
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

        /// <summary>
        /// Allows for clearing out old data from the Error Log Table
        /// </summary>
        /// <param name="accountName">The name of the account used to authenticate the data access operation. Cannot be null or empty.</param>
        /// <param name="accountKey">The key associated with the account used to authenticate the data access operation. Cannot be null or empty.</param>
        /// <param name="daysOld">Defaults to 60 days old</param>
        static public async Task ClearOldDataAsync(string accountName, string accountKey, int daysOld = 60)
        {
            DataAccess<ErrorLogData> da = new DataAccess<ErrorLogData>(accountName, accountKey);
            List<ErrorLogData> old = await da.GetCollectionAsync(e => e.Timestamp < DateTime.UtcNow.AddDays(-daysOld)); // Clean errors older than specified days
            await da.BatchUpdateListAsync(old, TableOperationType.Delete);
        }

        /// <summary>
        /// Clears old data from the Error Log Table based on the specified error type and age.
        /// </summary>
        /// <param name="accountName">The name of the account used to authenticate the data access operation. Cannot be null or empty.</param>
        /// <param name="accountKey">The key associated with the account used to authenticate the data access operation. Cannot be null or empty.</param>
        /// <param name="type">The type of error to clear.</param>
        /// <param name="daysOld">Defaults to 60 days old</param>
        /// <returns></returns>
        static public async Task ClearOldDataByType(string accountName, string accountKey, ErrorCodeTypes type, int daysOld = 60)
        {
            DataAccess<ErrorLogData> da = new DataAccess<ErrorLogData>(accountName, accountKey);
            List<ErrorLogData> old = await da.GetCollectionAsync(e => e.ErrorSeverity == type.ToString() && e.Timestamp < DateTime.UtcNow.AddDays(-daysOld));
            await da.BatchUpdateListAsync(old, TableOperationType.Delete);
        }
    } //end class ErrorLogData


    #region Blob Supporting Classes

    /// <summary>
    /// Information for uploading a blob with tags and metadata
    /// </summary>
    public class BlobUploadInfo
    {
        /// <summary>
        /// Local file path to upload
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Optional index tags for fast searching (max 10)
        /// </summary>
        public Dictionary<string, string>? IndexTags { get; set; }

        /// <summary>
        /// Optional metadata (not searchable but accessible)
        /// </summary>
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>
    /// Result of a blob upload operation
    /// </summary>
    public class BlobUploadResult
    {
        /// <summary>
        /// Whether the upload was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// URI of the uploaded blob (if successful)
        /// </summary>
        public Uri? BlobUri { get; set; }

        /// <summary>
        /// Error message (if unsuccessful)
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Original filename
        /// </summary>
        public string FileName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of a blob download operation
    /// </summary>
    public class BlobOperationResult
    {
        /// <summary>
        /// Whether the download was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Name of the blob that was downloaded
        /// </summary>
        public string BlobName { get; set; } = string.Empty;

        /// <summary>
        /// Local file path where blob was saved (if successful)
        /// </summary>
        public string? DestinationPath { get; set; }

        /// <summary>
        /// Error message (if unsuccessful)
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of a blob stream download operation
    /// </summary>
    public class BlobStreamDownloadResult
    {
        /// <summary>
        /// Whether the download was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Name of the blob that was downloaded
        /// </summary>
        public string BlobName { get; set; } = string.Empty;

        /// <summary>
        /// Stream containing the blob data (if successful)
        /// </summary>
        public Stream? Stream { get; set; }

        /// <summary>
        /// Error message (if unsuccessful)
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    #endregion Blob Supporting Classes

} // end namespace ASCTableStorage.Models
