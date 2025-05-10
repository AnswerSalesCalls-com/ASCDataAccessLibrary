using AZTableStorage.Common;
using AZTableStorage.Models;
using Microsoft.Azure.Cosmos.Table;
using System.Text;

namespace AZTableStorage.Data
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
    /// Handles all CRUD to the underlying DataTables.
    /// </summary>
    /// <typeparam name="T">The DataType this Instance will work with in the DB</typeparam>
    /// <remarks>
    /// NOTE: One DataAccess Object PER Table operation otherwise you will be going against the wrong table instance.
    /// Create a NEW instance of this object for each table you want to interact with
    /// </remarks>
    public class DataAccess<T> where T : TableEntityBase, ITableExtra, new()
    {
        private readonly CloudTableClient m_Client;

        /// <summary>
        /// Constructor allows for various connections independent of the config file. 
        /// Can you used for data migrations
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
        /// Returns a internal reference to the Table
        /// </summary>
        private CloudTable GetTable(string tableRef)
        {
            CloudTable ct = m_Client.GetTableReference(tableRef);
            ct.CreateIfNotExists(); //Cannot be async to make sure the table gets created before attempted use 
            return ct;
        }

        /// <summary>
        /// CRUD for a single entity. Replace is the default Action
        /// </summary>
        /// <param name="obj">The Entity to manage</param>
        /// <param name="direction">The direction of the data</param>
        public void ManageData(T obj, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            _ = obj.GetIDValue(); //Solves bug issue by making sure it always exists
            TableOperation op = TableOperation.InsertOrReplace(obj);
            switch (direction)
            {
                case TableOperationType.Delete:
                    op = TableOperation.Delete(obj);
                    break;
                case TableOperationType.InsertOrMerge:
                    op = TableOperation.InsertOrMerge(obj);
                    break;
            }
            GetTable(obj.TableReference).Execute(op);
        }

        /// <summary>
        /// Retrieves All Data from the underlying Table
        /// </summary>
        public List<T> GetAllTableData()
        {
            TableQuery<T> q = new TableQuery<T>();
            return this.GetCollection(q);
        }

        /// <summary>
        /// Returns a list of Objects
        /// </summary>
        /// <param name="partitionKeyID">The value that should be found in the PartitionKey space / field to bring back unique values</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(string partitionKeyID)
        {
            string queryString =
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, partitionKeyID);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return this.GetCollection(q);
        }
        /// <summary>
        /// Allows for dynamic generation of complicated queries to return a list of Objects
        /// </summary>
        /// <param name="queryTerms">A list of search definitions</param>
        /// <param name="combineStyle">If more than one search definition how should they all be combined</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(List<DBQueryItem> queryTerms, QueryCombineStyle combineStyle = QueryCombineStyle.or)
        {
            StringBuilder queryString = new StringBuilder();
            foreach (DBQueryItem qItem in queryTerms)
            {
                if (queryString.Length > 1) queryString.Append(" " + combineStyle.ToString() + " ");

                queryString.Append(qItem.FieldName + " " + qItem.HowToCompare.ToString());
                if (qItem.IsDateTime)
                    queryString.Append(" datetime'");
                else
                    queryString.Append(" '");
                queryString.Append(qItem.FieldValue + "'");
            }

            TableQuery<T> q = new TableQuery<T>().Where(queryString.ToString());
            return this.GetCollection(q);
        }
        /// <summary>
        /// Returns a list of Objects
        /// </summary>
        /// <param name="definedQuery">The Query Definition to run</param>
        /// <returns>A collection of the Type requested from the appropriate table</returns>
        public List<T> GetCollection(TableQuery<T> definedQuery)
        {
            T obj = Activator.CreateInstance<T>();
            return GetTable(obj.TableReference).ExecuteQuery(definedQuery).ToList();
        }
        /// <summary>
        /// Gets a specific row of data from the table
        /// </summary>
        /// <param name="rowKeyID">The RowKey in the table to grab. This is usually the ID property of the Object</param>
        public T GetRowObject(string rowKeyID)
        {
            string queryString =
                TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, rowKeyID);
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return this.GetCollection(q).FirstOrDefault()!;
        }

        /// <summary>
        /// Gets a specific row of data from the table based on your simple single query data
        /// </summary>
        /// <param name="fieldName">Name of the single field to compare</param>
        /// <param name="howToCompare">Direction of the comparison</param>
        /// <param name="fieldValue">The value the field should have</param>
        public T GetRowObject(string fieldName, ComparisonTypes howToCompare, string fieldValue)
        {
            string queryString = fieldName + " " + howToCompare.ToString() + " '" + fieldValue + "'";
            TableQuery<T> q = new TableQuery<T>().Where(queryString);
            return this.GetCollection(q).FirstOrDefault()!;
        }

        /// <summary>
        /// Asynchronously batch commits all of the data stored in a collection of T objects after organizing them by Partition
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the dB. Default is InsertOrReplace</param>
        /// <returns>Confirmation the data is committed</returns>
        public async Task<bool> BatchUpdateList(List<T> data, TableOperationType direction = TableOperationType.InsertOrReplace)
        {
            if (data.Count < 1) return false;
            await Task.Run(() =>
            {
                List<T> temp;
                List<T> gList;
                int remainingItems;
                //Table storage can only operate on a single partition at a time. Group the items by Partition
                IEnumerable<IGrouping<string, T>> groupedData = data.GroupBy(g => g.PartitionKey)!;

                foreach (var group in groupedData)
                {
                    gList = group.ToList();
                    TableBatchOperation tb;
                    for (int i = 0; i < gList.Count; i += 99)
                    {
                        tb = new TableBatchOperation();
                        temp = new List<T>();
                        remainingItems = gList.Count - i > 99 ? 99 : gList.Count - i;
                        temp.AddRange(gList.GetRange(i, remainingItems));
                        foreach (T o in temp)
                        {
                            _ = o.GetIDValue(); //Solves bug issue by making sure it always exists
                            switch (direction)
                            {
                                case TableOperationType.Delete:
                                    tb.Add(TableOperation.Delete(o));
                                    break;
                                case TableOperationType.InsertOrMerge:
                                    tb.Add(TableOperation.InsertOrMerge(o));
                                    break;
                                default:
                                    tb.Add(TableOperation.InsertOrReplace(o));
                                    break;
                            }
                        }

                        T obj = Activator.CreateInstance<T>();
                        this.GetTable(obj.TableReference).ExecuteBatch(tb);
                        ++i;
                    }
                }
            });
            return direction != TableOperationType.Delete;
        }//end BatchUpdate()

    } //End Class DataAccess

    /// <summary>
    /// Manages session based data into the database
    /// </summary>
    public class Session
    {
        private string? m_sessionID;
        private List<AppSessionData> m_sessionData = new List<AppSessionData>();
        private readonly DataAccess<AppSessionData> m_da;

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
        /// <param name="sessionID">Session Identifyer</param>
        public Session(string accountName, string accountKey, string sessionID) : this(accountName, accountKey)
        {
            m_sessionID = sessionID;
            m_sessionData = m_da.GetCollection(m_sessionID);
        }

        /// <summary>
        /// Finds the abandoned or stale conversations in session that are from specific sources that don't notify of session end and therefore have non-committed data
        /// </summary>
        /// <returns>The collection of stale Prospect object data that needs to be submitted to CRM</returns>
        public List<string> GetStaleSessions()
        {
            //note : filters are odd in Table storage. Its not related to the Row. Each FIELD stands on its own. So ask for the FIELDS you need to get the ROW it belongs to then filter further
            //Looking for the Prospect objects to NOT be submitted and the channel type to be facebook. Add other channel VALUES ONLY as needed with OR's
            string filter = @"(Key eq 'SessionSubmittedToCRM' and Value eq 'false') or (Key eq 'prospectChannel' and Value eq 'facebook')";
            TableQuery<AppSessionData> q = new TableQuery<AppSessionData>().Where(filter);

            //Look for Submitted value to be false and the Timing of the data to be within a tolerance to NOT get current conversations
            List<AppSessionData> coll = m_da.GetCollection(q);
            coll = coll.FindAll(s => s.Timestamp.IsTimeBetween(DateTime.Now, 5, 60));
            return coll.DistinctBy(s => s.SessionID).Select(s => s.SessionID).ToList()!;
        }
        /// <summary>
        /// Removes legacy session data
        /// </summary>
        /// <remarks>TODO: This needs to be an azure event as the table size grows. This will slow down processing</remarks>
        public async Task CleanSessionData()
        {
            await Task.Run(() =>
            {
                string queryString =
                TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.LessThan, DateTime.Now.AddHours(-2));
                TableQuery<AppSessionData> q = new TableQuery<AppSessionData>().Where(queryString);
                List<AppSessionData> data = m_da.GetCollection(q);
                _ = m_da.BatchUpdateList(data, TableOperationType.Delete);
            });
        }

        /// <summary>
        /// Uploads all session data to the dB
        /// </summary>
        /// <returns>Confirmaton the data is commited</returns>
        public bool CommitData()
        {
            _ = this.CleanSessionData();
            return this.CommitData(m_sessionData, TableOperationType.InsertOrReplace);
        }

        /// <summary>
        /// If a user comes in again to restart the conversation, we should remove previous session data as mistakes can be made by the system
        /// </summary>
        public void RestartSession()
        {
            _ = m_da.BatchUpdateList(SessionData!, TableOperationType.Delete);
            m_sessionData = new List<AppSessionData>();
        }

        /// <summary>
        /// Commits all of the data stored in a collection
        /// </summary>
        /// <param name="data">The data to work on</param>
        /// <param name="direction">How to put the data into the dB</param>
        /// <returns>Confirmation the data is committed</returns>
        public bool CommitData(List<AppSessionData> data, TableOperationType direction)
        {
            _ = m_da.BatchUpdateList(data, direction);
            this.DataHasBeenCommitted = direction != TableOperationType.Delete;
            return this.DataHasBeenCommitted;
        }

        /// <summary>
        /// Checks to see if an object already exists within the collection
        /// </summary>
        /// <param name="key">The Key to find</param>
        /// <returns>The object, new or existing</returns>
        private AppSessionData? Find(string key)
        {
            AppSessionData? b = this.SessionData!.Find(s => s.Key == key);
            if (b == null)
            {
                b = new AppSessionData() { SessionID = m_sessionID, Key = key };
                this.SessionData!.Add(b);
            }
            return b;
        }

        /// <summary>
        /// Locates the object within the collection matching the specified key
        /// </summary>
        /// <param name="key">Identifyer</param>
        /// <returns>The requested onject if its exists, null if not</returns>
        /// <remarks>This is called Indexers or default indexers</remarks>
        public AppSessionData? this[string key]
        {
            get { return this.Find(key); }
            set { this.Find(key)!.Value = value!.ToString(); }
        }

        /// <summary>
        /// Readonly reference to the data stored 
        /// </summary>
        public List<AppSessionData>? SessionData
        {
            get { return m_sessionData; }
        }
        /// <summary>
        /// Determines whether the data has successfully been committed to the database
        /// </summary>
        public bool DataHasBeenCommitted { get; set; }

        /// <summary>
        /// Destructor makes sure the data is committed to the DB
        /// </summary>
        ~Session()
        {
            if (!this.DataHasBeenCommitted) this.CommitData();
        }
    }//end class Session
}
