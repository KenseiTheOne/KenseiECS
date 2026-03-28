using System;
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Typed container for shared data accessible by systems.
    /// Supports optional string key for multiple instances of the same type.
    ///
    /// Usage:
    ///   var shared = new SharedData();
    ///   shared.Add(new GameConfig { Speed = 10f });
    ///   shared.Add(new SpawnConfig { Max = 100 }, "enemies");
    ///   shared.Add(new SpawnConfig { Max = 20 }, "pickups");
    ///
    ///   // In system Init:
    ///   var config = shared.Get<GameConfig>();
    ///   var enemySpawns = shared.Get<SpawnConfig>("enemies");
    /// </summary>
    public class SharedData {
        private readonly Dictionary<(Type, string), object> _data = new();

        /// <summary> Register a shared data instance. Optional key for disambiguation. </summary>
        public void Add<T>(T instance, string key = null) where T : class {
            _data[(typeof(T), key)] = instance;
        }

        /// <summary> Get a shared data instance. Throws if not found. </summary>
        public T Get<T>(string key = null) where T : class {
            if (!_data.TryGetValue((typeof(T), key), out var value)) {
                throw new KeyNotFoundException(
                    $"SharedData: {typeof(T).Name} not registered (key: '{key ?? "null"}')");
            }
            return (T)value;
        }

        /// <summary> Try to get a shared data instance. Returns false if not registered. </summary>
        public bool TryGet<T>(out T result, string key = null) where T : class {
            if (_data.TryGetValue((typeof(T), key), out object value)) {
                result = (T)value;
                return true;
            }

            result = null;
            return false;
        }
    }
}
