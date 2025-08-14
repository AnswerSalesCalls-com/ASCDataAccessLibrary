using ASCTableStorage.Common;
using ASCTableStorage.Data;
using Microsoft.Azure.Cosmos.Table;
using Newtonsoft.Json;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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
                    dynamic.DynamicProperties[f.Key] = ConvertFromEntityProperty(f.Value)!;
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
    /// Allows for a single collection of data to be stored in the DB for State Management with position tracking
    /// One QueueData = One StateList = One logical queue of work
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
        /// Name/category of the queue (used as PartitionKey for grouping similar queues)
        /// </summary>
        public string? Name
        {
            get => this.PartitionKey;
            set => this.PartitionKey = value;
        }

        /// <summary>
        /// The Actual Serialized StateList Data being Stored (includes position information)
        /// </summary>
        public string? Value { get; set; }

        /// <summary>
        /// Optional metadata about the queue processing state
        /// </summary>
        public string? ProcessingStatus { get; set; }

        /// <summary>
        /// Percentage complete based on StateList position
        /// </summary>
        public double PercentComplete { get; set; }

        /// <summary>
        /// Total items in this queue
        /// </summary>
        public int TotalItemCount { get; set; } = 0;

        /// <summary>
        /// Last processed index in the queue
        /// </summary>
        public int LastProcessedIndex { get; set; } = -1;

        /// <summary>
        /// Explodes the data into usable StateList form with preserved position
        /// </summary>
        public StateList<T> GetData()
        {
            if (string.IsNullOrEmpty(Value))
                return new StateList<T>();

            try
            {
                var stateList = JsonConvert.DeserializeObject<StateList<T>>(Value);
                return stateList ?? new StateList<T>();
            }
            catch
            {
                // Fallback: If it's old format (List<T>), convert to StateList
                try
                {
                    var list = JsonConvert.DeserializeObject<List<T>>(Value);
                    return new StateList<T>(list ?? new List<T>());
                }
                catch
                {
                    return new StateList<T>();
                }
            }
        }

        /// <summary>
        /// Shrinks the StateList to a string to store (preserves position)
        /// </summary>
        /// <param name="data">The StateList with position information</param>
        public void PutData(StateList<T> data)
        {
            Value = JsonConvert.SerializeObject(data, Functions.NewtonSoftRemoveNulls());
            TotalItemCount = data.Count;
            LastProcessedIndex = data.CurrentIndex;

            // Calculate and store completion percentage
            if (data.Count > 0 && data.CurrentIndex >= 0)
            {
                PercentComplete = ((double)(data.CurrentIndex + 1) / data.Count) * 100;
            }
            else
            {
                PercentComplete = 0;
            }

            // Set processing status based on position
            if (data.CurrentIndex < 0)
                ProcessingStatus = "Not Started";
            else if (data.CurrentIndex >= data.Count - 1)
                ProcessingStatus = "Completed";
            else
                ProcessingStatus = $"In Progress (Item {data.CurrentIndex + 1} of {data.Count})";
        }

        /// <summary>
        /// Overload to accept regular List<T> and convert to StateList
        /// </summary>
        /// <param name="data">Regular list to convert to StateList</param>
        public void PutData(List<T> data)
        {
            var stateList = new StateList<T>(data);
            PutData(stateList);
        }

        #region Save Methods

        /// <summary>
        /// Preserves the queued data to the DB
        /// </summary>
        public void SaveQueue(string accountName, string accountKey)
        {
            new DataAccess<QueueData<T>>(accountName, accountKey).ManageData(this);
        }

        /// <summary>
        /// Preserves the queued data to the DB asynchronously
        /// </summary>
        public async Task SaveQueueAsync(string accountName, string accountKey)
        {
            await new DataAccess<QueueData<T>>(accountName, accountKey).ManageDataAsync(this);
        }

        /// <summary>
        /// Saves current progress without removing from queue (for checkpointing)
        /// Updates the existing queue with new position information
        /// </summary>
        public async Task SaveProgressAsync(StateList<T> currentState, string accountName, string accountKey)
        {
            PutData(currentState);
            await SaveQueueAsync(accountName, accountKey);
        }

        #endregion Save Methods

        #region Single Queue Retrieval Methods

        /// <summary>
        /// Gets a single queue by its ID
        /// </summary>
        /// <param name="queueId">The unique queue identifier</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <param name="deleteAfterRetrieve">Whether to delete after retrieval</param>
        /// <returns>The StateList for this queue, or null if not found</returns>
        public static async Task<StateList<T>?> GetQueueAsync(string queueId, string accountName, string accountKey, bool deleteAfterRetrieve = false)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var queue = await da.GetRowObjectAsync(queueId);

            if (queue == null)
                return null;

            var stateList = queue.GetData();

            if (deleteAfterRetrieve)
            {
                await da.ManageDataAsync(queue, TableOperationType.Delete);
            }

            return stateList;
        }

        /// <summary>
        /// Gets a single queue without deleting it (for monitoring/inspection)
        /// </summary>
        public static async Task<StateList<T>?> PeekQueueAsync(string queueId, string accountName, string accountKey)
            => await GetQueueAsync(queueId, accountName, accountKey, deleteAfterRetrieve: false);
        

        #endregion Single Queue Retrieval Methods

        #region Multiple Queue Management (Each is Independent)

        /// <summary>
        /// Gets all queues with a given name/category
        /// Returns a dictionary where each QueueID maps to its own independent StateList
        /// </summary>
        /// <param name="name">The name/category of queues</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <param name="deleteAfterRetrieve">Whether to delete after retrieval</param>
        /// <returns>Dictionary of QueueID to StateList</returns>
        public static async Task<Dictionary<string, StateList<T>>> GetQueuesAsync(string name, string accountName, string accountKey, bool deleteAfterRetrieve = true)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var queues = await da.GetCollectionAsync(name);
            var result = new Dictionary<string, StateList<T>>();

            foreach (var queue in queues.OrderBy(q => q.Timestamp))
            {
                result[queue.QueueID ?? Guid.NewGuid().ToString()] = queue.GetData();
            }

            if (deleteAfterRetrieve && queues.Any())
            {
                await da.BatchUpdateListAsync(queues, TableOperationType.Delete);
            }

            return result;
        }

        /// <summary>
        /// Gets paged collection of independent queues
        /// </summary>
        /// <param name="name">The name/category of queues</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <param name="pageSize">Number of queues per page</param>
        /// <param name="continuationToken">Continuation token for paging</param>
        /// <returns>Paged result with independent queues</returns>
        public static async Task<PagedQueueResult<T>> GetPagedQueuesAsync(string name, string accountName, string accountKey, int pageSize = 10, string? continuationToken = null)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var pagedResult = await da.GetPagedCollectionAsync(name, pageSize, continuationToken!);

            var result = new PagedQueueResult<T>
            {
                Queues = new Dictionary<string, StateList<T>>(),
                ContinuationToken = pagedResult.ContinuationToken,
                HasMore = pagedResult.HasMore,
                PageSize = pagedResult.Count
            };

            foreach (var queue in pagedResult.Items.OrderBy(q => q.Timestamp))
            {
                var queueId = queue.QueueID ?? Guid.NewGuid().ToString();
                result.Queues[queueId] = queue.GetData();
                result.QueueMetadata.Add(new QueueMetadata
                {
                    QueueID = queueId,
                    ProcessingStatus = queue.ProcessingStatus ?? "Unknown",
                    PercentComplete = queue.PercentComplete,
                    TotalItems = queue.TotalItemCount,
                    LastProcessedIndex = queue.LastProcessedIndex,
                    LastModified = queue.Timestamp.DateTime
                });
            }

            return result;
        }

        /// <summary>
        /// Processes multiple independent queues with a callback
        /// </summary>
        /// <param name="name">The name/category of queues</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <param name="processQueue">Function to process each queue</param>
        /// <param name="deleteAfterProcess">Whether to delete after processing</param>
        /// <param name="maxQueues">Maximum number of queues to process</param>
        public static async Task<ProcessingResult> ProcessQueuesAsync(string name, string accountName,string accountKey, 
            Func<string, StateList<T>, Task<bool>> processQueue, bool deleteAfterProcess = true, int? maxQueues = null)
        {
            var result = new ProcessingResult();
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);

            var queues = await da.GetCollectionAsync(name);
            var sortedQueues = queues.OrderBy(q => q.Timestamp).ToList();

            if (maxQueues.HasValue)
            {
                sortedQueues = sortedQueues.Take(maxQueues.Value).ToList();
            }

            foreach (var queue in sortedQueues)
            {
                var queueId = queue.QueueID ?? "Unknown";
                var stateList = queue.GetData();

                result.TotalQueuesRetrieved++;

                bool success = await processQueue(queueId, stateList);

                if (success)
                {
                    result.TotalQueuesProcessed++;

                    if (deleteAfterProcess)
                    {
                        await da.ManageDataAsync(queue, TableOperationType.Delete);
                    }
                }
                else
                {
                    result.FailedQueueIds.Add(queueId);
                }
            }

            result.Completed = result.FailedQueueIds.Count == 0;
            return result;
        }

        #endregion Multiple Queue Management (Each is Independent)

        #region Batch Operations

        /// <summary>
        /// Deletes multiple queues by their IDs
        /// </summary>
        public static async Task<int> DeleteQueuesAsync(List<string> queueIds, string accountName, string accountKey)
        {
            if (!queueIds.Any()) return 0;

            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);

            // Get all queues at once using lambda
            var queuesToDelete = await da.GetCollectionAsync(q => queueIds.Contains(q.RowKey!));

            if (queuesToDelete.Any())
            {
                var result = await da.BatchUpdateListAsync(queuesToDelete, TableOperationType.Delete);
                return result.SuccessfulItems;
            }

            return 0;
        }

        /// <summary>
        /// Deletes queues matching a condition
        /// </summary>
        public static async Task<int> DeleteQueuesMatchingAsync(string accountName, string accountKey, Expression<Func<QueueData<T>, bool>> predicate)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);

            var queuesToDelete = await da.GetCollectionAsync(predicate);

            if (queuesToDelete.Any())
            {
                var result = await da.BatchUpdateListAsync(queuesToDelete, TableOperationType.Delete);
                return result.SuccessfulItems;
            }

            return 0;
        }

        #endregion Batch Operations

        #region Monitoring and Statistics

        /// <summary>
        /// Gets status of all queues in a category
        /// </summary>
        public static async Task<List<QueueMetadata>> GetQueueStatusesAsync(string name, string accountName, string accountKey)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var queues = await da.GetCollectionAsync(name);

            return queues.Select(q => new QueueMetadata
            {
                QueueID = q.QueueID ?? "Unknown",
                ProcessingStatus = q.ProcessingStatus ?? "Unknown",
                PercentComplete = q.PercentComplete,
                TotalItems = q.TotalItemCount,
                LastProcessedIndex = q.LastProcessedIndex,
                LastModified = q.Timestamp.DateTime
            }).OrderBy(q => q.LastModified).ToList();
        }

        /// <summary>
        /// Gets aggregate statistics for a queue category
        /// </summary>
        public static async Task<QueueCategoryStatistics> GetCategoryStatisticsAsync(string name, string accountName, string accountKey)
        {
            var statuses = await GetQueueStatusesAsync(name, accountName, accountKey);

            return new QueueCategoryStatistics
            {
                CategoryName = name,
                TotalQueues = statuses.Count,
                NotStartedCount = statuses.Count(s => s.ProcessingStatus.Contains("Not Started")),
                InProgressCount = statuses.Count(s => s.ProcessingStatus.Contains("In Progress")),
                CompletedCount = statuses.Count(s => s.ProcessingStatus.Contains("Completed")),
                AveragePercentComplete = statuses.Any() ? statuses.Average(s => s.PercentComplete) : 0,
                TotalItems = statuses.Sum(s => s.TotalItems),
                TotalProcessedItems = statuses.Sum(s => s.LastProcessedIndex >= 0 ? s.LastProcessedIndex + 1 : 0)
            };
        }

        #endregion Monitoring and Statistics

        #region Factory Methods

        /// <summary>
        /// Creates a new QueueData instance from a StateList
        /// </summary>
        public static QueueData<T> CreateFromStateList(StateList<T> stateList, string name, string? queueId = null)
        {
            var queue = new QueueData<T>
            {
                QueueID = queueId ?? Guid.NewGuid().ToString(),
                Name = name
            };
            queue.PutData(stateList);
            return queue;
        }

        /// <summary>
        /// Creates a new QueueData instance from a List
        /// </summary>
        public static QueueData<T> CreateFromList(List<T> list, string name, string? queueId = null)
            => CreateFromStateList(new StateList<T>(list), name, queueId);        

        #endregion Factory Methods

        /// <summary>
        /// Gets the reference name of the table
        /// </summary>
        [XmlIgnore]
        public string TableReference => "AppQueueData";

        /// <summary>
        /// Gets the unique identifier
        /// </summary>
        public string GetIDValue() => this.QueueID!;
    } // end class QueueData<T>

    /// <summary>
    /// A List that maintains its current position through serialization/deserialization
    /// </summary>
    /// <typeparam name="T">The datatype to manage</typeparam>
    public class StateList<T> : IList<T>, IReadOnlyList<T>
    {
        private List<T> _items = new List<T>();

        /// <summary>
        /// The current position being inspected - preserved through serialization
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("CurrentIndex")]
        [JsonProperty("CurrentIndex")]
        public int CurrentIndex { get; set; } = -1;

        /// <summary>
        /// Description of the data contained within the collection - preserved through serialization
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("Description")]
        [JsonProperty("Description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the internal items for serialization
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("Items")]
        [JsonProperty("Items")]
        public List<T> Items
        {
            get => _items;
            set => _items = value ?? new List<T>();
        }

        #region Constructors

        /// <summary>
        /// Default constructor
        /// </summary>
        public StateList() { }

        /// <summary>
        /// Constructor to build from existing collection
        /// </summary>
        /// <param name="source">List of items to manage</param>
        public StateList(IEnumerable<T> source)
        {
            if (source != null)
                _items.AddRange(source);
        }

        /// <summary>
        /// Constructor with initial capacity
        /// </summary>
        /// <param name="capacity">Initial capacity</param>
        public StateList(int capacity)
        {
            _items.Capacity = capacity;
        }

        #endregion

        #region IList<T> Implementation

        /// <summary>
        /// Gets or sets the element at the specified index
        /// </summary>
        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        /// <summary>
        /// Gets the number of elements contained in the StateList
        /// </summary>
        public int Count => _items.Count;

        /// <summary>
        /// Gets a value indicating whether the StateList is read-only
        /// </summary>
        public bool IsReadOnly => false;

        /// <summary>
        /// Adds an item to the StateList
        /// </summary>
        public void Add(T item)
        {
            _items.Add(item);
        }

        /// <summary>
        /// Removes all items from the StateList
        /// </summary>
        public void Clear()
        {
            _items.Clear();
            CurrentIndex = -1;
        }

        /// <summary>
        /// Determines whether the StateList contains a specific value
        /// </summary>
        public bool Contains(T item) => _items.Contains(item);

        /// <summary>
        /// Copies the elements of the StateList to an Array, starting at a particular Array index
        /// </summary>
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        /// <summary>
        /// Returns an enumerator that iterates through the collection
        /// </summary>
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        /// <summary>
        /// Determines the index of a specific item in the StateList
        /// </summary>
        public int IndexOf(T item) => _items.IndexOf(item);

        /// <summary>
        /// Inserts an item to the StateList at the specified index
        /// </summary>
        public void Insert(int index, T item)
        {
            _items.Insert(index, item);

            // Adjust CurrentIndex if necessary
            if (CurrentIndex >= index)
                CurrentIndex++;
        }

        /// <summary>
        /// Removes the first occurrence of a specific object from the StateList
        /// </summary>
        public bool Remove(T item)
        {
            int index = _items.IndexOf(item);
            if (index == -1) return false;

            RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Removes the StateList item at the specified index
        /// </summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            _items.RemoveAt(index);

            // Adjust CurrentIndex after removal
            if (CurrentIndex > index)
                CurrentIndex--;
            else if (CurrentIndex == index && CurrentIndex >= Count)
                CurrentIndex = Count - 1;
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Navigation Methods

        /// <summary>
        /// Moves the position of Current Index up by 1
        /// </summary>
        public bool MoveNext()
        {
            if (CurrentIndex + 1 >= Count) return false;
            CurrentIndex++;
            return true;
        }

        /// <summary>
        /// Moves the position of Current Index back by 1
        /// </summary>
        public bool MovePrevious()
        {
            if (CurrentIndex - 1 < 0) return false;
            CurrentIndex--;
            return true;
        }

        /// <summary>
        /// Moves Current Index to the first item in the collection
        /// </summary>
        public void First() => CurrentIndex = Count > 0 ? 0 : -1;

        /// <summary>
        /// Moves Current Index to the last item in the collection
        /// </summary>
        public void Last() => CurrentIndex = Count > 0 ? Count - 1 : -1;

        /// <summary>
        /// Moves Current Index before the first item in the collection
        /// </summary>
        public void Reset() => CurrentIndex = -1;

        #endregion

        #region Current Item Access

        /// <summary>
        /// Gets the object at the current index position
        /// </summary>
        public T? Current
        {
            get
            {
                if (!IsValidIndex && Count > 0)
                    CurrentIndex = 0;

                return IsValidIndex ? _items[CurrentIndex] : default(T);
            }
        }

        /// <summary>
        /// Gets the current item and its index as a tuple
        /// </summary>
        public (T Data, int Index)? Peek
        {
            get
            {
                if (IsValidIndex)
                    return (_items[CurrentIndex], CurrentIndex);
                return null;
            }
        }

        /// <summary>
        /// True if the collection can move the current index forward
        /// </summary>
        public bool HasNext => CurrentIndex + 1 < Count;

        /// <summary>
        /// True if the collection can move the current index backward
        /// </summary>
        public bool HasPrevious => CurrentIndex - 1 >= 0;

        /// <summary>
        /// True if the current index is within bounds
        /// </summary>
        public bool IsValidIndex => CurrentIndex >= 0 && CurrentIndex < Count;

        #endregion

        #region String-based Indexer

        /// <summary>
        /// Allows for quick case-insensitive searches by string representation. Updates Current Index when found.
        /// </summary>
        /// <param name="name">The string to search for</param>
        public T? this[string name]
        {
            get
            {
                for (int i = 0; i < Count; i++)
                {
                    var item = _items[i];
                    if (item != null && item.ToString() != null &&
                        item.ToString()!.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentIndex = i;
                        return item;
                    }
                }
                return default(T);
            }
        }

        #endregion

        #region Enhanced List Operations

        /// <summary>
        /// Adds an item and optionally sets it as current
        /// </summary>
        /// <param name="item">Item to add</param>
        /// <param name="setCurrent">Whether to set this as the current item (default: true)</param>
        public void Add(T item, bool setCurrent)
        {
            _items.Add(item);
            if (setCurrent)
                CurrentIndex = Count - 1;
        }

        /// <summary>
        /// Adds a range of items with optional state management
        /// </summary>
        /// <param name="collection">Items to add</param>
        /// <param name="setCurrentToFirst">Set current index to first added item</param>
        /// <param name="setCurrentToLast">Set current index to last added item</param>
        public void AddRange(IEnumerable<T> collection, bool setCurrentToFirst = false, bool setCurrentToLast = false)
        {
            if (collection == null) return;

            int startIndex = Count;
            _items.AddRange(collection);
            int endIndex = Count - 1;

            // Set current index based on parameters
            if (setCurrentToLast && endIndex >= startIndex)
                CurrentIndex = endIndex;
            else if (setCurrentToFirst && startIndex < Count)
                CurrentIndex = startIndex;
        }

        /// <summary>
        /// Adds a range and returns the StateList for method chaining
        /// </summary>
        /// <param name="collection">Items to add</param>
        /// <returns>This StateList instance</returns>
        public StateList<T> AddRangeAndReturn(IEnumerable<T> collection)
        {
            AddRange(collection);
            return this;
        }

        /// <summary>
        /// Inserts a range of items at the specified index
        /// </summary>
        public void InsertRange(int index, IEnumerable<T> collection)
        {
            var items = collection?.ToList();
            if (items == null || items.Count == 0) return;

            _items.InsertRange(index, items);

            // Adjust CurrentIndex if necessary
            if (CurrentIndex >= index)
                CurrentIndex += items.Count;
        }

        /// <summary>
        /// Removes all items matching the predicate
        /// </summary>
        public int RemoveAll(Predicate<T> match)
        {
            if (match == null)
                throw new ArgumentNullException(nameof(match));

            // Track which indices will be removed
            var indicesToRemove = new List<int>();
            for (int i = 0; i < Count; i++)
            {
                if (match(_items[i]))
                    indicesToRemove.Add(i);
            }

            int originalCurrentIndex = CurrentIndex;
            int removed = _items.RemoveAll(match);

            // Adjust CurrentIndex based on removals
            if (removed > 0 && originalCurrentIndex >= 0)
            {
                // Count how many items before current were removed
                int itemsRemovedBeforeCurrent = indicesToRemove.Count(i => i < originalCurrentIndex);

                // Check if current item was removed
                bool currentItemRemoved = indicesToRemove.Contains(originalCurrentIndex);

                if (currentItemRemoved)
                {
                    // Current item was removed, try to maintain position
                    CurrentIndex = Math.Min(originalCurrentIndex - itemsRemovedBeforeCurrent, Count - 1);
                }
                else
                {
                    // Current item still exists, adjust its index
                    CurrentIndex = originalCurrentIndex - itemsRemovedBeforeCurrent;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes a range of items
        /// </summary>
        public void RemoveRange(int index, int count)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (index + count > Count)
                throw new ArgumentException("Index and count exceed list bounds");

            _items.RemoveRange(index, count);

            // Adjust CurrentIndex
            if (CurrentIndex >= index + count)
            {
                // Current is after removed range
                CurrentIndex -= count;
            }
            else if (CurrentIndex >= index)
            {
                // Current was in removed range
                CurrentIndex = Math.Min(index, Count - 1);
            }
        }

        /// <summary>
        /// Sorts the list while maintaining the current item
        /// </summary>
        public void Sort()
        {
            T? currentItem = IsValidIndex ? _items[CurrentIndex] : default(T);
            _items.Sort();

            if (currentItem != null)
                CurrentIndex = _items.IndexOf(currentItem);
        }

        /// <summary>
        /// Sorts the list using a comparison while maintaining the current item
        /// </summary>
        public void Sort(Comparison<T> comparison)
        {
            T? currentItem = IsValidIndex ? _items[CurrentIndex] : default(T);
            _items.Sort(comparison);

            if (currentItem != null)
                CurrentIndex = _items.IndexOf(currentItem);
        }

        /// <summary>
        /// Sorts the list using a comparer while maintaining the current item
        /// </summary>
        public void Sort(IComparer<T>? comparer)
        {
            T? currentItem = IsValidIndex ? _items[CurrentIndex] : default(T);
            _items.Sort(comparer);

            if (currentItem != null)
                CurrentIndex = _items.IndexOf(currentItem);
        }

        /// <summary>
        /// Sorts a range of the list
        /// </summary>
        public void Sort(int index, int count, IComparer<T>? comparer)
        {
            T? currentItem = IsValidIndex ? _items[CurrentIndex] : default(T);
            _items.Sort(index, count, comparer);

            if (currentItem != null)
                CurrentIndex = _items.IndexOf(currentItem);
        }

        /// <summary>
        /// Reverses the entire list
        /// </summary>
        public void Reverse()
        {
            _items.Reverse();

            if (IsValidIndex)
                CurrentIndex = Count - 1 - CurrentIndex;
        }

        /// <summary>
        /// Reverses a range of the list
        /// </summary>
        public void Reverse(int index, int count)
        {
            _items.Reverse(index, count);

            // Adjust CurrentIndex if it's within the reversed range
            if (CurrentIndex >= index && CurrentIndex < index + count)
            {
                int relativePos = CurrentIndex - index;
                CurrentIndex = index + count - 1 - relativePos;
            }
        }

        #endregion

        #region Search and Filter Methods

        /// <summary>
        /// Finds a specific item and sets it as current if found
        /// </summary>
        /// <param name="match">Predicate to match</param>
        /// <returns>The found item or default</returns>
        public T? Find(Predicate<T> match)
        {
            if (match == null) return default(T);

            int index = _items.FindIndex(match);
            if (index >= 0)
            {
                CurrentIndex = index;
                return _items[index];
            }
            return default(T);
        }

        /// <summary>
        /// Finds the index of the first item matching the predicate
        /// </summary>
        public int FindIndex(Predicate<T> match) => _items.FindIndex(match);

        /// <summary>
        /// Finds the index of the first item matching the predicate within a range
        /// </summary>
        public int FindIndex(int startIndex, Predicate<T> match) => _items.FindIndex(startIndex, match);

        /// <summary>
        /// Finds the index of the first item matching the predicate within a range
        /// </summary>
        public int FindIndex(int startIndex, int count, Predicate<T> match) =>
            _items.FindIndex(startIndex, count, match);

        /// <summary>
        /// Finds all items matching the predicate
        /// </summary>
        public List<T> FindAll(Predicate<T> match) => _items.FindAll(match);

        /// <summary>
        /// Creates a new StateList with items that match the predicate
        /// </summary>
        /// <param name="predicate">Filter predicate</param>
        /// <returns>New StateList with filtered items</returns>
        public StateList<T> Where(Func<T, bool> predicate) =>
            new StateList<T>(_items.Where(predicate));

        /// <summary>
        /// Returns items not in the other collection
        /// </summary>
        /// <param name="other">Collection to exclude</param>
        /// <returns>New StateList with excluded items</returns>
        public StateList<T> Except(IEnumerable<T> other) =>
            new StateList<T>(_items.Except(other));

        /// <summary>
        /// Returns items that exist in both collections
        /// </summary>
        /// <param name="other">Collection to intersect with</param>
        /// <returns>New StateList with intersected items</returns>
        public StateList<T> Intersect(IEnumerable<T> other) =>
            new StateList<T>(_items.Intersect(other));

        /// <summary>
        /// Determines whether any element matches the conditions
        /// </summary>
        public bool Exists(Predicate<T> match) => _items.Exists(match);

        /// <summary>
        /// Determines whether all elements match the conditions
        /// </summary>
        public bool TrueForAll(Predicate<T> match) => _items.TrueForAll(match);

        #endregion

        #region Additional List Methods

        /// <summary>
        /// Gets or sets the capacity of the list
        /// </summary>
        public int Capacity
        {
            get => _items.Capacity;
            set => _items.Capacity = value;
        }

        /// <summary>
        /// Copies the list to an array
        /// </summary>
        public T[] ToArray() => _items.ToArray();

        /// <summary>
        /// Creates a shallow copy of a range of elements
        /// </summary>
        public List<T> GetRange(int index, int count) => _items.GetRange(index, count);

        /// <summary>
        /// Performs an action on each element
        /// </summary>
        public void ForEach(Action<T> action) => _items.ForEach(action);

        /// <summary>
        /// Binary search for an item (list must be sorted)
        /// </summary>
        public int BinarySearch(T item) => _items.BinarySearch(item);

        /// <summary>
        /// Binary search for an item using a comparer (list must be sorted)
        /// </summary>
        public int BinarySearch(T item, IComparer<T>? comparer) =>
            _items.BinarySearch(item, comparer);

        /// <summary>
        /// Binary search within a range (list must be sorted)
        /// </summary>
        public int BinarySearch(int index, int count, T item, IComparer<T>? comparer) =>
            _items.BinarySearch(index, count, item, comparer);

        /// <summary>
        /// Converts all elements to another type
        /// </summary>
        public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter) =>
            _items.ConvertAll(converter);

        /// <summary>
        /// Gets the last index of an item
        /// </summary>
        public int LastIndexOf(T item) => _items.LastIndexOf(item);

        /// <summary>
        /// Gets the last index of an item within a range
        /// </summary>
        public int LastIndexOf(T item, int index) => _items.LastIndexOf(item, index);

        /// <summary>
        /// Gets the last index of an item within a range
        /// </summary>
        public int LastIndexOf(T item, int index, int count) =>
            _items.LastIndexOf(item, index, count);

        /// <summary>
        /// Reduces the capacity to the actual number of elements
        /// </summary>
        public void TrimExcess() => _items.TrimExcess();

        #endregion

        #region Implicit Conversions

        /// <summary>
        /// Implicit conversion from array
        /// </summary>
        public static implicit operator StateList<T>(T[] source) => new StateList<T>(source);

        /// <summary>
        /// Implicit conversion from List
        /// </summary>
        public static implicit operator StateList<T>(List<T> source) => new StateList<T>(source);

        #endregion

        #region Overrides

        /// <summary>
        /// Returns a string representation of the StateList
        /// </summary>
        public override string ToString()
        {
            return $"StateList<{typeof(T).Name}>[Count={Count}, Current={CurrentIndex}, Description={Description ?? "None"}]";
        }

        #endregion
    } //end class StateList<T>

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

    #region Queue Supporting Classes
    /// <summary>
    /// Represents a paged result of INDEPENDENT queues
    /// </summary>
    public class PagedQueueResult<T>
    {
        /// <summary>
        /// Dictionary of QueueID to its StateList (each queue is independent)
        /// </summary>
        public Dictionary<string, StateList<T>> Queues { get; set; } = new Dictionary<string, StateList<T>>();

        /// <summary>
        /// Metadata for each queue in this page
        /// </summary>
        public List<QueueMetadata> QueueMetadata { get; set; } = new List<QueueMetadata>();

        /// <summary>
        /// Token to continue to the next page
        /// </summary>
        public string? ContinuationToken { get; set; }

        /// <summary>
        /// Indicates if there are more pages available
        /// </summary>
        public bool HasMore { get; set; }

        /// <summary>
        /// Number of queues in this page
        /// </summary>
        public int PageSize { get; set; }
    }

    /// <summary>
    /// Metadata about a single queue
    /// </summary>
    public class QueueMetadata
    {
        public string QueueID { get; set; } = string.Empty;
        public string ProcessingStatus { get; set; } = string.Empty;
        public double PercentComplete { get; set; }
        public int TotalItems { get; set; }
        public int LastProcessedIndex { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// Result of processing multiple queues
    /// </summary>
    public class ProcessingResult
    {
        public int TotalQueuesRetrieved { get; set; }
        public int TotalQueuesProcessed { get; set; }
        public List<string> FailedQueueIds { get; set; } = new List<string>();
        public bool Completed { get; set; }
    }

    /// <summary>
    /// Statistics for a category of queues
    /// </summary>
    public class QueueCategoryStatistics
    {
        public string CategoryName { get; set; } = string.Empty;
        public int TotalQueues { get; set; }
        public int NotStartedCount { get; set; }
        public int InProgressCount { get; set; }
        public int CompletedCount { get; set; }
        public double AveragePercentComplete { get; set; }
        public int TotalItems { get; set; }
        public int TotalProcessedItems { get; set; }
    }

    #endregion Queue Supporting Classes


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
