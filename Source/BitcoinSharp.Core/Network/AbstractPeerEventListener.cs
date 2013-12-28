﻿/*
 * Copyright 2011 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using BitCoinSharp.Core.Messages;

namespace BitCoinSharp.Core.Network
{
    /// <summary>
    ///     Convenience abstract class for implementing a PeerEventListener.
    /// </summary>
    /// <remarks>
    ///     The default method implementations do nothing.
    ///     @author miron@google.com (Miron Cuperman)
    /// </remarks>
    public class AbstractPeerEventListener : IPeerEventListener
    {
        public virtual void OnBlocksDownloaded(Peer peer, Block block, int blocksLeft)
        {
        }

        public virtual void OnChainDownloadStarted(Peer peer, int blocksLeft)
        {
        }
    }
}