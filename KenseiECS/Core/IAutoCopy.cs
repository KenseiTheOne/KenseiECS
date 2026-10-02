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
    /// <code>
    ///   struct Inventory : IComponent, IAutoCopy&lt;Inventory&gt; {
    ///       public List&lt;int&gt; Items;
    ///       public void AutoCopy(ref Inventory c) {
    ///           c.Items = c.Items != null ? new List&lt;int&gt;(c.Items) : null;
    ///       }
    ///   }
    /// </code>
    /// </summary>
    public interface IAutoCopy<T> where T : struct {
        /// <summary>
        /// Fix up <paramref name="component"/>, a shallow copy of the source, before it is stored
        /// on the destination entity. Invoked on a default instance, so act on the parameter, not on <c>this</c>.
        /// </summary>
        void AutoCopy(ref T component);
    }
}
