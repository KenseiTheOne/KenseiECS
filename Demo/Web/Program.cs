using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace KenseiECS.Demo.Web {
    /// <summary>
    /// JS bridge for one HordeGame instance. main.js drives the frame loop: it calls
    /// Tick once per animation frame, and Tick calls back into JS ("render") with a
    /// zero-copy view of the sprite buffer and the packed HUD values.
    /// </summary>
    [SupportedOSPlatform("browser")]
    public static partial class Interop {
        // Packed HUD, read by JS through a MemoryView (no allocation per frame).
        // Keep in sync with HUD_* indices in main.js.
        private const int HudHp = 0, HudMaxHp = 1, HudLevel = 2, HudXp = 3, HudXpToNext = 4, HudTime = 5,
            HudKills = 6, HudStatus = 7, HudChoice0 = 8, HudChoice1 = 9, HudChoice2 = 10, HudEnemies = 11,
            HudFrameMs = 12, HudPeakEnemies = 13, HudSize = 16;

        private static readonly double[] Hud = new double[HudSize];
        private static HordeGame _game;

        public static void Main() {
            // Nothing to do: JS creates the game through Init once the runtime is up.
        }

        [JSExport]
        public static void Init(int seed) {
            _game = new HordeGame(seed);
            Publish();
        }

        /// <summary> Advance one frame (dt in seconds, clamped by the game) and render it. </summary>
        [JSExport]
        public static void Tick(double dt, double inputX, double inputY) {
            _game.Tick((float)dt, (float)inputX, (float)inputY);
            Publish();
        }

        [JSExport]
        public static void ChooseUpgrade(int slot) {
            _game.ChooseUpgrade(slot);
            Publish();
        }

        [JSExport]
        public static void Restart() {
            _game.Restart();
            Publish();
        }

        [JSExport]
        public static void SetStress(double value) => _game.StressMultiplier = (float)value;

        [JSExport]
        public static double GetStress() => _game.StressMultiplier;

        [JSExport]
        public static void SetGodMode(bool value) => _game.GodMode = value;

        [JSExport]
        public static string StatsJson() => _game.StatsJson();

        [JSExport]
        public static bool SetSystemActive(string name, bool active) => _game.SetSystemActive(name, active);

        [JSExport]
        public static string UpgradeName(int id) => HordeGame.UpgradeName(id);

        [JSExport]
        public static string UpgradeDescription(int id) => HordeGame.UpgradeDescription(id);

        private static void Publish() {
            var h = _game.Hud;
            var hud = Hud;
            hud[HudHp] = h.Hp;
            hud[HudMaxHp] = h.MaxHp;
            hud[HudLevel] = h.Level;
            hud[HudXp] = h.Xp;
            hud[HudXpToNext] = h.XpToNext;
            hud[HudTime] = h.TimeSec;
            hud[HudKills] = h.Kills;
            hud[HudStatus] = (int)h.Status;
            hud[HudChoice0] = h.Choice0;
            hud[HudChoice1] = h.Choice1;
            hud[HudChoice2] = h.Choice2;
            hud[HudEnemies] = _game.EnemyCount;
            hud[HudFrameMs] = _game.FrameMs;
            hud[HudPeakEnemies] = _game.PeakEnemies;

            // MemoryView marshals Span<byte|int|double> only: hand the float buffer over
            // as bytes. JS reinterprets the same wasm memory as Float32Array (no copy).
            ReadOnlySpan<float> sprites = _game.RenderBuffer;
            var bytes = MemoryMarshal.CreateSpan(
                ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(sprites)),
                sprites.Length * sizeof(float));
            Render(bytes, _game.SpriteCount, _game.CameraX, _game.CameraY, hud);
        }

        /// <summary> Implemented in main.js; must consume the views synchronously. </summary>
        [JSImport("render", "main.js")]
        private static partial void Render(
            [JSMarshalAs<JSType.MemoryView>] Span<byte> sprites,
            int count,
            double cameraX,
            double cameraY,
            [JSMarshalAs<JSType.MemoryView>] Span<double> hud);
    }
}
