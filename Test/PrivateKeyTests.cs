using log4net;
using NUnit.Framework;

namespace BitCoinSharp.Test
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
            Logger.Debug(keyPair.PubKey);


            //create an address
            var address = keyPair.ToAddress(networkParameters);

            //address.
            Logger.Debug(address.ToString());
        }
 
    }
}