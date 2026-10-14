// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/facade.md, section "FacadeBase.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace MidManStudio.Patterns.Facade
{
    /// <summary>
    /// Base class for a facade: one small object that gives callers a few simple methods and hides the
    /// subsystems behind them. A subclass registers a creator for each subsystem and fetches it with
    /// <see cref="Get{T}"/>. Subsystems are created the first time they are used, and disposed in reverse
    /// order of creation when the facade is disposed, so a subsystem that was created from another one is
    /// disposed before it.
    /// </summary>
    public abstract class FacadeBase : IDisposable
    {
        private readonly Dictionary<Type, Func<object>> _creators = new Dictionary<Type, Func<object>>();
        private readonly Dictionary<Type, object> _created = new Dictionary<Type, object>();
        private readonly List<object> _creationOrder = new List<object>();
        private readonly HashSet<Type> _creating = new HashSet<Type>();
        private bool _disposed;

        public bool IsDisposed => _disposed;

        /// <summary>Registers how to create a subsystem. Nothing is created yet.</summary>
        /// <exception cref="InvalidOperationException">The type is already registered, or the facade is disposed.</exception>
        protected void Register<T>(Func<T> creator) where T : class
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            ThrowIfDisposed();
            if (_creators.ContainsKey(typeof(T)))
                throw new InvalidOperationException("A creator for " + typeof(T).Name + " is already registered.");
            _creators.Add(typeof(T), () => creator());
        }

        /// <summary>Returns the subsystem, creating it on first use.</summary>
        /// <exception cref="InvalidOperationException">Not registered, created from itself, or its creator returned null.</exception>
        /// <exception cref="ObjectDisposedException">The facade is disposed.</exception>
        protected T Get<T>() where T : class
        {
            ThrowIfDisposed();
            Type type = typeof(T);

            object existing;
            if (_created.TryGetValue(type, out existing)) return (T)existing;

            Func<object> creator;
            if (!_creators.TryGetValue(type, out creator))
                throw new InvalidOperationException("No creator is registered for " + type.Name + ".");
            if (!_creating.Add(type))
                throw new InvalidOperationException(type.Name + " is being created from itself.");

            object subsystem;
            try
            {
                subsystem = creator();
            }
            finally
            {
                _creating.Remove(type);
            }

            if (subsystem == null)
                throw new InvalidOperationException("The creator for " + type.Name + " returned null.");

            _created.Add(type, subsystem);
            _creationOrder.Add(subsystem);
            return (T)subsystem;
        }

        /// <summary>True when the subsystem has been created already. Does not create it.</summary>
        protected bool IsCreated<T>() where T : class { return _created.ContainsKey(typeof(T)); }

        /// <summary>
        /// Disposes every created subsystem that implements <see cref="IDisposable"/>, newest first. Every
        /// subsystem is disposed even if one throws; the first exception is rethrown afterwards.
        /// Calling it again does nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ExceptionDispatchInfo failure = null;
            for (int i = _creationOrder.Count - 1; i >= 0; i--)
            {
                var disposable = _creationOrder[i] as IDisposable;
                if (disposable == null) continue;
                try { disposable.Dispose(); }
                catch (Exception e) { if (failure == null) failure = ExceptionDispatchInfo.Capture(e); }
            }
            _creationOrder.Clear();
            _created.Clear();
            if (failure != null) failure.Throw();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
        }
    }
}
