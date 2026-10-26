using KenseiECS;

namespace StandShared {
    // Filter and pool components. The program generator keeps filters valid: Inc from C0-C3,
    // Any from C4-C7, Exc from C8-C11, so no type is both required and excluded.
    public struct C0 : IComponent { public int V; }
    public struct C1 : IComponent { public int V; }
    public struct C2 : IComponent { public int V; }
    public struct C3 : IComponent { public int V; }
    public struct C4 : IComponent { public int V; }
    public struct C5 : IComponent { public int V; }
    public struct C6 : IComponent { public int V; }
    public struct C7 : IComponent { public int V; }
    public struct C8 : IComponent { public int V; }
    public struct C9 : IComponent { public int V; }
    public struct C10 : IComponent { public int V; }
    public struct C11 : IComponent { public int V; }

    // Group components: a pool belongs to one owning group only, so groups use their own types,
    // always in the same sets (G0+G1, G2+G3, G4+G5+G6, G7+G8+G9+G10 for a group of four types).
    public struct G0 : IComponent { public int V; }
    public struct G1 : IComponent { public int V; }
    public struct G2 : IComponent { public int V; }
    public struct G3 : IComponent { public int V; }
    public struct G4 : IComponent { public int V; }
    public struct G5 : IComponent { public int V; }
    public struct G6 : IComponent { public int V; }
    public struct G7 : IComponent { public int V; }
    public struct G8 : IComponent { public int V; }
    public struct G9 : IComponent { public int V; }
    public struct G10 : IComponent { public int V; }

    // Shared data types.
    public class Cfg0 { }
    public class Cfg1 { }
    public class Cfg2 { }
    public class Cfg3 { }

    // Shared data of other kinds of reference types ([Shared] takes any reference type): an interface
    // and an abstract class (registered with an implementation), a closed generic class and a nested one.
    public interface ICfg { }
    public sealed class CfgImpl : ICfg { }
    public abstract class CfgBase { }
    public sealed class CfgDerived : CfgBase { }
    public class Box<T> { }
    public static class SharedHolder {
        public class Inner { }
    }

    // Not a component: for the negative KECS008 layout.
    public struct NotComponent { public int V; }
}
