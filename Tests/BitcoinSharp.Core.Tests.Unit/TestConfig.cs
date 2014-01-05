using log4net.Config;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Unit
{
    [SetUpFixture]
    public class TestConfig
    {
        [SetUp]
        public void SetUp()
        {
            XmlConfigurator.Configure();
        }

        [TearDown]
        public void TearDown()
        {
        }
    }
}