// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/spatial-partition.md, section "SpatialGrid2D.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.SpatialPartition
{
    /// <summary>
    /// Uniform 2D grid for finding items near a point without testing every item. Items live in square
    /// cells of <c>cellSize</c>; a circle or rectangle query only visits the cells it overlaps. Choose a cell
    /// size close to the typical query radius. Queries add to a list the caller supplies, so they do not allocate.
    /// </summary>
    public sealed class SpatialGrid2D<T>
    {
        private struct Node
        {
            public T Item;
            public float X, Y;
            public long Cell;
            public int IndexInCell;
            public int Generation;     // 0 until first use; odd values are not special, a freed node gets a new one
            public bool Alive;
        }

        private readonly float _cellSize;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly Stack<List<int>> _spareLists = new Stack<List<int>>();
        private readonly Stack<int> _freeNodes = new Stack<int>();
        private Node[] _nodes = new Node[16];
        private int _nodeCount;
        private int _count;

        public SpatialGrid2D(float cellSize)
        {
            if (float.IsNaN(cellSize) || float.IsInfinity(cellSize) || cellSize <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), "The cell size must be above zero.");
            _cellSize = cellSize;
        }

        public float CellSize => _cellSize;

        public int Count => _count;

        /// <summary>Number of cells that currently hold at least one item.</summary>
        public int OccupiedCells => _cells.Count;

        public GridHandle Add(T item, float x, float y)
        {
            long cell = CellKey(x, y);

            int index;
            if (_freeNodes.Count > 0)
            {
                index = _freeNodes.Pop();
            }
            else
            {
                if (_nodeCount == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
                index = _nodeCount++;
                _nodes[index].Generation = 1;
            }

            _nodes[index].Item = item;
            _nodes[index].X = x;
            _nodes[index].Y = y;
            _nodes[index].Alive = true;
            PutInCell(index, cell);
            _count++;
            return new GridHandle(index, _nodes[index].Generation);
        }

        public bool IsValid(GridHandle handle)
        {
            return handle.Generation != 0 && handle.Node >= 0 && handle.Node < _nodeCount
                && _nodes[handle.Node].Alive && _nodes[handle.Node].Generation == handle.Generation;
        }

        /// <summary>Moves the item. Returns false when the handle is not valid.</summary>
        public bool Move(GridHandle handle, float x, float y)
        {
            if (!IsValid(handle)) return false;
            long cell = CellKey(x, y);               // validates before anything changes
            int index = handle.Node;
            if (cell != _nodes[index].Cell)
            {
                TakeOutOfCell(index);
                PutInCell(index, cell);
            }
            _nodes[index].X = x;
            _nodes[index].Y = y;
            return true;
        }

        public bool Remove(GridHandle handle)
        {
            if (!IsValid(handle)) return false;
            int index = handle.Node;
            TakeOutOfCell(index);
            _nodes[index].Item = default(T);
            _nodes[index].Alive = false;
            unchecked { _nodes[index].Generation++; }
            if (_nodes[index].Generation == 0) _nodes[index].Generation = 1;
            _freeNodes.Push(index);
            _count--;
            return true;
        }

        public bool TryGetPosition(GridHandle handle, out float x, out float y)
        {
            if (!IsValid(handle)) { x = 0f; y = 0f; return false; }
            x = _nodes[handle.Node].X;
            y = _nodes[handle.Node].Y;
            return true;
        }

        public bool TryGetItem(GridHandle handle, out T item)
        {
            if (!IsValid(handle)) { item = default(T); return false; }
            item = _nodes[handle.Node].Item;
            return true;
        }

        public void Clear()
        {
            foreach (var pair in _cells) { pair.Value.Clear(); _spareLists.Push(pair.Value); }
            _cells.Clear();
            for (int i = 0; i < _nodeCount; i++)
            {
                if (!_nodes[i].Alive) continue;
                _nodes[i].Item = default(T);
                _nodes[i].Alive = false;
                unchecked { _nodes[i].Generation++; }
                if (_nodes[i].Generation == 0) _nodes[i].Generation = 1;
                _freeNodes.Push(i);
            }
            _count = 0;
        }

        /// <summary>Adds every item within <paramref name="radius"/> of the point (edge included) to <paramref name="results"/>. Returns how many were added.</summary>
        public int QueryCircle(float centerX, float centerY, float radius, List<T> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            CheckFinite(centerX, centerY);
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f) throw new ArgumentOutOfRangeException(nameof(radius));

            int before = results.Count;
            double r2 = (double)radius * radius;
            int x0 = CellCoord(centerX - radius), x1 = CellCoord(centerX + radius);
            int y0 = CellCoord(centerY - radius), y1 = CellCoord(centerY + radius);
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    List<int> list;
                    if (!_cells.TryGetValue(Pack(cx, cy), out list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        int n = list[i];
                        double dx = (double)_nodes[n].X - centerX;
                        double dy = (double)_nodes[n].Y - centerY;
                        if (dx * dx + dy * dy <= r2) results.Add(_nodes[n].Item);
                    }
                }
            return results.Count - before;
        }

        /// <summary>Adds every item inside the rectangle (edges included) to <paramref name="results"/>. Returns how many were added.</summary>
        public int QueryRect(float minX, float minY, float maxX, float maxY, List<T> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            CheckFinite(minX, minY);
            CheckFinite(maxX, maxY);
            if (minX > maxX || minY > maxY) throw new ArgumentException("The rectangle minimum must not exceed its maximum.");

            int before = results.Count;
            int x0 = CellCoord(minX), x1 = CellCoord(maxX), y0 = CellCoord(minY), y1 = CellCoord(maxY);
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    List<int> list;
                    if (!_cells.TryGetValue(Pack(cx, cy), out list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        int n = list[i];
                        float x = _nodes[n].X, y = _nodes[n].Y;
                        if (x >= minX && x <= maxX && y >= minY && y <= maxY) results.Add(_nodes[n].Item);
                    }
                }
            return results.Count - before;
        }

        private void PutInCell(int node, long cell)
        {
            List<int> list;
            if (!_cells.TryGetValue(cell, out list))
            {
                list = _spareLists.Count > 0 ? _spareLists.Pop() : new List<int>();
                _cells.Add(cell, list);
            }
            _nodes[node].Cell = cell;
            _nodes[node].IndexInCell = list.Count;
            list.Add(node);
        }

        private void TakeOutOfCell(int node)
        {
            List<int> list = _cells[_nodes[node].Cell];
            int at = _nodes[node].IndexInCell;
            int last = list.Count - 1;
            if (at != last)
            {
                int moved = list[last];
                list[at] = moved;
                _nodes[moved].IndexInCell = at;
            }
            list.RemoveAt(last);
            if (list.Count == 0)
            {
                _cells.Remove(_nodes[node].Cell);
                _spareLists.Push(list);
            }
        }

        private long CellKey(float x, float y)
        {
            CheckFinite(x, y);
            return Pack(CellCoord(x), CellCoord(y));
        }

        private int CellCoord(float v)
        {
            double c = Math.Floor(v / (double)_cellSize);
            if (c < int.MinValue || c > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(v), "The position is too far from the origin for this cell size.");
            return (int)c;
        }

        private static long Pack(int cx, int cy) { return ((long)cx << 32) | (uint)cy; }

        private static void CheckFinite(float x, float y)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
                throw new ArgumentOutOfRangeException("position", "Positions must be finite numbers.");
        }
    }
}
