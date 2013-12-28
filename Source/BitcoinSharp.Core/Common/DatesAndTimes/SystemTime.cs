using System;

namespace BitcoinSharp.Core.Common.DatesAndTimes
{
    public static class SystemTime
    {
        private static Func<ulong> _unixNow = () => UnixTime.ToUnixTime(DateTime.Now);

        public static Func<ulong> UnixNow
        {
            get { return _unixNow; }
            set { _unixNow = value; }
        }

        private static Func<DateTime> _now = () => DateTime.Now;

        public static Func<DateTime> Now
        {
            get { return _now; }
            set { _now = value; }
        }
    }
}