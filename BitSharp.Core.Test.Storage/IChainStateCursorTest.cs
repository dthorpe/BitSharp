﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Builders;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using BitSharp.Core.Test;
using BitSharp.Core.Test.Rules;
using BitSharp.Domain;
using BitSharp.Esent.Test;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Core.Test.Storage
{
    [TestClass]
    public class IChainStateCursorTest : StorageProviderTest
    {
        [TestMethod]
        public void TestBeginTransaction()
        {
            RunTest(TestBeginTransaction);
        }

        [TestMethod]
        public void TestCommitTransaction()
        {
            RunTest(TestCommitTransaction);
        }

        [TestMethod]
        public void TestRollbackTransaction()
        {
            RunTest(TestRollbackTransaction);
        }

        [TestMethod]
        public void TestReadChain()
        {
            RunTest(TestReadChain);
        }

        [TestMethod]
        public void TestAddChainedHeader()
        {
            RunTest(TestAddChainedHeader);
        }

        [TestMethod]
        public void TestRemoveChainedHeader()
        {
            RunTest(TestRemoveChainedHeader);
        }

        [TestMethod]
        public void TestUnspentTxCount()
        {
            RunTest(TestUnspentTxCount);
        }

        [TestMethod]
        public void TestConainsUnspentTx()
        {
            RunTest(TestConainsUnspentTx);
        }

        [TestMethod]
        public void TestTryGetUnspentTx()
        {
            RunTest(TestTryGetUnspentTx);
        }

        [TestMethod]
        public void TestTryAddUnspentTx()
        {
            RunTest(TestTryAddUnspentTx);
        }

        [TestMethod]
        public void TestTryRemoveUnspentTx()
        {
            RunTest(TestTryRemoveUnspentTx);
        }

        [TestMethod]
        public void TestTryUpdateUnspentTx()
        {
            RunTest(TestTryUpdateUnspentTx);
        }

        private void TestBeginTransaction(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestCommitTransaction(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestRollbackTransaction(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestReadChain(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestAddChainedHeader(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestRemoveChainedHeader(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestUnspentTxCount(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestConainsUnspentTx(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestTryGetUnspentTx(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestTryAddUnspentTx(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestTryRemoveUnspentTx(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }

        private void TestTryUpdateUnspentTx(ITestStorageProvider provider)
        {
            Assert.Inconclusive("TODO");
        }
    }
}
