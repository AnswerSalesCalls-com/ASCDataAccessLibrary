using ASCTableStorage.Common;
using ASCTableStorage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos.Table;
using System.Linq.Expressions;
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
    }//end enum ComparisonTypes

    /// <summary>
    /// Defines how the engine will combine search parameters with custom searches
    /// </summary>
    public enum QueryCombineStyle
    {
        and,
        or
    }

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
        /// The value your're searching for
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
    }

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

        /// <summary>
        /// Constructor allows for various connections independent of the config file. 
        /// Can be used for data migrations
        /// </summary>
        /// <param name="accountName">The Azure Account name for the Table Store</param>
        /// <param name="accountKey">The Azure account key for the table store</param>
        public DataAccess(string accountName, string accountKey)
        {
            StorageCredentials cred = new StorageCredentials(accountName, accountKey);
            CloudStorageAccount csa = new CloudStorageAccount(cred, true);
            m_Client = csa.CreateCloudTableClient();
        }

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
        /// Core implementation for managing single entity data
        /// </summary>
        private async Task ManageDataCore(T obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            _ = obj.GetIDValue(); // Ensures ID always exists
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
        /// </summary>
        private async Task<List<T>> GetCollectionCore(Expression<Func<T, bool>> predicate)
        {
            // Use hybrid filtering for maximum efficiency
            var hybridResult = ConvertLambdaToHybridFilter(predicate);

            // Execute server-side query with maximum filtering possible
            TableQuery<T> query;
            if (!string.IsNullOrEmpty(hybridResult.ServerSideFilter))
            {
                query = new TableQuery<T>().Where(hybridResult.ServerSideFilter);
            }
            else
            {
                // If no server-side filtering possible, get all data
                query = new TableQuery<T>();
            }

            var serverResults = await GetCollectionCore(query);

            // Apply client-side filtering if needed
            if (hybridResult.RequiresClientFiltering && hybridResult.ClientSidePredicate != null)
            {
                return serverResults.Where(hybridResult.ClientSidePredicate).ToList();
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
            T obj = Activator.CreateInstance<T>();
            CloudTable table = await GetTableCore(obj.TableReference);

            var results = new List<T>();
            TableContinuationToken token = null;

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
            return results.FirstOrDefault();
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
        private async Task<PagedResult<T>> GetPagedCollectionCore(int pageSize = DEFAULT_PAGE_SIZE, string continuationToken = null, 
            TableQuery<T> definedQuery = null!, Expression<Func<T, bool>> predicate = null!)
        {
            T obj = Activator.CreateInstance<T>();
            CloudTable table = await GetTableCore(obj.TableReference);

            TableQuery<T> query;
            Func<T, bool> clientFilter = null!;

            // Handle hybrid filtering for lambda expressions
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

            // Apply client-side filtering if needed
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
            IProgress<BatchUpdateProgress> progressCallback = null)
        {
            var result = new BatchUpdateResult();

            if (data == null || data.Count == 0)
            {
                result.Success = true;
                return result;
            }

            try
            {
                // Group data by partition key (Azure Table Storage requirement)
                var groupedData = data.GroupBy(g => g.PartitionKey).ToList();
                var totalBatches = groupedData.Sum(group => (int)Math.Ceiling((double)group.Count() / MAX_BATCH_SIZE));
                var completedBatches = 0;

                foreach (var group in groupedData)
                {
                    var groupList = group.ToList();

                    // CRITICAL: Process in strict batches of MAX_BATCH_SIZE (100) - Azure Table Storage limit
                    for (int i = 0; i < groupList.Count; i += MAX_BATCH_SIZE)
                    {
                        // Ensure we never exceed 100 items per batch
                        var batchSize = Math.Min(MAX_BATCH_SIZE, groupList.Count - i);
                        var batchItems = groupList.GetRange(i, batchSize);

                        // Validate batch size doesn't exceed Azure limit
                        if (batchItems.Count > MAX_BATCH_SIZE)
                        {
                            throw new InvalidOperationException($"Batch size {batchItems.Count} exceeds Azure Table Storage limit of {MAX_BATCH_SIZE}");
                        }

                        try
                        {
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
        /// Core implementation for processing a single batch
        /// </summary>
        private async Task ProcessBatchCore(List<T> batchItems, TableOperationType direction)
        {
            if (batchItems == null || batchItems.Count == 0) return;

            // Double-check the batch size limit
            if (batchItems.Count > MAX_BATCH_SIZE)
            {
                throw new InvalidOperationException($"Batch contains {batchItems.Count} items, exceeding Azure Table Storage limit of {MAX_BATCH_SIZE}");
            }

            var tableBatch = new TableBatchOperation();

            foreach (var item in batchItems)
            {
                _ = item.GetIDValue(); // Ensures ID always exists
                tableBatch.Add(CreateTableOperation(item, direction));
            }

            // Final validation before execution
            if (tableBatch.Count > MAX_BATCH_SIZE)
            {
                throw new InvalidOperationException($"TableBatchOperation contains {tableBatch.Count} operations, exceeding limit of {MAX_BATCH_SIZE}");
            }

            var sampleItem = Activator.CreateInstance<T>();
            var table = await GetTableCore(sampleItem.TableReference);
            await table.ExecuteBatchAsync(tableBatch);
        }

        #endregion Core Implementation Methods

        #region Public Synchronous Methods (Wrap Core)

        /// <summary>
        /// Returns an internal reference to the Table (synchronous)
        /// </summary>
        private CloudTable GetTable(string tableRef)
        {
            return GetTableCore(tableRef).GetAwaiter().GetResult();
        }

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
        /// Synchronous version of batch update (wrapper around async core)
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <returns>Success indicator</returns>
        public bool BatchUpdateList(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            var result = BatchUpdateListCore(data, direction).GetAwaiter().GetResult();
            return result.Success;
        }

        #endregion Public Synchronous Methods

        #region Public Asynchronous Methods (Call Core Directly)

        /// <summary>
        /// Returns an internal reference to the Table (asynchronous)
        /// </summary>
        private Task<CloudTable> GetTableAsync(string tableRef)
        {
            return GetTableCore(tableRef);
        }

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
        /// Asynchronously batch commits all data with strict 100-record limit enforcement
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the DB</param>
        /// <param name="progressCallback">Optional callback to track progress</param>
        /// <returns>Result indicating success and any errors encountered</returns>
        public Task<BatchUpdateResult> BatchUpdateListAsync(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace, IProgress<BatchUpdateProgress> progressCallback = null!)
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
        }

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
            return GetPagedCollectionCore(pageSize, continuationToken, null, predicate);
        }

        /// <summary>
        /// Gets an initial quick load of data followed by background loading
        /// </summary>
        /// <param name="initialLoadSize">Size of initial quick load (default: 100)</param>
        /// <param name="definedQuery">Optional query to filter results</param>
        /// <returns>Initial page result</returns>
        public Task<PagedResult<T>> GetInitialDataLoadAsync(int initialLoadSize = DEFAULT_PAGE_SIZE, TableQuery<T> definedQuery = null!)
        {
            return GetPagedCollectionCore(initialLoadSize, null!, definedQuery);
        }

        /// <summary>
        /// Gets an initial quick load using lambda expression with hybrid filtering
        /// </summary>
        /// <param name="predicate">Lambda expression defining the filter criteria</param>
        /// <param name="initialLoadSize">Size of initial quick load</param>
        /// <returns>Initial page result</returns>
        public Task<PagedResult<T>> GetInitialDataLoadAsync(Expression<Func<T, bool>> predicate, int initialLoadSize = DEFAULT_PAGE_SIZE)
        {
            return GetPagedCollectionCore(initialLoadSize, null!, null, predicate);
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

            // Simple serialization - in production, consider using JSON or more robust serialization
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
        }

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
        }

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

            return new HybridFilterResult<T>
            {
                ServerSideFilter = result.ServerSideOData,
                ClientSidePredicate = result.HasClientSideOperations ? predicate.Compile() : null,
                RequiresClientFiltering = result.HasClientSideOperations
            };
        }

        /// <summary>
        /// Backwards compatibility: Converts lambda to OData filter (throws if unsupported operations found)
        /// </summary>
        /// <param name="predicate">The lambda expression to convert</param>
        /// <returns>OData filter string</returns>
        private string ConvertLambdaToODataFilter(Expression<Func<T, bool>> predicate)
        {
            var hybridResult = ConvertLambdaToHybridFilter(predicate);

            if (hybridResult.RequiresClientFiltering)
            {
                throw new NotSupportedException(
                    "The lambda expression contains operations not supported by Azure Table Storage. " +
                    "Use GetCollection/GetCollectionAsync methods which automatically handle hybrid server/client-side filtering.");
            }

            return hybridResult.ServerSideFilter ?? "";
        }

        /// <summary>
        /// Result of hybrid filter processing
        /// </summary>
        private class HybridFilterResult<TEntity>
        {
            public string? ServerSideFilter { get; set; }
            public Func<TEntity, bool>? ClientSidePredicate { get; set; }
            public bool RequiresClientFiltering { get; set; }
        }

        /// <summary>
        /// Helper class to build OData filter strings from expression trees with intelligent server/client separation
        /// </summary>
        private class ODataFilterBuilder : ExpressionVisitor
        {
            private StringBuilder _filter = new StringBuilder();
            private bool _hasClientSideOperations = false;

            public FilterAnalysisResult BuildHybridFilter(Expression expression)
            {
                _filter.Clear();
                _hasClientSideOperations = false;

                Visit(expression);

                return new FilterAnalysisResult
                {
                    ServerSideOData = _filter.Length > 0 ? _filter.ToString() : null,
                    HasClientSideOperations = _hasClientSideOperations
                };
            }

            public string Build(Expression expression)
            {
                var result = BuildHybridFilter(expression);
                return result.ServerSideOData ?? "";
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                // Handle AND operations - we can extract supported parts for server filtering
                if (node.NodeType == ExpressionType.AndAlso)
                {
                    return HandleAndOperation(node);
                }

                // Handle OR operations - need both sides supported for server filtering
                if (node.NodeType == ExpressionType.OrElse)
                {
                    return HandleOrOperation(node);
                }

                // Handle other binary operations
                return HandleSimpleBinary(node);
            }

            private Expression HandleAndOperation(BinaryExpression node)
            {
                var leftBuilder = new ODataFilterBuilder();
                var leftResult = leftBuilder.BuildHybridFilter(node.Left);

                var rightBuilder = new ODataFilterBuilder();
                var rightResult = rightBuilder.BuildHybridFilter(node.Right);

                // AND Operation Logic:
                // - Can use ANY server-supported parts to reduce dataset
                // - Client-side will handle the complete filtering
                // - Safe to combine server parts with AND because we're being MORE restrictive

                var serverParts = new List<string>();

                if (!string.IsNullOrEmpty(leftResult.ServerSideOData))
                    serverParts.Add($"({leftResult.ServerSideOData})");

                if (!string.IsNullOrEmpty(rightResult.ServerSideOData))
                    serverParts.Add($"({rightResult.ServerSideOData})");

                if (serverParts.Any())
                {
                    _filter.Append(string.Join(" and ", serverParts));
                    Console.WriteLine($"AND operation - using server filter: {string.Join(" and ", serverParts)}");
                }

                // Mark for client processing if either side has unsupported operations
                if (leftResult.HasClientSideOperations || rightResult.HasClientSideOperations)
                {
                    _hasClientSideOperations = true;
                    Console.WriteLine($"AND operation will also apply client-side filtering");
                }

                return node;
            } // end HandleAndOperation

            // The issue might be in the HandleOrOperation method. Let me fix it:

            private Expression HandleOrOperation(BinaryExpression node)
            {
                var leftBuilder = new ODataFilterBuilder();
                var leftResult = leftBuilder.BuildHybridFilter(node.Left);

                var rightBuilder = new ODataFilterBuilder();
                var rightResult = rightBuilder.BuildHybridFilter(node.Right);

                // OR Operation Logic:
                // - If BOTH sides are fully server-supported → Use server-side OR
                // - If ANY side needs client processing → Must do ALL processing client-side
                // - Cannot use partial server filtering for OR because it would miss records

                bool leftFullySupported = !leftResult.HasClientSideOperations && !string.IsNullOrEmpty(leftResult.ServerSideOData);
                bool rightFullySupported = !rightResult.HasClientSideOperations && !string.IsNullOrEmpty(rightResult.ServerSideOData);

                System.Diagnostics.Debug.WriteLine($"OR Operation Analysis:");
                System.Diagnostics.Debug.WriteLine($"  Left fully supported: {leftFullySupported}");
                System.Diagnostics.Debug.WriteLine($"  Right fully supported: {rightFullySupported}");

                if (leftFullySupported && rightFullySupported)
                {
                    // Both sides fully supported - can do pure server-side OR
                    _filter.Append($"({leftResult.ServerSideOData}) or ({rightResult.ServerSideOData})");
                    System.Diagnostics.Debug.WriteLine($"  Using pure server-side OR");
                    // No client-side processing needed
                }
                else
                {
                    // At least one side needs client processing
                    // CRITICAL: We must mark this for client-side processing AND not add server filtering
                    _hasClientSideOperations = true;

                    // Do NOT add any server-side filtering for mixed OR operations
                    // The _filter should remain empty to indicate no server filtering

                    System.Diagnostics.Debug.WriteLine($"  Mixed OR detected - will use client-side filtering only");
                }

                return node;
            } // end HandleOrOperation

            private Expression HandleSimpleBinary(BinaryExpression node)
            {
                if (IsServerSupported(node.Left) && IsServerSupported(node.Right) && IsSupportedOperator(node.NodeType))
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
            /// Visits a <see cref="MemberExpression"/> and appends the name of the member to the filter.
            /// </summary>
            /// <param name="node">The <see cref="MemberExpression"/> to visit. Cannot be <see langword="null"/>.</param>
            /// <returns>The original <see cref="MemberExpression"/> passed to the method.</returns>
            protected override Expression VisitMember(MemberExpression node)
            {
                // Parameter member access (like c.CompanyName) is server-supported
                if (node.Expression is ParameterExpression)
                {
                    _filter.Append(node.Member.Name);
                    return node;
                }

                // Try to evaluate other member access (like variables from outer scope)
                if (!ContainsParameterReference(node))
                {
                    try
                    {
                        var lambda = Expression.Lambda(node);
                        var compiled = lambda.Compile();
                        var result = compiled.DynamicInvoke();
                        Visit(Expression.Constant(result));
                        return node;
                    }
                    catch
                    {
                        _hasClientSideOperations = true;
                    }
                }
                else
                {
                    _hasClientSideOperations = true;
                }

                return node;
            }

            /// <summary>
            /// Visits a <see cref="ConstantExpression"/> and appends its value to the filter string.
            /// </summary>
            /// <param name="node">The <see cref="ConstantExpression"/> to visit. Must not be <c>null</c>.</param>
            /// <returns>The original <see cref="ConstantExpression"/> after processing.</returns>
            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (node.Value == null)
                {
                    _filter.Append("'__null__'");  // Azure Table Storage representation for null
                }
                else if (node.Type == typeof(string))
                {
                    _filter.Append($"'{EscapeODataString(node.Value.ToString()!)}'");
                }
                else if (node.Type == typeof(DateTime) || node.Type == typeof(DateTime?))
                {
                    var dateTime = (DateTime)node.Value;
                    _filter.Append($"datetime'{dateTime:yyyy-MM-ddTHH:mm:ss.fffZ}'");
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
                    _filter.Append($"'{EscapeODataString(node.Value.ToString()!)}'");
                }

                return node;
            }

            /// <summary>
            /// Visits a <see cref="MethodCallExpression"/> and processes it based on the method being called.
            /// </summary>
            /// <param name="node">The <see cref="MethodCallExpression"/> to visit.</param>
            /// <returns>The original <see cref="MethodCallExpression"/> after processing.</returns>
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // Handle static string methods that ARE supported by Table Storage
                if (node.Method.DeclaringType == typeof(string) && node.Method.IsStatic)
                {
                    switch (node.Method.Name)
                    {
                        case "IsNullOrEmpty":
                            // Generate condition for "field IS null or empty"
                            _filter.Append("(");
                            Visit(node.Arguments[0]);
                            _filter.Append(" eq '__null__' or ");
                            Visit(node.Arguments[0]);
                            _filter.Append(" eq '')");
                            return node;

                        case "IsNullOrWhiteSpace":
                            // Table Storage doesn't support trimming, so treat same as IsNullOrEmpty
                            _filter.Append("(");
                            Visit(node.Arguments[0]);
                            _filter.Append(" eq '__null__' or ");
                            Visit(node.Arguments[0]);
                            _filter.Append(" eq '')");
                            return node;
                    }
                }

                // Handle instance string methods - these are NOT supported by Table Storage
                if (node.Method.DeclaringType == typeof(string) && !node.Method.IsStatic)
                {
                    switch (node.Method.Name)
                    {
                        case "Contains":
                        case "StartsWith":
                        case "EndsWith":
                        case "ToLower":
                        case "ToUpper":
                        case "Trim":
                        case "TrimStart":
                        case "TrimEnd":
                            // Mark for client-side processing
                            _hasClientSideOperations = true;
                            return node;
                    }
                }

                // Handle DateTime methods
                if (node.Method.DeclaringType == typeof(DateTime))
                {
                    switch (node.Method.Name)
                    {
                        case "AddDays":
                        case "AddHours":
                        case "AddMinutes":
                        case "AddSeconds":
                            // For method calls on DateTime, evaluate if possible
                            if (!ContainsParameterReference(node))
                            {
                                try
                                {
                                    var lambda = Expression.Lambda(node);
                                    var compiled = lambda.Compile();
                                    var result = compiled.DynamicInvoke();
                                    Visit(Expression.Constant(result));
                                    return node;
                                }
                                catch
                                {
                                    _hasClientSideOperations = true;
                                    return node;
                                }
                            }
                            else
                            {
                                _hasClientSideOperations = true;
                                return node;
                            }
                    }
                }

                // Try to evaluate method calls that don't involve parameters
                if (!ContainsParameterReference(node))
                {
                    try
                    {
                        var lambda = Expression.Lambda(node);
                        var compiled = lambda.Compile();
                        var result = compiled.DynamicInvoke();
                        Visit(Expression.Constant(result));
                        return node;
                    }
                    catch
                    {
                        _hasClientSideOperations = true;
                    }
                }
                else
                {
                    // Method involves parameters, mark for client-side
                    _hasClientSideOperations = true;
                }

                return node;
            } //end VisitMethodCall

            /// <summary>
            /// Visits a <see cref="UnaryExpression"/> and processes it based on its <see cref="ExpressionType"/>.
            /// </summary>
            /// <param name="node">The <see cref="UnaryExpression"/> to visit. Must not be <see langword="null"/>.</param>
            /// <returns>The original <see cref="UnaryExpression"/> after processing.</returns>
            protected override Expression VisitUnary(UnaryExpression node)
            {
                if (node.NodeType == ExpressionType.Not)
                {
                    // Handle specific cases where we can convert NOT to supported operations
                    if (node.Operand is MethodCallExpression methodCall)
                    {
                        // Handle !string.IsNullOrEmpty() -> convert to "field ne '__null__' and field ne ''"
                        if (methodCall.Method.DeclaringType == typeof(string) &&
                            methodCall.Method.IsStatic &&
                            methodCall.Method.Name == "IsNullOrEmpty")
                        {
                            // This means the field IS NOT null or empty (has a value)
                            _filter.Append("(");
                            Visit(methodCall.Arguments[0]);
                            _filter.Append(" ne '__null__' and ");
                            Visit(methodCall.Arguments[0]);
                            _filter.Append(" ne '')");
                            return node;
                        }

                        // Handle !string.IsNullOrWhiteSpace() 
                        if (methodCall.Method.DeclaringType == typeof(string) &&
                            methodCall.Method.IsStatic &&
                            methodCall.Method.Name == "IsNullOrWhiteSpace")
                        {
                            // This means the field IS NOT null or whitespace (has meaningful content)
                            _filter.Append("(");
                            Visit(methodCall.Arguments[0]);
                            _filter.Append(" ne '__null__' and ");
                            Visit(methodCall.Arguments[0]);
                            _filter.Append(" ne '')");
                            return node;
                        }
                    }

                    // Handle !booleanExpression -> convert to opposite
                    if (node.Operand is BinaryExpression binaryExpr)
                    {
                        // Convert !(...) to the opposite operation if possible
                        var oppositeOperator = GetOppositeOperator(binaryExpr.NodeType);
                        if (oppositeOperator.HasValue &&
                            IsServerSupported(binaryExpr.Left) &&
                            IsServerSupported(binaryExpr.Right))
                        {
                            _filter.Append("(");
                            Visit(binaryExpr.Left);
                            _filter.Append($" {ConvertOperator(oppositeOperator.Value)} ");
                            Visit(binaryExpr.Right);
                            _filter.Append(")");
                            return node;
                        }
                    }

                    // Handle !booleanField -> convert to "field eq false"
                    if (node.Operand is MemberExpression memberExpr &&
                        memberExpr.Expression is ParameterExpression &&
                        (memberExpr.Type == typeof(bool) || memberExpr.Type == typeof(bool?)))
                    {
                        _filter.Append("(");
                        Visit(memberExpr);
                        _filter.Append(" eq false)");
                        return node;
                    }

                    // If we can't handle the NOT operation server-side, mark for client processing
                    _hasClientSideOperations = true;
                    return node;
                }

                return base.VisitUnary(node);
            } // end VisitUnary
            /// <summary>
            /// Gets the opposite operator for negation purposes
            /// </summary>
            private ExpressionType? GetOppositeOperator(ExpressionType nodeType)
            {
                return nodeType switch
                {
                    ExpressionType.Equal => ExpressionType.NotEqual,
                    ExpressionType.NotEqual => ExpressionType.Equal,
                    ExpressionType.GreaterThan => ExpressionType.LessThanOrEqual,
                    ExpressionType.GreaterThanOrEqual => ExpressionType.LessThan,
                    ExpressionType.LessThan => ExpressionType.GreaterThanOrEqual,
                    ExpressionType.LessThanOrEqual => ExpressionType.GreaterThan,
                    // Note: AND/OR opposites require De Morgan's law which is complex, so we don't handle them
                    _ => null
                };
            }

            private bool IsServerSupported(Expression expression)
            {
                var analyzer = new ODataFilterBuilder();
                var result = analyzer.BuildHybridFilter(expression);
                return !result.HasClientSideOperations && !string.IsNullOrEmpty(result.ServerSideOData);
            }

            private bool IsSupportedOperator(ExpressionType nodeType)
            {
                return nodeType switch
                {
                    ExpressionType.Equal => true,
                    ExpressionType.NotEqual => true,
                    ExpressionType.GreaterThan => true,
                    ExpressionType.GreaterThanOrEqual => true,
                    ExpressionType.LessThan => true,
                    ExpressionType.LessThanOrEqual => true,
                    ExpressionType.AndAlso => true,
                    ExpressionType.OrElse => true,
                    _ => false
                };
            }

            private string ConvertOperator(ExpressionType nodeType)
            {
                return nodeType switch
                {
                    ExpressionType.Equal => "eq",
                    ExpressionType.NotEqual => "ne",
                    ExpressionType.GreaterThan => "gt",
                    ExpressionType.GreaterThanOrEqual => "ge",
                    ExpressionType.LessThan => "lt",
                    ExpressionType.LessThanOrEqual => "le",
                    ExpressionType.AndAlso => "and",
                    ExpressionType.OrElse => "or",
                    _ => throw new NotSupportedException($"Operator {nodeType} is not supported")
                };
            }

            private bool IsNumericType(Type type)
            {
                var nonNullableType = Nullable.GetUnderlyingType(type) ?? type;
                return nonNullableType == typeof(int) || nonNullableType == typeof(long) ||
                       nonNullableType == typeof(float) || nonNullableType == typeof(double) ||
                       nonNullableType == typeof(decimal) || nonNullableType == typeof(byte) ||
                       nonNullableType == typeof(short) || nonNullableType == typeof(sbyte) ||
                       nonNullableType == typeof(ushort) || nonNullableType == typeof(uint) ||
                       nonNullableType == typeof(ulong);
            }

            private string EscapeODataString(string value)
            {
                return value?.Replace("'", "''") ?? "";
            }

            private bool ContainsParameterReference(Expression expression)
            {
                var finder = new ParameterFinder();
                finder.Visit(expression);
                return finder.HasParameter;
            }
        }

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

            public override Expression Visit(Expression? node)
            {
                if (HasParameter) return node!; // Short circuit
                return base.Visit(node!);
            }
        }

        #endregion Lambda Expression Processing
    } // class DataAccess<T>

    /// <summary>
    /// Manages session based data into the database with unified async/sync operations
    /// </summary>
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
            // Check cache first
            if (_cache.TryGetValue(key, out value!))
            {
                return true;
            }

            // Check underlying session data
            var data = this[key];
            if (data != null && !string.IsNullOrEmpty(data.Value))
            {
                try
                {
                    // Try to decode as base64
                    value = Convert.FromBase64String(data.Value);
                    _cache[key] = value; // Cache it
                    return true;
                }
                catch
                {
                    // If not base64, treat as UTF8 string
                    value = System.Text.Encoding.UTF8.GetBytes(data.Value);
                    _cache[key] = value;
                    return true;
                }
            }

            value = null!;
            return false;
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
            m_sessionData = LoadSessionDataCore(sessionID).GetAwaiter().GetResult();
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
            m_sessionData = await LoadSessionDataCore(sessionID);
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
                DataHasBeenCommitted = false; // Mark as needing commit
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
            set 
            { 
                Find(key)!.Value = value!.ToString(); 
                DataHasBeenCommitted = false; // Mark as needing commit
            }
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
    } //end class Session
}
