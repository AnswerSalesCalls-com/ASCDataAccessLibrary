using ASCTableStorage.Models;
using Microsoft.Azure.Cosmos.Table;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

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
        /// ENHANCED WITH FAIL-SAFE
        /// </summary>
        private async Task<List<T>> GetCollectionCore(Expression<Func<T, bool>> predicate)
        {
            var visitor = new ODataFilterVisitor<T>();
            var analysisResult = visitor.Analyze(predicate);
            var serverFilter = visitor.GenerateFilter(analysisResult.ServerExpression);

            // FAIL-SAFE: If no server filter could be generated and no client filter exists,
            // this means the entire predicate failed to translate
            if (string.IsNullOrEmpty(serverFilter) && analysisResult.ClientExpression == null)
            {
                throw new InvalidOperationException(
                    "The provided lambda expression could not be translated into a valid server-side query. " +
                    "To prevent returning the entire table, this operation has been aborted. " +
                    "Expression: " + predicate.ToString()
                );
            }

            // Build the query
            TableQuery<T> query = new TableQuery<T>();
            if (!string.IsNullOrEmpty(serverFilter))
            {
                query = query.Where(serverFilter);
            }

            // FAIL-SAFE: If we have a predicate but no server filter, only proceed if we have client filter
            if (string.IsNullOrEmpty(serverFilter) && analysisResult.ClientExpression == null)
            {
                throw new InvalidOperationException(
                    "No valid filter could be generated from the provided lambda expression. " +
                    "This would result in fetching the entire table, which is not allowed."
                );
            }

            // Execute the query
            List<T> serverResults = await GetCollectionCore(query);

            // Apply client-side filtering if needed
            if (analysisResult.ClientExpression != null)
            {
                // This "rebuilds" the lambda by creating a new visitor that substitutes
                // the body of the original predicate with our new client-side expression.
                var replacer = new ExpressionReplacer(predicate.Body, analysisResult.ClientExpression);
                var newBody = replacer.Visit(predicate.Body);
                var clientLambda = Expression.Lambda<Func<T, bool>>(newBody!, predicate.Parameters);

                var clientPredicate = clientLambda.Compile();
                return serverResults.Where(clientPredicate).ToList();
            }

            return serverResults;
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

        /// <summary>
        /// The primary function in all the code that retrieves data from the Tables
        /// </summary>
        /// <param name="tableName">Name of the table</param>
        /// <param name="definedQuery">The query derived from all source types</param>
        private async Task<List<T>> GetCollectionCore(string tableName, TableQuery<T> definedQuery)
        {
            CloudTable table = await GetTableCore(tableName);
            var results = new List<T>();
            TableContinuationToken token = null!;

            var resolver = new EntityResolver<T>((pk, rk, ts, props, etag) =>
            {
                var entity = Activator.CreateInstance<T>();
                entity.ReadEntity(props, null!);
                entity.PartitionKey = pk;
                entity.RowKey = rk;
                entity.Timestamp = ts;
                entity.ETag = etag;
                return entity;
            });

            do
            {
                var segment = await table.ExecuteQuerySegmentedAsync(definedQuery, resolver, token);
                results.AddRange(segment.Results);
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
            return results?.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for getting single row by field criteria
        /// </summary>
        private async Task<T> GetRowObjectCore(string fieldName, ComparisonTypes howToCompare, string fieldValue)
        {
            string queryString = $"{fieldName} {howToCompare} '{fieldValue}'";
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            var results = await GetCollectionCore(q);
            return results?.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for getting single row by lambda expression with hybrid filtering
        /// ENHANCED WITH FAIL-SAFE
        /// </summary>
        private async Task<T> GetRowObjectCore(Expression<Func<T, bool>> predicate)
        {
            var results = await GetCollectionCore(predicate);
            return results?.FirstOrDefault()!;
        }

        /// <summary>
        /// Core implementation for paginated collection retrieval with hybrid filtering support
        /// ENHANCED WITH FAIL-SAFE
        /// </summary>
        private async Task<PagedResult<T>> GetPagedCollectionCore(int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null!,
            TableQuery<T> definedQuery = null!, Expression<Func<T, bool>> predicate = null!)
        {
            CloudTable table = await GetTableCore(ResolveTableName());
            TableQuery<T> query;
            Func<T, bool> clientFilter = null!;

            if (predicate != null)
            {
                var visitor = new ODataFilterVisitor<T>();
                var analysisResult = visitor.Analyze(predicate);
                var serverFilter = visitor.GenerateFilter(analysisResult.ServerExpression);

                // FAIL-SAFE: Similar to GetCollectionCore
                if (string.IsNullOrEmpty(serverFilter) && analysisResult.ClientExpression == null)
                {
                    throw new InvalidOperationException(
                        "The provided lambda expression could not be translated into a valid query. " +
                        "To prevent returning the entire table, this operation has been aborted."
                    );
                }

                query = !string.IsNullOrEmpty(serverFilter)
                    ? new TableQuery<T>().Where(serverFilter)
                    : new TableQuery<T>();

                if (analysisResult.ClientExpression != null)
                {
                    clientFilter = Expression.Lambda<Func<T, bool>>(
                        analysisResult.ClientExpression,
                        predicate.Parameters
                    ).Compile();
                }

                // Additional check: if no server filter and no client filter, abort
                if (string.IsNullOrEmpty(serverFilter) && clientFilter == null)
                {
                    throw new InvalidOperationException(
                        "No valid filter could be generated from the provided lambda expression."
                    );
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
        public Task<BatchUpdateResult> BatchUpdateListAsync(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace, IProgress<BatchUpdateProgress> progressCallback = null!)
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
        public Task<BatchUpdateResult> BatchUpdateListAsync(List<DynamicEntity> data, TableOperationType direction = TableOperationType.InsertOrReplace, IProgress<BatchUpdateProgress> progressCallback = null!)
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
        public async Task<PagedResult<T>> GetPagedCollectionAsync(string partitionKeyID, int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null!)
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

        #endregion Helper Methods and Support Classes
    }
}