namespace AZTableStorage.Common
{
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
    }// class Extensions
}
