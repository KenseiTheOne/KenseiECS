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

    // #region view-ref
    // The damage of this frame, as a one-frame component on the entity that took it.
    public struct Damaged : IComponent {
        public float Amount;
    }

    // The view that shows it, as a component of the same entity.
    public struct HealthBarRef : IComponent {
        public IDamageListener View;
    }

    public sealed class HealthBarSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Damaged> _damaged;
        private ComponentPool<HealthBarRef> _bars;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Damaged>().Inc<HealthBarRef>().End();
            _damaged = world.Pool<Damaged>();
            _bars = world.Pool<HealthBarRef>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                _bars.Get(e).View.OnDamage(_damaged.Get(e).Amount);
            }
        }
    }
    // #endregion view-ref

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
        private ComponentPool<Damaged> _damaged;
        private ComponentPool<Listeners<IDamageListener>> _listeners;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Damaged>().Inc<Listeners<IDamageListener>>().End();
            _damaged = world.Pool<Damaged>();
            _listeners = world.Pool<Listeners<IDamageListener>>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                float damage = _damaged.Get(e).Amount;
                // A direct call: no delegate, nothing allocated. A callback must not change this list.
                var listeners = _listeners.Get(e).Values;   // null on a Listeners added without a list
                for (int i = 0; listeners != null && i < listeners.Count; i++) {
                    listeners[i].OnDamage(damage);
                }
            }
        }
    }
    // #endregion system

    // #region unsubscribe-later
    // Shows a hint on the first hit only, then leaves from inside its own callback.
    public sealed class FirstHitHint : IDamageListener {
        private readonly World _world;
        private readonly Entity _entity;

        public FirstHitHint(World world, Entity entity) {
            _world = world;
            _entity = entity;
        }

        public void OnDamage(float damage) {
            Console.WriteLine("Hit! Dodge with Space.");
            // Unsubscribe would change the list the system is walking; this waits for the end of the tick.
            _world.UnsubscribeLater<IDamageListener>(_entity, this);
        }
    }
    // #endregion unsubscribe-later

    public static class Errors {
        public static void Notify(World world, Entity entity, float damage) {
            // #region on-error
            // A throwing listener is reported and the others are still called.
            var listeners = world.Get<Listeners<IDamageListener>>(entity).Values;
            for (int i = 0; listeners != null && i < listeners.Count; i++) {
                try {
                    listeners[i].OnDamage(damage);
                } catch (Exception ex) {
                    Console.WriteLine($"{listeners[i]} failed on {damage} damage: {ex}");
                }
            }
            // #endregion on-error
        }
    }
}
