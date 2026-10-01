using System;
using System.Runtime.CompilerServices;

namespace KenseiECS.Demo {
    /// <summary>
    /// Flat sprite list handed to the host: SpriteStride floats per sprite
    /// (x, y, radius, kind, flash). Render systems append to it in draw order;
    /// sprites outside the camera rectangle are culled.
    /// </summary>
    public sealed class SpriteBuffer {
        public const int Stride = 5;

        public float[] Data = new float[4096 * Stride];
        public int Count;

        public float CameraX, CameraY;
        public float HalfWidth, HalfHeight;

        public void Begin(float cameraX, float cameraY, float halfWidth, float halfHeight) {
            CameraX = cameraX;
            CameraY = cameraY;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
            Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(float x, float y, float radius, float kind, float flash) {
            float dx = x - CameraX;
            float dy = y - CameraY;
            if (dx < -HalfWidth - radius || dx > HalfWidth + radius || dy < -HalfHeight - radius || dy > HalfHeight + radius) {
                return;
            }
            int o = Count * Stride;
            if (o + Stride > Data.Length) {
                Array.Resize(ref Data, Data.Length * 2);
            }
            var d = Data;
            d[o] = x;
            d[o + 1] = y;
            d[o + 2] = radius;
            d[o + 3] = kind;
            d[o + 4] = flash;
            Count++;
        }
    }
}
