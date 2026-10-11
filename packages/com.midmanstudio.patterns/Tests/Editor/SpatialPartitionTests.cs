// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/spatial-partition.md, section "SpatialPartitionTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using MidManStudio.Patterns.SpatialPartition;

namespace MidManStudio.Patterns.SpatialPartition.Tests
{
    public class SpatialPartitionTests
    {
        private static string Sorted(List<string> list) { return string.Join(",", list.OrderBy(s => s).ToArray()); }

        [Test]
        public void ACircleQueryFindsItemsInsideAndSkipsItemsOutside()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("near", 1f, 1f);
            grid.Add("far", 50f, 50f);
            var results = new List<string>();
            int found = grid.QueryCircle(0f, 0f, 5f, results);
            Assert.AreEqual(1, found);
            Assert.AreEqual("near", results[0]);
        }

        [Test]
        public void ACircleIncludesItemsExactlyOnTheEdge()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("edge", 3f, 4f);
            var results = new List<string>();
            grid.QueryCircle(0f, 0f, 5f, results);
            Assert.AreEqual(1, results.Count);
            results.Clear();
            grid.QueryCircle(0f, 0f, 4.99f, results);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void ACornerOfTheSquareBoundIsNotTreatedAsInsideTheCircle()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("corner", 4f, 4f);
            var results = new List<string>();
            grid.QueryCircle(0f, 0f, 5f, results);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void QueriesCrossCellBorders()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("left", 9.5f, 5f);
            grid.Add("right", 10.5f, 5f);
            var results = new List<string>();
            grid.QueryCircle(10f, 5f, 1f, results);
            Assert.AreEqual("left,right", Sorted(results));
        }

        [Test]
        public void NegativeCoordinatesAreHandled()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("a", -0.5f, -0.5f);
            grid.Add("b", 0.5f, 0.5f);
            grid.Add("c", -25f, -25f);
            var results = new List<string>();
            grid.QueryCircle(0f, 0f, 2f, results);
            Assert.AreEqual("a,b", Sorted(results));
        }

        [Test]
        public void MoveUpdatesWhatQueriesFind()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var h = grid.Add("mover", 1f, 1f);
            Assert.IsTrue(grid.Move(h, 95f, 95f));
            var results = new List<string>();
            grid.QueryCircle(1f, 1f, 5f, results);
            Assert.AreEqual(0, results.Count);
            grid.QueryCircle(95f, 95f, 5f, results);
            Assert.AreEqual(1, results.Count);
            float x, y;
            Assert.IsTrue(grid.TryGetPosition(h, out x, out y));
            Assert.AreEqual(95f, x, 0.0001f);
        }

        [Test]
        public void MoveWithinTheSameCellKeepsTheItemFindable()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var h = grid.Add("a", 1f, 1f);
            grid.Move(h, 2f, 2f);
            var results = new List<string>();
            grid.QueryCircle(2f, 2f, 0.5f, results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(1, grid.OccupiedCells);
        }

        [Test]
        public void RemoveTakesTheItemOutAndInvalidatesTheHandle()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var a = grid.Add("a", 1f, 1f);
            grid.Add("b", 2f, 2f);
            Assert.IsTrue(grid.Remove(a));
            Assert.IsFalse(grid.Remove(a));
            Assert.IsFalse(grid.IsValid(a));
            Assert.IsFalse(grid.Move(a, 3f, 3f));
            Assert.AreEqual(1, grid.Count);
            var results = new List<string>();
            grid.QueryRect(-10f, -10f, 10f, 10f, results);
            Assert.AreEqual("b", results[0]);
        }

        [Test]
        public void ARemovedHandleStaysInvalidWhenItsNodeIsReused()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var old = grid.Add("old", 1f, 1f);
            grid.Remove(old);
            var fresh = grid.Add("fresh", 1f, 1f);
            Assert.IsFalse(grid.IsValid(old));
            Assert.IsTrue(grid.IsValid(fresh));
            string item;
            Assert.IsTrue(grid.TryGetItem(fresh, out item));
            Assert.AreEqual("fresh", item);
            Assert.IsFalse(grid.TryGetItem(old, out item));
        }

        [Test]
        public void QueriesAppendToTheListWithoutClearingItAndReturnTheAddedCount()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("a", 1f, 1f);
            var results = new List<string> { "existing" };
            int added = grid.QueryCircle(0f, 0f, 5f, results);
            Assert.AreEqual(1, added);
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("existing", results[0]);
        }

        [Test]
        public void ARectQueryIncludesItsEdges()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("corner", 10f, 20f);
            grid.Add("outside", 10.01f, 20f);
            var results = new List<string>();
            grid.QueryRect(0f, 0f, 10f, 20f, results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("corner", results[0]);
        }

        [Test]
        public void TheSameItemAddedTwiceIsReturnedTwice()
        {
            var grid = new SpatialGrid2D<string>(10f);
            grid.Add("dup", 1f, 1f);
            grid.Add("dup", 2f, 2f);
            var results = new List<string>();
            grid.QueryCircle(0f, 0f, 10f, results);
            Assert.AreEqual(2, results.Count);
        }

        [Test]
        public void EmptiedCellsAreReleased()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var a = grid.Add("a", 1f, 1f);
            var b = grid.Add("b", 55f, 55f);
            Assert.AreEqual(2, grid.OccupiedCells);
            grid.Remove(a);
            Assert.AreEqual(1, grid.OccupiedCells);
            grid.Move(b, 1f, 1f);
            Assert.AreEqual(1, grid.OccupiedCells);
        }

        [Test]
        public void ClearEmptiesTheGridAndInvalidatesHandles()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var a = grid.Add("a", 1f, 1f);
            grid.Add("b", 20f, 20f);
            grid.Clear();
            Assert.AreEqual(0, grid.Count);
            Assert.AreEqual(0, grid.OccupiedCells);
            Assert.IsFalse(grid.IsValid(a));
            var h = grid.Add("c", 1f, 1f);
            Assert.IsTrue(grid.IsValid(h));
            Assert.IsFalse(grid.IsValid(a));
        }

        [Test]
        public void BadArgumentsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid2D<int>(0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid2D<int>(float.NaN));
            var grid = new SpatialGrid2D<int>(1f);
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.Add(1, float.NaN, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.Add(1, 0f, float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.Add(1, 1e12f, 0f));
            var results = new List<int>();
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryCircle(0f, 0f, -1f, results));
            Assert.Throws<ArgumentNullException>(() => grid.QueryCircle(0f, 0f, 1f, null));
            Assert.Throws<ArgumentException>(() => grid.QueryRect(5f, 0f, 1f, 1f, results));
            Assert.AreEqual(0, grid.Count);
        }

        [Test]
        public void AFailedMoveLeavesTheItemWhereItWas()
        {
            var grid = new SpatialGrid2D<string>(10f);
            var h = grid.Add("a", 1f, 1f);
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.Move(h, float.NaN, 0f));
            var results = new List<string>();
            grid.QueryCircle(1f, 1f, 0.5f, results);
            Assert.AreEqual(1, results.Count);
        }

        [Test]
        public void RandomAddsMovesAndRemovalsMatchABruteForceSearch()
        {
            var rng = new Random(2024);
            var grid = new SpatialGrid2D<int>(7f);
            var handles = new List<GridHandle>();
            var xs = new List<float>(); var ys = new List<float>(); var ids = new List<int>();
            int nextId = 0;
            for (int step = 0; step < 1500; step++)
            {
                int roll = rng.Next(100);
                if (handles.Count == 0 || roll < 40)
                {
                    float x = (float)(rng.NextDouble() * 200 - 100), y = (float)(rng.NextDouble() * 200 - 100);
                    handles.Add(grid.Add(nextId, x, y)); xs.Add(x); ys.Add(y); ids.Add(nextId++);
                }
                else if (roll < 70)
                {
                    int i = rng.Next(handles.Count);
                    float x = (float)(rng.NextDouble() * 200 - 100), y = (float)(rng.NextDouble() * 200 - 100);
                    Assert.IsTrue(grid.Move(handles[i], x, y)); xs[i] = x; ys[i] = y;
                }
                else if (roll < 85)
                {
                    int i = rng.Next(handles.Count);
                    Assert.IsTrue(grid.Remove(handles[i]));
                    handles.RemoveAt(i); xs.RemoveAt(i); ys.RemoveAt(i); ids.RemoveAt(i);
                }
                else
                {
                    float cx = (float)(rng.NextDouble() * 200 - 100), cy = (float)(rng.NextDouble() * 200 - 100);
                    float radius = (float)(rng.NextDouble() * 30);
                    var found = new List<int>();
                    grid.QueryCircle(cx, cy, radius, found);
                    var expected = new List<int>();
                    for (int i = 0; i < ids.Count; i++)
                    {
                        double dx = (double)xs[i] - cx, dy = (double)ys[i] - cy;
                        if (dx * dx + dy * dy <= (double)radius * radius) expected.Add(ids[i]);
                    }
                    found.Sort(); expected.Sort();
                    Assert.AreEqual(string.Join(",", expected.Select(v => v.ToString()).ToArray()), string.Join(",", found.Select(v => v.ToString()).ToArray()));
                }
                Assert.AreEqual(handles.Count, grid.Count);
            }
        }
    }
}
