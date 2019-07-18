﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using FASTER.core;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using NUnit.Framework.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using static FASTER.test.CloneTests;

namespace FASTER.test
{
    [TestFixture]
    internal class CloneTests
    {
        public const int TotalRecordSlotsInMemory = 336;
        public static int DirectoryFileCount(string path) => new DirectoryInfo(path).GetFiles().Length;

        public class CloneTestKey
        {
            public long Value;
        }

        public class CloneTestPayload : IEquatable<CloneTestPayload>
        {
            public string Value;

            public bool Equals(CloneTestPayload other) => this.Value == other.Value;
        }

        public struct RecordWrapper : IFasterEqualityComparer<RecordWrapper>
        {
            public CloneTestKey Value;

            public static IObjectSerializer<RecordWrapper> CreateSerializer() => new RecordSerializer();

            public long GetHashCode64(ref RecordWrapper k)
            {
                var hash32 = (ulong)(uint)k.Value.Value.GetHashCode();
                if (hash32 == 0)
                {
                    hash32 = 1;
                }

                return (long)((hash32 << 32) | hash32);
            }

            public bool Equals(ref RecordWrapper k1, ref RecordWrapper k2) => k1.Value != null && k1.Value.Value == k2.Value?.Value;

            public class RecordSerializer : IObjectSerializer<RecordWrapper>
            {
                public Stream stream;

                public void BeginDeserialize(Stream stream) => this.stream = stream;
                public void BeginSerialize(Stream stream) => this.stream = stream;
                public void EndDeserialize() => this.stream = null;
                public void EndSerialize() => this.stream = null;

                public void Serialize(ref RecordWrapper wrappedKey)
                {
                    using (var writer = new BinaryWriter(this.stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(wrappedKey.Value.Value);
                    }
                }

                public void Deserialize(ref RecordWrapper wrappedKey)
                {
                    using (var reader = new BinaryReader(this.stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        wrappedKey.Value = new CloneTestKey() { Value = reader.ReadInt64() };
                    }
                }
            }
        }

        public class RecordList
        {
            public List<CloneTestPayload> List = new List<CloneTestPayload>();

            public static IObjectSerializer<RecordList> CreateSerializer() => new RecordListSerializer();

            public class RecordListSerializer : IObjectSerializer<RecordList>
            {
                public Stream stream;

                public void BeginDeserialize(Stream stream) => this.stream = stream;
                public void BeginSerialize(Stream stream) => this.stream = stream;
                public void EndDeserialize() => this.stream = null;
                public void EndSerialize() => this.stream = null;

                public void Serialize(ref RecordList wrappedPayload)
                {
                    using (var binaryWriter = new BinaryWriter(this.stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        binaryWriter.Write(wrappedPayload.List.Count);
                        foreach (var payload in wrappedPayload.List)
                        {
                            binaryWriter.Write(payload.Value);
                        }
                    }
                }

                public void Deserialize(ref RecordList wrappedPayload)
                {
                    using (var binaryReader = new BinaryReader(this.stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        var count = binaryReader.ReadInt32();
                        for (var i = 0; i < count; i++)
                        {
                            var value = binaryReader.ReadString();
                            wrappedPayload.List.Add(new CloneTestPayload { Value = value });
                        }
                    }
                }
            }
        }

        public struct RecordOperation
        {
            public readonly CloneTestPayload Payload;

            public RecordOperation(CloneTestPayload payload, bool isAdd)
            {
                this.Payload = payload;
                this.IsAdd = isAdd;
            }

            public bool IsAdd { get; }
            public bool IsDelete => !this.IsAdd;

            public static RecordOperation CreateAddOperation(CloneTestPayload payload) => new RecordOperation(payload, true);
            public static RecordOperation CreateDeleteOperation(CloneTestPayload payload) => new RecordOperation(payload, false);
        }

        public class CloneTestDevice : LocalStorageDevice
        {
            public int lastSegmentId = 0;

            // File Buffering - When we aren't performing data integrity checks, all writes/reads are sector aligned, so we can disable file buffering for performance.
            // When performing data integrity checks, we need to write/read at non-sector aligned offsets, so we need file buffering enabled
            public CloneTestDevice(string name) : base(name, preallocateFile: false, deleteOnClose: true, disableFileBuffering: false)
            {
                if (!Environment.Is64BitProcess)
                {
                    throw new InvalidOperationException("Only 64 bit is supported");
                }
            }

            public CloneTestDevice(CloneTestDevice cloneSource, string newDeviceName) : this(newDeviceName)
            {
                // When cloning a device, we need to open handles to all shared log files
                // so they persist if the source device is disposed.
                this.lastSegmentId = cloneSource.lastSegmentId;
                for (int segmentId = 0; segmentId <= this.lastSegmentId; segmentId++)
                {
                    var hardLinkName = GetSegmentName(segmentId);
                    var sourceFileName = cloneSource.GetSegmentName(segmentId);
                    File.Copy(sourceFileName, hardLinkName);
                    try
                    {
                        GetOrAddHandle(segmentId);
                    }
                    catch
                    {
                        File.Delete(hardLinkName);
                        throw;
                    }
                }
            }

            public CloneTestDevice Clone(string newDeviceName) => new CloneTestDevice(this, newDeviceName);

            public override void DeleteSegmentRange(int fromSegment, int toSegment) => throw new NotImplementedException();
            
            public override unsafe void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, IOCompletionCallback callback, IAsyncResult asyncResult)
            {
                InterlockedExchangeIfGreaterThan(ref this.lastSegmentId, segmentId);
                base.WriteAsync(sourceAddress, segmentId, destinationAddress, numBytesToWrite, callback, asyncResult);
            }
            
            public override unsafe void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, IOCompletionCallback callback, IAsyncResult asyncResult)
            {
                Debug.Assert(readLength > 0, "validation");
                // Uncomment this line to repro FataExecutionEngineError
                // byte[] buffer = new byte[readLength];
                base.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, callback, asyncResult);
            }
            
            public static void InterlockedExchangeIfGreaterThan(ref int location, int comparand)
            {
                int originalLocationValue;
                do
                {
                    originalLocationValue = location;
                    if (originalLocationValue >= comparand)
                    {
                        return;
                    }
                } while (Interlocked.CompareExchange(ref location, comparand, originalLocationValue) != originalLocationValue);
            }
        }

        public LookupStore CreateStore()
        {
            return new LookupStore();
        }

        public class CloneTestCallbacks : IFunctions<RecordWrapper, RecordList, RecordOperation, RecordList, Empty>
        {
            #region Read Exclusive Operations

            /// <summary>
            /// Called during a successful Read operation where value lives outside the mutable region, so it is guaranteed that no writes can occur concurrently on value.
            /// Reads payload from the "Value" format (used by FASTER to instance the payload) into the "Output" format (returned by FASTER).
            /// Optionally accounts for input passed by caller of the Read operation.
            /// </summary>
            public void SingleReader(ref RecordWrapper key, ref RecordOperation input, ref RecordList value, ref RecordList output)
            {
                // We use the same format for both Value and Output, so just copy
                output.List = value.List;
            }

            /// <summary>
            /// Called during a successful Read operation on a record that lives in the mutable region, so concurrent writes may occur during this operation.
            /// Reads payload from the "Value" format (used by FASTER to instance the payload) into the "Output" format (returned by FASTER).
            /// Optionally accounts for input passed by caller of the Read operation.
            /// </summary>
            public void ConcurrentReader(ref RecordWrapper key, ref RecordOperation input, ref RecordList value, ref RecordList output)
            {
                // Since this instance uses two mutually exclusive Write and Read phases, we can guarantee
                // that no concurrent writes will occur during a Read, so just use SingleReader
                SingleReader(ref key, ref input, ref value, ref output);
            }

            /// <summary>
            /// Called after a Read operation completes after originally returning Status.PENDING, during the subsequent call to CompletePending.
            /// If the "Output" is a value type, this callback must process the output appropriately, as it will not reach the original caller of Read.
            /// </summary>
            public void ReadCompletionCallback(ref RecordWrapper key, ref RecordOperation input, ref RecordList output, Empty context, Status status)
            {
                if (status != Status.OK && status != Status.NOTFOUND)
                {
                    throw new InvalidOperationException($"FASTER Read failed with {status}");
                }
            }

            #endregion Read Exclusive Operations

            #region RMW Exclusive Operations

            /// <summary>
            /// Called in response to RMW on a key that was not present in the instance.
            /// Writes input payload to the "Value" format (used by FASTER to instance payloads)
            /// </summary>
            public void InitialUpdater(ref RecordWrapper key, ref RecordOperation input, ref RecordList value)
            {
                if (input.IsDelete)
                {
                    throw new InvalidOperationException("Deleting record that does not exist");
                }

                value = new RecordList();
                value.List.Add(input.Payload);
            }

            /// <summary>
            /// Called in response to RMW when the key already exists in and is being in-place updated in the mutable region.
            /// Uses input payload to construct an initial record value in the "Value" format (used by FASTER to instance payloads)
            /// </summary>
            public void InPlaceUpdater(ref RecordWrapper key, ref RecordOperation input, ref RecordList value)
            {
                lock (value.List)
                {
                    if (input.IsAdd)
                    {
                        value.List.Add(input.Payload);
                    }
                    else
                    {
                        value.List.Remove(input.Payload);
                    }
                }
            }

            /// <summary>
            /// Called in response to RMW when the record is no longer in the mutable region and is being modified and copied to the tail of the log.
            /// Uses input payload and oldValue to construct newValue, the latter two being in the "Value" format (used by FASTER to instance payloads)
            /// </summary>
            public void CopyUpdater(ref RecordWrapper key, ref RecordOperation input, ref RecordList oldValue, ref RecordList newValue)
            {
                // No need to preserve the old value, just update in place and return it as the new value
                InPlaceUpdater(ref key, ref input, ref oldValue);
                newValue = oldValue;
            }

            /// <summary>
            /// Called after a RMW operation completes after originally returning Status.PENDING, during the subsequent call to CompletePending.
            /// </summary>
            public void RMWCompletionCallback(ref RecordWrapper key, ref RecordOperation input, Empty context, Status status)
            {
                if (status != Status.OK && status != Status.NOTFOUND)
                {
                    throw new InvalidOperationException($"FASTER RMW failed with {status}");
                }
            }

            #endregion RMW Exclusive Operations

            /// <summary>
            /// Called after a successful Checkpoint operation
            /// </summary>
            public void CheckpointCompletionCallback(Guid sessionId, long serialNum)
            {
            }

            /// <summary>
            /// Called during an Upsert or CopyReadsToTail operation to move or copy sourceValue from outside the mutable region to the tail of the log,
            /// so it is guaranteed that no writes can occur concurrently on sourceValue.
            /// Writes payload from sourceValue to destValue. Since both payloads live in FASTER storage, the "Value" format is used for both.
            /// </summary>
            public void SingleWriter(ref RecordWrapper key, ref RecordList sourceValue, ref RecordList destValue)
            {
                // Even though we specify CopyReadsToTail=false on the main log configuration, we still need to implement for the ReadCache log,
                // which always uses the CopyReadsToTail mechanism to cache.
                destValue = sourceValue;
            }

            /// <summary>
            /// Called during an Upsert or Delete operation when sourceValue lives in the mutable region, so concurrent writes may occur during this operation.
            /// Writes payload from sourceValue to destValue. Since both payloads live in FASTER storage, the "Value" format is used for both.
            /// </summary>
            public void ConcurrentWriter(ref RecordWrapper key, ref RecordList sourceValue, ref RecordList destValue)
                => throw new NotImplementedException(); // We don't use Upsert or Delete

            /// <summary>
            /// Called after a successful Upsert operation that was not able to complete immediately due to concurrent operations
            /// </summary>
            public void UpsertCompletionCallback(ref RecordWrapper key, ref RecordList value, Empty context)
                => throw new NotImplementedException(); // We don't use Upsert

            /// <summary>
            /// Called after a successful Delete operation that was not able to complete immediately due to concurrent operations
            /// </summary>
            public void DeleteCompletionCallback(ref RecordWrapper key, Empty context)
                => throw new NotImplementedException(); // We don't use Delete
        }

        private readonly Queue<IDisposable> disposables = new Queue<IDisposable>();

        [TearDown]
        public void TestCleanup()
        {
            while (this.disposables.Count > 0)
            {
                var disposable = this.disposables.Dequeue();
                try
                {
                    disposable.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        [Test]
        public void CircularBufferOfClones()
        {
            // Creates a circular buffer of 5 instance clones, at each iteration 
            const int concurrentInstances = 5;
            const int totalClones = 20;
            var originalSourceCount = TotalRecordSlotsInMemory;
            var deltaCount = TotalRecordSlotsInMemory;
            var instanceList = new LinkedList<Tuple<LookupStore, int /*start of record range*/, int /*end of record range*/>>();

            // Establish the base full snapshot instance
            var fullInstance = CreateStore();
            fullInstance.PopulateDefault(originalSourceCount);
            fullInstance.VerifyDefault(originalSourceCount);
            Assert.IsTrue(Directory.Exists(fullInstance.LogPath));
            var logFileCount = DirectoryFileCount(fullInstance.LogPath);
            Assert.IsTrue(logFileCount > 0);

            instanceList.AddFirst(new Tuple<LookupStore, int, int>(fullInstance, 0, originalSourceCount));

            var netCloneCount = 0;
            void CreateNewClone()
            {
                var tuple = instanceList.Last.Value;
                var sourceInstance = tuple.Item1;
                var sourceFirst = tuple.Item2;
                var sourceLast = tuple.Item3;
                var nextClone = sourceInstance.Clone();
                this.disposables.Enqueue(nextClone);
                netCloneCount++;

                var cloneFirst = sourceLast + deltaCount;
                var cloneLast = sourceLast + deltaCount;
                instanceList.AddLast(new Tuple<LookupStore, int, int>(nextClone, cloneFirst, cloneLast));

                // Add deltaCount records to the end of source's range
                for (long i = sourceLast; i < cloneLast; i++)
                {
                    nextClone.Add(new CloneTestKey() { Value = i }, new CloneTestPayload() { Value = $"Payload.{i}" });
                }

                // Delete deltaCount records from beginning of source's range
                for (long i = sourceFirst; i < cloneFirst; i++)
                {
                    nextClone.Delete(new CloneTestKey() { Value = i }, new CloneTestPayload() { Value = $"Payload.{i}" });
                }

                nextClone.Seal();
                nextClone.VerifyDefault(cloneFirst, cloneLast - cloneFirst);

                // Log files should be growing as we add more instances/records
                var newLogFileCount = DirectoryFileCount(nextClone.LogPath);
                Assert.IsTrue(newLogFileCount > logFileCount);
                logFileCount = newLogFileCount;
            }

            void VerifyAllInstances()
            {
                // Verify all instances
                foreach (var tuple in instanceList)
                {
                    var instance = tuple.Item1;
                    var first = tuple.Item2;
                    var last = tuple.Item3;
                    instance.VerifyDefault(first, last - first);

                    // Make sure deleted records not found
                    if (first > deltaCount)
                    {
                        instance.VerifyNotFound(first - deltaCount, deltaCount);
                    }

                    // Make sure future records not found
                    instance.VerifyNotFound(last, deltaCount);
                }
            }

            // Establish the baseline concurrent stores
            while (instanceList.Count < concurrentInstances)
            {
                CreateNewClone();
            }

            VerifyAllInstances();

            // Start exiring the oldest stores as we add new stores
            while (netCloneCount < totalClones)
            {
                CreateNewClone();

                var oldestInstance = instanceList.First.Value.Item1;
                oldestInstance.Dispose();
                instanceList.RemoveFirst();

                VerifyAllInstances();
            }
        }
    }


    internal class LookupStore : IDisposable
    {
        const int ConcurrentRMWCount = 100;
        private readonly int cloneNumber = 0;
        private readonly CloneTestDevice logDevice;
        private readonly CloneTestDevice objectLogDevice;
        private readonly FasterKV<RecordWrapper, RecordList, RecordOperation, RecordList, Empty, CloneTestCallbacks> faster;
        private int pendingRMWCount = 0;
        private Guid checkpoint;

        public LookupStore() : this(null)
        {
        }

        private LookupStore(LookupStore storeToClone)
        {
            this.cloneNumber = storeToClone?.cloneNumber + 1 ?? 0; // Must be set before referencing checkpoint path

            if (storeToClone == null)
            {
                Directory.CreateDirectory(RootDirectory);
                Directory.CreateDirectory(this.CheckpointPath);
                Directory.CreateDirectory(this.LogPath);
                this.logDevice = new CloneTestDevice(this.LogDevicePath);
                this.objectLogDevice = new CloneTestDevice(this.ObjectLogDevicePath);
            }
            else
            {
                Directory.CreateDirectory(this.LogPath);
                this.logDevice = storeToClone.logDevice.Clone(this.LogDevicePath);
                this.objectLogDevice = storeToClone.objectLogDevice.Clone(this.ObjectLogDevicePath);
            }

            this.faster = new FasterKV<
                RecordWrapper,      // Key
                RecordList,         // Value - storage format of all payloads in FASTER. We instance a list of records for each key.
                RecordOperation,    // Input - input format of payloads to FASTER. We input Add or Delete record operations.
                RecordList,         // Output - output format of payloads returned by FASTER reads. We read a list of records for each key.
                Empty,              // Context - arbitrary context passed to callback functions
                CloneTestCallbacks>(
                1 << 10,
                new CloneTestCallbacks(),
                new LogSettings
                {
                    LogDevice = logDevice,
                    ObjectLogDevice = objectLogDevice,
                    CopyReadsToTail = false,
                    PageSizeBits = 9,
                    SegmentSizeBits = 13,
                    MemorySizeBits = 13,
                    MutableFraction = 0.9,
                    ReadCacheSettings = new ReadCacheSettings
                    {
                        PageSizeBits = 9,
                        MemorySizeBits = 13,
                        SecondChanceFraction = 0.9
                    }
                },
                new CheckpointSettings { CheckpointDir = CheckpointPath, CheckPointType = CheckpointType.FoldOver },
                new SerializerSettings<RecordWrapper, RecordList>
                {
                    keySerializer = () => RecordWrapper.CreateSerializer(),
                    valueSerializer = () => RecordList.CreateSerializer(),
                });

            // Restore the new faster instance from the parent instance's checkpoint
            if (storeToClone != null)
            {
                this.faster.Recover(storeToClone.checkpoint);
            }

            this.faster.StartSession();
        }

        internal static string RootDirectory { get; } = $"{Path.GetTempPath()}\\FASTER";
        internal string CheckpointPath => $"{RootDirectory}\\Checkpoints";
        internal string LogPath => $"{RootDirectory}\\Logs_{this.cloneNumber}";
        internal string LogDevicePath => $"{this.LogPath}\\Log";
        internal string ObjectLogDevicePath => $"{this.LogPath}\\ObjectLog";

        public void Seal()
        {
            // We can flush the entire main log to disk, since we use ReadCache for our cache
            this.faster.CompletePending(true);
            this.faster.Log.DisposeFromMemory();

            // Checkpoint. This will essentially only store the index due to FoldOver. We don't need to copy the files
            // on disk until we need to clone, since they will no longer change.
            this.faster.TakeFullCheckpoint(out this.checkpoint);
            this.faster.CompleteCheckpoint(wait: true);
        }

        public LookupStore Clone() => new LookupStore(this);

        public void Add(CloneTestKey key, CloneTestPayload payload)
                => RMW(key, RecordOperation.CreateAddOperation(payload));

        public void Delete(CloneTestKey key, CloneTestPayload payload)
                => RMW(key, RecordOperation.CreateDeleteOperation(payload));

        public IEnumerable<CloneTestPayload> Lookup(CloneTestKey key)
        {
            var input = default(RecordOperation); // don't care about input value for Read
            var outputValue = new RecordList();
            var fasterKey = new RecordWrapper() { Value = key };
            var status = this.faster.Read(ref fasterKey, ref input, ref outputValue, Empty.Default, 0);

            if (status == Status.ERROR)
            {
                throw new InvalidOperationException($"FASTER Read failed with {Status.ERROR}");
            }
            else if (status == Status.NOTFOUND)
            {
                return null;
            }
            else if (status == Status.PENDING)
            {
                this.faster.CompletePending(true);
            }

            if (outputValue.List.Count == 0)
            {
                return null;
            }

            return outputValue.List;
        }

        public void RMW(CloneTestKey key, RecordOperation operation)
        {
            var fasterKey = new RecordWrapper() { Value = key };
            var status = this.faster.RMW(ref fasterKey, ref operation, Empty.Default, 0);
            if (status == Status.ERROR)
            {
                throw new InvalidOperationException($"FASTER RMW failed with {Status.ERROR}");
            }

            if (status == Status.PENDING)
            {
                if (++this.pendingRMWCount >= ConcurrentRMWCount)
                {
                    this.pendingRMWCount = 0;
                    this.faster.CompletePending(false);
                }
            }
        }

        public void PopulateDefault(int startingIndex, int count)
        {
            for (long i = startingIndex; i < startingIndex + count; i++)
            {
                Add(new CloneTestKey() { Value = i }, new CloneTestPayload() { Value = $"Payload.{i}" });
            }

            Seal();
        }

        public void PopulateDefault(int count) => PopulateDefault(0, count);

        public void VerifyDefault(int startingIndex, int count)
        {
            for (long i = startingIndex; i < startingIndex + count; i++)
            {
                var results = Lookup(new CloneTestKey() { Value = i })?.ToArray();
                Assert.AreEqual(1, results.Count());
                Assert.AreEqual($"Payload.{i}", results[0].Value);
            }
        }

        public void VerifyNotFound(int startingIndex, int count)
        {
            for (long i = startingIndex; i < startingIndex + count; i++)
            {
                var results = Lookup(new CloneTestKey() { Value = i });
                Assert.IsNull(results);
            }
        }

        public void VerifyDefault(int count) => VerifyDefault(0, count);
        public void Dispose() => this.faster.Dispose();
    }
}