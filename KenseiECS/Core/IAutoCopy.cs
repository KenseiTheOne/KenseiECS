namespace KenseiECS {
    /// <summary>
    /// Implement on a component struct to provide custom copy logic
    /// when the component is copied via World.CopyEntity().
    ///
    /// Called on a shallow copy of the source component.
    /// Use this to deep-copy reference-type fields.
    /// If not implemented, a shallow copy (default struct assignment) is used.
    ///
    /// Usage:
    ///   struct Inventory : IComponent, IAutoCopy<Inventory> {
    ///       public List<int> Items;
    ///       public void AutoCopy(ref Inventory c) {
    ///           c.Items = c.Items != null ? new List<int>(c.Items) : null;
    ///       }
    ///   }
    /// </summary>
    public interface IAutoCopy<T> where T : struct {
        void AutoCopy(ref T component);
    }
}
