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
}
