﻿using BitSharp.Blockchain;
using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using BitSharp.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Daemon
{
    public class TargetChainWorker : IDisposable
    {
        public event EventHandler<ChainedBlock> OnWinningBlockChanged;

        private readonly CacheContext cacheContext;

        private readonly IBlockchainRules rules;

        private ChainedBlocks targetChainedBlocks;

        private readonly Dictionary<UInt256, Dictionary<UInt256, BlockHeader>> unchainedBlocksByPrevious;

        private readonly CancellationTokenSource shutdownToken;

        private readonly Worker readTargetChainedBlocksWorker;

        private readonly Worker chainBlocksWorker;

        private readonly ConcurrentQueue<BlockHeader> chainBlocksPending;

        private ChainedBlock targetBlock;
        private readonly ReaderWriterLockSlim targetBlockLock;

        public TargetChainWorker(IBlockchainRules rules, CacheContext cacheContext)
        {
            this.shutdownToken = new CancellationTokenSource();

            this.rules = rules;
            this.cacheContext = cacheContext;

            this.targetChainedBlocks = ChainedBlocks.CreateForGenesisBlock(this.rules.GenesisChainedBlock);

            this.targetBlock = this.rules.GenesisChainedBlock;
            this.targetBlockLock = new ReaderWriterLockSlim();

            this.chainBlocksPending = new ConcurrentQueue<BlockHeader>();

            this.unchainedBlocksByPrevious = new Dictionary<UInt256, Dictionary<UInt256, BlockHeader>>();

            // wire up cache events
            this.cacheContext.BlockHeaderCache.OnAddition += ChainBlockHeader;
            this.cacheContext.BlockHeaderCache.OnModification += ChainBlockHeader;
            this.cacheContext.BlockCache.OnAddition += ChainBlock;
            this.cacheContext.BlockCache.OnModification += ChainBlock;
            this.cacheContext.ChainedBlockCache.OnAddition += HandleChainedBlock;
            this.cacheContext.ChainedBlockCache.OnModification += HandleChainedBlock;

            // create workers
            this.chainBlocksWorker = new Worker("TargetChainWorker.ChainBlocksWorker", ChainBlocksWorker,
                runOnStart: true, waitTime: TimeSpan.FromSeconds(0), maxIdleTime: TimeSpan.FromSeconds(30));

            this.readTargetChainedBlocksWorker = new Worker("TargetChainWorker.ReadTargetChainedBlocksWorker", ReadTargetChainedBlocksWorker,
                runOnStart: true, waitTime: TimeSpan.FromSeconds(0), maxIdleTime: TimeSpan.FromSeconds(30));

            new Thread(
                () => ChainMissingBlocks())
                .Start();

            //TODO periodic rescan
            var checkThread = new Thread(() =>
            {
                new MethodTimer().Time("SelectMaxTotalWorkBlocks", () =>
                {
                    foreach (var chainedBlock in this.cacheContext.StorageContext.ChainedBlockStorage.SelectMaxTotalWorkBlocks())
                        HandleChainedBlock(chainedBlock.BlockHash, chainedBlock);
                });

                //Debugger.Break();
            });
            checkThread.Start();
        }

        public CacheContext CacheContext { get { return this.cacheContext; } }

        public IStorageContext StorageContext { get { return this.CacheContext.StorageContext; } }

        public ChainedBlocks TargetChainedBlocks { get { return this.targetChainedBlocks; } }

        public ChainedBlock WinningBlock { get { return this.targetChainedBlocks.LastBlock; } }

        public void Start()
        {
            try
            {
                // start loading the existing state from storage
                //TODO LoadExistingState();

                // startup workers
                this.chainBlocksWorker.Start();
                this.readTargetChainedBlocksWorker.Start();
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // cleanup events
            this.CacheContext.BlockHeaderCache.OnAddition -= ChainBlockHeader;
            this.CacheContext.BlockHeaderCache.OnModification -= ChainBlockHeader;
            this.CacheContext.BlockCache.OnAddition -= ChainBlock;
            this.CacheContext.BlockCache.OnModification -= ChainBlock;
            this.CacheContext.ChainedBlockCache.OnAddition -= HandleChainedBlock;
            this.CacheContext.ChainedBlockCache.OnModification -= HandleChainedBlock;

            // notify threads to begin shutting down
            this.shutdownToken.Cancel();

            // cleanup workers
            new IDisposable[]
            {
                this.chainBlocksWorker,
                this.readTargetChainedBlocksWorker,
                this.shutdownToken
            }.DisposeList();
        }

        private void ChainMissingBlocks()
        {
            foreach (var unchainedBlockHash in
                this.CacheContext.BlockHeaderCache.Keys
                .Except(this.CacheContext.ChainedBlockCache.Keys))
            {
                BlockHeader unchainedBlockHeader;
                if (this.CacheContext.BlockHeaderCache.TryGetValue(unchainedBlockHash, out unchainedBlockHeader))
                {
                    ChainBlockHeader(unchainedBlockHash, unchainedBlockHeader);
                }
            }
        }

        private void ChainBlockHeader(UInt256 blockHash, BlockHeader blockHeader)
        {
            try
            {
                if (blockHeader == null)
                    blockHeader = this.CacheContext.BlockHeaderCache[blockHash];
            }
            catch (MissingDataException) { return; }

            this.chainBlocksPending.Enqueue(blockHeader);
            this.chainBlocksWorker.NotifyWork();
        }

        private void ChainBlock(UInt256 blockHash, Block block)
        {
            ChainBlockHeader(blockHash, block != null ? block.Header : null);
        }

        private void HandleChainedBlock(UInt256 blockHash, ChainedBlock chainedBlock)
        {
            try
            {
                if (chainedBlock == null)
                    chainedBlock = this.CacheContext.ChainedBlockCache[blockHash];
            }
            catch (MissingDataException) { return; }

            this.targetBlockLock.DoWrite(() =>
            {
                if (this.targetBlock == null
                    || chainedBlock.TotalWork > this.targetBlock.TotalWork)
                {
                    this.targetBlock = chainedBlock;
                    this.readTargetChainedBlocksWorker.NotifyWork();
                }
            });
        }

        private void ChainBlocksWorker()
        {
            BlockHeader workBlock;
            while (this.chainBlocksPending.TryDequeue(out workBlock))
            {
                // cooperative loop
                if (this.shutdownToken.IsCancellationRequested)
                    return;

                if (!this.CacheContext.ChainedBlockCache.ContainsKey(workBlock.Hash))
                {
                    ChainedBlock prevChainedBlock;
                    if (this.CacheContext.ChainedBlockCache.TryGetValue(workBlock.PreviousBlock, out prevChainedBlock))
                    {
                        var newChainedBlock = new ChainedBlock
                        (
                            blockHash: workBlock.Hash,
                            previousBlockHash: workBlock.PreviousBlock,
                            height: prevChainedBlock.Height + 1,
                            totalWork: prevChainedBlock.TotalWork + workBlock.CalculateWork()
                        );

                        this.CacheContext.ChainedBlockCache[workBlock.Hash] = newChainedBlock;

                        if (this.unchainedBlocksByPrevious.ContainsKey(workBlock.Hash))
                        {
                            this.chainBlocksPending.EnqueueRange(this.unchainedBlocksByPrevious[workBlock.Hash].Values);
                        }

                        if (this.unchainedBlocksByPrevious.ContainsKey(workBlock.PreviousBlock))
                        {
                            this.unchainedBlocksByPrevious[workBlock.PreviousBlock].Remove(workBlock.Hash);
                            if (this.unchainedBlocksByPrevious[workBlock.PreviousBlock].Count == 0)
                                this.unchainedBlocksByPrevious.Remove(workBlock.PreviousBlock);
                        }
                    }
                    else
                    {
                        if (!this.unchainedBlocksByPrevious.ContainsKey(workBlock.PreviousBlock))
                            this.unchainedBlocksByPrevious[workBlock.PreviousBlock] = new Dictionary<UInt256, BlockHeader>();
                        this.unchainedBlocksByPrevious[workBlock.PreviousBlock][workBlock.Hash] = workBlock;
                    }
                }
                else
                {
                    if (this.unchainedBlocksByPrevious.ContainsKey(workBlock.Hash))
                    {
                        this.chainBlocksPending.EnqueueRange(this.unchainedBlocksByPrevious[workBlock.Hash].Values);
                    }
                }
            }
        }

        private void ReadTargetChainedBlocksWorker()
        {
            try
            {
                var targetBlockLocal = this.targetBlock;
                var targetChainedBlocksLocal = this.targetChainedBlocks;

                if (targetBlockLocal.BlockHash != targetChainedBlocksLocal.LastBlock.BlockHash)
                {
                    var newTargetChainedBlocks = targetChainedBlocksLocal.ToBuilder();

                    var deltaBlockPath = new MethodTimer(false).Time("deltaBlockPath", () =>
                        new BlockchainWalker().GetBlockchainPath(newTargetChainedBlocks.LastBlock, targetBlockLocal, blockHash => this.CacheContext.ChainedBlockCache[blockHash]));

                    foreach (var rewindBlock in deltaBlockPath.RewindBlocks)
                        newTargetChainedBlocks.RemoveBlock(rewindBlock);
                    foreach (var advanceBlock in deltaBlockPath.AdvanceBlocks)
                        newTargetChainedBlocks.AddBlock(advanceBlock);

                    //Debug.WriteLine("Winning chained block {0} at height {1}, total work: {2}".Format2(targetBlock.BlockHash.ToHexNumberString(), targetBlock.Height, targetBlock.TotalWork.ToString("X")));
                    this.targetChainedBlocks = newTargetChainedBlocks.ToImmutable();

                    var handler = this.OnWinningBlockChanged;
                    if (handler != null)
                        handler(this, targetBlockLocal);
                }
            }
            catch (MissingDataException) { }
        }
    }
}
