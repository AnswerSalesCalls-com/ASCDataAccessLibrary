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
    } // end FileTypes Property
} //end class BlobData
