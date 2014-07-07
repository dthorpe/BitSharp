﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Builders;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using BitSharp.Core.Test;
using BitSharp.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Esent.Test
{
    //TODO i'd like a better way than an abstract base for providing tests that storage providers can plug into
    public abstract class StorageProviderTest
    {
        public abstract IBlockStorageNew OpenBlockStorage();

        public abstract IChainStateBuilderStorage OpenChainStateBuilderStorage(IChainStateStorage parentUtxo, Logger logger);

        public void TestPrune()
        {
            var txCount = 100;
            var transactions = Enumerable.Range(0, txCount).Select(x => RandomData.RandomTransaction()).ToImmutableArray();
            var blockHeader = RandomData.RandomBlockHeader().With(MerkleRoot: DataCalculator.CalculateMerkleRoot(transactions));
            var block = new Block(blockHeader, transactions);

            var expectedFinalDepth = (int)Math.Ceiling(Math.Log(txCount, 2));
            var expectedFinalElement = new BlockElement(index: 0, depth: expectedFinalDepth, hash: blockHeader.MerkleRoot, pruned: true);

            var pruneOrderSource = Enumerable.Range(0, txCount).ToList();
            var pruneOrder = new List<int>(txCount);

            var random = new Random();
            while (pruneOrderSource.Count > 0)
            {
                var randomIndex = random.Next(pruneOrderSource.Count);

                pruneOrder.Add(pruneOrderSource[randomIndex]);
                pruneOrderSource.RemoveAt(randomIndex);
            }

            using (var blockStorage = this.OpenBlockStorage())
            {
                blockStorage.AddBlock(block);
                var blockTxes = blockStorage.ReadBlock(block.Hash, block.Header.MerkleRoot).ToList();

                new MethodTimer().Time(() =>
                {
                    foreach (var pruneIndex in pruneOrder)
                    {
                        blockStorage.PruneElements(block.Hash, new[] { pruneIndex });
                        blockStorage.ReadBlockElements(block.Hash, block.Header.MerkleRoot).ToList();
                    }

                    var finalBlockElements = blockStorage.ReadBlockElements(block.Hash, block.Header.MerkleRoot).ToList();

                    Assert.AreEqual(1, finalBlockElements.Count);
                    Assert.AreEqual(expectedFinalElement, finalBlockElements[0]);
                });
            }
        }

        public void TestRollback()
        {
            //TODO
            MainnetRules.BypassValidation = true;

            var logger = LogManager.CreateNullLogger();
            var sha256 = new SHA256Managed();

            var blockProvider = new MainnetBlockProvider();
            var blocks = Enumerable.Range(0, 1000).Select(x => blockProvider.GetBlock(x)).ToList();

            var genesisBlock = blocks[0];
            var genesisHeader = new ChainedHeader(genesisBlock.Header, height: 0, totalWork: 0);
            var genesisChain = Chain.CreateForGenesisBlock(genesisHeader);
            var genesisUtxo = Utxo.CreateForGenesisBlock(genesisBlock.Hash);
            var chainBuilder = genesisChain.ToBuilder();

            var rules = new MainnetRules(logger, null);

            using (var blockStorage = this.OpenBlockStorage())
            using (var chainStateBuilderStorage = this.OpenChainStateBuilderStorage(genesisUtxo.Storage, logger))
            using (var chainStateBuilder = new ChainStateBuilder(chainBuilder, chainStateBuilderStorage, logger, rules, blockStorage))
            {
                // add blocks to storage
                foreach (var block in blocks)
                    blockStorage.AddBlock(block);

                // store the genesis utxo state
                var expectedUtxos = new List<List<KeyValuePair<UInt256, UnspentTx>>>();
                using (var chainState = chainStateBuilder.ToImmutable())
                {
                    expectedUtxos.Add(chainState.Utxo.GetUnspentTransactions().ToList());
                }

                // calculate utxo forward and store its state at each step along the way
                for (var blockIndex = 1; blockIndex < blocks.Count; blockIndex++)
                {
                    var block = blocks[blockIndex];
                    var chainedHeader = new ChainedHeader(block.Header, blockIndex, 0);
                    var blockTxes = block.Transactions.Select((x, i) => new BlockTx(i, 0, x.Hash, x));

                    chainStateBuilder.AddBlock(chainedHeader, blockTxes);

                    using (var chainState = chainStateBuilder.ToImmutable())
                    {
                        expectedUtxos.Add(chainState.Utxo.GetUnspentTransactions().ToList());
                    }
                }

                // verify the utxo state before rolling back
                //TODO verify the UTXO hash hard-coded here is correct
                var expectedUtxoHash = UInt256.Parse("7e155a373c9a97d6d6f7f985e4d43f31a80833e9b4fce865c85552a650dd630e", NumberStyles.HexNumber);
                using (var utxoStream = new UtxoStream(logger, expectedUtxos.Last()))
                {
                    var utxoHash = new UInt256(sha256.ComputeDoubleHash(utxoStream));
                    Assert.AreEqual(expectedUtxoHash, utxoHash);
                }
                expectedUtxos.RemoveAt(expectedUtxos.Count - 1);

                // roll utxo backwards and validate its state at each step along the way
                for (var blockIndex = blocks.Count - 1; blockIndex >= 1; blockIndex--)
                {
                    var block = blocks[blockIndex];
                    var chainedHeader = new ChainedHeader(block.Header, blockIndex, 0);
                    var blockTxes = block.Transactions.Select((x, i) => new BlockTx(i, 0, x.Hash, x));

                    chainStateBuilder.RollbackBlock(chainedHeader, blockTxes);

                    var expectedUtxo = expectedUtxos.Last();
                    expectedUtxos.RemoveAt(expectedUtxos.Count - 1);

                    List<KeyValuePair<UInt256, UnspentTx>> actualUtxo;
                    using (var chainState = chainStateBuilder.ToImmutable())
                    {
                        actualUtxo = chainState.Utxo.GetUnspentTransactions().ToList();
                    }

                    CollectionAssert.AreEqual(expectedUtxo, actualUtxo, new UtxoComparer(), "UTXO differs at height: {0}".Format2(blockIndex));
                }
            }
        }

        private class UtxoComparer : IComparer, IComparer<KeyValuePair<UInt256, UnspentTx>>
        {
            public int Compare(KeyValuePair<UInt256, UnspentTx> x, KeyValuePair<UInt256, UnspentTx> y)
            {
                if (x.Key == y.Key && x.Value.Equals(y.Value))
                    return 0;
                else
                    return -1;
            }

            int IComparer.Compare(object x, object y)
            {
                return this.Compare((KeyValuePair<UInt256, UnspentTx>)x, (KeyValuePair<UInt256, UnspentTx>)y);
            }
        }
    }
}