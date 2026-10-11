// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/spatial-partition.md, section "GridHandle.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.SpatialPartition
{
    /// <summary>Reference to an item in a <see cref="SpatialGrid2D{T}"/>. Invalid after the item is removed. <c>default</c> is never valid.</summary>
    public struct GridHandle : IEquatable<GridHandle>
    {
        internal readonly int Node;
        internal readonly int Generation;

        internal GridHandle(int node, int generation)
        {
            Node = node;
            Generation = generation;
        }

        public bool IsAssigned => Generation != 0;

        public bool Equals(GridHandle other) { return Node == other.Node && Generation == other.Generation; }
        public override bool Equals(object obj) { return obj is GridHandle && Equals((GridHandle)obj); }
        public override int GetHashCode() { unchecked { return (Node * 397) ^ Generation; } }
        public static bool operator ==(GridHandle a, GridHandle b) { return a.Equals(b); }
        public static bool operator !=(GridHandle a, GridHandle b) { return !a.Equals(b); }
    }
}
