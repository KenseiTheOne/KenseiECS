using System;
using KenseiECS;

namespace Docs.Snippets.ListenerBridge {
    // #region interface
    public interface IDamageListener {
        void OnDamage(float damage);
    }
    // #endregion interface

    public struct Health : IComponent {
        public float Value;
    }

    // Stands in for a MonoBehaviour such as EnemyView.
    public sealed class EnemyView : IDamageListener {
        public void OnDamage(float damage) => Console.WriteLine($"took {damage} damage");
    }

    public static class Usage {
        public static void Subscribe(World world, EnemyView enemyView) {
            var entity = world.CreateEntity(new Health { Value = 100f });

            // #region subscribe
            // Subscribe: adds a Listeners<IDamageListener> component if the entity has none
            world.Subscribe<IDamageListener>(entity, enemyView);

            // Iterate listeners directly — no delegates, zero allocation.
            // Reverse so a listener may unsubscribe itself from inside the callback.
            ref var listeners = ref world.Pool<Listeners<IDamageListener>>().Get(entity.Index);
            for (int i = listeners.Values.Count - 1; i >= 0; i--) {
                listeners.Values[i].OnDamage(10f);
            }

            // Unsubscribe — the (now empty) Listeners component stays, the entity stays alive
            world.Unsubscribe<IDamageListener>(entity, enemyView);
            bool any = world.HasListeners<IDamageListener>(entity);   // false when empty
            // #endregion subscribe
        }

        public static void Create(World world, EnemyView enemyView) {
            // #region create
            // Create an entity with a listener in one call (CreateEntity + Subscribe)
            var enemy = world.CreateWithListener<IDamageListener>(enemyView);
            // #endregion create
        }
    }

    // #region system
    public sealed class DamageNotifySystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Listeners<IDamageListener>> _listeners;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Health>().Inc<Listeners<IDamageListener>>().End();
            _listeners = world.Pool<Listeners<IDamageListener>>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var listeners = ref _listeners.Get(e);
                for (int i = listeners.Values.Count - 1; i >= 0; i--) {
                    listeners.Values[i].OnDamage(10f);
                }
            }
        }
    }
    // #endregion system
}
