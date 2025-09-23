using ASCTableStorage.Common;
using ASCTableStorage.Data;
using Microsoft.Azure.Cosmos.Table;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        private string? _partitionKeySourceField;
        private string? _rowKeySourceField;

        // Pattern configuration - can be overridden or extended
        private static readonly KeyPatternConfig DefaultPatternConfig = new();

        #region Pattern Configuration Classes

        /// <summary>
        /// Configuration for key pattern matching
        /// </summary>
        public class KeyPatternConfig
        {
            /// <summary>
            /// The accepable patterns for determining partition keys
            /// </summary>
            public List<PatternRule> PartitionKeyPatterns { get; set; }
            /// <summary>
            /// The acceptable patterns for determining row keys
            /// </summary>
            public List<PatternRule> RowKeyPatterns { get; set; }
            /// <summary>
            /// A settable threshold to help with matching from 0.0 to 1.0.
            /// Note that 1.0 is absolute match with no fuzzy tolerance while 0.0 means everything matches
            /// </summary>
            public double FuzzyMatchThreshold { get; set; } = 0.6;

            /// <summary>
            /// List of available known patterns to dynamically match and find rowkey field names
            /// </summary>
            public KeyPatternConfig()
            {
                // Default patterns - these can be modified or replaced
                PartitionKeyPatterns = new List<PatternRule>
            {
                // Pattern for hierarchical/ownership fields
                new PatternRule {
                    Pattern = @".*(?:customer|client|tenant|account|company|org|user|owner).*(?:id|key|code|number)?.*",
                    Priority = 100,
                    KeyType = KeyGenerationType.DirectValue
                },
                // Pattern for system/type classification
                new PatternRule {
                    Pattern = @".*(?:system|service|type|category|class|kind).*(?:name|id|code)?.*",
                    Priority = 90,
                    KeyType = KeyGenerationType.DirectValue
                },
                // Pattern for date-based partitioning
                new PatternRule {
                    Pattern = @".*(?:date|time|created|modified|updated).*",
                    Priority = 80,
                    KeyType = KeyGenerationType.DateBased
                },
                // Generic foreign key pattern
                new PatternRule {
                    Pattern = @".*_(?:id|key|code|fk)$",
                    Priority = 70,
                    KeyType = KeyGenerationType.DirectValue
                }
            };

                RowKeyPatterns = new List<PatternRule>
            {
                // Pattern for unique identifiers
                new PatternRule {
                    Pattern = @"^(?:id|key|identifier|guid|uuid)$",
                    Priority = 100,
                    KeyType = KeyGenerationType.DirectValue
                },
                // Pattern for entity-specific IDs
                new PatternRule {
                    Pattern = @".*(?:record|entity|item|entry|row).*(?:id|key|number|code).*",
                    Priority = 95,
                    KeyType = KeyGenerationType.DirectValue
                },
                // Pattern for request/transaction IDs
                new PatternRule {
                    Pattern = @".*(?:request|transaction|operation|job|task).*(?:id|number|code).*",
                    Priority = 90,
                    KeyType = KeyGenerationType.DirectValue
                },
                // Pattern for sequential/time-based ordering
                new PatternRule {
                    Pattern = @".*(?:sequence|order|index|position|timestamp).*",
                    Priority = 85,
                    KeyType = KeyGenerationType.Sequential
                },
                // Generic ID suffix pattern
                new PatternRule {
                    Pattern = @".*(?:_id|_key|_code|_number|_no)$",
                    Priority = 80,
                    KeyType = KeyGenerationType.DirectValue
                }
            };
            }
        } //end class KeyPatternConfig

        /// <summary>
        /// Defines a pattern rule for key matching
        /// </summary>
        public class PatternRule
        {
            /// <summary>
            /// The pattern to use to help generate a row key
            /// </summary>
            public string? Pattern { get; set; } = "";
            /// <summary>
            /// The priority to assign to the pattern
            /// </summary>
            public int Priority { get; set; }
            /// <summary>
            /// The enumeration of generation types
            /// </summary>
            public KeyGenerationType KeyType { get; set; }
            /// <summary>
            /// The transform value
            /// </summary>
            public Func<object, string>? ValueTransform { get; set; }

            private Regex? _compiledRegex;
            /// <summary>
            /// True if the pattern matches with known data
            /// </summary>
            /// <param name="fieldName">The field to look for patterns</param>
            /// <returns></returns>
            public bool Matches(string fieldName)
            {
                _compiledRegex ??= new Regex(Pattern!, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                return _compiledRegex.IsMatch(fieldName);
            }
        }

        /// <summary>
        /// Enumeration of the Types of RowKey generation styles that can be considered
        /// </summary>
        public enum KeyGenerationType
        {
            /// <summary>
            /// Use the field value directly
            /// </summary>
            DirectValue,
            /// <summary>
            /// Convert to date-based partition (YYYY-MM)
            /// </summary>
            DateBased,
            /// <summary>
            /// Generate sequential key
            /// </summary>
            Sequential,
            /// <summary>
            /// Generate reverse timestamp
            /// </summary>
            ReverseTimestamp,
            /// <summary>
            /// Combine multiple fields
            /// </summary>
            Composite,
            /// <summary>
            /// Generate new value
            /// </summary>
            Generated
        }

        #endregion Pattern Configuration Classes

        #region Constructors

        /// <summary>
        /// Creates a new dynamic table entity with explicit keys
        /// </summary>
        /// <param name="tableName">Name of the table to assign to manage the objects of this type in the Data Store</param>
        /// <param name="partitionKey">The name of the foreign key style relationship match for the table to search with</param>
        /// <param name="rowKey">The unique data to assign as the main field identifyer</param>
        public DynamicEntity(string tableName, string partitionKey, string rowKey)
        {
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));

            SetPartitionKey(partitionKey);
            SetRowKey(rowKey);

            EnsureKeysInitialized();
        }

        /// <summary>
        /// Creates a new dynamic entity with automatic pattern-based key detection
        /// </summary>
        /// <param name="tableName">Name of the table to assign to manage the objects of this type in the Data Store</param>
        /// <param name="initialProperties">List of properties you know you would like to assign to be stored</param>
        /// <param name="patternConfig">The pattern to use to help dynamically define the RowKey</param>
        public DynamicEntity(string tableName, Dictionary<string, object>? initialProperties = null, KeyPatternConfig? patternConfig = null)
        {
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));

            // Apply initial properties
            if (initialProperties != null)
            {
                foreach (var kvp in initialProperties)
                {
                    _properties[kvp.Key] = kvp.Value;
                }
            }

            // Detect and set keys using patterns
            DetectKeysUsingPatterns(patternConfig ?? DefaultPatternConfig);

            EnsureKeysInitialized();
        }

        /// <summary>
        /// Parameterless constructor for deserialization
        /// </summary>
        public DynamicEntity()
        {
            EnsureKeysInitialized();
        }

        #endregion Constructors

        #region Pattern-Based Key Detection

        /// <summary>
        /// Detects partition and row keys using configurable patterns
        /// </summary>
        private void DetectKeysUsingPatterns(KeyPatternConfig config)
        {
            // Find best partition key match
            var partitionKeyMatch = FindBestKeyMatch(_properties, config.PartitionKeyPatterns, config.FuzzyMatchThreshold);
            if (partitionKeyMatch != null)
            {
                _partitionKeySourceField = partitionKeyMatch.FieldName;
                PartitionKey = GenerateKeyValue(partitionKeyMatch.FieldValue, partitionKeyMatch.Rule.KeyType, true);
            }

            // Find best row key match
            var rowKeyMatch = FindBestKeyMatch(_properties, config.RowKeyPatterns, config.FuzzyMatchThreshold);
            if (rowKeyMatch != null)
            {
                _rowKeySourceField = rowKeyMatch.FieldName;
                RowKey = GenerateKeyValue(rowKeyMatch.FieldValue, rowKeyMatch.Rule.KeyType, false);
            }
        }

        /// <summary>
        /// Finds the best matching field for a key based on patterns
        /// </summary>
        private KeyMatch? FindBestKeyMatch(IDictionary<string, object> properties, List<PatternRule> patterns, double fuzzyThreshold)
        {
            var matches = new List<KeyMatch>();

            foreach (var kvp in properties)
            {
                if (kvp.Value == null || string.IsNullOrWhiteSpace(kvp.Value.ToString()))
                    continue;

                // Try regex pattern matching first
                foreach (var pattern in patterns)
                {
                    if (pattern.Matches(kvp.Key))
                    {
                        matches.Add(new KeyMatch
                        {
                            FieldName = kvp.Key,
                            FieldValue = kvp.Value,
                            Rule = pattern,
                            Score = pattern.Priority + 100 // Regex matches get bonus score
                        });
                    }
                }

                // Try fuzzy matching on pattern keywords
                var fuzzyScore = CalculateFuzzyScore(kvp.Key, patterns);
                if (fuzzyScore.Score >= fuzzyThreshold)
                {
                    matches.Add(new KeyMatch
                    {
                        FieldName = kvp.Key,
                        FieldValue = kvp.Value,
                        Rule = fuzzyScore.Rule!,
                        Score = fuzzyScore.Score * 100
                    });
                }
            }

            // Return highest scoring match
            return matches.OrderByDescending(m => m.Score).FirstOrDefault();
        }

        /// <summary>
        /// Calculates fuzzy matching score for a field name against patterns
        /// </summary>
        private (double Score, PatternRule? Rule) CalculateFuzzyScore(string fieldName, List<PatternRule> patterns)
        {
            double bestScore = 0;
            PatternRule? bestRule = null;

            var fieldLower = fieldName.ToLowerInvariant();

            foreach (var pattern in patterns)
            {
                // Extract keywords from the pattern
                var keywords = ExtractKeywordsFromPattern(pattern.Pattern);

                double score = 0;
                int matchCount = 0;

                foreach (var keyword in keywords)
                {
                    if (fieldLower.Contains(keyword.ToLowerInvariant()))
                    {
                        matchCount++;
                        // Score based on position and length ratio
                        var positionScore = 1.0 - (fieldLower.IndexOf(keyword.ToLowerInvariant()) / (double)fieldLower.Length);
                        var lengthScore = keyword.Length / (double)fieldLower.Length;
                        score += (positionScore + lengthScore) / 2;
                    }
                }

                if (matchCount > 0)
                {
                    var normalizedScore = (score / keywords.Count) * (pattern.Priority / 100.0);
                    if (normalizedScore > bestScore)
                    {
                        bestScore = normalizedScore;
                        bestRule = pattern;
                    }
                }
            }

            return (bestScore, bestRule);
        }

        /// <summary>
        /// Extracts meaningful keywords from a regex pattern
        /// </summary>
        private List<string> ExtractKeywordsFromPattern(string pattern)
        {
            // Extract words from the pattern, ignoring regex syntax
            var keywords = new List<string>();
            var matches = Regex.Matches(pattern, @"[a-zA-Z]+");

            foreach (Match match in matches)
            {
                var keyword = match.Value;
                // Skip common regex keywords
                if (!new[] { "id", "key", "code", "name", "number" }.Contains(keyword.ToLowerInvariant()))
                {
                    keywords.Add(keyword);
                }
            }

            // Also add the common suffixes as keywords
            keywords.AddRange(new[] { "id", "key", "code" });

            return keywords.Distinct().ToList();
        }

        /// <summary>
        /// Generates a key value based on the generation type
        /// </summary>
        /// <param name="value">The object to generate the key for</param>
        /// <param name="type">Enum of Key types</param>
        /// <param name="isPartitionKey">True if this represents a partition key value</param>
        private string GenerateKeyValue(object value, KeyGenerationType type, bool isPartitionKey)
        {
            switch (type)
            {
                case KeyGenerationType.DirectValue:
                    return SanitizeKeyValue(value.ToString()!);

                case KeyGenerationType.DateBased:
                    if (value is DateTime dt)
                        return dt.ToString("yyyy-MM");
                    if (DateTime.TryParse(value.ToString(), out var parsedDate))
                        return parsedDate.ToString("yyyy-MM");
                    return DateTime.UtcNow.ToString("yyyy-MM");

                case KeyGenerationType.Sequential:
                    var timestamp = DateTime.UtcNow.Ticks;
                    return $"{timestamp:D19}_{SanitizeKeyValue(value.ToString()!)}";

                case KeyGenerationType.ReverseTimestamp:
                    var reverseTimestamp = DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks;
                    return $"{reverseTimestamp:D19}_{SanitizeKeyValue(value.ToString()!)}";

                case KeyGenerationType.Composite:
                    // For composite, combine with other high-scoring fields
                    return $"{SanitizeKeyValue(value.ToString()!)}_{Guid.NewGuid():N}".Substring(0, 255);

                case KeyGenerationType.Generated:
                default:
                    return isPartitionKey
                        ? SanitizeKeyValue(_tableName.ToUpperInvariant())
                        : Guid.NewGuid().ToString("N");
            }
        }

        private class KeyMatch
        {
            public string FieldName { get; set; } = "";
            public object FieldValue { get; set; } = "";
            public PatternRule Rule { get; set; } = new();
            public double Score { get; set; }
        }

        #endregion Pattern-Based Key Detection

        #region Key Management

        /// <summary>
        /// Ensures both PartitionKey and RowKey have valid values
        /// </summary>
        private void EnsureKeysInitialized()
        {
            if (string.IsNullOrEmpty(PartitionKey))
            {
                // Try to infer from table name or use default
                PartitionKey = InferPartitionKeyFromContext();
            }

            if (string.IsNullOrEmpty(RowKey))
            {
                RowKey = GenerateDefaultRowKey();
            }
        }

        /// <summary>
        /// Infers a partition key from available context
        /// </summary>
        private string InferPartitionKeyFromContext()
        {
            // Try to extract a meaningful partition from the table name
            var tableNameParts = Regex.Split(_tableName, @"(?=[A-Z])");

            // Look for patterns in table name that suggest partitioning strategy
            if (Regex.IsMatch(_tableName, @".*(?:log|audit|history|trace).*", RegexOptions.IgnoreCase))
            {
                // Time-based partitioning for log-like tables
                return DateTime.UtcNow.ToString("yyyy-MM-dd");
            }

            if (tableNameParts.Length > 1)
            {
                // Use the first meaningful part of the table name
                return SanitizeKeyValue(tableNameParts[0].ToUpperInvariant());
            }

            // Default to sanitized table name
            return SanitizeKeyValue(_tableName.Replace("Table", "").Replace("Entity", "").ToUpperInvariant());
        }

        /// <summary>
        /// Generates a default row key based on context
        /// </summary>
        private string GenerateDefaultRowKey()
        {
            // Check if any properties suggest time-series data
            var hasTimeProperty = _properties.Keys.Any(k =>
                Regex.IsMatch(k, @".*(?:date|time|created|modified|timestamp).*", RegexOptions.IgnoreCase));

            if (hasTimeProperty)
            {
                // Use reverse timestamp for time-series data
                var reverseTimestamp = DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks;
                return $"{reverseTimestamp:D19}_{Guid.NewGuid():N}".Substring(0, 255);
            }

            // Default to GUID
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Sanitizes a value to be valid for use as a PartitionKey or RowKey
        /// </summary>
        /// <param name="value">The value to scrub for validity</param>
        private string SanitizeKeyValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "EMPTY";

            // Remove invalid characters for Azure Table Storage keys
            var sanitized = Regex.Replace(value, @"[^\w\-]", "_");

            // Ensure it doesn't start or end with underscore
            sanitized = sanitized.Trim('_');

            // Limit length to 255 characters
            if (sanitized.Length > 255)
                sanitized = sanitized.Substring(0, 255);

            return string.IsNullOrWhiteSpace(sanitized) ? "INVALID" : sanitized;
        }

        /// <summary>
        /// Sets the partition key with validation
        /// </summary>
        /// <param name="value">The assigned Foreign Key style relationship to assign to the  data</param>
        public void SetPartitionKey(string? value)
        {
            PartitionKey = string.IsNullOrWhiteSpace(value)
                ? InferPartitionKeyFromContext()
                : SanitizeKeyValue(value);
        }

        /// <summary>
        /// Sets the row key with validation
        /// </summary>
        /// <param name="value">The assigned UNIQUE value for the rowKey</param>
        public void SetRowKey(string? value)
        {
            RowKey = string.IsNullOrWhiteSpace(value)
                ? GenerateDefaultRowKey()
                : SanitizeKeyValue(value);
        }

        #endregion Key Management

        #region ITableExtra Implementation
        /// <summary>
        /// The name of the table to assign to the managed entities
        /// </summary>
        public string TableReference
        {
            get => _tableName;
            set => _tableName = value ?? throw new ArgumentNullException(nameof(value));
        }
        /// <summary>
        /// The RowKey for proper alignment with Table Storage
        /// </summary>
        /// <returns></returns>
        public string GetIDValue() => RowKey ?? GenerateDefaultRowKey();

        #endregion ITableExtra Implementation

        #region IDynamicProperties Implementation
        /// <summary>
        /// Maintains an inner list of the available properties and their assigned values
        /// </summary>
        IDictionary<string, object> IDynamicProperties.DynamicProperties => _properties;

        #endregion IDynamicProperties Implementation

        #region Property Management Methods

        /// <summary>
        /// Sets a dynamic property and re-evaluates keys if needed
        /// </summary>
        /// <param name="name">The name to assign the property</param>
        /// <param name="value">The value to assign</param>
        public void SetProperty(string name, object value)
        {
            ValidatePropertyName(name);

            if (value == null)
            {
                _properties.TryRemove(name, out _);
                return;
            }

            _properties[name] = value is string or bool or char
                or byte or sbyte or short or ushort or int or uint
                or long or ulong or float or double or decimal
                or DateTime or DateTimeOffset or Guid
                    ? value
                    : JsonSerializer.Serialize(value, Functions.JsonOptions);

            // Optional: re-evaluate keys if needed
            ReevaluateKeysIfNeeded(name, _properties[name]);
        }

        /// <summary>
        /// Re-evaluates keys when new properties are added
        /// </summary>
        /// <param name="propertyName">The name to assign the property</param>
        /// <param name="value">The value to assign</param>
        private void ReevaluateKeysIfNeeded(string propertyName, object value)
        {
            // Only re-evaluate if we're using generated/default keys
            bool isDefaultPartition = PartitionKey?.Contains(_tableName.ToUpperInvariant()) == true ||
                                     PartitionKey?.StartsWith("EMPTY") == true ||
                                     PartitionKey?.StartsWith("INVALID") == true;

            bool isDefaultRow = Guid.TryParse(RowKey?.Replace("-", ""), out _);

            if (!isDefaultPartition && !isDefaultRow)
                return;

            // Create a temporary config for re-evaluation
            var config = DefaultPatternConfig;

            if (isDefaultPartition)
            {
                var partitionMatch = FindBestKeyMatch(
                    new Dictionary<string, object> { { propertyName, value } },
                    config.PartitionKeyPatterns,
                    config.FuzzyMatchThreshold);

                if (partitionMatch != null && partitionMatch.Score > 50) // Threshold for replacing default
                {
                    _partitionKeySourceField = partitionMatch.FieldName;
                    PartitionKey = GenerateKeyValue(partitionMatch.FieldValue, partitionMatch.Rule.KeyType, true);
                }
            }

            if (isDefaultRow)
            {
                var rowMatch = FindBestKeyMatch(
                    new Dictionary<string, object> { { propertyName, value } },
                    config.RowKeyPatterns,
                    config.FuzzyMatchThreshold);

                if (rowMatch != null && rowMatch.Score > 50) // Threshold for replacing default
                {
                    _rowKeySourceField = rowMatch.FieldName;
                    RowKey = GenerateKeyValue(rowMatch.FieldValue, rowMatch.Rule.KeyType, false);
                }
            }
        }
        /// <summary>
        /// Attempts to retrieve the value of the dynamic property maintained by the object
        /// </summary>
        /// <typeparam name="T">The datatype of the property</typeparam>
        /// <param name="name">The name of the property</param>
        public T? GetProperty<T>(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return default;

            if (!_properties.TryGetValue(name, out var value))
                return default;

            // 1. Exact match?
            if (value is T tValue)
                return tValue;

            // 2. Is it null?
            if (value == null)
                return default;

            // 3. If T is a string, return .ToString() (except for objects)
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;

            // 4. Assume it's a JSON string and deserialize
            try
            {
                string json = value as string
                    ?? JsonSerializer.Serialize(value, Functions.JsonOptions); // Boxed primitives, etc.

                return JsonSerializer.Deserialize<T>(json, Functions.JsonOptions);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Retrieves the value of the dynamically generated property
        /// </summary>
        /// <param name="name">The name of the proeprty</param>
        /// <returns></returns>
        public object GetProperty(string name)
        {
            return _properties.TryGetValue(name, out var value) ? value : null!;
        }

        /// <summary>
        /// True if the property name you are looking for is available
        /// </summary>
        /// <param name="name">The name of the dynamically generated property</param>
        public bool HasProperty(string name) => _properties.ContainsKey(name);
        /// <summary>
        /// Attempts to delete the property from the collection of properties maintained by the dynamic instance
        /// </summary>
        /// <param name="name">The name of the property to remove</param>
        public void RemoveProperty(string name) => _properties.TryRemove(name, out _);
        /// <summary>
        /// Retrieves all the dynamically generated properties within the object
        /// </summary>
        public Dictionary<string, object> GetAllProperties() => new(_properties);
        /// <summary>
        /// Helpful to inspect the available property values of the dynamic object
        /// </summary>
        /// <param name="propertyName">The property to inspect</param>
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

            if (!Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException($"Property name '{name}' contains invalid characters");
        }

        #endregion Property Management Methods

        #region Static Factory Methods with Custom Patterns

        /// <summary>
        /// Creates a DynamicEntity from the information within a Dictionary with optional custom pattern configuration
        /// </summary>
        /// <param name="tableName">The name of the table to assign to manage the objects of this type in the Data Store</param>
        /// <param name="properties">The properties to initialize the DynamicEntity with</param>
        /// <param name="patternConfig">Optional: custom pattern configuration</param>
        public static DynamicEntity CreateFromDictionary(string tableName, Dictionary<string, object>? properties, KeyPatternConfig patternConfig)
        {
            return new DynamicEntity(tableName, properties, patternConfig);
        }

        /// <summary>
        /// Creates a pattern configuration from a JSON configuration
        /// </summary>
        /// <param name="jsonConfig">The JSON configuration string</param>
        public static KeyPatternConfig LoadPatternConfig(string jsonConfig)
        {
            return JsonSerializer.Deserialize<KeyPatternConfig>(jsonConfig, Functions.JsonOptions) ?? new KeyPatternConfig();
        }

        /// <summary>
        /// Creates a DynamicEntity from JSON with pattern-based key detection
        /// </summary>
        /// <param name="tableName">The name of the table to assign to manage the objects of this type in the Data Store</param>
        /// <param name="json">The JSON string containing the entity data</param>
        /// <param name="patternConfig">Optional: custom pattern configuration</param>
        public static DynamicEntity CreateFromJson(string tableName, string json, KeyPatternConfig? patternConfig = null)
        {
            var properties = JsonSerializer.Deserialize<Dictionary<string, object>>(json, Functions.JsonOptions) ?? new Dictionary<string, object>();
            return new DynamicEntity(tableName, properties, patternConfig);
        }

        /// <summary>
        /// Converts any object to a DynamicEntity by reflecting on its public properties.
        /// </summary>
        /// <param name="obj">The object to convert</param>
        /// <param name="tableName">The table name</param>
        /// <param name="partitionKeyPropertyName">Optional: property to use as PartitionKey</param>
        /// <returns>A DynamicEntity with the object's data</returns>
        public static DynamicEntity CreateFromObject<T>(T obj, string tableName, string? partitionKeyPropertyName = null) where T : class
        {
            var de = new DynamicEntity(tableName);

            PropertyInfo[] props = TableEntityTypeCache.GetWritableProperties(obj.GetType());
            foreach (var prop in props)
            {
                if (prop.CanRead)
                {
                    var value = prop.GetValue(obj);
                    if (value != null)
                    {
                        de[prop.Name] = value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(partitionKeyPropertyName))
            {
                PropertyInfo pkProp = props.FirstOrDefault(p => p.Name == partitionKeyPropertyName)!;
                if (pkProp != null && pkProp.CanRead)
                {
                    var pkValue = pkProp.GetValue(obj)?.ToString();
                    if (!string.IsNullOrWhiteSpace(pkValue))
                    {
                        de.SetPartitionKey(pkValue);
                    }
                }
            }

            return de;
        }

        #endregion Static Factory Methods

        #region Diagnostics
        /// <summary>
        /// Retrieves metadata information about the entities properties
        /// </summary>
        public Dictionary<string, string> GetDiagnostics()
        {
            return new Dictionary<string, string>
            {
                ["TableName"] = _tableName,
                ["PartitionKey"] = PartitionKey ?? "NULL",
                ["RowKey"] = RowKey ?? "NULL",
                ["PartitionKeySource"] = _partitionKeySourceField ?? "GENERATED",
                ["RowKeySource"] = _rowKeySourceField ?? "GENERATED",
                ["PropertyCount"] = _properties.Count.ToString(),
                ["Properties"] = string.Join(", ", _properties.Keys.Take(10)) // Limit to first 10
            };
        }
        /// <summary>
        /// Best used for diagnostic purposes to see what the important serializable data points have
        /// </summary>
        public override string ToString()
        {
            var diagnostics = GetDiagnostics();
            return $"DynamicEntity[Table={diagnostics["TableName"]}, PK={diagnostics["PartitionKey"]}, RK={diagnostics["RowKey"]}, Properties={diagnostics["PropertyCount"]}]";
        }

        #endregion Diagnostics
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
                 .Where(p => p.GetIndexParameters().Length == 0) // ✅ Skip indexers
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
            var overflowFields = props
                .Where(p => p.Value.PropertyType == EdmType.String && p.Key.Contains(fieldExtendedName))
                .ToList();

            if (overflowFields.Any())
            {
                List<string> origFieldNames = overflowFields
                    .Select(p => p.Key.Substring(0, p.Key.IndexOf(fieldExtendedName)))
                    .Distinct()
                    .ToList();

                foreach (string origField in origFieldNames)
                {
                    if (props.TryGetValue(origField, out var baseProp))
                        overflowFields.Insert(0, new KeyValuePair<string, EntityProperty>(origField, baseProp));
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

                foreach (var data in propData)
                {
                    if (propertyLookup.TryGetValue(data.Key, out var prop))
                    {
                        try
                        {
                            object? val = ConvertValue(data.Value, prop.PropertyType);
                            prop.SetValue(this, val);
                        }
                        catch { }
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
            var nonOverflowFields = props
                .Where(p => !processedProps.Contains(p.Key) && !systemProps.Contains(p.Key) && !p.Key.StartsWith("odata."));

            foreach (var f in nonOverflowFields)
            {
                processedProps.Add(f.Key);

                if (propertyLookup.TryGetValue(f.Key, out var prop))
                {
                    try
                    {
                        object? val = ConvertValue(f.Value, prop.PropertyType);
                        prop.SetValue(this, val);
                    }
                    catch { }
                }
                else if (this is IDynamicProperties dynamic)
                {
                    dynamic.DynamicProperties[f.Key] = ConvertFromEntityProperty(f.Value)!;
                }
            }
        } // end ReadEntity

        /// <summary>
        /// Converts a value to the target type, with special handling for DateTime, Enum, and JSON-deserializable objects
        /// </summary>
        private object? ConvertValue(object? value, Type targetType)
        {
            if (value == null) return null;

            // Handle DateTime
            if (TableEntityTypeCache.IsDateTimeType(targetType))
                return Convert.ToDateTime(value);

            // Handle Enum
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value.ToString()!);

            // Handle nullable
            Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // If it's a string and target is not string, try JSON deserialization
            if (value is string stringValue && targetType != typeof(string))
            {
                try
                {
                    // If it looks like JSON, try to deserialize
                    if (stringValue.StartsWith("{") || stringValue.StartsWith("["))
                    {
                        return JsonSerializer.Deserialize(stringValue, underlyingType, Functions.JsonOptions);
                    }
                }
                catch { /* ignore */ }
            }

            // Try direct conversion
            try
            {
                return Convert.ChangeType(value, underlyingType);
            }
            catch
            {
                return value;
            }
        } // end ConvertValue()

        /// <summary>
        /// Serialize the data to the database chunking any large data blocks into separated DB fields
        /// </summary>
        /// <returns>Data for Table Storage that considers correct sized chunks</returns>
        public virtual IDictionary<string, EntityProperty> WriteEntity(OperationContext ctx)
        {
            Dictionary<string, EntityProperty> ret = new();

            // Get cached properties once
            var properties = TableEntityTypeCache.GetWritableProperties(this.GetType());

            foreach (PropertyInfo pI in properties)
            {
                object? value = null;
                try
                {
                    value = pI.GetValue(this);
                }
                catch
                {
                    continue; // Skip properties that throw on read
                }

                if (value == null)
                    continue;

                // Handle string with chunking
                if (pI.PropertyType == typeof(string))
                {
                    string strValue = (string)value;
                    if (strValue.Length > maxFieldSize)
                    {
                        ChunkString(pI.Name, strValue, ret);
                    }
                    else
                    {
                        ret.Add(pI.Name, new EntityProperty(strValue));
                    }
                }
                else
                {
                    // Serialize ANY complex type to JSON
                    EntityProperty? entityProp = TrySerializeComplexType(value);
                    if (entityProp != null)
                    {
                        if (entityProp.PropertyType == EdmType.String && entityProp.StringValue?.Length > maxFieldSize)
                        {
                            ChunkString(pI.Name, entityProp.StringValue, ret);
                        }
                        else
                        {
                            ret.Add(pI.Name, entityProp);
                        }
                    }
                }
            }

            // Process dynamic properties if entity implements IDynamicProperties
            if (this is IDynamicProperties dynamic)
            {
                foreach (var prop in dynamic.DynamicProperties)
                {
                    if (ret.ContainsKey(prop.Key))
                        continue;

                    EntityProperty? entityProp = TrySerializeComplexType(prop.Value);
                    if (entityProp != null)
                    {
                        if (entityProp.PropertyType == EdmType.String && entityProp.StringValue?.Length > maxFieldSize)
                        {
                            ChunkString(prop.Key, entityProp.StringValue, ret);
                        }
                        else
                        {
                            ret.Add(prop.Key, entityProp);
                        }
                    }
                }
            }

            return ret;
        } // end WriteEntity

        /// <summary>
        /// Chunks a large string into multiple fields with extension suffix
        /// </summary>
        /// <param name="baseName">The base field name</param>
        /// <param name="value">The large string value</param>
        /// <param name="result">The result dictionary to populate</param>
        private void ChunkString(string baseName, string value, Dictionary<string, EntityProperty> result)
        {
            int cursor = 0;
            int howManyChunks = (int)Math.Ceiling((double)value.Length / maxFieldSize);
            for (int i = 0; i < howManyChunks; i++)
            {
                int charsToGrab = Math.Min(maxFieldSize, (value.Length - cursor));
                string fieldName = (i == 0) ? baseName : baseName + fieldExtendedName + i.ToString();
                result.Add(fieldName, new EntityProperty(value.Substring(cursor, charsToGrab)));
                cursor += charsToGrab;
            }
        }

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
                _ => TrySerializeComplexType(value)
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

        /// <summary>
        /// Attempts to serialize complex types (IDictionary, IEnumerable, custom objects) to JSON
        /// </summary>
        private static EntityProperty TrySerializeComplexType(object value)
        {
            Type type = value.GetType();

            // Let Azure handle known types
            if (IsSupportedByAzure(type))
                return EntityProperty.CreateEntityPropertyFromObject(value);

            // Serialize everything else to JSON
            try
            {
                string json = JsonSerializer.Serialize(value, Functions.JsonOptions);
                return new EntityProperty(json);
            }
            catch
            {
                // Fallback to ToString() if serialization fails
                return new EntityProperty(value.ToString() ?? string.Empty);
            }
        }

        /// <summary>
        /// Checks if a type is natively supported by Azure Table Storage
        /// </summary>
        private static bool IsSupportedByAzure(Type type)
        {
            return type == typeof(string) ||
                   type == typeof(int) || type == typeof(int?) ||
                   type == typeof(long) || type == typeof(long?) ||
                   type == typeof(bool) || type == typeof(bool?) ||
                   type == typeof(double) || type == typeof(double?) ||
                   type == typeof(float) || type == typeof(float?) ||
                   type == typeof(decimal) || type == typeof(decimal?) ||
                   type == typeof(DateTime) || type == typeof(DateTime?) ||
                   type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?) ||
                   type == typeof(Guid) || type == typeof(Guid?) ||
                   type == typeof(byte[]) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>));
        }
    }//End Class TableEntityBase

    /// <summary>
    /// Represents configuration options for table operations in DataAccess.
    /// Allows overriding table name and key property names for types that don't implement ITableEntity semantics.
    /// </summary>
    public class TableOptions
    {
        /// <summary>
        /// Gets or sets the name of the table to use.
        /// If not provided, the type T must implement ITableExtra and provide TableReference.
        /// </summary>
        public string? TableName { get; set; }

        /// <summary>
        /// Gets or sets the name of the property on T to use as PartitionKey.
        /// If not provided, PartitionKey is used from ITableEntity or derived from context.
        /// </summary>
        public string? PartitionKeyPropertyName { get; set; }

        /// <summary>
        /// Gets or sets the Azure Storage account name.
        /// Required unless using a shared CloudTableClient.
        /// </summary>
        public string? TableStorageName { get; set; }

        /// <summary>
        /// Gets or sets the Azure Storage account key.
        /// Required unless using a shared CloudTableClient.
        /// </summary>
        public string? TableStorageKey { get; set; }

        /// <summary>
        /// Validates that the required options are set.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if TableStorageName or TableStorageKey are missing.</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(TableStorageName))
                throw new InvalidOperationException($"{nameof(TableStorageName)} is required.");
            if (string.IsNullOrWhiteSpace(TableStorageKey))
                throw new InvalidOperationException($"{nameof(TableStorageKey)} is required.");
            if (string.IsNullOrWhiteSpace(TableName))
                throw new InvalidOperationException($"{nameof(TableName)} is required.");

        }
    } // End Class TableOptions

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

        private const string PREFIX = "B64J:"; // Base64 JSON prefix - unique but short
        private string? _rawValue;
        /// <summary>
        /// Data of the object for retrieval
        /// </summary>
        public object? Value
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_rawValue))
                    return null;

                // Skip our prefix if present
                var data = _rawValue.StartsWith(PREFIX)
                    ? _rawValue.Substring(PREFIX.Length)
                    : _rawValue;

                var jsonBytes = Convert.FromBase64String(data);
                var json = Encoding.UTF8.GetString(jsonBytes);

                // Parse to get the actual value type
                using var doc = JsonDocument.Parse(json);
                var element = doc.RootElement;

                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => json // Return complex objects as JSON string
                };
            }
            set
            {
                if (value == null)
                {
                    _rawValue = null;
                    return;
                }

                // If it's already prefixed (coming back from storage), store as-is
                if (value is EntityProperty entityProp &&
                    entityProp.StringValue?.StartsWith(PREFIX) == true)
                {
                    _rawValue = entityProp.StringValue;
                    return;
                }

                // Extract from EntityProperty if needed
                var actualValue = value is EntityProperty ep ? ep.StringValue : value;

                // Serialize and prefix
                var json = JsonSerializer.Serialize(actualValue, Functions.JsonOptions);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                _rawValue = PREFIX + Convert.ToBase64String(jsonBytes);
            }
        }

        /// <summary>
        /// Allows for backward compatibiilty if the developer knows the value is supposed to be a string this allows that expectation
        /// </summary>
        /// <param name="data">The data to manage</param>
        public static explicit operator string?(AppSessionData? data)
            => data?.ToString();

        /// <summary>
        /// Allows for direct string manipulation to get the string value maintained by the instance.
        /// Assumes you are knowledgeable on the data being maintained by the instance. 
        /// Can produce unexpected results otherwise
        /// </summary>
        public override string ToString()
          => this.Value?.ToString()!;        

        /// <summary>
        /// The table that will get created in your Table Storage account for managing session data.
        /// </summary>
        [XmlIgnore]
        public string TableReference => Constants.DefaultSessionTableName;
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
            { ".cshtml", "text/html" },
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
        /// Raw byte data of the blob content (null if not loaded)
        /// </summary>
        public byte[]? Data { get; set; }

        /// <summary>
        /// Reads the blob data and returns it as an appropriate string format
        /// For images, returns base64 encoded string
        /// For text-based content, returns the actual text content
        /// </summary>
        /// <returns>String representation of the blob content</returns>
        public string Read()
        {
            if (Data == null)
                throw new InvalidOperationException("Blob data has not been loaded");

            // Check if this is an image based on content type
            if (IsImage())
            {
                // For images, return base64 encoded string
                return Convert.ToBase64String(Data);
            }

            // For all other content types, decode as UTF-8 string
            // This covers text/, application/json, application/xml, application/javascript, etc.
            return Encoding.UTF8.GetString(Data);
        }

        /// <summary>
        /// Returns a string representation of the blob data
        /// </summary>
        /// <returns>String containing the actual blob content</returns>
        public override string ToString()
        {
            try
            {
                return Read();
            }
            catch
            {
                // Fallback to original behavior if reading fails
                return $"{OriginalFilename ?? Name} ({GetFormattedSize()}) - {UploadDate:yyyy-MM-dd HH:mm:ss}";
            }
        }

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
    /// One QueueData = One StateList = One logical Queue of work
    /// </summary>
    public class QueueData<T> : TableEntityBase, ITableExtra
    {
        /// <summary>
        /// Gets or sets the unique identifier for the q.
        /// </summary>
        public string? QueueID
        {
            get => this.RowKey;
            set => this.RowKey = value;
        }

        /// <summary>
        /// Name/category of the q (used as PartitionKey for grouping similar queues)
        /// </summary>
        public string? Name
        {
            get => this.PartitionKey;
            set => this.PartitionKey = value;
        }

        /// <summary>
        /// The StateList data with position tracking (automatically serialized/deserialized)
        /// </summary>
        public StateList<T> Data { get; set; } = new();

        /// <summary>
        /// Optional metadata about the q processing state
        /// </summary>
        public string ProcessingStatus
        {
            get
            {
                if (Data == null || Data.Count == 0)
                    return "Empty";

                if (Data.CurrentIndex < 0)
                    return "Not Started";
                else if (Data.CurrentIndex >= Data.Count - 1)
                    return "Completed";
                else
                    return $"In Progress (Item {Data.CurrentIndex + 1} of {Data.Count})";
            }
        }

        /// <summary>
        /// Percentage complete based on StateList position
        /// </summary>
        public double PercentComplete
        {
            get
            {
                if (Data == null || Data.Count == 0)
                    return 0;

                if (Data.CurrentIndex >= 0)
                    return ((double)(Data.CurrentIndex + 1) / Data.Count) * 100;

                return 0;
            }
        }

        /// <summary>
        /// Total items in this q
        /// </summary>
        public int TotalItemCount => Data?.Count ?? 0;

        /// <summary>
        /// Last processed index in the q
        /// </summary>
        public int LastProcessedIndex => Data?.CurrentIndex ?? -1;

        #region Save Methods

        /// <summary>
        /// Preserves the queued data to the DB
        /// </summary>
        public void SaveQueue(string accountName, string accountKey)
        {
            new DataAccess<QueueData<T>>(accountName, accountKey).ManageData(this);
        }

        /// <summary>
        /// Saves OR Updates the queued data and its State to the DB asynchronously.
        /// </summary>
        public async Task SaveQueueAsync(string accountName, string accountKey)
        {
            await new DataAccess<QueueData<T>>(accountName, accountKey).ManageDataAsync(this);
        }

        #endregion Save Methods

        #region Single Queue Retrieval Methods

        /// <summary>
        /// Gets a single queue by its ID
        /// </summary>
        /// <param name="queueId">The unique q identifier</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <returns>The QueueData object, or null if not found</returns>
        public static async Task<QueueData<T>?> GetQueueAsync(string queueId, string accountName, string accountKey)
            => await new DataAccess<QueueData<T>>(accountName, accountKey).GetRowObjectAsync(queueId);
        

        /// <summary>
        /// Gets a single queue by its ID (synchronous)
        /// </summary>
        /// <param name="queueId">The unique q identifier</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <returns>The QueueData object, or null if not found</returns>
        public static QueueData<T>? GetQueue(string queueId, string accountName, string accountKey)
            => new DataAccess<QueueData<T>>(accountName, accountKey).GetRowObject(queueId);       

        /// <summary>
        /// Gets a single queue without deleting it (alias for GetQueueAsync for clarity)
        /// </summary>
        public static async Task<QueueData<T>?> PeekQueueAsync(string queueId, string accountName, string accountKey)
            => await GetQueueAsync(queueId, accountName, accountKey);

        /// <summary>
        /// Gets all queues for a given name/category
        /// </summary>
        /// <param name="name">The name/category (PartitionKey)</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <returns>List of QueueData objects</returns>
        public static async Task<List<QueueData<T>>> GetQueuesAsync(string name, string accountName, string accountKey)
            => await new DataAccess<QueueData<T>>(accountName, accountKey).GetCollectionAsync(name);

        /// <summary>
        /// Gets all queues for a given name/category (synchronous)
        /// </summary>
        /// <param name="name">The name/category (PartitionKey)</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <returns>List of QueueData objects</returns>
        public static List<QueueData<T>> GetQueues(string name, string accountName, string accountKey)
            => new DataAccess<QueueData<T>>(accountName, accountKey).GetCollection(name);
        

        /// <summary>
        /// Gets and deletes a queue (for backward compatibility and one-shot processing)
        /// </summary>
        /// <param name="queueId">The unique q identifier</param>
        /// <param name="accountName">The Azure Account name</param>
        /// <param name="accountKey">The Azure Account key</param>
        /// <returns>The QueueData object, or null if not found</returns>
        public static async Task<QueueData<T>?> GetAndDeleteQueueAsync(string queueId, string accountName, string accountKey)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var queue = await da.GetRowObjectAsync(queueId);

            if (queue != null)
            {
                await da.ManageDataAsync(queue, TableOperationType.Delete);
            }

            return queue;
        }

        #endregion Single Queue Retrieval Methods

        #region Batch Operations

        /// <summary>
        /// Deletes a single queue by its ID
        /// </summary>
        public static async Task<bool> DeleteQueueAsync(string queueId, string accountName, string accountKey)
        {
            DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
            var queue = await da.GetRowObjectAsync(queueId);

            if (queue != null)
            {
                await da.ManageDataAsync(queue, TableOperationType.Delete);
                return true;
            }

            return false;
        }

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

        /// <summary>
        /// Deletes all queues in a category and returns the data (for migration/cleanup)
        /// </summary>
        public static async Task<List<StateList<T>>> DeleteAndReturnAllAsync(string name, string accountName, string accountKey)
        {
            var queues = await GetQueuesAsync(name, accountName, accountKey);
            var dataLists = queues.Select(q => q.Data).ToList();

            if (queues.Any())
            {
                DataAccess<QueueData<T>> da = new DataAccess<QueueData<T>>(accountName, accountKey);
                await da.BatchUpdateListAsync(queues, TableOperationType.Delete);
            }

            return dataLists;
        }

        #endregion Batch Operations

        #region Factory Methods

        /// <summary>
        /// Creates a new QueueData instance from a StateList
        /// </summary>
        public static QueueData<T> CreateFromStateList(StateList<T> stateList, string name, string? queueId = null)
        {
            return new QueueData<T>
            {
                QueueID = queueId ?? Guid.NewGuid().ToString(),
                Name = name,
                Data = stateList
            };
        }

        /// <summary>
        /// Creates a new QueueData instance from a List
        /// </summary>
        public static QueueData<T> CreateFromList(List<T> list, string name, string? queueId = null)
            => CreateFromStateList(list, name, queueId);

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
        public int CurrentIndex { get; set; } = -1;

        /// <summary>
        /// Description of the data contained within the collection - preserved through serialization
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("Description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the internal items for serialization
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("Items")]
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
        /// <param name="collection">Data to add</param>
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
        /// <param name="collection">Data to add</param>
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
        public ErrorLogData(Exception? e, string errDescription, ErrorCodeTypes severity, string cID = "undefined")
        {
            var callStackInfo = GetCallStackInfo();

            this.ApplicationName = callStackInfo.ApplicationName;
            this.ErrorSeverity = severity.ToString();
            this.ErrorMessage = string.IsNullOrWhiteSpace(errDescription)
                ? e?.Message ?? "No message provided."
                : $"{errDescription} {(e?.Message ?? "")}".Trim();
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
        /// Creates an ErrorLogData instance with caller information and formatted message.
        /// Attempts to thoroughly capture the error context including exception details and caller information.
        /// </summary>
        /// <typeparam name="TState">The type of the state object.</typeparam>
        /// <param name="state">The state object.</param>
        /// <param name="exception">The exception that occurred.</param>
        /// <param name="formatter">A function to format the error message.</param>
        /// <param name="severity">The severity level of the error.</param>
        /// <param name="customerId">The customer identifier associated with the error.</param>
        /// <param name="callerMemberName">Automatically captured caller member name.</param>
        /// <param name="callerFilePath">Automatically captured caller file path.</param>
        /// <param name="callerLineNumber">Automatically captured caller line number.</param>
        /// <returns>A new ErrorLogData instance.</returns>
        public static ErrorLogData CreateWithCallerInfo<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter,
            ErrorCodeTypes severity, string customerId = "undefined", [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var errorLog = new ErrorLogData();
            var callStackInfo = GetCallStackInfo();

            var baseMessage = formatter(state, exception);
            var fullMessage = exception != null
                ? $"{baseMessage}{Environment.NewLine}{GetFullExceptionDetails(exception)}"
                : baseMessage;

            errorLog.ApplicationName = callStackInfo.ApplicationName;
            errorLog.ErrorSeverity = severity.ToString();
            errorLog.ErrorMessage = fullMessage;
            errorLog.FunctionName = $"{callerMemberName} (Line: {callerLineNumber})";
            errorLog.CustomerID = customerId;

            return errorLog;
        }

        /// <summary>
        /// Retrieves full exception details including inner exceptions.
        /// </summary>
        /// <param name="ex">The exception to retrieve details from.</param>
        private static string GetFullExceptionDetails(Exception ex)
        {
            var sb = new StringBuilder();

            while (ex != null)
            {
                sb.AppendLine($"[{ex.GetType().Name}] {ex.Message}");
                sb.AppendLine(ex.StackTrace);
                ex = ex.InnerException!;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets detailed information from the current call stack
        /// </summary>
        /// <returns>Call stack information including application name and calling function</returns>
        private static (string ApplicationName, string CallingFunction) GetCallStackInfo()
        {
            try
            {
                var stackTrace = new StackTrace(skipFrames: 1, fNeedFileInfo: true);
                var frames = stackTrace.GetFrames();
                if (frames == null) return ("Unknown", "Unknown");

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var type = method?.DeclaringType;

                    if (type == null || type == typeof(ErrorLogData)) continue;
                    if (type.Name.Contains("Exception") || type.Name.Contains("Task")) continue;
                    if (method!.Name == "MoveNext") continue; // async state machine

                    var appName = type.Assembly.GetName().Name ?? "Unknown";
                    var className = type.Name;
                    var methodName = method.Name;
                    var line = frame.GetFileLineNumber();

                    var functionInfo = line > 0
                        ? $"{className}.{methodName} (Line: {line})"
                        : $"{className}.{methodName}";

                    return (appName, functionInfo);
                }

                return ("Unknown", "Unknown");
            }
            catch
            {
                return ("Unknown", "Unknown");
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
        public string TableReference => Constants.DefaultLogTableName;
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

    /// <summary>
    /// Maintains constant values used across the application.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// The default table name for application session data.
        /// </summary>
        public static readonly string DefaultSessionTableName = "AppSessionData";
        /// <summary>
        /// The default table name for application logs.
        /// </summary>
        public static readonly string DefaultLogTableName = "AppLoggingData";

        /// <summary>
        /// Maximum batch size for Azure Table operations
        /// </summary>
        public static readonly int MaxBatchSize = 100;

        /// <summary>
        /// Default retention period in days
        /// </summary>
        public static readonly int DefaultRetentionDays = 60;
    }

} // end namespace ASCTableStorage.Models
