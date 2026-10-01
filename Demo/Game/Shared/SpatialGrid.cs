using System;
using System.Runtime.CompilerServices;

namespace KenseiECS.Demo {
    /// <summary>
    /// Uniform grid over a square window centred on the player, rebuilt every
    /// frame by GridBuildSystem with a counting sort: no allocations once the
    /// arrays have grown to the peak enemy count. Items of one cell are stored
    /// contiguously together with a copy of their position and radius, so
    /// neighbour queries never touch the component pools.
    ///
    /// Shared through SharedData; every collision system reads it.
    /// </summary>
    public sealed class SpatialGrid {
        public const float CellSize = 2f;
        public const int Dim = 96;                   // cells per side: 192 world units
        public const int CellCount = Dim * Dim;
        public const float InvCell = 1f / CellSize;

        public float OriginX, OriginY;               // world position of cell (0, 0)

        /// <summary> Items of cell c are [CellStart[c], CellStart[c + 1]). </summary>
        public readonly int[] CellStart = new int[CellCount + 1];

        public int Count;
        public int[] Entity = new int[1024];
        public float[] X = new float[1024];
        public float[] Y = new float[1024];
        public float[] R = new float[1024];

        // Build scratch: cell of each inserted item, then the per-cell write cursor.
        private int[] _itemCell = new int[1024];
        private int[] _tmpEntity = new int[1024];
        private float[] _tmpX = new float[1024];
        private float[] _tmpY = new float[1024];
        private float[] _tmpR = new float[1024];
        private readonly int[] _cursor = new int[CellCount];
        private int _pending;

        /// <summary>
        /// Empty the grid. Called before every sim step, so with GridBuildSystem
        /// switched off the collision systems see no enemies instead of stale indices.
        /// </summary>
        public void Clear() {
            Array.Clear(CellStart, 0, CellStart.Length);
            Count = 0;
        }

        public void Begin(float centerX, float centerY) {
            OriginX = centerX - Dim * CellSize * 0.5f;
            OriginY = centerY - Dim * CellSize * 0.5f;
            _pending = 0;
            Array.Clear(_cursor, 0, CellCount);
        }

        /// <summary> Stage one item; items outside the window are skipped. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Insert(int entity, float x, float y, float r) {
            int cx = (int)((x - OriginX) * InvCell);
            int cy = (int)((y - OriginY) * InvCell);
            if ((uint)cx >= Dim || (uint)cy >= Dim || x < OriginX || y < OriginY) {
                return;
            }
            if (_pending == _itemCell.Length) {
                Grow(_pending * 2);
            }
            int cell = cy * Dim + cx;
            int i = _pending++;
            _itemCell[i] = cell;
            _tmpEntity[i] = entity;
            _tmpX[i] = x;
            _tmpY[i] = y;
            _tmpR[i] = r;
            _cursor[cell]++;
        }

        /// <summary> Sort the staged items by cell. </summary>
        public void End() {
            int sum = 0;
            for (int c = 0; c < CellCount; c++) {
                int n = _cursor[c];
                CellStart[c] = sum;
                _cursor[c] = sum;
                sum += n;
            }
            CellStart[CellCount] = sum;
            Count = sum;

            var cells = _itemCell;
            for (int i = 0; i < sum; i++) {
                int dst = _cursor[cells[i]]++;
                Entity[dst] = _tmpEntity[i];
                X[dst] = _tmpX[i];
                Y[dst] = _tmpY[i];
                R[dst] = _tmpR[i];
            }
        }

        /// <summary> Cell coordinate along X, clamped to the window. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CellX(float x) => Clamp((int)MathF.Floor((x - OriginX) * InvCell));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CellY(float y) => Clamp((int)MathF.Floor((y - OriginY) * InvCell));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clamp(int c) => c < 0 ? 0 : c >= Dim ? Dim - 1 : c;

        /// <summary>
        /// Index of the nearest item within maxDistance of (x, y), or -1.
        /// Scans square rings of cells outward and stops once no closer item can exist.
        /// </summary>
        public int Nearest(float x, float y, float maxDistance) {
            int cx = CellX(x);
            int cy = CellY(y);
            int maxRing = (int)(maxDistance * InvCell) + 1;
            float best = maxDistance * maxDistance;
            int bestItem = -1;

            for (int ring = 0; ring <= maxRing; ring++) {
                int y0 = cy - ring, y1 = cy + ring, x0 = cx - ring, x1 = cx + ring;
                for (int gy = y0; gy <= y1; gy++) {
                    if ((uint)gy >= Dim) {
                        continue;
                    }
                    bool edgeRow = gy == y0 || gy == y1;
                    int step = edgeRow ? 1 : x1 - x0;
                    if (step == 0) {
                        step = 1;
                    }
                    for (int gx = x0; gx <= x1; gx += step) {
                        if ((uint)gx >= Dim) {
                            continue;
                        }
                        int cell = gy * Dim + gx;
                        for (int i = CellStart[cell], end = CellStart[cell + 1]; i < end; i++) {
                            float dx = X[i] - x;
                            float dy = Y[i] - y;
                            float d2 = dx * dx + dy * dy;
                            if (d2 < best) {
                                best = d2;
                                bestItem = i;
                            }
                        }
                    }
                }
                // Every unvisited cell is at least `ring` cells away.
                if (bestItem >= 0) {
                    float reach = ring * CellSize;
                    if (reach * reach >= best) {
                        break;
                    }
                }
            }
            return bestItem;
        }

        private void Grow(int size) {
            Array.Resize(ref _itemCell, size);
            Array.Resize(ref _tmpEntity, size);
            Array.Resize(ref _tmpX, size);
            Array.Resize(ref _tmpY, size);
            Array.Resize(ref _tmpR, size);
            Array.Resize(ref Entity, size);
            Array.Resize(ref X, size);
            Array.Resize(ref Y, size);
            Array.Resize(ref R, size);
        }
    }
}
