using FluentAssertions;
using log4net;
using NUnit.Framework;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities.Encoders;

namespace BitCoinSharp.Tests.Unit
{
    public class PrivateKeyTests
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof (PrivateKeyTests));

        [Test]
        public void CreatePrivateKey()
        {
            var networkParameters = NetworkParameters.TestNet();

            var keyPair = new EcKey();
            
            //the tostring displays public and private.
            Logger.Debug(keyPair);

            //private key in format for import/export from bitcoin-qt
            Logger.Debug(keyPair.GetPrivateKeyEncoded(networkParameters));

            //public key. this is not the address.
            Logger.Debug(keyPair.PublicKey);


            //create an address
            var address = keyPair.ToAddress(networkParameters);

            //address.
            Logger.Debug(address.ToString());
        }


        [Test]
        public void ImportExistingPrivateKey()
        {
            //arrange
            //this is an exsiting (testnet) private key. 
            const string testPrivateKey = "00d1470e342840b283c8007291992b21176d610a732a1f900334c0d71866422ad5";

            var privkey = new BigInteger(1, Hex.Decode(testPrivateKey));
            var key = new EcKey(privkey);

            key.ToString().Should().Be("pub:045c7266196cb522208fad3541854a412e238044de415e7f9071ac27124481d0130e4eee6f76e0649eaa84bf35b1f84444f337ac902569c631fa73be80c5583fc0 priv:00d1470e342840b283c8007291992b21176d610a732a1f900334c0d71866422ad5");
        }

 
    }
}