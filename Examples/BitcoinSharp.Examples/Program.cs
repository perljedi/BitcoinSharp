﻿using System;
using System.Collections.Generic;
using System.Linq;
using log4net.Config;

namespace BitcoinSharp.Examples
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            XmlConfigurator.Configure();

            if (args == null || args.Length == 0)
            {
                Console.WriteLine("BitcoinSharp.Examples <name> <args>");
                return;
            }

            var examples = new Dictionary<string, Action<string[]>>(StringComparer.InvariantCultureIgnoreCase)
                           {
                               {"DumpWallet", DumpWallet.Run},
                               {"FetchBlock", FetchBlock.Run},
                               {"PingService", PingService.Run},
                               {"PrintPeers", PrintPeers.Run},
                               {"PrivateKeys", PrivateKeys.Run},
                               {"RefreshWallet", RefreshWallet.Run}
                           };

            var name = args[0];
            Action<string[]> run;
            if (!examples.TryGetValue(name, out run))
            {
                Console.WriteLine("Example '{0}' not found", name);
                return;
            }

            run(args.Skip(1).ToArray());
        }
    }
}