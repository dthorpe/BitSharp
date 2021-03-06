﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using BitSharp.Node.Domain;
using BitSharp.Node.Network;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Node.Workers
{
    internal class BlockRequestWorker : Worker
    {
        private static readonly TimeSpan STALE_REQUEST_TIME = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MISSED_STALE_REQUEST_TIME = TimeSpan.FromSeconds(3);
        private static readonly int MAX_REQUESTS_PER_PEER = 100;

        private readonly Logger logger;
        private readonly LocalClient localClient;
        private readonly CoreDaemon coreDaemon;
        private readonly CoreStorage coreStorage;

        private readonly ConcurrentDictionary<UInt256, BlockRequest> allBlockRequests;
        private readonly ConcurrentDictionary<Peer, ConcurrentDictionary<UInt256, DateTime>> blockRequestsByPeer;
        private readonly ConcurrentDictionary<UInt256, BlockRequest> missedBlockRequests;

        private int targetChainLookAhead;
        private List<ChainedHeader> targetChainQueue;
        private int targetChainQueueIndex;
        private DateTime targetChainQueueTime;
        private bool targetChainChanged;

        private readonly DurationMeasure blockRequestDurationMeasure;
        private readonly RateMeasure blockDownloadRateMeasure;
        private readonly CountMeasure duplicateBlockDownloadCountMeasure;

        private readonly WorkerMethod flushWorker;
        private readonly ConcurrentQueue<FlushBlock> flushQueue;
        private readonly ConcurrentSet<UInt256> flushBlocks;

        private readonly WorkerMethod diagnosticWorker;

        public BlockRequestWorker(WorkerConfig workerConfig, Logger logger, LocalClient localClient, CoreDaemon coreDaemon)
            : base("BlockRequestWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime, logger)
        {
            this.logger = logger;
            this.localClient = localClient;
            this.coreDaemon = coreDaemon;
            this.coreStorage = coreDaemon.CoreStorage;

            this.allBlockRequests = new ConcurrentDictionary<UInt256, BlockRequest>();
            this.blockRequestsByPeer = new ConcurrentDictionary<Peer, ConcurrentDictionary<UInt256, DateTime>>();
            this.missedBlockRequests = new ConcurrentDictionary<UInt256, BlockRequest>();

            this.localClient.OnBlock += HandleBlock;
            this.coreDaemon.OnChainStateChanged += HandleChainStateChanged;
            this.coreDaemon.OnTargetChainChanged += HandleTargetChainChanged;
            this.coreStorage.BlockTxesMissed += HandleBlockTxesMissed;
            this.coreDaemon.BlockMissed += HandleBlockMissed;

            this.blockRequestDurationMeasure = new DurationMeasure(sampleCutoff: TimeSpan.FromMinutes(5));
            this.blockDownloadRateMeasure = new RateMeasure();
            this.duplicateBlockDownloadCountMeasure = new CountMeasure(TimeSpan.FromSeconds(30));

            this.targetChainQueue = new List<ChainedHeader>();
            this.targetChainQueueIndex = 0;
            this.targetChainLookAhead = 1;

            this.flushWorker = new WorkerMethod("BlockRequestWorker.FlushWorker", FlushWorkerMethod, initialNotify: true, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue, logger: this.logger);
            this.flushQueue = new ConcurrentQueue<FlushBlock>();
            this.flushBlocks = new ConcurrentSet<UInt256>();

            this.diagnosticWorker = new WorkerMethod("BlockRequestWorker.DiagnosticWorker", DiagnosticWorkerMethod, initialNotify: true, minIdleTime: TimeSpan.FromSeconds(10), maxIdleTime: TimeSpan.FromSeconds(10), logger: this.logger);
        }

        public float GetBlockDownloadRate(TimeSpan perUnitTime)
        {
            return this.blockDownloadRateMeasure.GetAverage(perUnitTime);
        }

        public int GetDuplicateBlockDownloadCount()
        {
            return this.duplicateBlockDownloadCountMeasure.GetCount();
        }

        protected override void SubDispose()
        {
            this.localClient.OnBlock -= HandleBlock;
            this.coreDaemon.OnChainStateChanged -= HandleChainStateChanged;
            this.coreDaemon.OnTargetChainChanged -= HandleTargetChainChanged;
            this.coreStorage.BlockTxesMissed -= HandleBlockTxesMissed;
            this.coreDaemon.BlockMissed -= HandleBlockMissed;

            this.blockRequestDurationMeasure.Dispose();
            this.blockDownloadRateMeasure.Dispose();
            this.duplicateBlockDownloadCountMeasure.Dispose();

            this.flushWorker.Dispose();
            this.diagnosticWorker.Dispose();
        }

        protected override void SubStart()
        {
            this.flushWorker.Start();
            //this.diagnosticWorker.Start();
        }

        protected override void SubStop()
        {
            this.flushWorker.Stop();
            this.diagnosticWorker.Stop();
        }

        protected override void WorkAction()
        {
            // update rates
            new MethodTimer(false).Time("UpdateLookAhead", () =>
                UpdateLookAhead());

            // update list of blocks on target chain to request
            new MethodTimer(false).Time("UpdateTargetChainQueue", () =>
                UpdateTargetChainQueue());

            // send out request to peers
            //      missing blocks will be requested from every peer
            //      target chain blocks will be requested from each peer in non-overlapping chunks
            new MethodTimer(false).Time("SendBlockRequests", () =>
                SendBlockRequests());
        }

        private void UpdateLookAhead()
        {
            //TODO this needs to work properly when the internet connection is slower than blocks can be processed

            var blockProcessingTime = this.coreDaemon.AverageBlockProcessingTime();
            if (blockProcessingTime == TimeSpan.Zero)
            {
                this.targetChainLookAhead = 1;
            }
            else
            {
                // determine target chain look ahead
                var lookAheadTime = TimeSpan.FromSeconds(30);
                this.targetChainLookAhead = 1 + (int)(lookAheadTime.TotalSeconds / blockProcessingTime.TotalSeconds);

                this.logger.Debug(new string('-', 80));
                this.logger.Debug("Look Ahead: {0:#,##0}".Format2(this.targetChainLookAhead));
                this.logger.Debug("Block Request Count: {0:#,##0}".Format2(this.allBlockRequests.Count));
                this.logger.Debug(new string('-', 80));
            }
        }

        private void UpdateTargetChainQueue()
        {
            // force update if the target chain changed
            if (this.targetChainChanged)
                this.targetChainChanged = false;
            // update the target chain queue at most once per second
            else if (this.targetChainQueueTime != null && DateTime.UtcNow - targetChainQueueTime < TimeSpan.FromSeconds(1))
                return;

            this.targetChainQueueTime = DateTime.UtcNow;

            var currentChainLocal = this.coreDaemon.CurrentChain;
            var targetChainLocal = this.coreDaemon.TargetChain;

            // find missing blocks on the target chain to be requested, taking a chunk at a time
            if (targetChainLocal != null && this.targetChainQueueIndex >= this.targetChainQueue.Count)
            {
                this.targetChainQueue = currentChainLocal.NavigateTowards(targetChainLocal)
                    .Select(x => x.Item2)
                    .Take(this.targetChainLookAhead)
                    .Where(x => !this.coreStorage.ContainsBlockTxes(x.Hash))
                    .ToList();
                this.targetChainQueueIndex = 0;
            }
        }

        private void SendBlockRequests()
        {
            // don't do work on empty target chain queue
            if (this.targetChainQueue.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var requestTasks = new List<Task>();

            // take and remove any missed block requests that are now stale
            var staleMissedBlockRequests = this.missedBlockRequests.TakeAndRemoveWhere(
                x => (now - x.Value.RequestTime) > MISSED_STALE_REQUEST_TIME)
                .ToDictionary();

            // count missed block requests against their peers
            foreach (var peer in staleMissedBlockRequests.Values.Select(x => x.Peer))
                peer.AddBlockMiss();

            // remove any stale requests from the global list of requests
            this.allBlockRequests.RemoveWhere(x =>
                (now - x.Value.RequestTime) > STALE_REQUEST_TIME
                || staleMissedBlockRequests.ContainsKey(x.Key)
                || !this.localClient.ConnectedPeers.Contains(x.Value.Peer));

            var peerCount = this.localClient.ConnectedPeers.Count;
            if (peerCount == 0)
                return;

            // clear any disconnected peers
            foreach (var peer in blockRequestsByPeer.Keys)
            {
                if (!this.localClient.ConnectedPeers.Contains(peer))
                {
                    ConcurrentDictionary<UInt256, DateTime> remove;
                    blockRequestsByPeer.TryRemove(peer, out remove);
                }
            }

            // reset target queue index
            this.targetChainQueueIndex = 0;

            // spread the number of blocks queued to be requested over each peer
            var requestsPerPeer = Math.Max(1, this.targetChainLookAhead / peerCount);
            requestsPerPeer = Math.Min(requestsPerPeer, MAX_REQUESTS_PER_PEER);

            // loop through each connected peer
            foreach (var peer in this.localClient.ConnectedPeers)
            {
                // don't request blocks from seed peers
                if (peer.IsSeed)
                    continue;

                // retrieve the peer's currently requested blocks
                var peerBlockRequests = this.blockRequestsByPeer.AddOrUpdate(
                    peer,
                    addKey => new ConcurrentDictionary<UInt256, DateTime>(),
                    (existingKey, existingValue) => existingValue);

                // remove any stale requests from the peer's list of requests
                peerBlockRequests.RemoveWhere(x => (now - x.Value) > STALE_REQUEST_TIME);

                // determine the number of requests that can be sent to the peer
                var requestCount = requestsPerPeer - peerBlockRequests.Count;
                if (requestCount > 0)
                {
                    // iterate through the blocks that should be requested for this peer
                    var invVectors = ImmutableArray.CreateBuilder<InventoryVector>();
                    foreach (var requestBlock in GetRequestBlocksForPeer(requestCount, peerBlockRequests))
                    {
                        // track block requests
                        peerBlockRequests[requestBlock] = now;
                        this.allBlockRequests.TryAdd(requestBlock, new BlockRequest(peer, now));
                        BlockRequest ignore;
                        this.missedBlockRequests.TryRemove(requestBlock, out ignore);

                        // add block to inv request
                        invVectors.Add(new InventoryVector(InventoryVector.TYPE_MESSAGE_BLOCK, requestBlock));
                    }

                    // send out the request for blocks
                    if (invVectors.Count > 0)
                        requestTasks.Add(peer.Sender.SendGetData(invVectors.ToImmutable()));
                }
            }

            // wait for request tasks to complete
            if (!Task.WaitAll(requestTasks.ToArray(), TimeSpan.FromSeconds(10)))
            {
                this.logger.Info("Request tasks timed out.");
            }

            // notify for another loop of work when there are missed block requests remaining
            if (this.missedBlockRequests.Count > 0)
                this.NotifyWork();

            // notify for another loop of work when out of target chain queue to use
            if (this.targetChainQueueIndex >= this.targetChainQueue.Count)
                this.NotifyWork();
        }

        private IEnumerable<UInt256> GetRequestBlocksForPeer(int count, ConcurrentDictionary<UInt256, DateTime> peerBlockRequests)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            else if (count == 0)
                yield break;

            // keep track of blocks iterated blocks for peer
            var currentCount = 0;

            // iterate through the blocks on the target chain, each peer will request a separate chunk of blocks
            for (; this.targetChainQueueIndex < this.targetChainQueue.Count && currentCount < count; this.targetChainQueueIndex++)
            {
                var requestBlock = this.targetChainQueue[this.targetChainQueueIndex];

                if (!this.flushBlocks.Contains(requestBlock.Hash)
                    && !peerBlockRequests.ContainsKey(requestBlock.Hash)
                    && !this.allBlockRequests.ContainsKey(requestBlock.Hash)
                    && !this.coreStorage.ContainsBlockTxes(requestBlock.Hash))
                {
                    yield return requestBlock.Hash;
                    currentCount++;
                }
            }
        }

        private void FlushWorkerMethod(WorkerMethod instance)
        {
            var initalCount = this.flushQueue.Count;
            var count = 0;

            FlushBlock flushBlock;
            while (this.flushQueue.TryDequeue(out flushBlock))
            {
                // cooperative loop
                this.ThrowIfCancelled();

                var peer = flushBlock.Peer;
                var block = flushBlock.Block;

                if (this.coreStorage.TryAddBlock(block))
                    this.blockDownloadRateMeasure.Tick();
                else
                    this.duplicateBlockDownloadCountMeasure.Tick();

                this.flushBlocks.Remove(block.Hash);

                BlockRequest blockRequest;
                this.allBlockRequests.TryRemove(block.Hash, out blockRequest);

                DateTime requestTime;
                ConcurrentDictionary<UInt256, DateTime> peerBlockRequests;
                if (this.blockRequestsByPeer.TryGetValue(peer, out peerBlockRequests)
                    && peerBlockRequests.TryRemove(block.Hash, out requestTime))
                {
                    this.blockRequestDurationMeasure.Tick(DateTime.UtcNow - requestTime);
                }

                this.NotifyWork();

                count++;
                if (count > initalCount)
                    break;
            }

            //this.blockCache.Flush();
        }

        private void DiagnosticWorkerMethod(WorkerMethod instance)
        {
            this.logger.Info(new string('-', 80));
            this.logger.Info("allBlockRequests.Count: {0:#,##0}".Format2(this.allBlockRequests.Count));
            this.logger.Info("blockRequestsByPeer.InnerCount: {0:#,##0}".Format2(this.blockRequestsByPeer.Sum(x => x.Value.Count)));
            this.logger.Info("targetChainQueue.Count: {0:#,##0}".Format2(this.targetChainQueue.Count));
            this.logger.Info("targetChainQueueIndex: {0:#,##0}".Format2(this.targetChainQueueIndex));
            this.logger.Info("targetChainQueueTime: {0}".Format2(this.targetChainQueueTime));
            this.logger.Info("blockRequestDurationMeasure: {0}".Format2(this.blockRequestDurationMeasure.GetAverage()));
            this.logger.Info("blockDownloadRateMeasure: {0}/s".Format2(this.blockDownloadRateMeasure.GetAverage(TimeSpan.FromSeconds(1))));
            this.logger.Info("duplicateBlockDownloadCountMeasure: {0}/s".Format2(this.duplicateBlockDownloadCountMeasure.GetCount()));
            this.logger.Info("targetChainLookAhead: {0}".Format2(this.targetChainLookAhead));
            this.logger.Info("flushQueue.Count: {0}".Format2(this.flushQueue.Count));
            this.logger.Info("flushBlocks.Count: {0}".Format2(this.flushBlocks.Count));
        }

        private void HandleBlock(Peer peer, Block block)
        {
            this.flushBlocks.Add(block.Hash);
            this.flushQueue.Enqueue(new FlushBlock(peer, block));
            this.flushWorker.NotifyWork();

            // stop tracking any missed block requests for the received block
            BlockRequest ignore;
            this.missedBlockRequests.TryRemove(block.Hash, out ignore);
        }

        private void HandleChainStateChanged(object sender, EventArgs e)
        {
            this.NotifyWork();
        }

        private void HandleTargetChainChanged(object sender, EventArgs e)
        {
            this.targetChainChanged = true;
            this.NotifyWork();
        }

        private void HandleBlockTxesMissed(UInt256 blockHash)
        {
            this.NotifyWork();
        }

        private void HandleBlockMissed(UInt256 blockHash)
        {
            //TODO block should be tracked as soon as the block message is received so it isn't re-requested while it is still downloading
            // don't send re-requests against blocks in the flush queue
            if (this.flushBlocks.Contains(blockHash))
                return;

            // retrieve the block request for the missed block and track it as missing
            BlockRequest blockRequest;
            if (this.allBlockRequests.TryGetValue(blockHash, out blockRequest))
            {
                this.missedBlockRequests.TryAdd(blockHash, blockRequest);
            }

            // notify now that missed block request is being tracked
            this.NotifyWork();
        }

        private sealed class HeightComparer : IComparer<ChainedHeader>
        {
            public int Compare(ChainedHeader x, ChainedHeader y)
            {
                return x.Height - y.Height;
            }
        }

        private sealed class BlockRequest
        {
            private readonly Peer peer;
            private readonly DateTime requestTime;

            public BlockRequest(Peer peer, DateTime requestTime)
            {
                this.peer = peer;
                this.requestTime = requestTime;
            }

            public Peer Peer { get { return this.peer; } }

            public DateTime RequestTime { get { return this.requestTime; } }
        }

        private sealed class FlushBlock
        {
            private readonly Peer peer;
            private readonly Block block;

            public FlushBlock(Peer peer, Block block)
            {
                this.peer = peer;
                this.block = block;
            }

            public Peer Peer { get { return this.peer; } }

            public Block Block { get { return this.block; } }
        }
    }
}
