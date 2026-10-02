using System.IO;

namespace KenseiECS {
    /// <summary>
    /// Custom binary format for one component type. Required for components
    /// that hold references (lists, strings, objects); unmanaged components
    /// are written bit-for-bit without a formatter.
    ///
    /// Usage:
    /// <code>
    ///   sealed class InventoryFormatter : IComponentFormatter&lt;Inventory&gt; {
    ///       public void Write(BinaryWriter writer, ref Inventory c) {
    ///           int count = c.Items?.Count ?? 0;
    ///           writer.Write(count);
    ///           for (int i = 0; i &lt; count; i++) { writer.Write(c.Items[i]); }
    ///       }
    ///       public void Read(BinaryReader reader, out Inventory c) {
    ///           int count = reader.ReadInt32();
    ///           c = new Inventory { Items = new List&lt;int&gt;(count) };
    ///           for (int i = 0; i &lt; count; i++) { c.Items.Add(reader.ReadInt32()); }
    ///       }
    ///   }
    /// </code>
    /// </summary>
    public interface IComponentFormatter<T> where T : struct, IComponent {
        /// <summary> Write one component. The entity index is written by the caller. </summary>
        void Write(BinaryWriter writer, ref T component);
        /// <summary> Read one component written by <see cref="Write"/>, consuming exactly the same bytes. </summary>
        void Read(BinaryReader reader, out T component);
    }
}
