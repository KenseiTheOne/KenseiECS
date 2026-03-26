namespace KenseiECS {
    /// <summary>
    /// Implement on a component struct to provide custom reset logic
    /// when the component is removed from an entity.
    ///
    /// If not implemented, all fields are reset to default(T) automatically.
    /// Use this when you need to null out reference-type fields
    /// or set specific default values.
    ///
    /// Usage:
    ///   struct InventoryComponent : IComponent, IAutoReset<InventoryComponent> {
    ///       public List<int> Items;
    ///       public void AutoReset(ref InventoryComponent c) {
    ///           c.Items?.Clear();
    ///           c.Items = null;
    ///       }
    ///   }
    /// </summary>
    public interface IAutoReset<T> where T : struct {
        void AutoReset(ref T component);
    }
}
