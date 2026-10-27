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
    /// <code>
    ///   struct InventoryComponent : IComponent, IAutoReset&lt;InventoryComponent&gt; {
    ///       public List&lt;int&gt; Items;
    ///       public void AutoReset(ref InventoryComponent c) {
    ///           c.Items?.Clear();
    ///           c.Items = null;
    ///       }
    ///   }
    /// </code>
    /// </summary>
    public interface IAutoReset<T> where T : struct {
        /// <summary>
        /// Reset <paramref name="component"/> in place. Invoked on a default instance,
        /// so act on the parameter, not on <c>this</c>.
        /// It runs in the middle of a removal, before the pool and the world drop the component:
        /// reset the value only. Changing the world from here (adding or removing components,
        /// creating or destroying entities, subscribing listeners) is not supported.
        /// </summary>
        void AutoReset(ref T component);
    }
}
