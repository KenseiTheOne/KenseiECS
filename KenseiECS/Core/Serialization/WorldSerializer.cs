using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace KenseiECS {
    /// <summary>
    /// Saves a world to a stream and restores it into an empty world.
    ///
    /// Entities keep their index and generation, so Entity values stored inside
    /// components stay valid after a round trip. Component types are identified
    /// by assembly-qualified name, not by their runtime index, so the file does
    /// not depend on first-touch order.
    ///
    /// Unmanaged components are written bit-for-bit. Components with reference
    /// fields need a registered IComponentFormatter<T>. Register<T>() (with or
    /// without a formatter) also lets Load create the pool without reflection,
    /// which matters under IL2CPP when the type is not otherwise touched.
    ///
    /// Load fires no world events; filters and groups are populated normally.
    ///
    /// Usage:
    ///   var serializer = new WorldSerializer();
    ///   serializer.Register(new InventoryFormatter());
    ///   using (var file = File.Create(path)) { serializer.Save(world, file); }
    ///   world.Clear();
    ///   using (var file = File.OpenRead(path)) { serializer.Load(world, file); }
    /// </summary>
    public sealed class WorldSerializer {
        private const uint Magic = 0x5343454B; // "KECS"
        private const int Version = 1;

        private readonly Dictionary<Type, object> _formatters = new();
        private readonly Dictionary<Type, MethodInfo> _poolAccessors = new();
        private readonly Dictionary<string, Type> _resolvedTypes = new();

        private static readonly MethodInfo PoolMethod = typeof(World).GetMethod(nameof(World.Pool));

        /// <summary> Register a custom formatter for T. </summary>
        public void Register<T>(IComponentFormatter<T> formatter) where T : struct, IComponent {
            _formatters[typeof(T)] = formatter;
            _poolAccessors[typeof(T)] = PoolMethod.MakeGenericMethod(typeof(T));
        }

        /// <summary> Register an unmanaged component type so Load can create its pool without reflection lookups. </summary>
        public void Register<T>() where T : struct, IComponent {
            _poolAccessors[typeof(T)] = PoolMethod.MakeGenericMethod(typeof(T));
        }

        /// <summary> Write all alive entities and their components. The stream stays open. </summary>
        public void Save(World world, Stream stream) {
            var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(world.Tick);

            int aliveCount = world.EntityCount;
            writer.Write(aliveCount);
            foreach (var entity in world.AliveEntities) {
                writer.Write(entity.Index);
                writer.Write(entity.Generation);
            }

            int poolCount = 0;
            foreach (var pool in world.ActivePools) {
                if (pool.Count > 0) {
                    poolCount++;
                }
            }
            writer.Write(poolCount);

            foreach (var pool in world.ActivePools) {
                if (pool.Count == 0) {
                    continue;
                }
                writer.Write(pool.ComponentType.AssemblyQualifiedName);
                writer.Write(pool.Count);
                _formatters.TryGetValue(pool.ComponentType, out var formatter);
                pool.WriteComponents(writer, formatter);
            }

            writer.Flush();
        }

        /// <summary> Restore a snapshot into an empty world. Throws if the world has entities or the stream is not a snapshot. </summary>
        public void Load(World world, Stream stream) {
            if (world.EntityCount != 0) {
                throw new InvalidOperationException(
                    $"WorldSerializer.Load requires an empty world, but it has {world.EntityCount} entities — call world.Clear() first");
            }

            var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (reader.ReadUInt32() != Magic) {
                throw new InvalidDataException("Stream is not a KenseiECS world snapshot");
            }
            int version = reader.ReadInt32();
            if (version != Version) {
                throw new InvalidDataException($"Unsupported snapshot version {version} (expected {Version})");
            }

            int tick = reader.ReadInt32();
            int aliveCount = reader.ReadInt32();

            world.SuppressEvents(true);
            try {
                for (int i = 0; i < aliveCount; i++) {
                    int index = reader.ReadInt32();
                    int generation = reader.ReadInt32();
                    world.RestoreEntity(index, generation);
                }
                world.FinishRestore(tick);

                int poolCount = reader.ReadInt32();
                for (int p = 0; p < poolCount; p++) {
                    string typeName = reader.ReadString();
                    int count = reader.ReadInt32();
                    var pool = ResolvePool(world, typeName);
                    _formatters.TryGetValue(pool.ComponentType, out var formatter);
                    for (int i = 0; i < count; i++) {
                        int entityIndex = reader.ReadInt32();
                        pool.ReadComponent(reader, entityIndex, formatter);
                    }
                }
            } finally {
                world.SuppressEvents(false);
            }
        }

        private ComponentPoolBase ResolvePool(World world, string typeName) {
            if (!_resolvedTypes.TryGetValue(typeName, out var type)) {
                type = Type.GetType(typeName) ?? FindTypeByFullName(typeName);
                if (type == null) {
                    throw new InvalidDataException(
                        $"Snapshot contains component type '{typeName}' which is not loaded in this process");
                }
                _resolvedTypes[typeName] = type;
            }

            if (!_poolAccessors.TryGetValue(type, out var accessor)) {
                accessor = PoolMethod.MakeGenericMethod(type);
                _poolAccessors[type] = accessor;
            }
            return (ComponentPoolBase)accessor.Invoke(world, null);
        }

        // Assembly-qualified names embed the assembly version; after a version
        // bump Type.GetType fails, so fall back to the namespace-qualified name.
        private static Type FindTypeByFullName(string assemblyQualifiedName) {
            int comma = assemblyQualifiedName.IndexOf(',');
            string fullName = comma >= 0 ? assemblyQualifiedName.Substring(0, comma) : assemblyQualifiedName;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                var type = assembly.GetType(fullName);
                if (type != null) {
                    return type;
                }
            }
            return null;
        }
    }
}
