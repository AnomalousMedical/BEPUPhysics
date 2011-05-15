﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BEPUphysics.ResourceManagement;
using BEPUphysics.DataStructures;
using Microsoft.Xna.Framework;
using System.Diagnostics;
using BEPUphysics.Threading;

namespace BEPUphysics.BroadPhaseSystems.SortAndSweep
{
    /// <summary>
    /// Broad phase implementation that partitions objects into a 2d grid, and then performs a sort and sweep on the final axis.
    /// </summary>
    /// <remarks>
    /// This broad phase typically has very good collision performance and scales very well with multithreading, but its query times can sometimes be worse than tree-based systems
    /// since it must scan cells.  Keeping rays as short as possible helps avoid unnecessary cell checks.</remarks>
    public class Grid2DSortAndSweep : BroadPhase
    {
        /// <summary>
        /// Gets or sets the width of cells in the 2D grid.  For sparser, larger scenes, increasing this can help performance.
        /// For denser scenes, decreasing this may help.
        /// </summary>
        public static float CellSize
        {
            get
            {
                return 1 / cellSizeInverse;
            }
            set
            {
                cellSizeInverse = 1 / value;
            }
        }
        //TODO: Try different values for this.
        internal static float cellSizeInverse = 1 / 8f; 

        internal static void ComputeCell(ref Vector3 v, out Int2 cell)
        {
            cell.Y = (int)Math.Floor(v.Y * cellSizeInverse);
            cell.Z = (int)Math.Floor(v.Z * cellSizeInverse);
        }

        

        UnsafeResourcePool<GridCell2D> cellPool = new UnsafeResourcePool<GridCell2D>();


        SortedGrid2DSet cellSet = new SortedGrid2DSet();


        RawList<Grid2DEntry> entries = new RawList<Grid2DEntry>();
        Action<int> updateEntry, updateCell;

        public Grid2DSortAndSweep(IThreadManager threadManager)
            :base(threadManager)
        {
            updateEntry = UpdateEntry;
            updateCell = UpdateCell;
        }

        public Grid2DSortAndSweep()
        {
            updateEntry = UpdateEntry;
            updateCell = UpdateCell;
        }

        public override void Add(BroadPhaseEntry entry)
        {
            var newEntry = new Grid2DEntry(entry);
            entries.Add(newEntry);
            //Add the object to the grid.
            for (int i = newEntry.previousMin.Y; i <= newEntry.previousMax.Y; i++)
            {
                for (int j = newEntry.previousMin.Z; j <= newEntry.previousMax.Z; j++)
                {
                    var index = new Int2();
                    index.Y = i;
                    index.Z = j;
                    cellSet.Add(ref index, newEntry);
                }
            }
        }

        public override void Remove(BroadPhaseEntry entry)
        {
            for (int i = 0; i < entries.count; i++)
            {
                if (entries.Elements[i].item == entry)
                {
                    var gridEntry = entries.Elements[i];
                    entries.RemoveAt(i);
                    //Remove the object from any cells that it is held by.
                    for (int j = gridEntry.previousMin.Y; j <= gridEntry.previousMax.Y; j++)
                    {
                        for (int k = gridEntry.previousMin.Z; k <= gridEntry.previousMax.Z; k++)
                        {
                            var index = new Int2();
                            index.Y = j;
                            index.Z = k;
                            cellSet.Remove(ref index, gridEntry);
                        }
                    }
                    return;
                }
            }
        }

        protected override void UpdateMultithreaded()
        {
            Overlaps.Clear();
            //Update the entries!
            ThreadManager.ForLoop(0, entries.count, updateEntry);
            //Update the cells!
            ThreadManager.ForLoop(0, cellSet.count, updateCell);
        }

        protected override void UpdateSingleThreaded()
        {
            Overlaps.Clear();
            //Update the placement of objects.
            for (int i = 0; i < entries.count; i++)
            {
                //Compute the current cells occupied by the entry.
                var entry = entries.Elements[i];
                Int2 min, max;
                ComputeCell(ref entry.item.boundingBox.Min, out min);
                ComputeCell(ref entry.item.boundingBox.Max, out max);
                //For any cell that used to be occupied (defined by the previous min/max),
                //remove the entry.
                for (int j = entry.previousMin.Y; j <= entry.previousMax.Y; j++)
                {
                    for (int k = entry.previousMin.Z; k <= entry.previousMax.Z; k++)
                    {
                        if (j >= min.Y && j <= max.Y && k >= min.Z && k <= max.Z)
                            continue; //This cell is currently occupied, do not remove.
                        var index = new Int2();
                        index.Y = j;
                        index.Z = k;
                        cellSet.Remove(ref index, entry);
                    }
                }
                //For any cell that is newly occupied (was not previously contained),
                //add the entry.
                for (int j = min.Y; j <= max.Y; j++)
                {
                    for (int k = min.Z; k <= max.Z; k++)
                    {
                        if (j >= entry.previousMin.Y && j <= entry.previousMax.Y && k >= entry.previousMin.Z && k <= entry.previousMax.Z)
                            continue; //This cell is already occupied, do not add.
                        var index = new Int2();
                        index.Y = j;
                        index.Z = k;
                        cellSet.Add(ref index, entry);
                    }
                }
                entry.previousMin = min;
                entry.previousMax = max;
            }

            //Update each cell to find the overlaps.
            for (int i = 0; i < cellSet.count; i++)
            {
                cellSet.cells.Elements[i].UpdateOverlaps(this);
            }
        }

        SpinLock cellSetLocker = new SpinLock();
        void UpdateEntry(int i)
        {
            //Compute the current cells occupied by the entry.
            var entry = entries.Elements[i];
            Int2 min, max;
            ComputeCell(ref entry.item.boundingBox.Min, out min);
            ComputeCell(ref entry.item.boundingBox.Max, out max);
            //For any cell that used to be occupied (defined by the previous min/max),
            //remove the entry.
            for (int j = entry.previousMin.Y; j <= entry.previousMax.Y; j++)
            {
                for (int k = entry.previousMin.Z; k <= entry.previousMax.Z; k++)
                {
                    if (j >= min.Y && j <= max.Y && k >= min.Z && k <= max.Z)
                        continue; //This cell is currently occupied, do not remove.
                    var index = new Int2();
                    index.Y = j;
                    index.Z = k;
                    cellSetLocker.Enter();
                    cellSet.Remove(ref index, entry);
                    cellSetLocker.Exit();
                }
            }
            //For any cell that is newly occupied (was not previously contained),
            //add the entry.
            for (int j = min.Y; j <= max.Y; j++)
            {
                for (int k = min.Z; k <= max.Z; k++)
                {
                    if (j >= entry.previousMin.Y && j <= entry.previousMax.Y && k >= entry.previousMin.Z && k <= entry.previousMax.Z)
                        continue; //This cell is already occupied, do not add.
                    var index = new Int2();
                    index.Y = j;
                    index.Z = k;
                    cellSetLocker.Enter();
                    cellSet.Add(ref index, entry);
                    cellSetLocker.Exit();
                }
            }
            entry.previousMin = min;
            entry.previousMax = max;
        }

        void UpdateCell(int i)
        {
            cellSet.cells.Elements[i].UpdateOverlaps(this);
        }
    }

    struct Int2
    {
        internal int Y;
        internal int Z;

        public override int GetHashCode()
        {
            return Y + Z;
        }



        internal int GetSortingHash()
        {
            return (int)(Y * 15485863L + Z * 32452843L);
        }

        public override string ToString()
        {
            return "{" + Y + ", " + Z + "}";
        }
    }


}