using Microsoft.Azure.Cosmos.Table;
using System.Reflection;
using System.Xml.Serialization;

namespace AZTableStorage.Models
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
}
