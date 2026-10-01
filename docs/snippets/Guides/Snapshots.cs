namespace Docs.Snippets.Snapshots {
    using System.Collections.Generic;
    using System.IO;
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }

    // #region formatter
    struct Inventory : IComponent {
        public List<int> Items;                  // reference field: needs a formatter
    }

    sealed class InventoryFormatter : IComponentFormatter<Inventory> {
        public void Write(BinaryWriter writer, ref Inventory c) {
            int count = c.Items?.Count ?? 0;
            writer.Write(count);
            for (int i = 0; i < count; i++) {
                writer.Write(c.Items[i]);
            }
        }

        public void Read(BinaryReader reader, out Inventory c) {
            int count = reader.ReadInt32();
            c = new Inventory { Items = new List<int>(count) };
            for (int i = 0; i < count; i++) {
                c.Items.Add(reader.ReadInt32());
            }
        }
    }
    // #endregion formatter

    class Example {
        void SaveLoad(World world, string path) {
            // #region save-load
            var serializer = new WorldSerializer();
            serializer.Register(new InventoryFormatter());   // only for components with reference fields

            using (var file = File.Create(path)) {
                serializer.Save(world, file);
            }

            world.Clear();
            using (var file = File.OpenRead(path)) {
                serializer.Load(world, file);                 // fires no world events; filters and groups fill normally
            }
            // #endregion save-load
        }

        void RegisterUnmanaged(WorldSerializer serializer) {
            // #region register-unmanaged
            serializer.Register<Position>();   // no formatter: Load reaches Pool<Position>() without reflection
            // #endregion register-unmanaged
        }
    }
}
