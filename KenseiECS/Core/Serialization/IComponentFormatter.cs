using System.IO;

namespace KenseiECS {
    /// <summary>
    /// Custom binary format for one component type. Required for components
    /// that hold references (lists, strings, objects); unmanaged components
    /// are written bit-for-bit without a formatter.
    ///
    /// Usage:
    ///   sealed class InventoryFormatter : IComponentFormatter<Inventory> {
    ///       public void Write(BinaryWriter writer, ref Inventory c) {
    ///           int count = c.Items?.Count ?? 0;
    ///           writer.Write(count);
    ///           for (int i = 0; i < count; i++) { writer.Write(c.Items[i]); }
    ///       }
    ///       public void Read(BinaryReader reader, out Inventory c) {
    ///           int count = reader.ReadInt32();
    ///           c = new Inventory { Items = new List<int>(count) };
    ///           for (int i = 0; i < count; i++) { c.Items.Add(reader.ReadInt32()); }
    ///       }
    ///   }
    /// </summary>
    public interface IComponentFormatter<T> where T : struct, IComponent {
        void Write(BinaryWriter writer, ref T component);
        void Read(BinaryReader reader, out T component);
    }
}
