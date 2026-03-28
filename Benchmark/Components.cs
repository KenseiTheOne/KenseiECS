namespace EcsBenchmark.Components.Kensei {
    internal struct Position : KenseiECS.IComponent { public float X, Y; }
    internal struct Velocity : KenseiECS.IComponent { public float X, Y; }
    internal struct Health   : KenseiECS.IComponent { public float Value; }
    internal struct Damage   : KenseiECS.IComponent { }
}

namespace EcsBenchmark.Components.LeoLite {
    internal struct Position { public float X, Y; }
    internal struct Velocity { public float X, Y; }
    internal struct Health   { public float Value; }
    internal struct Damage   { }
}

namespace EcsBenchmark.Components.ArchEcs {
    internal struct Position { public float X, Y; }
    internal struct Velocity { public float X, Y; }
    internal struct Health   { public float Value; }
    internal struct Damage   { }
}

namespace EcsBenchmark.Components.Leo {
    internal struct Position { public float X, Y; }
    internal struct Velocity { public float X, Y; }
    internal struct Health   { public float Value; }
    internal struct Damage   { }
}
