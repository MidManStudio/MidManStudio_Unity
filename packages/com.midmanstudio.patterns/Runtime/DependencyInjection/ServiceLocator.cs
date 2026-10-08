// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "ServiceLocator.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.DependencyInjection
{
    /// <summary>
    /// A registry of ready-made service instances, looked up by type. A locator can have a parent, which
    /// is asked when the locator itself has no entry, so a scene locator can sit over an application one.
    /// It never creates or disposes anything. Not thread-safe; use it from one thread (the Unity main thread).
    /// </summary>
    public sealed class ServiceLocator
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();
        private readonly ServiceLocator _parent;

        public ServiceLocator(ServiceLocator parent = null)
        {
            _parent = parent;
        }

        /// <summary>Services registered on this locator, not counting its parent's.</summary>
        public int Count => _services.Count;

        /// <summary>Registers <paramref name="service"/> under <typeparamref name="TService"/>, replacing any earlier entry on this locator.</summary>
        public void Register<TService>(TService service) where TService : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            _services[typeof(TService)] = service;
        }

        /// <summary>Removes this locator's entry. A parent's entry for the same type is untouched. Returns false when there was none.</summary>
        public bool Unregister<TService>() where TService : class => _services.Remove(typeof(TService));

        public bool Has<TService>() where TService : class
        {
            TService ignored;
            return TryGet(out ignored);
        }

        public bool TryGet<TService>(out TService service) where TService : class
        {
            for (var locator = this; locator != null; locator = locator._parent)
            {
                object found;
                if (locator._services.TryGetValue(typeof(TService), out found))
                {
                    service = (TService)found;
                    return true;
                }
            }
            service = null;
            return false;
        }

        /// <summary>Returns the service or throws <see cref="ServiceNotFoundException"/>.</summary>
        public TService Get<TService>() where TService : class
        {
            TService service;
            if (!TryGet(out service)) throw new ServiceNotFoundException(typeof(TService));
            return service;
        }

        /// <summary>Removes every entry on this locator. The parent is untouched.</summary>
        public void Clear() => _services.Clear();
    }
}
