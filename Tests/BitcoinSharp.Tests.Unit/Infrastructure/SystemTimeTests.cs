using System;
using BitCoinSharp.Common;
using NUnit.Framework;

namespace BitCoinSharp.Tests.Unit.Infrastructure
{
    public class SystemTimeTests
    {
        [Test]
        public void defaults_to_date_time_now()
        {
            var date = DateTime.Now;
            SystemTime.Now = () => date;

            Assert.AreEqual(date, SystemTime.Now());
        }

        [Test]
        public void can_be_set_to_specific_time()
        {
            SystemTime.Now = () => DateTime.Now.AddDays(-1);
            Assert.AreNotEqual(DateTime.Now, SystemTime.Now());
        }

        [Test]
        public void can_be_used_to_get_unix_date_time()
        {
            var date = UnixTime.ToUnixTime(DateTime.Now);
            SystemTime.UnixNow = () => date;

            Assert.AreEqual(date, SystemTime.UnixNow());
        }

        [Test]
        public void can_be_used_to_set_specific_unix_date_time()
        {
            SystemTime.UnixNow = () => (UnixTime.ToUnixTime(DateTime.Now.AddDays(-1)));
            Assert.AreNotEqual(UnixTime.ToUnixTime(DateTime.Now), SystemTime.UnixNow());
        }
    }
}