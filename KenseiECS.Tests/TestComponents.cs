using System.Collections.Generic;

namespace KenseiECS.Tests {
    public struct Position : IComponent {
        public float X, Y;
    }

    public struct Velocity : IComponent {
        public float X, Y;
    }

    public struct Health : IComponent {
        public float Value;
    }

    public struct Frozen : IComponent {
    }

    public struct Damage : IComponent {
        public float Value;
    }

    public struct Inventory : IComponent, IAutoReset<Inventory> {
        public List<int> Items;

        public void AutoReset(ref Inventory c) {
            c.Items?.Clear();
            c.Items = null;
        }
    }

    public struct ResetTracked : IComponent, IAutoReset<ResetTracked> {
        public static int ResetCalls;
        public int V;

        public void AutoReset(ref ResetTracked c) {
            ResetCalls++;
            c.V = 0;
        }
    }

    public struct DeepInventory : IComponent, IAutoCopy<DeepInventory> {
        public List<int> Items;

        public void AutoCopy(ref DeepInventory c) {
            c.Items = c.Items != null ? new List<int>(c.Items) : null;
        }
    }

    public struct ExplicitReset : IComponent, IAutoReset<ExplicitReset> {
        public static int ResetCalls;
        public int V;

        void IAutoReset<ExplicitReset>.AutoReset(ref ExplicitReset c) {
            ResetCalls++;
            c.V = -1;
        }
    }

    public struct ExplicitCopy : IComponent, IAutoCopy<ExplicitCopy> {
        public int V;

        void IAutoCopy<ExplicitCopy>.AutoCopy(ref ExplicitCopy c) {
            c.V *= 10;
        }
    }

    public struct ThrowingReset : IComponent, IAutoReset<ThrowingReset> {
        public bool Throw;

        public void AutoReset(ref ThrowingReset c) {
            if (c.Throw) {
                throw new System.InvalidOperationException("reset failed");
            }
        }
    }
}
