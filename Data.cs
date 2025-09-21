using ASCTableStorage.Common;
using ASCTableStorage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ASCTableStorage.Data
{
    /// <summary>
    /// Describes the QueryComparisons Constants within the CosmosTables
    /// </summary>
    public enum ComparisonTypes
    {
        /// <summary>
        /// Equal to
        /// </summary>
        eq,
        /// <summary>
        /// Not Equal to
        /// </summary>
        ne,
        /// <summary>
        /// Greater than
        /// </summary>
        gt,
        /// <summary>
        /// Greater than or equal to
        /// </summary>
        ge,
        /// <summary>
        /// Less than
        /// </summary>
        lt,
        /// <summary>
        /// Less than or equal to
        /// </summary>
        le
    } // end enum ComparisonTypes

    /// <summary>
    /// Defines how the engine will combine search parameters with custom searches
    /// </summary>
    public enum QueryCombineStyle
    {
        and,
        or
    } // end enum QueryCombineStyle

    /// <summary>
    /// Defines how to create custom queries within the DB Tables
    /// </summary>
    public class DBQueryItem
    {
        /// <summary>
        /// Name of the field
        /// </summary>
        public string? FieldName;
        /// <summary>
        /// The value you're searching for
        /// </summary>
        public string? FieldValue;
        /// <summary>
        /// The way to compare the field and value
        /// </summary>
        public ComparisonTypes HowToCompare;
        /// <summary>
        /// DateTimes need special formatting
        /// </summary>
        public bool IsDateTime;
    } // end class DBQueryItem

    /// <summary>
    /// Enhanced DataAccess component that handles all CRUD operations to Azure Table Storage
    /// with unified async/sync support using async-first pattern, lambda expressions, proper batch processing, and pagination.
    /// </summary>
    /// <typeparam name="T">The DataType this Instance will work with in the DB</typeparam>
    /// <remarks>
    /// NOTE: One DataAccess Object PER Table operation otherwise you will be going against the wrong table instance.
    /// Create a NEW instance of this object for each table you want to interact with
    /// </remarks>
    public class DataAccess<T> where T : TableEntityBase, ITableExtra, new()
    {
        private readonly CloudTableClient m_Client;
        private const int DEFAULT_PAGE_SIZE = 100;
        private const int MAX_BATCH_SIZE = 100; // Azure Table Storage strict limit

        // New: Optional overrides
        private readonly string? _tableNameOverride;
        private readonly string? _partitionKeyPropertyName;

        #region Constructor Overloads

        /// <summary>
        /// Constructor allows for various connections independent of the config file. 
        /// Can be used for data migrations.
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure account key for the table store</param>
        public DataAccess(string accountName, string accountKey)
        {
            StorageCredentials cred = new StorageCredentials(accountName, accountKey);
            CloudStorageAccount csa = new CloudStorageAccount(cred, true);
            m_Client = csa.CreateCloudTableClient();
        }

        /// <summary>
        /// Constructor with table and storage account configuration.
        /// Resolves constructor ambiguity by using a single options object.
        /// </summary>
        /// <param name="options">The table and account configuration</param>
        public DataAccess(TableOptions options)
        {
            options.Validate();

            StorageCredentials cred = new StorageCredentials(options.TableStorageName, options.TableStorageKey);
            CloudStorageAccount csa = new CloudStorageAccount(cred, true);
            m_Client = csa.CreateCloudTableClient();

            _tableNameOverride = options.TableName;
            _partitionKeyPropertyName = options.PartitionKeyPropertyName;
        }

        #endregion Constructor Overloads

        #region Core Implementation Methods (Async-First)

        /// <summary>
        /// Core implementation for getting table reference
        /// </summary>
        private async Task<CloudTable> GetTableCore(string tableRef)
        {
            CloudTable ct = m_Client.GetTableReference(tableRef);
            await ct.CreateIfNotExistsAsync();
            return ct;
        }

        /// <summary>
        /// Resolves the table name to use for operations.
        /// Uses override if provided, otherwise uses ITableExtra.
        /// </summary>
        /// <returns>The table name to use</returns>
        private string ResolveTableName()
        {
            if (!string.IsNullOrWhiteSpace(_tableNameOverride))
                return _tableNameOverride;

            T obj = new();
            return !string.IsNullOrWhiteSpace(obj.TableReference)
                ? obj.TableReference
                : throw new InvalidOperationException($"Type {typeof(T).Name} must provide a valid TableReference.");
        }

        /// <summary>
        /// Resolves the partition key value from the entity.
        /// Uses ITableEntity first, then named property, then fallback.
        /// </summary>
        /// <param name="obj">The entity to extract PK from</param>
        /// <returns>The partition key value</returns>
        private string ResolvePartitionKey(T obj)
        {
            if (!string.IsNullOrWhiteSpace(obj.PartitionKey))
                return obj.PartitionKey;

            if (!string.IsNullOrWhiteSpace(_partitionKeyPropertyName))
            {
                var prop = TableEntityTypeCache.GetPropertyLookup(typeof(T))[_partitionKeyPropertyName];
                if (prop != null)
                {
                    var value = prop.GetValue(obj)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return ResolveTableName();
        }

        /// <summary>
        /// Resolves the row key value from the entity.
        /// Uses ITableEntity first, otherwise generates a new Guid.
        /// </summary>
        /// <param name="obj">The entity to extract RK from</param>
        /// <returns>The row key value</returns>
        private string ResolveRowKey(T obj)
        {
            return !string.IsNullOrWhiteSpace(obj.RowKey)
                ? obj.RowKey
                : Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Prepares the entity for storage by setting PartitionKey and RowKey if not already set.
        /// </summary>
        /// <param name="obj">The entity to prepare</param>
        private void PrepareEntity(object obj)
        {
            if (obj is DynamicEntity dynamicEntity)
            {
                // Set TableReference if not already set
                if (string.IsNullOrWhiteSpace(dynamicEntity.TableReference) && !string.IsNullOrWhiteSpace(_tableNameOverride))
                {
                    dynamicEntity.TableReference = _tableNameOverride;
                }

                // Only set PartitionKey if not already set AND we have a source property name
                if (string.IsNullOrWhiteSpace(dynamicEntity.PartitionKey) &&
                    !string.IsNullOrWhiteSpace(_partitionKeyPropertyName))
                {
                    // Try to get the value from the original object's properties (e.g., TestClass.OzPartitionData)
                    var props = TableEntityTypeCache.GetPropertyLookup(obj.GetType());
                    if (props.TryGetValue(_partitionKeyPropertyName, out PropertyInfo? pkProp) && pkProp.CanRead)
                    {
                        var pkValue = pkProp.GetValue(obj)?.ToString();
                        if (!string.IsNullOrWhiteSpace(pkValue))
                        {
                            dynamicEntity.SetPartitionKey(pkValue);
                        }
                    }
                }
            }
            else if (obj is T tObj)
            {
                // For regular T entities, use standard prep logic
                PrepareEntityPrep(tObj);
            }
        }

        private void PrepareEntityPrep(T obj)
        {
            obj.PartitionKey ??= ResolvePartitionKey(obj);
            obj.RowKey ??= ResolveRowKey(obj);
        }

        /// <summary>
        /// Core implementation for managing single entity data
        /// </summary>
        private async Task ManageDataCore(T obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            PrepareEntity(obj);
            TableOperation op = CreateTableOperation(obj, direction);
            CloudTable table = await GetTableCore(ResolveTableName());
            await table.ExecuteAsync(op);
        }

        private async Task ManageDataCore(DynamicEntity obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            PrepareEntity(obj);
            TableOperation op = CreateTableOperation(obj, direction);
            CloudTable table = await GetTableCore(obj.TableReference);
            await table.ExecuteAsync(op);
        }

        /// <summary>
        /// Core implementation for retrieving all table data
        /// </summary>
        private async Task<List<T>> GetAllTableDataCore()
        {
            TableQuery<T> q = new TableQuery<T>();
            return await GetCollectionCore(q);
        }

        /// <summary>
        /// Replaces specific operator names in the input string with their corresponding  Dynamic LINQ operator
        /// equivalents and removes parameter prefixes. 
        /// </summary>
        /// <param name="input">The input string containing operator names and parameter prefixes to be replaced.</param>
        /// <returns>A string where recognized operator names are replaced with their Dynamic LINQ equivalents  and parameter
        /// prefixes (e.g., "u.") are removed.</returns>
        /// <exception cref="NotSupportedException">Thrown if the input string contains an unsupported operator name.</exception>
        private static string ReplaceOperators(string input)
        {
            // Replace ExpressionType operators with Dynamic LINQ equivalents
            string cleaned = Regex.Replace(input, @"\b(Equal|OrElse|AndAlso|NotEqual|GreaterThan|LessThan)\b", match =>
            {
                return match.Value switch
                {
                    "Equal" => "==",
                    "OrElse" => "||",
                    "AndAlso" => "&&",
                    "NotEqual" => "!=",
                    "GreaterThan" => ">",
                    "LessThan" => "<",
                    _ => throw new NotSupportedException($"Unsupported operator: {match.Value}")
                };
            }, RegexOptions.IgnoreCase);

            // Remove parameter prefix (e.g., "u.")
            cleaned = Regex.Replace(cleaned, @"\bu\.", "", RegexOptions.IgnoreCase);

            return cleaned;
        }

        /// <summary>
        /// Core implementation for getting collection by partition key
        /// </summary>
        private async Task<List<T>> GetCollectionCore(string partitionKeyID)
        {
            string queryString = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, partitionKeyID);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return await GetCollectionCore(q);
        }

        /// <summary>
        /// Core implementation for getting collection by lambda expression with hybrid filtering
        /// </summary>
        private async Task<List<T>> GetCollectionCore(Expression<Func<T, bool>> predicate)
        {
            var hybridResult = ConvertLambdaToHybridFilter(predicate);

            TableQuery<T> query = new TableQuery<T>();
            if (!string.IsNullOrEmpty(hybridResult.ServerSideFilter))
                query = new TableQuery<T>().Where(hybridResult.ServerSideFilter);

            List<T> clientResults = new();
            List<T> serverResults = await GetCollectionCore(query);
            if (hybridResult.RequiresClientFiltering && hybridResult.ClientSidePredicate != null)
            {
                clientResults = serverResults.Where(hybridResult.ClientSidePredicate).ToList();
            }

            //return the smaller of the two result sets because that is actually what the client is looking for -- narrowed results
            //Also solves a bug because the ServerResults is always going to not compile with the ClientResults so this is a workaround
            return new[] { clientResults, serverResults}.Where(l => l.Count > 0).OrderBy(l => l.Count).FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for getting collection by query terms
        /// </summary>
        private async Task<List<T>> GetCollectionCore(List<DBQueryItem> queryTerms, QueryCombineStyle combineStyle = QueryCombineStyle.or)
        {
            string queryString = BuildQueryString(queryTerms, combineStyle);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return await GetCollectionCore(q);
        }

        /// <summary>
        /// Core implementation for getting collection by defined query
        /// </summary>
        private async Task<List<T>> GetCollectionCore(TableQuery<T> definedQuery)
        {
            return await GetCollectionCore(ResolveTableName(), definedQuery);
        }

        private async Task<List<T>> GetCollectionCore(string tableName, TableQuery<T> definedQuery)
        {
            CloudTable table = await GetTableCore(tableName);
            var results = new List<T>();
            TableContinuationToken token = null!;
            do
            {
                var segment = await table.ExecuteQuerySegmentedAsync(definedQuery, token);
                results.AddRange(segment);
                token = segment.ContinuationToken;
            } while (token != null);
            return results;
        }

        /// <summary>
        /// Core implementation for getting single row by RowKey
        /// </summary>
        private async Task<T> GetRowObjectCore(string rowKeyID)
        {
            string queryString = TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, rowKeyID);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            var results = await GetCollectionCore(q);
            return results.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for getting single row by field criteria
        /// </summary>
        private async Task<T> GetRowObjectCore(string fieldName, ComparisonTypes howToCompare, string fieldValue)
        {
            string queryString = $"{fieldName} {howToCompare} '{fieldValue}'";
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            var results = await GetCollectionCore(q);
            return results.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for getting single row by lambda expression with hybrid filtering
        /// </summary>
        private async Task<T> GetRowObjectCore(Expression<Func<T, bool>> predicate)
        {
            var results = await GetCollectionCore(predicate);
            return results.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for paginated collection retrieval with hybrid filtering support
        /// </summary>
        private async Task<PagedResult<T>> GetPagedCollectionCore(int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null!,
            TableQuery<T> definedQuery = null!, Expression<Func<T, bool>> predicate = null!)
        {
            CloudTable table = await GetTableCore(ResolveTableName());
            TableQuery<T> query;
            Func<T, bool> clientFilter = null!;
            if (predicate != null)
            {
                var hybridResult = ConvertLambdaToHybridFilter(predicate);
                if (!string.IsNullOrEmpty(hybridResult.ServerSideFilter))
                {
                    query = new TableQuery<T>().Where(hybridResult.ServerSideFilter);
                }
                else
                {
                    query = new TableQuery<T>();
                }
                if (hybridResult.RequiresClientFiltering)
                {
                    clientFilter = hybridResult.ClientSidePredicate!;
                }
            }
            else
            {
                query = definedQuery ?? new TableQuery<T>();
            }
            query = query.Take(pageSize);
            TableContinuationToken token = null!;
            if (!string.IsNullOrEmpty(continuationToken))
            {
                token = DeserializeContinuationToken(continuationToken);
            }
            var segment = await table.ExecuteQuerySegmentedAsync(query, token);
            var results = segment.ToList();
            if (clientFilter != null)
            {
                results = results.Where(clientFilter).ToList();
            }
            return new PagedResult<T>
            {
                Data = results,
                ContinuationToken = SerializeContinuationToken(segment.ContinuationToken),
                HasMore = segment.ContinuationToken != null,
                Count = results.Count
            };
        }

        /// <summary>
        /// Core implementation for batch operations
        /// </summary>
        private async Task<BatchUpdateResult> BatchUpdateListCore(List<T> data,
            TableOperationType direction = TableOperationType.InsertOrReplace,
            IProgress<BatchUpdateProgress> progressCallback = null!)
        {
            var result = new BatchUpdateResult();
            if (data == null || data.Count == 0)
            {
                result.Success = true;
                return result;
            }
            try
            {
                var groupedData = data.GroupBy(g => ResolvePartitionKey(g)).ToList();
                var totalBatches = groupedData.Sum(group => (int)Math.Ceiling((double)group.Count() / MAX_BATCH_SIZE));
                var completedBatches = 0;
                foreach (var group in groupedData)
                {
                    var groupList = group.ToList();
                    for (int i = 0; i < groupList.Count; i += MAX_BATCH_SIZE)
                    {
                        var batchSize = Math.Min(MAX_BATCH_SIZE, groupList.Count - i);
                        var batchItems = groupList.GetRange(i, batchSize);
                        if (batchItems.Count > MAX_BATCH_SIZE)
                        {
                            throw new InvalidOperationException($"Batch size {batchItems.Count} exceeds Azure Table Storage limit of {MAX_BATCH_SIZE}");
                        }
                        try
                        {
                            foreach (var item in batchItems)
                                PrepareEntity(item);
                            await ProcessBatchCore(batchItems, direction);
                            result.SuccessfulItems += batchSize;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Batch starting at index {i} (size: {batchSize}): {ex.Message}");
                            result.FailedItems += batchSize;
                        }
                        completedBatches++;
                        progressCallback?.Report(new BatchUpdateProgress
                        {
                            CompletedBatches = completedBatches,
                            TotalBatches = totalBatches,
                            ProcessedItems = result.SuccessfulItems + result.FailedItems,
                            TotalItems = data.Count,
                            CurrentBatchSize = batchSize
                        });
                    }
                }
                result.Success = result.FailedItems == 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"General batch update error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Core implementation for batch operations on DynamicEntity objects.
        /// Converts each DynamicEntity to T using the configured PartitionKeyPropertyName from TableOptions.
        /// Reuses the existing batch core for final processing.
        /// </summary>
        /// <param name="data">The collection of DynamicEntity objects to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <param name="progressCallback">Optional callback to track progress</param>
        /// <returns>Result indicating success and any errors encountered</returns>
        private async Task<BatchUpdateResult> BatchUpdateListCore(List<DynamicEntity> data,
            TableOperationType direction = TableOperationType.InsertOrReplace,
            IProgress<BatchUpdateProgress> progressCallback = null!)
        {
            if (data == null || data.Count == 0)
                return new BatchUpdateResult { Success = true };

            // Convert List<DynamicEntity> to List<T>
            var convertedList = new List<T>();
            foreach (var de in data)
            {
                // Create a new instance of T
                T instance = Activator.CreateInstance<T>();

                // If T is DynamicEntity, just add it
                if (instance is DynamicEntity)
                {
                    convertedList.Add((T)(object)de);
                }
                // Otherwise, hydrate T from DynamicEntity properties
                else
                {
                    var props = de.GetAllProperties();
                    var targetProps = TableEntityTypeCache.GetPropertyLookup(typeof(T));

                    foreach (var kvp in props)
                    {
                        if (targetProps.TryGetValue(kvp.Key, out PropertyInfo? prop) && prop.CanWrite)
                        {
                            try
                            {
                                if (kvp.Value != null && prop.PropertyType.IsAssignableFrom(kvp.Value.GetType()))
                                {
                                    prop.SetValue(instance, kvp.Value);
                                }
                                else if (kvp.Value != null)
                                {
                                    prop.SetValue(instance, Convert.ChangeType(kvp.Value, prop.PropertyType));
                                }
                            }
                            catch { /* ignore conversion errors */ }
                        }
                    }

                    // Set RowKey if not already set
                    if (string.IsNullOrWhiteSpace(instance.RowKey) && !string.IsNullOrWhiteSpace(de.RowKey))
                        instance.RowKey = de.RowKey;

                    // Set PartitionKey: prefer value from _partitionKeyPropertyName if available
                    if (string.IsNullOrWhiteSpace(instance.PartitionKey) &&
                        !string.IsNullOrWhiteSpace(_partitionKeyPropertyName) &&
                        props.TryGetValue(_partitionKeyPropertyName, out object? pkValue) &&
                        pkValue != null)
                    {
                        instance.PartitionKey = pkValue.ToString();
                    }
                    // Fallback: use de.PartitionKey if set
                    else if (string.IsNullOrWhiteSpace(instance.PartitionKey) && !string.IsNullOrWhiteSpace(de.PartitionKey))
                    {
                        instance.PartitionKey = de.PartitionKey;
                    }
                }

                convertedList.Add(instance);
            }

            // Reuse the existing core method
            return await BatchUpdateListCore(convertedList, direction, progressCallback);
        } // end BatchUpdateListCore DynamicEntity

        /// <summary>
        /// Core implementation for processing a single batch
        /// </summary>
        private async Task ProcessBatchCore(List<T> batchItems, TableOperationType direction)
        {
            if (batchItems == null || batchItems.Count == 0) return;
            if (batchItems.Count > MAX_BATCH_SIZE)
            {
                throw new InvalidOperationException($"Batch contains {batchItems.Count} items, exceeding Azure Table Storage limit of {MAX_BATCH_SIZE}");
            }
            var tableBatch = new TableBatchOperation();
            foreach (var item in batchItems)
            {
                PrepareEntity(item);
                tableBatch.Add(CreateTableOperation(item, direction));
            }
            if (tableBatch.Count > MAX_BATCH_SIZE)
            {
                throw new InvalidOperationException($"TableBatchOperation contains {tableBatch.Count} operations, exceeding limit of {MAX_BATCH_SIZE}");
            }
            var table = await GetTableCore(ResolveTableName());
            await table.ExecuteBatchAsync(tableBatch);
        }

        #endregion Core Implementation Methods

        #region Public Synchronous Methods (Wrap Core)

        /// <summary>
        /// CRUD for a single entity. Replace is the default Action (synchronous)
        /// </summary>
        /// <param name="obj">The Entity to manage</param>
        /// <param name="direction">The direction of the data</param>
        public void ManageData(T obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            ManageDataCore(obj, direction).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Manages a single custom entity of any type by converting it to a DynamicEntity first.
        /// </summary>
        /// <param name="obj">The object to manage</param>
        /// <param name="direction">The operation to perform</param>
        /// <returns>Task representing the operation</returns>
        public void ManageData(object obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            string tableName = _tableNameOverride ?? ResolveTableName();
            DynamicEntity de = DynamicEntity.CreateFromObject(obj, tableName, _partitionKeyPropertyName);
            ManageDataCore(de, direction).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Retrieves All Data from the underlying Table (synchronous)
        /// </summary>
        public List<T> GetAllTableData()
        {
            return GetAllTableDataCore().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a list of Objects by PartitionKey (synchronous)
        /// </summary>
        /// <param name="partitionKeyID">The value that should be found in the PartitionKey space</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(string partitionKeyID)
        {
            return GetCollectionCore(partitionKeyID).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a list of Objects using a lambda expression with hybrid server/client filtering (synchronous)
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        /// <example>
        /// var results = dataAccess.GetCollection(x => x.Status == "Active" && x.CreatedDate > DateTime.Today.AddDays(-30));
        /// // Complex expressions like ToLower().Contains() are automatically handled with hybrid filtering
        /// var companies = dataAccess.GetCollection(c => c.CompanyName.ToLower().Contains("test") || c.CompanyID == "123");
        /// </example>
        public List<T> GetCollection(Expression<Func<T, bool>> predicate)
        {
            return GetCollectionCore(predicate).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Allows for dynamic generation of complicated queries (synchronous)
        /// </summary>
        /// <param name="queryTerms">A list of search definitions</param>
        /// <param name="combineStyle">How multiple search definitions should be combined</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(List<DBQueryItem> queryTerms, QueryCombineStyle combineStyle = QueryCombineStyle.or)
        {
            return GetCollectionCore(queryTerms, combineStyle).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a list of Objects based on defined query (synchronous)
        /// </summary>
        /// <param name="definedQuery">The Query Definition to run</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(TableQuery<T> definedQuery)
        {
            return GetCollectionCore(definedQuery).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets a specific row of data from the table by RowKey (synchronous)
        /// </summary>
        /// <param name="rowKeyID">The RowKey in the table to grab</param>
        public T GetRowObject(string rowKeyID)
        {
            return GetRowObjectCore(rowKeyID).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets a specific row of data based on field criteria (synchronous)
        /// </summary>
        /// <param name="fieldName">Name of the field to compare</param>
        /// <param name="howToCompare">Direction of the comparison</param>
        /// <param name="fieldValue">The value the field should have</param>
        public T GetRowObject(string fieldName, ComparisonTypes howToCompare, string fieldValue)
        {
            return GetRowObjectCore(fieldName, howToCompare, fieldValue).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets a specific row using a lambda expression with hybrid filtering (synchronous)
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <returns>First matching entity or null</returns>
        /// <example>
        /// var user = dataAccess.GetRowObject(x => x.Email == "user@example.com" && x.IsActive);
        /// // Complex expressions are automatically handled
        /// var company = dataAccess.GetRowObject(c => c.CompanyName.ToLower().Contains("microsoft"));
        /// </example>
        public T GetRowObject(Expression<Func<T, bool>> predicate)
        {
            return GetRowObjectCore(predicate).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous version of batch update for List<typeparamref name="T"/>
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <returns>Success indicator</returns>
        public bool BatchUpdateList(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            var result = BatchUpdateListCore(data, direction).GetAwaiter().GetResult();
            return result.Success;
        }

        /// <summary>
        /// Synchronous version of batch update for DynamicEntity
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <returns>Success indicator</returns>
        public bool BatchUpdateList(List<DynamicEntity> data, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            var result = BatchUpdateListCore(data, direction).GetAwaiter().GetResult();
            return result.Success;
        }

        #endregion Public Synchronous Methods

        #region Public Asynchronous Methods (Call Core Directly)

        /// <summary>
        /// CRUD for a single entity. Replace is the default Action (asynchronous)
        /// </summary>
        /// <param name="obj">The Entity to manage</param>
        /// <param name="direction">The direction of the data</param>
        public Task ManageDataAsync(T obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            return ManageDataCore(obj, direction);
        }

        /// <summary>
        /// Manages a single custom entity of any type by converting it to a DynamicEntity first.
        /// </summary>
        /// <param name="obj">The object to manage</param>
        /// <param name="direction">The operation to perform</param>
        /// <returns>Task representing the operation</returns>
        public async Task ManageDataAsync(object obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            string tableName = _tableNameOverride ?? ResolveTableName();
            DynamicEntity de = DynamicEntity.CreateFromObject(obj, tableName, _partitionKeyPropertyName);
            await ManageDataCore(de, direction);
        }

        /// <summary>
        /// Retrieves All Data from the underlying Table (asynchronous)
        /// </summary>
        public Task<List<T>> GetAllTableDataAsync()
        {
            return GetAllTableDataCore();
        }

        /// <summary>
        /// Returns a list of Objects by PartitionKey (asynchronous)
        /// </summary>
        /// <param name="partitionKeyID">The value that should be found in the PartitionKey space</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public Task<List<T>> GetCollectionAsync(string partitionKeyID)
        {
            return GetCollectionCore(partitionKeyID);
        }

        /// <summary>
        /// Returns a list of Objects using a lambda expression with hybrid server/client filtering (asynchronous)
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        /// <example>
        /// var results = await dataAccess.GetCollectionAsync(x => x.Status == "Active" && x.CreatedDate > DateTime.Today.AddDays(-30));
        /// // Complex expressions like ToLower().Contains() are automatically handled with hybrid filtering
        /// var companies = await dataAccess.GetCollectionAsync(c => c.CompanyName.ToLower().Contains("test") || c.CompanyID == "123");
        /// </example>
        public Task<List<T>> GetCollectionAsync(Expression<Func<T, bool>> predicate)
        {
            return GetCollectionCore(predicate);
        }

        /// <summary>
        /// Allows for dynamic generation of complicated queries (asynchronous)
        /// </summary>
        /// <param name="queryTerms">A list of search definitions</param>
        /// <param name="combineStyle">How multiple search definitions should be combined</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public Task<List<T>> GetCollectionAsync(List<DBQueryItem> queryTerms, QueryCombineStyle combineStyle = QueryCombineStyle.or)
        {
            return GetCollectionCore(queryTerms, combineStyle);
        }

        /// <summary>
        /// Returns a list of Objects based on defined query (asynchronous)
        /// </summary>
        /// <param name="definedQuery">The Query Definition to run</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public Task<List<T>> GetCollectionAsync(TableQuery<T> definedQuery)
        {
            return GetCollectionCore(definedQuery);
        }

        /// <summary>
        /// Gets a specific row of data from the table by RowKey (asynchronous)
        /// </summary>
        /// <param name="rowKeyID">The RowKey in the table to grab</param>
        public Task<T> GetRowObjectAsync(string rowKeyID)
        {
            return GetRowObjectCore(rowKeyID);
        }

        /// <summary>
        /// Gets a specific row of data based on field criteria (asynchronous)
        /// </summary>
        /// <param name="fieldName">Name of the field to compare</param>
        /// <param name="howToCompare">Direction of the comparison</param>
        /// <param name="fieldValue">The value the field should have</param>
        public Task<T> GetRowObjectAsync(string fieldName, ComparisonTypes howToCompare, string fieldValue)
        {
            return GetRowObjectCore(fieldName, howToCompare, fieldValue);
        }

        /// <summary>
        /// Gets a specific row using a lambda expression with hybrid filtering (asynchronous)
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <returns>First matching entity or null</returns>
        /// <example>
        /// var user = await dataAccess.GetRowObjectAsync(x => x.Email == "user@example.com" && x.IsActive);
        /// // Complex expressions are automatically handled
        /// var company = await dataAccess.GetRowObjectAsync(c => c.CompanyName.ToLower().Contains("microsoft"));
        /// </example>
        public Task<T> GetRowObjectAsync(Expression<Func<T, bool>> predicate)
        {
            return GetRowObjectCore(predicate);
        }

        /// <summary>
        /// Asynchronously batch commits all data with strict 100-record limit enforcement for List<typeparamref name="T"/>
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <param name="progressCallback">Optional callback to track progress</param>
        /// <returns>Result indicating success and any errors encountered</returns>
        public Task<BatchUpdateResult> BatchUpdateListAsync(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace, IProgress<BatchUpdateProgress> progressCallback = null)
        {
            return BatchUpdateListCore(data, direction, progressCallback);
        }

        /// <summary>
        /// Asynchronously batch commits all data with strict 100-record limit enforcement for DynamicEntity objects.
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <param name="progressCallback">Optional callback to track progress</param>
        /// <returns>Result indicating success and any errors encountered</returns>
        public Task<BatchUpdateResult> BatchUpdateListAsync(List<DynamicEntity> data, TableOperationType direction = TableOperationType.InsertOrReplace, IProgress<BatchUpdateProgress> progressCallback = null)
        {
            return BatchUpdateListCore(data, direction, progressCallback);
        }

        #endregion Public Asynchronous Methods

        #region Pagination Support

        /// <summary>
        /// Represents a paginated result set
        /// </summary>
        public class PagedResult<T>
        {
            /// <summary>
            /// The data items in the current page
            /// </summary>
            public List<T> Data { get; set; } = new();
            /// <summary>
            /// The continuation token for the next page
            /// </summary>
            public string? ContinuationToken { get; set; }
            /// <summary>
            /// Determines if there are more pages available
            /// </summary>
            public bool HasMore { get; set; }
            /// <summary>
            /// The total count of items in the collection
            /// </summary>
            public int Count { get; set; }
        } // end class PagedResult<T>

        /// <summary>
        /// Gets a paginated collection of data (asynchronous)
        /// </summary>
        /// <param name="pageSize">Number of items per page (default: 100)</param>
        /// <param name="continuationToken">Token to continue from previous page (null for first page)</param>
        /// <param name="definedQuery">Optional query to filter results</param>
        /// <returns>Paginated result with items and continuation information</returns>
        public Task<PagedResult<T>> GetPagedCollectionAsync(int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null!, TableQuery<T> definedQuery = null!)
        {
            return GetPagedCollectionCore(pageSize, continuationToken, definedQuery);
        }

        /// <summary>
        /// Gets a paginated collection by PartitionKey (asynchronous)
        /// </summary>
        /// <param name="partitionKeyID">The partition key to filter by</param>
        /// <param name="pageSize">Number of items per page</param>
        /// <param name="continuationToken">Token to continue from previous page</param>
        /// <returns>Paginated result</returns>
        public async Task<PagedResult<T>> GetPagedCollectionAsync(string partitionKeyID, int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null)
        {
            string queryString = TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, partitionKeyID);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return await GetPagedCollectionCore(pageSize, continuationToken, q);
        }

        /// <summary>
        /// Gets a paginated collection using a lambda expression with hybrid filtering (asynchronous)
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <param name="pageSize">Number of items per page</param>
        /// <param name="continuationToken">Token to continue from previous page</param>
        /// <returns>Paginated result</returns>
        /// <example>
        /// var page = await dataAccess.GetPagedCollectionAsync(x => x.Status == "Active", 50);
        /// // Complex expressions are automatically handled with hybrid filtering
        /// var companies = await dataAccess.GetPagedCollectionAsync(c => c.CompanyName.ToLower().Contains("test"), 25);
        /// </example>
        public Task<PagedResult<T>> GetPagedCollectionAsync(Expression<Func<T, bool>> predicate, int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null!)
        {
            return GetPagedCollectionCore(pageSize, continuationToken, null!, predicate);
        }

        /// <summary>
        /// Gets an initial quick load of data followed by background loading
        /// </summary>
        /// <param name="initialLoadSize">Size of initial quick load (default: 100)</param>
        /// <param name="definedQuery">Optional query to filter results</param>
        /// <returns>Initial page result</returns>
        public Task<PagedResult<T>> GetInitialDataLoadAsync(int initialLoadSize = DEFAULT_PAGE_SIZE, TableQuery<T> definedQuery = null!)
        {
            return GetPagedCollectionCore(initialLoadSize, null, definedQuery);
        }

        /// <summary>
        /// Gets an initial quick load using lambda expression with hybrid filtering
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <param name="initialLoadSize">Size of initial quick load</param>
        /// <returns>Initial page result</returns>
        public Task<PagedResult<T>> GetInitialDataLoadAsync(Expression<Func<T, bool>> predicate, int initialLoadSize = DEFAULT_PAGE_SIZE)
        {
            return GetPagedCollectionCore(initialLoadSize, null!, null!, predicate);
        }

        #endregion Pagination Support

        #region Helper Methods and Support Classes

        /// <summary>
        /// Helper method to create table operations
        /// </summary>
        private TableOperation CreateTableOperation(T obj, TableOperationType direction)
        {
            return direction switch
            {
                TableOperationType.Delete => TableOperation.Delete(obj),
                TableOperationType.InsertOrMerge => TableOperation.InsertOrMerge(obj),
                _ => TableOperation.InsertOrReplace(obj)
            };
        }

        private static TableOperation CreateTableOperation(DynamicEntity obj, TableOperationType direction)
        {
            return direction switch
            {
                TableOperationType.Delete => TableOperation.Delete(obj),
                TableOperationType.InsertOrMerge => TableOperation.InsertOrMerge(obj),
                _ => TableOperation.InsertOrReplace(obj)
            };
        }

        /// <summary>
        /// Builds a query string from query terms
        /// </summary>
        private string BuildQueryString(List<DBQueryItem> queryTerms, QueryCombineStyle combineStyle)
        {
            var queryString = new StringBuilder();
            foreach (var qItem in queryTerms)
            {
                if (queryString.Length > 0)
                    queryString.Append($" {combineStyle} ");
                queryString.Append($"{qItem.FieldName} {qItem.HowToCompare}");
                if (qItem.IsDateTime)
                    queryString.Append($" datetime'{qItem.FieldValue}'");
                else
                    queryString.Append($" '{qItem.FieldValue}'");
            }
            return queryString.ToString();
        }

        /// <summary>
        /// Serializes continuation token for pagination
        /// </summary>
        private string SerializeContinuationToken(TableContinuationToken token)
        {
            if (token == null) return null!;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token.NextPartitionKey}|{token.NextRowKey}"));
        }

        /// <summary>
        /// Deserializes continuation token for pagination
        /// </summary>
        private TableContinuationToken DeserializeContinuationToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null!;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                var parts = decoded.Split('|');
                return new TableContinuationToken
                {
                    NextPartitionKey = parts.Length > 0 ? parts[0] : null,
                    NextRowKey = parts.Length > 1 ? parts[1] : null
                };
            }
            catch
            {
                return null!;
            }
        }

        /// <summary>
        /// Batch update result information
        /// </summary>
        public class BatchUpdateResult
        {
            public bool Success { get; set; }
            public int SuccessfulItems { get; set; }
            public int FailedItems { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        } // end class BatchUpdateResult

        /// <summary>
        /// Batch update progress information
        /// </summary>
        public class BatchUpdateProgress
        {
            public int CompletedBatches { get; set; }
            public int TotalBatches { get; set; }
            public int ProcessedItems { get; set; }
            public int TotalItems { get; set; }
            public int CurrentBatchSize { get; set; }
            public double PercentComplete => TotalItems > 0 ? (double)ProcessedItems / TotalItems * 100 : 0;
        } // end class BatchUpdateProgress

        #endregion Helper Methods and Support Classes

        #region Lambda Expression Processing

        /// <summary>
        /// Converts a lambda expression to OData filter string for Azure Table Storage
        /// with hybrid server/client-side filtering for maximum efficiency
        /// </summary>
        /// <param name="predicate">The lambda expression to convert</param>
        /// <returns>Hybrid filter result containing server filter and client predicate</returns>
        private HybridFilterResult<T> ConvertLambdaToHybridFilter(Expression<Func<T, bool>> predicate)
        {
            var builder = new ODataFilterBuilder();
            var result = builder.BuildHybridFilter(predicate.Body);

            Func<T, bool>? clientPredicate = null;

            if (result.HasClientSideOperations)
            {
                if (typeof(T) == typeof(DynamicEntity))
                {
                    clientPredicate = _ => true;
                    System.Diagnostics.Debug.WriteLine("DynamicEntity expression requires client-side filtering - returning all results");
                }
                else
                {
                    try
                    {
                        // Attempt to compile only method calls that aren't ToString()
                        var methodPredicates = CompileNonToStringMethods(predicate, builder);
                        if (methodPredicates.Count > 0)
                        {
                            // Combine all compiled method predicates into a single delegate
                            clientPredicate = entity => methodPredicates.All(p => p(entity));
                        }
                        else
                        {
                            // Fallback to full predicate compilation
                            clientPredicate = predicate.Compile();
                        }

                        // Validate compiled predicate
                        try
                        {
                            T testEntity = Activator.CreateInstance<T>();
                            var testResult = clientPredicate(testEntity);
                        }
                        catch (Exception testEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Compiled predicate is not executable: {testEx.Message}");
                            clientPredicate = _ => true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to compile predicate: {ex.Message}");
                        clientPredicate = _ => true;
                    }
                }
            }

            return new HybridFilterResult<T>
            {
                ServerSideFilter = result.ServerSideOData,
                ClientSidePredicate = clientPredicate,
                RequiresClientFiltering = result.HasClientSideOperations
            };
        }

        /// <summary>
        /// Extracts and compiles all method call expressions from a predicate that invoke methods
        /// other than <c>ToString()</c>. This enables partial client-side evaluation of filters
        /// that cannot be translated to server-side OData queries.
        /// </summary>
        /// <remarks>
        /// This method avoids compiling the entire predicate, which may include unsupported constructs
        /// or brittle logic. Instead, it walks the expression tree recursively and isolates method calls
        /// that are safe and meaningful for client-side filtering. Calls to <c>ToString()</c> are excluded
        /// to reduce noise and avoid redundant evaluations.
        /// </remarks>
        /// <typeparam name="T">The entity type being filtered.</typeparam>
        /// <param name="predicate">The original predicate expression to inspect.</param>
        /// <param name="builder">The OData filter builder instance used for constructing OData queries.</param>
        /// <returns>
        /// A list of compiled delegates representing each non-<c>ToString()</c> method call found
        /// in the expression tree. These can be combined or evaluated independently for client-side filtering.
        /// </returns>
        private static List<Func<T, bool>> CompileNonToStringMethods<T>(Expression<Func<T, bool>> predicate, ODataFilterBuilder builder)
        {
            var compiledList = new List<Func<T, bool>>();
            var param = predicate.Parameters[0];

            void Traverse(Expression expr)
            {
                switch (expr)
                {
                    case MethodCallExpression methodCall:
                        if (methodCall.Method.Name != "ToString" &&
                            methodCall.Method.DeclaringType != typeof(object) &&
                            methodCall.Type == typeof(bool))
                        {
                            try
                            {
                                builder.Parameter = param;
                                var reboundCall = (MethodCallExpression)builder.Visit(methodCall);
                                var lambda = Expression.Lambda<Func<T, bool>>(reboundCall, param);
                                compiledList.Add(lambda.Compile());
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to compile method call '{methodCall.Method.Name}': {ex.Message}");
                            }
                        }
                        break;

                    case BinaryExpression binary:
                        Traverse(binary.Left);
                        Traverse(binary.Right);
                        break;

                    case UnaryExpression unary:
                        Traverse(unary.Operand);
                        break;

                    case MemberExpression member:
                        Traverse(member.Expression!);
                        break;

                    case ConditionalExpression conditional:
                        Traverse(conditional.Test);
                        Traverse(conditional.IfTrue);
                        Traverse(conditional.IfFalse);
                        break;

                    case InvocationExpression invocation:
                        Traverse(invocation.Expression);
                        foreach (var arg in invocation.Arguments)
                            Traverse(arg);
                        break;

                    case LambdaExpression lambda:
                        Traverse(lambda.Body);
                        break;

                    case NewArrayExpression newArray:
                        foreach (var exprItem in newArray.Expressions)
                            Traverse(exprItem);
                        break;

                    case ListInitExpression listInit:
                        Traverse(listInit.NewExpression);
                        foreach (var init in listInit.Initializers)
                            foreach (var arg in init.Arguments)
                                Traverse(arg);
                        break;

                    case MemberInitExpression memberInit:
                        Traverse(memberInit.NewExpression);
                        foreach (var binding in memberInit.Bindings.OfType<MemberAssignment>())
                            Traverse(binding.Expression);
                        break;

                    case BlockExpression block:
                        foreach (var exprItem in block.Expressions)
                            Traverse(exprItem);
                        break;

                        // Add more cases as needed for completeness
                }
            }

            Traverse(predicate.Body);
            return compiledList;
        }

        /// <summary>
        /// Result of hybrid filter processing
        /// </summary>
        private class HybridFilterResult<TEntity>
        {
            public string? ServerSideFilter { get; set; }
            public Func<TEntity, bool>? ClientSidePredicate { get; set; }
            public bool RequiresClientFiltering { get; set; }
        } // end class HybridFilterResult<TEntity>


        /// <summary>
        /// Expression visitor that converts a LINQ expression tree into an OData $filter string,
        /// while detecting parts that must be evaluated client-side.
        /// Supports closure-captured variables (e.g., local variables in lambdas).
        /// </summary>
        private class ODataFilterBuilder : ExpressionVisitor
        {
            private StringBuilder _filter = new();
            private bool _hasClientSideOperations = false;

            /// <summary>
            /// The parameter expression representing the lambda parameter (e.g., 'd' in 'd => d.Name == "test"').
            /// Must be set before calling BuildHybridFilter if rebinding is needed.
            /// </summary>
            public ParameterExpression? Parameter { get; set; }

            /// <summary>
            /// Analyzes the expression and returns a server-side OData filter string and a flag indicating
            /// whether any part of the expression requires client-side evaluation.
            /// </summary>
            /// <param name="expression">The expression tree to analyze (e.g., d => d.Name == "John").</param>
            /// <returns>Result containing OData filter string and client-side flag.</returns>
            public FilterAnalysisResult BuildHybridFilter(Expression expression)
            {
                _filter.Clear();
                _hasClientSideOperations = false;

                // Normalize the expression: replace closure references with constants
                var normalized = NormalizeClosureExpressions(expression);
                Visit(normalized);

                return new FilterAnalysisResult
                {
                    ServerSideOData = _filter.Length > 0 ? _filter.ToString() : null,
                    HasClientSideOperations = _hasClientSideOperations
                };
            }

            /// <summary>
            /// Converts an expression directly into an OData filter string (legacy support).
            /// Returns empty string if no server-side part exists.
            /// </summary>
            /// <param name="expression">The expression to convert.</param>
            /// <returns>OData $filter string, or empty if fully client-side.</returns>
            public string Build(Expression expression)
            {
                var result = BuildHybridFilter(expression);
                return result.ServerSideOData ?? "";
            }

            /// <summary>
            /// Handles binary operations (==, !=, &&, ||, etc.).
            /// Splits AND/OR into hybrid-aware branches to allow partial server-side filtering.
            /// </summary>
            protected override Expression VisitBinary(BinaryExpression node)
            {
                return node.NodeType switch
                {
                    ExpressionType.AndAlso => HandleLogicalAndOr(node),
                    ExpressionType.OrElse => HandleLogicalAndOr(node),
                    _ => HandleSimpleBinary(node)
                };
            }

            /// <summary>
            /// Processes logical AND/OR by analyzing each branch independently.
            /// Allows combining server-side filters while preserving client-side requirements.
            /// </summary>
            private Expression HandleLogicalAndOr(BinaryExpression node)
            {
                var leftResult = AnalyzeBranch(node.Left);
                var rightResult = AnalyzeBranch(node.Right);

                var serverParts = new List<string>();
                if (!string.IsNullOrEmpty(leftResult.ServerSideOData))
                    serverParts.Add($"({leftResult.ServerSideOData})");
                if (!string.IsNullOrEmpty(rightResult.ServerSideOData))
                    serverParts.Add($"({rightResult.ServerSideOData})");

                // Append server-side parts with correct operator
                foreach (var part in serverParts)
                {
                    if (_filter.Length > 0) _filter.Append(node.NodeType == ExpressionType.AndAlso ? " and " : " or ");
                    _filter.Append(part);
                }

                // If either side needs client-side eval, mark entire expression as client-side
                _hasClientSideOperations = leftResult.HasClientSideOperations || rightResult.HasClientSideOperations;

                return node;
            }

            /// <summary>
            /// Handles simple binary operations (==, !=, <, >, etc.).
            /// Only emits OData if both sides are server-compatible.
            /// </summary>
            private Expression HandleSimpleBinary(BinaryExpression node)
            {
                if (IsSupportedOperator(node.NodeType) && IsServerSupported(node.Left) && IsServerSupported(node.Right))
                {
                    _filter.Append("(");
                    Visit(node.Left);
                    _filter.Append($" {ConvertOperator(node.NodeType)} ");
                    Visit(node.Right);
                    _filter.Append(")");
                }
                else
                {
                    _hasClientSideOperations = true;
                }
                return node;
            }

            /// <summary>
            /// Visits member expressions (e.g., d.Property or closure.Value).
            /// Resolves closure-captured values into constants.
            /// Rebinds known members to the target parameter if possible.
            /// </summary>
            protected override Expression VisitMember(MemberExpression node)
            {
                // Case 1: Direct access to parameter (e.g., d.BaseUrl)
                if (node.Expression == Parameter || (node.Expression is ParameterExpression))
                {
                    _filter.Append(node.Member.Name);
                    return node;
                }

                // Case 2: Access to closure field (e.g., value(...).userPartitionKey)
                if (node.Expression is ConstantExpression constExpr && constExpr.Value != null)
                {
                    var field = node.Member as FieldInfo;
                    if (field != null)
                    {
                        var value = field.GetValue(constExpr.Value);
                        var constant = Expression.Constant(value, node.Type);
                        Visit(constant); // Will call VisitConstant
                        return constant;
                    }
                }

                // Case 3: Any other member access (e.g., d.Nested.Prop) not directly supported
                _hasClientSideOperations = true;
                return node;
            }

            /// <summary>
            /// Visits constant values and appends them in OData-safe format.
            /// Handles strings, nulls, dates, booleans, GUIDs, and numbers.
            /// </summary>
            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node.Value == null)
                {
                    _filter.Append("'__null__'");
                }
                else if (node.Type == typeof(string))
                {
                    _filter.Append($"'{EscapeODataString(node.Value.ToString()!)}'");
                }
                else if (node.Type == typeof(DateTime) || node.Type == typeof(DateTime?))
                {
                    var dt = (DateTime)node.Value;
                    _filter.Append($"datetime'{dt:yyyy-MM-ddTHH:mm:ss.fffZ}'");
                }
                else if (node.Type == typeof(bool) || node.Type == typeof(bool?))
                {
                    _filter.Append(node.Value.ToString()!.ToLower());
                }
                else if (node.Type == typeof(Guid) || node.Type == typeof(Guid?))
                {
                    _filter.Append($"guid'{node.Value}'");
                }
                else if (IsNumericType(node.Type))
                {
                    _filter.Append(node.Value.ToString());
                }
                else
                {
                    // Fallback for other types (e.g., enums)
                    _filter.Append($"'{EscapeODataString(node.Value.ToString()!)}'");
                }

                return node;
            }

            /// <summary>
            /// Visits method calls and determines whether they can be translated to OData.
            /// Only allows .ToString() on direct property/indexer access in equality checks.
            /// All other methods (Contains, StartsWith, etc.) go client-side.
            /// </summary>
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Handle DynamicEntity indexer: d["Field"]
                if (IsIndexerAccess(node))
                {
                    if (node.Method.Name == "ToString" && node.Arguments.Count == 0 && IsDirectComparison(node))
                    {
                        var indexer = (MethodCallExpression)node.Object!;
                        var keyArg = (ConstantExpression)indexer.Arguments[0];
                        _filter.Append(keyArg.Value!.ToString());
                        return node;
                    }
                    _hasClientSideOperations = true;
                    return node;
                }

                // Handle POCO property method: u.Field.ToString()
                if (IsPropertyAccess(node.Object!))
                {
                    if (node.Method.Name == "ToString" && node.Arguments.Count == 0 && IsDirectComparison(node))
                    {
                        Visit(node.Object);
                        return node;
                    }
                    _hasClientSideOperations = true;
                    return node;
                }

                // Handle static string checks
                if (node.Method.DeclaringType == typeof(string) && node.Method.IsStatic)
                {
                    return node.Method.Name switch
                    {
                        "IsNullOrEmpty" => ProcessStringNullCheck(node, "eq"),
                        "IsNullOrWhiteSpace" => ProcessStringNullCheck(node, "ne"),
                        _ => throw new NotSupportedException($"Static method {node.Method.Name} not supported in OData.")
                    };
                }

                // Handle DateTime.Add* if evaluatable
                if (node.Method.DeclaringType == typeof(DateTime) && node.Method.Name.StartsWith("Add"))
                {
                    if (IsEvaluatable(node))
                    {
                        var compiled = Expression.Lambda(node).Compile().DynamicInvoke();
                        Visit(Expression.Constant(compiled, node.Type));
                        return node;
                    }
                    _hasClientSideOperations = true;
                    return node;
                }

                // All other methods (Contains, custom, etc.) → client-side
                _hasClientSideOperations = true;
                return node;
            }

            /// <summary>
            /// Visits unary expressions (e.g., !expression).
            /// Translates !string.IsNullOrEmpty and !boolField to OData.
            /// </summary>
            protected override Expression VisitUnary(UnaryExpression node)
            {
                if (node.NodeType == ExpressionType.Not)
                {
                    if (node.Operand is MethodCallExpression mce && mce.Method.DeclaringType == typeof(string))
                    {
                        var op = mce.Method.Name == "IsNullOrEmpty" ? "ne" : "ne";
                        _filter.Append("(");
                        Visit(mce.Arguments[0]);
                        _filter.Append($" {op} '__null__' and ");
                        Visit(mce.Arguments[0]);
                        _filter.Append($" {op} '')");
                        return node;
                    }

                    if (node.Operand is MemberExpression me && me.Expression is ParameterExpression)
                    {
                        _filter.Append("(");
                        Visit(me);
                        _filter.Append(" eq false)");
                        return node;
                    }

                    _hasClientSideOperations = true;
                    return node;
                }

                return base.VisitUnary(node);
            }

            // ========================================
            // Helper Methods
            // ========================================

            /// <summary>
            /// Normalizes an expression by replacing closure references (e.g., value(...).field) with constants.
            /// This prevents malformed OData like "value(...).field" from appearing in the output.
            /// </summary>
            /// <param name="expression">The original expression tree.</param>
            /// <returns>A new expression with closure values inlined as constants.</returns>
            private Expression NormalizeClosureExpressions(Expression expression)
            {
                return new ClosureExpressionRewriter().Visit(expression);
            }

            /// <summary>
            /// Analyzes a sub-expression independently to determine its server/client behavior.
            /// Prevents state pollution between left/right sides of logical operations.
            /// </summary>
            /// <param name="expr">The expression branch to analyze.</param>
            /// <returns>Filter analysis result for this branch.</returns>
            private FilterAnalysisResult AnalyzeBranch(Expression expr)
            {
                var builder = new ODataFilterBuilder { Parameter = this.Parameter };
                return builder.BuildHybridFilter(expr);
            }

            /// <summary>
            /// Checks if a method call is accessing a DynamicEntity indexer (d["Field"]).
            /// </summary>
            private bool IsIndexerAccess(MethodCallExpression node) =>
                node.Method.DeclaringType == typeof(DynamicEntity) &&
                node.Method.Name == "get_Item" &&
                node.Object is ParameterExpression &&
                node.Arguments.Count == 1 &&
                node.Arguments[0] is ConstantExpression;

            /// <summary>
            /// Checks if the expression is a direct property access (e.g., u.Field).
            /// </summary>
            private bool IsPropertyAccess(Expression expr) =>
                expr is MemberExpression me && (me.Expression == Parameter || me.Expression is ParameterExpression);

            /// <summary>
            /// Determines if a method call (e.g., .ToString()) is used directly in a comparison.
            /// Prevents misuse like (x.ToString().Length > 0).
            /// </summary>
            private bool IsDirectComparison(MethodCallExpression node)
            {
                var parent = GetParent(node);
                return parent is BinaryExpression binary &&
                       IsSupportedOperator(binary.NodeType) &&
                       (binary.Left == node || binary.Right == node);
            }

            /// <summary>
            /// Gets the parent expression of the given node in the expression tree.
            /// Uses a visitor to walk the tree and find the first node that contains the target.
            /// </summary>
            /// <param name="node">The node to find the parent of.</param>
            /// <returns>The parent expression, or null if not found.</returns>
            private Expression? GetParent(Expression node)
            {
                var parentFinder = new ParentFinderVisitor(node);
                return parentFinder.Find();
            }

            /// <summary>
            /// Handles string null/empty checks and converts them to OData conditions.
            /// </summary>
            private Expression ProcessStringNullCheck(MethodCallExpression node, string op)
            {
                _filter.Append("(");
                Visit(node.Arguments[0]);
                _filter.Append($" {op} '__null__' or ");
                Visit(node.Arguments[0]);
                _filter.Append($" {op} '')");
                return node;
            }

            /// <summary>
            /// Checks if an expression can be compiled and evaluated immediately (e.g., DateTime.Now.AddDays(1)).
            /// </summary>
            private bool IsEvaluatable(Expression expr)
            {
                try
                {
                    Expression.Lambda(expr).Compile().DynamicInvoke();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// Determines if the expression can be fully handled server-side (i.e., no client-side ops).
            /// </summary>
            private bool IsServerSupported(Expression expression) =>
                AnalyzeBranch(expression).ServerSideOData != null;

            /// <summary>
            /// Checks if the binary operator is supported in OData/Azure Table Storage.
            /// </summary>
            private bool IsSupportedOperator(ExpressionType nodeType) =>
                nodeType is ExpressionType.Equal or ExpressionType.NotEqual or
                           ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                           ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

            /// <summary>
            /// Converts C# binary operator to OData equivalent string.
            /// </summary>
            private string ConvertOperator(ExpressionType nodeType) => nodeType switch
            {
                ExpressionType.Equal => "eq",
                ExpressionType.NotEqual => "ne",
                ExpressionType.GreaterThan => "gt",
                ExpressionType.GreaterThanOrEqual => "ge",
                ExpressionType.LessThan => "lt",
                ExpressionType.LessThanOrEqual => "le",
                _ => throw new NotSupportedException($"Operator {nodeType} not supported in OData.")
            };

            /// <summary>
            /// Checks if a type is numeric (int, double, decimal, etc.) for OData formatting.
            /// </summary>
            private bool IsNumericType(Type type)
            {
                var t = Nullable.GetUnderlyingType(type) ?? type;
                return t.IsPrimitive || t == typeof(decimal);
            }

            /// <summary>
            /// Escapes single quotes in strings for OData (replace ' with '').
            /// </summary>
            /// <param name="value">Input string.</param>
            /// <returns>Escaped string safe for OData.</returns>
            private string EscapeODataString(string? value) => value?.Replace("'", "''") ?? "";
        }

        /// <summary>
        /// Rewrites expression trees to replace closure-captured values (e.g., value(...).field) with constants.
        /// This ensures expressions like `d => d.Url == localVar` become `d => d.Url == "https://..."`.
        /// </summary>
        private class ClosureExpressionRewriter : ExpressionVisitor
        {
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression is ConstantExpression constExpr && constExpr.Value != null)
                {
                    var field = node.Member as FieldInfo;
                    if (field != null)
                    {
                        var value = field.GetValue(constExpr.Value);
                        return Expression.Constant(value, node.Type);
                    }
                }
                return base.VisitMember(node);
            }
        }

        /// <summary>
        /// Helper visitor to find the parent of a given expression node in the tree.
        /// </summary>
        private class ParentFinderVisitor : ExpressionVisitor
        {
            private readonly Expression _target;
            private Expression? _parent;

            public ParentFinderVisitor(Expression target) => _target = target;

            public Expression? Find()
            {
                Visit(_target);
                return _parent;
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (node.Left == _target || node.Right == _target)
                {
                    _parent = node;
                    return node;
                }
                return base.VisitBinary(node);
            }

            protected override Expression VisitUnary(UnaryExpression node)
            {
                if (node.Operand == _target)
                {
                    _parent = node;
                    return node;
                }
                return base.VisitUnary(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Object == _target || node.Arguments.Contains(_target))
                {
                    _parent = node;
                    return node;
                }
                return base.VisitMethodCall(node);
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _target)
                {
                    _parent = node;
                    return node;
                }
                return base.VisitMember(node);
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node == _target)
                {
                    _parent = null;
                }
                return base.VisitConstant(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _target)
                {
                    _parent = null;
                }
                return base.VisitParameter(node);
            }

            protected override Expression VisitConditional(ConditionalExpression node)
            {
                if (node.Test == _target || node.IfTrue == _target || node.IfFalse == _target)
                {
                    _parent = node;
                }
                return base.VisitConditional(node);
            }

            protected override Expression VisitNew(NewExpression node)
            {
                if (node.Arguments.Contains(_target))
                {
                    _parent = node;
                }
                return base.VisitNew(node);
            }

            protected override Expression VisitNewArray(NewArrayExpression node)
            {
                if (node.Expressions.Contains(_target))
                {
                    _parent = node;
                }
                return base.VisitNewArray(node);
            }

            protected override Expression VisitInvocation(InvocationExpression node)
            {
                if (node.Expression == _target || node.Arguments.Contains(_target))
                {
                    _parent = node;
                }
                return base.VisitInvocation(node);
            }

            protected override Expression VisitIndex(IndexExpression node)
            {
                if (node.Object == _target || node.Arguments.Contains(_target))
                {
                    _parent = node;
                }
                return base.VisitIndex(node);
            }
        } // end class ParentFinderVisitor

        /// <summary>
        /// Result of OData filter analysis.
        /// </summary>
        private class FilterAnalysisResult
        {
            public string? ServerSideOData { get; set; }
            public bool HasClientSideOperations { get; set; }
        }

        private class ParameterFinder : ExpressionVisitor
        {
            public bool HasParameter { get; private set; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                HasParameter = true;
                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Visit the object the method is called on
                if (node.Object != null)
                {
                    Visit(node.Object);
                }

                // Visit all arguments
                foreach (var arg in node.Arguments)
                {
                    Visit(arg);
                }

                // If we've found a parameter, we can stop
                if (HasParameter)
                    return node;

                // Don't call base.VisitMethodCall as it might have issues with certain patterns
                return node;
            }

            public override Expression Visit(Expression? node)
            {
                if (node == null)
                    return null!;

                if (HasParameter)
                    return node; // Short circuit if we already found a parameter

                // Special handling for MethodCallExpression to avoid crashes
                if (node is MethodCallExpression mce)
                {
                    return VisitMethodCall(mce);
                }

                try
                {
                    return base.Visit(node);
                }
                catch (Exception ex)
                {
                    // If there's an error visiting the expression, assume it might have parameters
                    System.Diagnostics.Debug.WriteLine($"ParameterFinder error: {ex.Message}");
                    HasParameter = true;
                    return node;
                }
            }
        } // end class ParameterFinder

        #endregion Lambda Expression Processing

    } // end class DataAccess<T>

    /// <summary>
    /// Manages session based data into the database with unified async/sync operations
    /// Now implements ISession for compatibility with ASP.NET Core
    /// </summary>
    public class Session : IDisposable, IAsyncDisposable, ISession
    {
        private string? m_sessionID;
        private List<AppSessionData> m_sessionData = new List<AppSessionData>();
        private readonly DataAccess<AppSessionData> m_da;
        private readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();

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
        void ISession.Clear()
        {
            RestartSession();
            _cache.Clear();
        }

        /// <summary>
        /// ISession - Commits session changes asynchronously
        /// </summary>
        async Task ISession.CommitAsync(CancellationToken cancellationToken)
        {
            await CommitDataAsync();
        }

        /// <summary>
        /// ISession - Loads session data asynchronously
        /// </summary>
        async Task ISession.LoadAsync(CancellationToken cancellationToken)
        {
            await RefreshSessionDataAsync();
        }

        /// <summary>
        /// ISession - Removes a key from the session
        /// </summary>
        void ISession.Remove(string key)
        {
            m_sessionData?.RemoveAll(s => s.Key == key);
            _cache.Remove(key);
        }

        /// <summary>
        /// ISession - Sets a byte array value in the session
        /// </summary>
        public void Set(string key, byte[] value)
        {
            _cache[key] = value;
            var data = this[key];
            if (data != null)
            {
                data.Value = Convert.ToBase64String(value);
            }
        }

        /// <summary>
        /// ISession - Tries to get a byte array value from the session
        /// </summary>
        public bool TryGetValue(string key, out byte[] value)
        {
            if (_cache.TryGetValue(key, out value!))
            {
                return true;
            }
            var data = this[key];
            if (data != null && !string.IsNullOrEmpty(data.Value))
            {
                try
                {
                    value = Convert.FromBase64String(data.Value);
                    _cache[key] = value;
                    return true;
                }
                catch
                {
                    value = System.Text.Encoding.UTF8.GetBytes(data.Value);
                    _cache[key] = value;
                    return true;
                }
            }
            value = null!;
            return false;
        }

        private readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Stores a strongly typed object in the session state.
        /// </summary>
        /// <typeparam name="T">The type of the object to store.</typeparam>
        /// <param name="key">The key to use for storing the object.</param>
        /// <param name="value">The object to store.</param>
        public void SetObject<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;

            byte[] serializedBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            this.Set(key, serializedBytes);
        }

        /// <summary>
        /// Retrieves a strongly typed object from the session state.
        /// </summary>
        /// <typeparam name="T">The type of the object to retrieve.</typeparam>
        /// <param name="key">The key used to store the object.</param>
        /// <returns>The deserialized object, or the default value for <typeparamref name="T"/> if the key is not found or the value is null.</returns>
        public T? GetObject<T>(string key)
        {
            if (string.IsNullOrEmpty(key)) return default;

            if (TryGetValue(key, out byte[]? valueBytes) && valueBytes != null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(valueBytes, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Failed to deserialize object for key '{key}'.", ex);
                }
            }

            return default(T);
        }
        #endregion

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
        /// <returns>The collection of stale Prospect object data that needs to be submitted to CRM</returns>
        public List<string> GetStaleSessions() => GetStaleSessionsCore().GetAwaiter().GetResult();

        /// <summary>
        /// Removes legacy session data (Synchronous)
        /// </summary>
        public void CleanSessionData() => CleanSessionDataCore().GetAwaiter().GetResult();

        /// <summary>
        /// Uploads all session data to the dB (Synchronous)
        /// </summary>
        /// <returns>Confirmation the data is committed</returns>
        public bool CommitData()
        {
            CleanSessionData();
            return CommitData(m_sessionData, TableOperationType.InsertOrReplace);
        }

        /// <summary>
        /// Commits all of the data stored in a collection (Synchronous)
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the dB</param>
        /// <returns>Confirmation the data is committed</returns>
        public bool CommitData(List<AppSessionData> data, TableOperationType direction) => CommitDataCore(data, direction).GetAwaiter().GetResult();

        /// <summary>
        /// Restarts the session by removing previous session data (Synchronous)
        /// </summary>
        public void RestartSession() => RestartSessionCore().GetAwaiter().GetResult();

        /// <summary>
        /// Loads session data from the database (Synchronous)
        /// </summary>
        /// <param name="sessionID">The session ID to load data for</param>
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
        /// <returns>The collection of stale Prospect object data that needs to be submitted to CRM</returns>
        public Task<List<string>> GetStaleSessionsAsync() => GetStaleSessionsCore();

        /// <summary>
        /// Removes legacy session data (Asynchronous)
        /// </summary>
        public Task CleanSessionDataAsync() => CleanSessionDataCore();

        /// <summary>
        /// Uploads all session data to the dB (Asynchronous)
        /// </summary>
        /// <returns>Confirmation the data is committed</returns>
        public async Task<bool> CommitDataAsync()
        {
            await CleanSessionDataAsync();
            return await CommitDataAsync(m_sessionData, TableOperationType.InsertOrReplace);
        }

        /// <summary>
        /// Commits all of the data stored in a collection (Asynchronous)
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the dB</param>
        /// <returns>Confirmation the data is committed</returns>
        public Task<bool> CommitDataAsync(List<AppSessionData> data, TableOperationType direction) => CommitDataCore(data, direction);

        /// <summary>
        /// Restarts the session by removing previous session data (Asynchronous)
        /// </summary>
        public Task RestartSessionAsync() => RestartSessionCore();

        /// <summary>
        /// Loads session data from the database (Asynchronous)
        /// </summary>
        /// <param name="sessionID">The session ID to load data for</param>
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

        #endregion

        #region Helper Methods and Properties

        /// <summary>
        /// Checks to see if an object already exists within the collection
        /// </summary>
        /// <param name="key">The Key to find</param>
        /// <returns>The object, new or existing</returns>
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
        /// <param name="key">Identifier</param>
        /// <returns>The requested object if it exists, null if not</returns>
        /// <remarks>This is called Indexers or default indexers</remarks>
        public AppSessionData? this[string key]
        {
            get { return Find(key); }
            set { Find(key)!.Value = value!.ToString(); DataHasBeenCommitted = false; }
        }

        /// <summary>
        /// Readonly reference to the data stored 
        /// </summary>
        public List<AppSessionData>? SessionData
        {
            get { return m_sessionData; }
        }

        /// <summary>
        /// Gets the current session ID
        /// </summary>
        public string? SessionID
        {
            get { return m_sessionID; }
        }

        /// <summary>
        /// Determines whether the data has successfully been committed to the database
        /// </summary>
        public bool DataHasBeenCommitted { get; set; }

        #endregion

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
        /// <param name="disposing">True if disposing, false if finalizing</param>
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

        #endregion

    } // end class Session

} // end namespace ASCTableStorage.Data