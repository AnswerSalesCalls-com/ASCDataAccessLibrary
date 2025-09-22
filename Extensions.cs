using ASCTableStorage.Models;
using Newtonsoft.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASCTableStorage.Common
{
    /// <summary>
    /// Provides needed extension methods for various objects
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// Determine if the time given in the offset is in between the intervals desired compared to another DateTime
        /// </summary>
        /// <param name="t">The Offset of when the data came into the system</param>
        /// <param name="tCompare">The DateTime to compare / check against</param>
        /// <param name="startIntervalMinutes">An acceptible tolerance start</param>
        /// <param name="endIntervalMinutes">An acceptible tolerance end</param>
        /// <returns>True if the timeframes are within tolerance of each other</returns>
        public static bool IsTimeBetween(this DateTimeOffset t, DateTime tCompare, int startIntervalMinutes, int endIntervalMinutes)
        {
            TimeSpan ts = tCompare - t.ToLocalTime();
            return ts.TotalMinutes >= startIntervalMinutes && ts.TotalMinutes <= endIntervalMinutes;
        }
        /// <summary>
        /// Converts any list into an indexable list So that we always know where we left off during any interruptions.
        /// </summary>
        /// <typeparam name="T">The dataType being managed by the list</typeparam>
        /// <param name="data">The Data of the list</param>
        /// <returns>An Enumeration that has CURRENT Object which will have a Data and Index Property to be used</returns>
        public static IEnumerator<(T data, int index)> AsIndexable<T>(this IList<T> data)
        {
            return data.Select((data, index) => (data, index)).GetEnumerator();
        }
        /// <summary>
        /// Creates a savable collection of data that can be brought back at a later time to be worked on again.
        /// Works best if the data was in process of being USED and then interrupted use now needs to be stored for later use.
        /// </summary>
        /// <typeparam name="T">The DataType to manage</typeparam>
        /// <param name="coll">The Collection Data</param>
        /// <param name="name">The Name you wish to store the data as</param>
        public static QueueData<T> CreateQueue<T>(this IEnumerator<(T data, int index)> coll, string name)
        {
            List<T> newColl = new();
            if (coll.Current.data != null) newColl.Add(coll.Current.data); // Needs to also include the CURRENT location
            while (coll.MoveNext()) newColl.Add(coll.Current.data); // Appends from the last known working position

            return QueueData<T>.CreateFromList(newColl, name);
        }
    }// class Extensions

    /// <summary>
    /// Provides shared functions across the project
    /// </summary>
    public static class Functions
    {
        /// <summary>
        /// Gets the option to removes nullables from object when serializing with NewtonSoft
        /// </summary>
        /// <returns>The option</returns>
        public static JsonSerializerSettings NewtonSoftRemoveNulls() { return new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }; }

        /// <summary>
        /// Basic options for all serialization
        /// </summary>
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true, 
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };
    }
}
