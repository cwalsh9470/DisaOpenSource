using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Disa.Framework.Bubbles;
using ProtoBuf;
using System.Globalization;

namespace Disa.Framework
{
    internal static class BubbleGroupDatabase
    {
        internal static readonly object OperationLock = new object();

        public static string GetBaseLocation()
        {
            var databasePath = Platform.GetDatabasePath();
            if (!Directory.Exists(databasePath))
            {
                Directory.CreateDirectory(databasePath);
            }

            var bubbleGroupsBasePath = Path.Combine(databasePath, "bubblegroups");
            if (!Directory.Exists(bubbleGroupsBasePath))
            {
                Directory.CreateDirectory(bubbleGroupsBasePath);
            }

            return bubbleGroupsBasePath;
        }

        public static string GetServiceLocation(ServiceInfo info)
        {
            var bubbleGroupServicePath = Path.Combine(GetBaseLocation(), info.ServiceName);
            if (!Directory.Exists(bubbleGroupServicePath))
            {
                Directory.CreateDirectory(bubbleGroupServicePath);
            }

            return bubbleGroupServicePath;
        }

        public static string GetLocation(BubbleGroup theGroup)
        {
            var tableLocation = GetServiceLocation(theGroup.Service.Information);
            var groupLocation = Path.Combine(tableLocation,
                theGroup.Service.Information.ServiceName + "^" + theGroup.ID + ".group");

            return groupLocation;
        }

        private static VisualBubble Deserialize(byte[] bubbleData, bool skipDeleted = true)
        {
            using (var bubbleDataRawStream = new MemoryStream(bubbleData))
            {
                var visualBubble = Serializer.Deserialize<VisualBubble>(bubbleDataRawStream);

                //TODO: we should check for this prior to deserializing ... but the performance gain is minimal atm... so no point =)
                if (visualBubble is NewDayBubble)
                    return null;
                else if (skipDeleted && visualBubble.Deleted)
                    return null;

                return visualBubble;
            }
        }

        internal static void RollBackTo(BubbleGroup group, long index)
        {
            if (index <= 0)
            {
                throw new ArgumentException("Index can't be <= 0!");
            }

            var groupDatabaseLocation = GetLocation(@group);

            using (var stream = File.Open(groupDatabaseLocation, FileMode.Open, FileAccess.ReadWrite))
            {
                if (index > stream.Length)
                {
                    throw new Exception("File length can't be greater than index.");
                }
                stream.SetLength(index);
            }
        }

        public static void AddBubble(BubbleGroup group, VisualBubble b)
        {
            lock (OperationLock)
            {
                using (var ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, b);

                    var bubbleData = ms.ToArray();
                    var bubbleHeader = b.GetType().Name + ":" + b.ID + ":" + b.Time;

                    var file = GetLocation(@group);

                    using (var stream = File.Open(file, FileMode.Append, FileAccess.Write))
                    {
                        BubbleGroupIndex.UpdateLastBubbleOrAndLastModifiedIndex(group.ID, b, stream.Position);
                        BubbleGroupDatabasePrimitives.WriteBubbleData(stream, bubbleData);
                        BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, bubbleHeader);
                    }
                }
            }
        }

        public static void AddBubbles(BubbleGroup group, VisualBubble[] bubbles)
        {
            lock (OperationLock)
            {
                var file = GetLocation(@group);

                using (var stream = File.Open(file, FileMode.Append, FileAccess.Write))
                {
                    BubbleGroupIndex.UpdateLastBubbleOrAndLastModifiedIndex(group.ID, bubbles.Last(), stream.Position);

                    foreach (var b in bubbles)
                    {
                        using (var ms = new MemoryStream())
                        {
                            Serializer.Serialize(ms, b);

                            var bubbleData = ms.ToArray();
                            var bubbleHeader = b.GetType().Name + ":" + b.ID + ":" + b.Time;

                            BubbleGroupDatabasePrimitives.WriteBubbleData(stream, bubbleData);
                            BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, bubbleHeader);
                        }
                    }
                }
            }
        }

        public static bool InsertBubbleByTime(BubbleGroup group, VisualBubble bubble, int maxDepth = 1000)
        {
            return InsertBubblesByTime(@group, new [] { bubble }, maxDepth);
        }

        public static bool InsertBubblesByTime(BubbleGroup group, VisualBubble[] bubbles, int maxDepth = 1000, bool guidCheck = false, bool insertAtTop = false)
        {
            //we can't operate if a thread/worker is concurrently processing a new bubble
            lock (OperationLock)
            {
                var groupDatabaseLocation = GetLocation(@group);
                var bubbleTuples = bubbles.Select(x => new Tuple<long, VisualBubble>(x.Time, x)).ToList();

                VisualBubble lastBubble = null;
                long insertPosition = -1;

                using (var stream = File.Open(groupDatabaseLocation, FileMode.Open, FileAccess.ReadWrite))
                {
                    stream.Seek(stream.Length, SeekOrigin.Begin);

                    for (var i = 0; i < (maxDepth != -1 ? maxDepth : Int32.MaxValue); i++)
                    {
                        if (stream.Position == 0)
                        {
                            if (insertAtTop)
                            {
                                insertPosition = 0;

                                var cut = new byte[(int)stream.Length];
                                stream.Read(cut, 0, (int)stream.Length);
                                stream.Position = 0;

                                var bubblesToInsert = bubbleTuples.Select(x => x.Item2).ToList();
                                bubblesToInsert.TimSort((x, y) =>
                                {
                                    // IMPORTANT: Our Time field is specified in seconds, however scenarios are appearing
                                    //            (e.g., bots) where messages are sent in on the same second but still require
                                    //            proper ordering. In this case, Services may set a flag specifying a fallback 
                                    //            to the ID assigned by the Service (e.g. Telegram).
                                    if ((x.Time == y.Time) &&
                                        (x.IsServiceIdSequence && y.IsServiceIdSequence))
                                    {
                                        return string.Compare(
                                            strA: x.IdService,
                                            strB: y.IdService,
                                            ignoreCase: false,
                                            culture: CultureInfo.InvariantCulture);
                                    }
                                    // OK, simpler scenario, just compare the Time field for the bubbles
                                    else
                                    {
                                        return x.Time.CompareTo(y.Time);
                                    }
                                });

                                foreach (var bubbleToInsert in bubblesToInsert)
                                {
                                    using (var ms = new MemoryStream())
                                    {
                                        Serializer.Serialize(ms, bubbleToInsert);

                                        var bubbleToInsertData = ms.ToArray();
                                        var bubbleToInsertHeader = bubbleToInsert.GetType().Name + ":" + bubbleToInsert.ID
                                                                   + ":" + bubbleToInsert.Time;

                                        BubbleGroupDatabasePrimitives.WriteBubbleData(stream, bubbleToInsertData);
                                        BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, bubbleToInsertHeader);
                                    }
                                }

                                stream.Write(cut, 0, cut.Length);
                                stream.SetLength(stream.Position);

                                break;
                            }
                            else
                            {
                                break;
                            }
                        }

                        byte[] headerBytes;
                        int headerBytesLength;
                        int endPosition;
                        int bubbleDataLength;

                        BubbleGroupDatabasePrimitives.ReadBubbleHeader(stream, out headerBytes, out headerBytesLength);
                        BubbleGroupDatabasePrimitives.FindBubbleHeaderDelimiter(headerBytes, headerBytesLength, 0, out endPosition);

                        //we need to seek over the data.
                        var bubbleData = BubbleGroupDatabasePrimitives.ReadBubbleData(stream, out bubbleDataLength);
                        var nBubble = Deserialize(bubbleData);

                        var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                            endPosition + 1, out endPosition);
                        var time = BubbleGroupDatabasePrimitives.AsciiBytesToString(headerBytes, endPosition + 1,
                            headerBytesLength);
                        long longTime;
                        Int64.TryParse(time, out longTime);

                        {
                            var bubblesToInsert = bubbleTuples.Where(x =>
                            {
                                if (guidCheck && guid == x.Item2.ID)
                                    return false;

                                // IMPORTANT: Our Time field is specified in seconds, however scenarios are appearing
                                //            (e.g., bots) where messages are sent in on the same second but still require
                                //            proper ordering. In this case, Services may set a flag specifying a fallback 
                                //            to the ID assigned by the Service (e.g. Telegram).
                                if ((longTime == x.Item1) &&
                                    (nBubble.IsServiceIdSequence && x.Item2.IsServiceIdSequence))
                                {
                                    if (string.Compare(
                                        strA: nBubble.IdService,
                                        strB: x.Item2.IdService,
                                        ignoreCase: false,
                                        culture: CultureInfo.InvariantCulture) < 0)
                                    {
                                        //
                                        // Incoming bubble qualifies to be placed AFTER current bubble we are evaluating
                                        //

                                        return true;
                                    }
                                    else
                                    {
                                        //
                                        // Incoming bubble DOES NOT qualify to be placed AFTER current bubble we are evaluating
                                        //

                                        return false;
                                    }
                                }
                                // OK, simpler scenario, DOES incoming bubble qualify to be placed AFTER current bubble we are evaluating
                                if (longTime <= x.Item1)
                                {
                                    return true;
                                }
                                else
                                {
                                    return false;
                                }

                            }).ToList();
                            if (!bubblesToInsert.Any())
                            {
                                continue;
                            }

                            var bubbleSize = headerBytesLength + bubbleDataLength + 8;
                            var insertLocation = stream.Position + bubbleSize;
                            stream.Seek(insertLocation, SeekOrigin.Begin);

                            var cutLength = stream.Length - insertLocation;
                            var cut = new byte[cutLength];
                            stream.Read(cut, 0, (int)cutLength); //should always work as long as the count ain't crazy high

                            stream.Seek(insertLocation, SeekOrigin.Begin);

                            insertPosition = stream.Position;

                            foreach (var bubbleToInsert in bubblesToInsert.Select(x => x.Item2))
                            {
                                using (var ms = new MemoryStream())
                                {
                                    Serializer.Serialize(ms, bubbleToInsert);

                                    var bubbleToInsertData = ms.ToArray();
                                    var bubbleToInsertHeader = bubbleToInsert.GetType().Name + ":" + bubbleToInsert.ID
                                                               + ":" + bubbleToInsert.Time;

                                    BubbleGroupDatabasePrimitives.WriteBubbleData(stream, bubbleToInsertData);
                                    BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, bubbleToInsertHeader);
                                }
                            }

                            stream.Write(cut, 0, cut.Length);
                            stream.SetLength(stream.Position);

                            // adding to end
                            if (cut.Length == 0)
                            {
                                lastBubble = bubblesToInsert.Last().Item2;
                            }

                            foreach (var bubbleToInsert in bubblesToInsert)
                            {
                                bubbleTuples.Remove(bubbleToInsert);
                            }

                            if (!bubbleTuples.Any())
                            {
                                break;
                            }
                        }
                    }

                    BubbleGroupIndex.UpdateLastBubbleOrAndLastModifiedIndex(group.ID, lastBubble, insertPosition);
                }

                if (!bubbleTuples.Any())
                {
                    return true;
                }

                return false;
            }
        }

        public static bool UpdateBubble(BubbleGroup group, VisualBubble bubble, int searchDepth = 100)
        {
            return UpdateBubble(@group, new[] { bubble }, searchDepth);
        }

        public static bool UpdateBubble(BubbleGroup group, VisualBubble[] bubbles, int searchDepth = 100)
        {
            //we can't operate if a thread/worker is concurrently processing a new bubble
            lock (OperationLock)
            {
                var groupDatabaseLocation = GetLocation(@group);
                var bubbleTuples = bubbles.Select(x => new Tuple<string, VisualBubble>(x.ID, x)).ToList();

                VisualBubble lastBubble = null;
                long insertPosition = -1;

                using (var stream = File.Open(groupDatabaseLocation, FileMode.Open, FileAccess.ReadWrite))
                {
                    stream.Seek(stream.Length, SeekOrigin.Begin);

                    for (var i = 0; i < (searchDepth != -1 ? searchDepth : Int32.MaxValue); i++)
                    {
                        if (stream.Position == 0)
                        {
                            break;
                        }

                        byte[] headerBytes;
                        int headerBytesLength;
                        int endPosition;

                        BubbleGroupDatabasePrimitives.ReadBubbleHeader(stream, out headerBytes, out headerBytesLength);
                        BubbleGroupDatabasePrimitives.FindBubbleHeaderDelimiter(headerBytes, headerBytesLength, 0, out endPosition);
                        var bubbleDataLength = BubbleGroupDatabasePrimitives.JumpBubbleData(stream); //we need to seek over the data.

                        var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                            endPosition + 1, out endPosition);

                        Tuple<string, VisualBubble> bubbleTuple;
                        if ((bubbleTuple = bubbleTuples.FirstOrDefault(x => x.Item1 == guid)) == null)
                        {
                            continue;
                        }

                        var b = bubbleTuple.Item2;

                        var bubbleSize = headerBytesLength + bubbleDataLength + 8;
                        var cutStart = stream.Position + bubbleSize;
                        var cutLength = stream.Length - cutStart;

                        insertPosition = stream.Position;

                        using (var ms = new MemoryStream())
                        {
                            Serializer.Serialize(ms, b);

                            var updatedBubbleData = ms.ToArray();
                            var updatedBubbleHeader = b.GetType().Name + ":" + b.ID + ":" + b.Time;
                            var updatedBubbleSize = updatedBubbleHeader.Length + updatedBubbleData.Length + 8;

                            var bubbleInjectDelta = bubbleSize - updatedBubbleSize;
                            //enough room
                            if (bubbleInjectDelta != 0)
                            {
                                var injectPosition = stream.Position;

                                stream.Position = cutStart; //higher 
                                var cut = new byte[cutLength];
                                stream.Read(cut, 0, (int)cutLength);//should always work as long as the count ain't crazy high

                                //var bw = new BinaryWriter(stream, Encoding.ASCII);
                                stream.Position = injectPosition;
                                BubbleGroupDatabasePrimitives.WriteBubbleData(stream, updatedBubbleData);
                                BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, updatedBubbleHeader);

                                stream.Write(cut, 0, cut.Length);
                                stream.SetLength(stream.Position);
                            }
                            //they're the same!
                            else if (bubbleInjectDelta == 0)
                            {
                                //var bw = new BinaryWriter(stream, Encoding.ASCII);
                                BubbleGroupDatabasePrimitives.WriteBubbleData(stream, updatedBubbleData);
                                BubbleGroupDatabasePrimitives.WriteBubbleHeader(stream, updatedBubbleHeader);
                            }
                        }
                            
                        if (cutLength == 0)
                        {
                            lastBubble = b;
                        }

                        bubbleTuples.Remove(bubbleTuple);
                        if (!bubbleTuples.Any())
                        {
                            break;
                        }
                    }

                    BubbleGroupIndex.UpdateLastBubbleOrAndLastModifiedIndex(group.ID, lastBubble, insertPosition);
                }

                if (!bubbleTuples.Any())
                {
                    return true;
                }

                return false;
            }
        }

        public static long FetchBubblesOnDay(BubbleGroup group, Stream stream, Action<VisualBubble> updateCallback,
            int day, long cursor = -1, string[] bubbleTypes = null, Func<VisualBubble, bool> comparer = null)
        {
            var lowerTime = Time.GetNowUnixTimestamp() - (day * 86400);
            var upperTime = lowerTime + 86400 + 600;

            lock (OperationLock)
            {
                stream.Seek(cursor == -1 ? stream.Length : cursor, SeekOrigin.Begin);
                long streamPosition;

                while (true)
                {
                    streamPosition = stream.Position;

                    if (stream.Position == 0)
                    {
                        return -2;
                    }

                    var found = false;
                    byte[] headerBytes;
                    int headerBytesLength;
                    int endPosition;

                    if (bubbleTypes == null)
                    {
                        BubbleGroupDatabasePrimitives.ReadBubbleHeader(stream, out headerBytes, out headerBytesLength);
                        BubbleGroupDatabasePrimitives.FindBubbleHeaderDelimiter(headerBytes, headerBytesLength, 0, out endPosition);
                        found = true;
                    }
                    else
                    {
                        var bubbleType = BubbleGroupDatabasePrimitives.ReadBubbleHeaderType(stream, out headerBytes,
                            out headerBytesLength, out endPosition);
                        for (var x = 0; x < bubbleTypes.Length; x++)
                        {
                            if (bubbleTypes[x] != bubbleType) continue;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                                       endPosition + 1, out endPosition);
                        var time = BubbleGroupDatabasePrimitives.AsciiBytesToString(headerBytes, endPosition + 1,
                                       headerBytesLength);
                        long longTime;
                        Int64.TryParse(time, out longTime);

                        if (longTime < lowerTime)
                            break;

                        if (longTime > upperTime)
                        {
                            BubbleGroupDatabasePrimitives.JumpBubbleData(stream);
                            continue;
                        }

                        var bubbleData = BubbleGroupDatabasePrimitives.ReadBubbleData(stream);

                        var visualBubble = Deserialize(bubbleData);
                        if (visualBubble == null)
                            continue;
                        if (comparer != null && !comparer(visualBubble))
                            continue;
                        visualBubble.Service = @group.Service;
                        visualBubble.ID = guid;
                        visualBubble.BubbleGroupReference = @group;
                        updateCallback(visualBubble);
                    }
                    else
                    {
                        BubbleGroupDatabasePrimitives.JumpBubbleData(stream);
                    }
                }

                return streamPosition;
            }
        }

        public static long FetchBubblesAt(BubbleGroup group, long fromTime, int max,
            ref List<VisualBubble> bubbles, long cursor = -1)
        {
            lock (OperationLock)
            {
                var groupLocation = GetLocation(@group);
                using (var stream = File.Open(groupLocation, FileMode.Open, FileAccess.Read))
                {
                    stream.Seek(cursor != -1 ? cursor : stream.Length, SeekOrigin.Begin);

                    while (true)
                    {
                        if (stream.Position == 0)
                        {
                            goto End;
                        }

                        byte[] headerBytes;
                        int headerBytesLength;
                        int endPosition;

                        BubbleGroupDatabasePrimitives.ReadBubbleHeader(stream, out headerBytes, out headerBytesLength);
                        BubbleGroupDatabasePrimitives.FindBubbleHeaderDelimiter(headerBytes, headerBytesLength, 0, out endPosition);

                        var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                            endPosition + 1, out endPosition);
                        var time = BubbleGroupDatabasePrimitives.AsciiBytesToString(headerBytes, endPosition + 1,
                            headerBytesLength);

                        long longTime;
                        Int64.TryParse(time, out longTime);

                        if (longTime > fromTime)
                        {
                            BubbleGroupDatabasePrimitives.JumpBubbleData(stream);
                            continue;
                        }

                        var bubbleData = BubbleGroupDatabasePrimitives.ReadBubbleData(stream);

                        var visualBubble = Deserialize(bubbleData);
                        if (visualBubble == null) continue;
                        visualBubble.Service = @group.Service;
                        visualBubble.ID = guid;

                        bubbles.Add(visualBubble);
                        if (bubbles.Count >= max)
                        {
                            goto End;
                        }
                    }

                End:
                    bubbles.Reverse();
                    return stream.Position;
                }
            }
        }

        public static IEnumerable<VisualBubble> FetchBubbles(BubbleGroup group, int count = 100,
            bool skipDeleted = true)
        {
            return FetchBubbles(GetLocation(@group), @group.Service, count, skipDeleted);
        }

        public static IEnumerable<VisualBubble> FetchBubbles(string location, Service service, int count = 100,
            bool skipDeleted = true)
        {
            lock (OperationLock)
            {
                using (var stream = File.Open(location, FileMode.Open, FileAccess.Read))
                {
                    stream.Seek(stream.Length, SeekOrigin.Begin);

                    for (var i = 0; i < count; i++)
                    {
                        if (stream.Position == 0)
                            break;

                        byte[] headerBytes;
                        int headerBytesLength;
                        int endPosition;

                        BubbleGroupDatabasePrimitives.ReadBubbleHeader(stream, out headerBytes, out headerBytesLength);
                        BubbleGroupDatabasePrimitives.FindBubbleHeaderDelimiter(headerBytes, headerBytesLength, 0,
                            out endPosition);

                        var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                            endPosition + 1, out endPosition);

                        var bubbleData = BubbleGroupDatabasePrimitives.ReadBubbleData(stream);

                        var visualBubble = Deserialize(bubbleData, skipDeleted);
                        if (visualBubble == null) continue;
                        visualBubble.Service = service;
                        visualBubble.ID = guid;

                        yield return visualBubble;
                    }
                }
            }
        }

        public static VisualBubble FetchNewestBubbleIfNotWaiting(Stream stream, Service service, 
            int searchDepth = 10)
        {
            VisualBubble firstBubble = null;

            lock (OperationLock)
            {
                stream.Seek(stream.Length, SeekOrigin.Begin);

                for (var i = 0; i < searchDepth; i++)
                {
                    if (stream.Position == 0)
                        break;
                            
                    byte[] headerBytes;
                    int headerBytesLength;
                    int endPosition;

                    BubbleGroupDatabasePrimitives.ReadBubbleHeaderType(stream, out headerBytes,
                        out headerBytesLength, out endPosition);
                            
                    var guid = BubbleGroupDatabasePrimitives.ReadBubbleHeaderStruct(headerBytes, headerBytesLength,
                        endPosition + 1, out endPosition);

                    var bubbleData = BubbleGroupDatabasePrimitives.ReadBubbleData(stream);

                    var visualBubble = Deserialize(bubbleData);
                    if (visualBubble == null) continue;

                    // if any of the bubbles are waiting, then make sure to perform a full load
                    if (!(visualBubble is NewBubble) && 
                        (visualBubble.Status == Bubble.BubbleStatus.Waiting && visualBubble.Direction == Bubble.BubbleDirection.Outgoing))
                    {
                        return null;
                    }

                    if (firstBubble == null)
                    {
                        visualBubble.Service = service;
                        visualBubble.ID = guid;
                        firstBubble = visualBubble;
                    }
                }
            }

            return firstBubble;
        }

        internal static void Kill(BubbleGroup group)
        {
            lock (OperationLock)
            {
                var file = GetLocation(@group);
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}