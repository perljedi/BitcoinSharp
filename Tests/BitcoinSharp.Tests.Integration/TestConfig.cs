using log4net.Config;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Integration
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