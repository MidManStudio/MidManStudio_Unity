// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "Container.cs"
// ============================================================================
using System;
using System.Collections.Generic;

namespace MidManStudio.Patterns.DependencyInjection
{
    /// <summary>
    /// A small dependency injection container. Services are registered with factories, so no reflection
    /// is used and it is safe under IL2CPP stripping. A factory receives an <see cref="IResolver"/> and
    /// asks it for the services it needs. <see cref="CreateScope"/> makes a child container that sees its
    /// parent's registrations and holds its own Scoped instances. Not thread-safe; use it from one thread
    /// (the Unity main thread).
    /// </summary>
    public sealed class Container : IResolver, IDisposable
    {
        private sealed class Registration
        {
            public Func<IResolver, object> Factory;
            public Lifetime Lifetime;
            public bool OwnsInstance;
            public IDisposable OwnedFixedInstance;
            public bool Resolved;
        }

        // One list for a container and all its scopes: a resolution that starts in a scope and continues
        // in the root (a singleton's factory runs against its owner) is still one chain.
        private sealed class ResolutionChain
        {
            public readonly List<Type> Types = new List<Type>();
        }

        private readonly Container _parent;
        private readonly ResolutionChain _chain;
        private readonly Dictionary<Type, Registration> _registrations = new Dictionary<Type, Registration>();
        private readonly Dictionary<Type, object> _instances = new Dictionary<Type, object>();
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private bool _disposed;

        public Container()
        {
            _chain = new ResolutionChain();
        }

        private Container(Container parent)
        {
            _parent = parent;
            _chain = parent._chain;
        }

        public bool IsRoot => _parent == null;

        /// <summary>
        /// Creates a child container. It resolves everything its parent can, may add registrations of its own
        /// that shadow the parent's, and keeps its own Scoped instances. Disposing the parent does not dispose
        /// its scopes.
        /// </summary>
        public Container CreateScope()
        {
            ThrowIfDisposed();
            return new Container(this);
        }

        /// <summary>
        /// Registers a factory. A service registered again replaces the earlier registration, but only
        /// until the first time it is resolved; after that this throws, so no code ever holds an instance
        /// from a registration that is no longer in effect.
        /// </summary>
        public void Register<TService>(Func<IResolver, TService> factory, Lifetime lifetime = Lifetime.Singleton)
            where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Add(typeof(TService), new Registration
            {
                Factory = r => factory(r),
                Lifetime = lifetime,
                OwnsInstance = true
            });
        }

        /// <summary>
        /// Registers an existing object as a singleton. The container disposes it only when
        /// <paramref name="ownsInstance"/> is true.
        /// </summary>
        public void RegisterInstance<TService>(TService instance, bool ownsInstance = false) where TService : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            Add(typeof(TService), new Registration
            {
                Factory = r => instance,
                Lifetime = Lifetime.Singleton,
                OwnsInstance = ownsInstance,
                OwnedFixedInstance = ownsInstance ? instance as IDisposable : null
            });
        }

        public bool IsRegistered<TService>() where TService : class => IsRegistered(typeof(TService));

        public bool IsRegistered(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            ThrowIfDisposed();
            Registration registration;
            Container owner;
            return TryFind(serviceType, out registration, out owner);
        }

        public T Resolve<T>() where T : class => (T)Resolve(typeof(T));

        public bool TryResolve<T>(out T service) where T : class
        {
            object found;
            if (TryResolve(typeof(T), out found))
            {
                service = (T)found;
                return true;
            }
            service = null;
            return false;
        }

        public object Resolve(Type serviceType)
        {
            object service;
            if (!TryResolve(serviceType, out service)) throw new ServiceNotFoundException(serviceType);
            return service;
        }

        public bool TryResolve(Type serviceType, out object service)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            ThrowIfDisposed();

            Registration registration;
            Container owner;
            if (!TryFind(serviceType, out registration, out owner))
            {
                service = null;
                return false;
            }

            service = Get(serviceType, registration, owner);
            return true;
        }

        /// <summary>
        /// Disposes the instances this container created or was told to own, newest first. Every one is
        /// attempted; failures are collected and thrown together afterwards. Transient instances are not
        /// tracked, so they are not disposed here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            List<Exception> errors = null;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    _owned[i].Dispose();
                }
                catch (Exception e)
                {
                    if (errors == null) errors = new List<Exception>();
                    errors.Add(e);
                }
            }

            _owned.Clear();
            _instances.Clear();
            _registrations.Clear();

            if (errors != null) throw new AggregateException("One or more services failed to dispose.", errors);
        }

        private void Add(Type serviceType, Registration registration)
        {
            ThrowIfDisposed();

            Registration existing;
            if (_registrations.TryGetValue(serviceType, out existing))
            {
                if (existing.Resolved)
                    throw new InvalidOperationException(
                        serviceType.FullName + " was already resolved, so its registration can no longer be replaced.");
                if (existing.OwnedFixedInstance != null) _owned.Remove(existing.OwnedFixedInstance);
            }
            if (_instances.ContainsKey(serviceType))
                throw new InvalidOperationException(
                    serviceType.FullName + " was already resolved in this container, so it can no longer be registered here.");

            _registrations[serviceType] = registration;
            if (registration.OwnedFixedInstance != null) _owned.Add(registration.OwnedFixedInstance);
        }

        private bool TryFind(Type serviceType, out Registration registration, out Container owner)
        {
            for (var container = this; container != null; container = container._parent)
            {
                if (container._registrations.TryGetValue(serviceType, out registration))
                {
                    owner = container;
                    return true;
                }
            }
            registration = null;
            owner = null;
            return false;
        }

        private object Get(Type serviceType, Registration registration, Container owner)
        {
            registration.Resolved = true;

            switch (registration.Lifetime)
            {
                case Lifetime.Transient:
                    return Create(serviceType, registration, this);

                case Lifetime.Singleton:
                {
                    object cached;
                    if (owner._instances.TryGetValue(serviceType, out cached)) return cached;
                    var created = Create(serviceType, registration, owner);
                    owner._instances[serviceType] = created;
                    owner.Track(created, registration.OwnsInstance && registration.OwnedFixedInstance == null);
                    return created;
                }

                default:
                {
                    if (IsRoot)
                        throw new InvalidOperationException(
                            serviceType.FullName + " is Scoped and cannot be resolved from the root container. " +
                            "Resolve it from a scope, and do not depend on it from a singleton.");
                    object cached;
                    if (_instances.TryGetValue(serviceType, out cached)) return cached;
                    var created = Create(serviceType, registration, this);
                    _instances[serviceType] = created;
                    Track(created, true);
                    return created;
                }
            }
        }

        private object Create(Type serviceType, Registration registration, Container context)
        {
            var chain = _chain.Types;
            if (chain.Contains(serviceType))
            {
                var loop = new List<Type>(chain) { serviceType };
                throw new CircularDependencyException(loop);
            }

            chain.Add(serviceType);
            try
            {
                var instance = registration.Factory(context);
                if (instance == null)
                    throw new InvalidOperationException("The factory registered for " + serviceType.FullName + " returned null.");
                if (!serviceType.IsInstanceOfType(instance))
                    throw new InvalidOperationException(
                        "The factory registered for " + serviceType.FullName + " returned a " + instance.GetType().FullName + ".");
                return instance;
            }
            finally
            {
                chain.RemoveAt(chain.Count - 1);
            }
        }

        private void Track(object instance, bool owns)
        {
            if (!owns) return;
            var disposable = instance as IDisposable;
            if (disposable != null) _owned.Add(disposable);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Container));
        }
    }
}
