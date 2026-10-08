// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/dependency-injection.md, section "DependencyInjectionTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.DependencyInjection;

namespace MidManStudio.Patterns.DependencyInjection.Tests
{
    public class DependencyInjectionTests
    {
        private interface IClock { }
        private interface IWallet { }
        private interface IShop { IClock Clock { get; } IWallet Wallet { get; } }

        private class Clock : IClock { }
        private class Wallet : IWallet { }
        private sealed class Shop : IShop
        {
            public IClock Clock { get; }
            public IWallet Wallet { get; }
            public Shop(IClock clock, IWallet wallet) { Clock = clock; Wallet = wallet; }
        }

        private sealed class Tracked : IDisposable
        {
            private readonly string _name;
            private readonly List<string> _log;
            public bool ThrowOnDispose;
            public Tracked(string name, List<string> log) { _name = name; _log = log; }
            public void Dispose()
            {
                _log.Add(_name);
                if (ThrowOnDispose) throw new InvalidOperationException("dispose " + _name);
            }
        }

        private interface IA { }
        private interface IB { }
        private sealed class A : IA { public A(IB b) { } }
        private sealed class B : IB { public B(IA a) { } }

        // ── ServiceLocator ───────────────────────────────────────────────────

        [Test]
        public void Locator_RegistersAndFindsByType()
        {
            var l = new ServiceLocator();
            var clock = new Clock();
            l.Register<IClock>(clock);
            Assert.IsTrue(ReferenceEquals(clock, l.Get<IClock>()));
            Assert.IsTrue(l.Has<IClock>());
            Assert.IsFalse(l.Has<IWallet>());
            Assert.AreEqual(1, l.Count);
        }

        [Test]
        public void Locator_Register_ReplacesTheEarlierEntry()
        {
            var l = new ServiceLocator();
            l.Register<IClock>(new Clock());
            var second = new Clock();
            l.Register<IClock>(second);
            Assert.IsTrue(ReferenceEquals(second, l.Get<IClock>()));
            Assert.AreEqual(1, l.Count);
        }

        [Test]
        public void Locator_Get_WhenMissing_ThrowsWithTheType_AndTryGetReturnsFalse()
        {
            var l = new ServiceLocator();
            var ex = Assert.Throws<ServiceNotFoundException>(() => l.Get<IClock>());
            Assert.AreEqual(typeof(IClock), ex.ServiceType);
            IClock found;
            Assert.IsFalse(l.TryGet(out found));
            Assert.IsNull(found);
        }

        [Test]
        public void Locator_AskedTypeIsTheRegisteredType_NotTheInstancesRuntimeType()
        {
            var l = new ServiceLocator();
            l.Register<IClock>(new Clock());
            Assert.IsFalse(l.Has<Clock>());
        }

        [Test]
        public void Locator_FallsBackToItsParent_AndChildEntriesShadowIt()
        {
            var parent = new ServiceLocator();
            var parentClock = new Clock();
            parent.Register<IClock>(parentClock);
            var child = new ServiceLocator(parent);
            Assert.IsTrue(ReferenceEquals(parentClock, child.Get<IClock>()));
            Assert.AreEqual(0, child.Count);

            var childClock = new Clock();
            child.Register<IClock>(childClock);
            Assert.IsTrue(ReferenceEquals(childClock, child.Get<IClock>()));
            Assert.IsTrue(ReferenceEquals(parentClock, parent.Get<IClock>()));

            Assert.IsTrue(child.Unregister<IClock>());
            Assert.IsTrue(ReferenceEquals(parentClock, child.Get<IClock>()));
            Assert.IsFalse(child.Unregister<IClock>());
        }

        [Test]
        public void Locator_Clear_OnlyAffectsItself()
        {
            var parent = new ServiceLocator();
            parent.Register<IClock>(new Clock());
            var child = new ServiceLocator(parent);
            child.Register<IWallet>(new Wallet());
            child.Clear();
            Assert.IsFalse(child.Has<IWallet>());
            Assert.IsTrue(child.Has<IClock>());
        }

        [Test]
        public void Locator_NullService_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ServiceLocator().Register<IClock>(null));
        }

        // ── Container: basics and lifetimes ──────────────────────────────────

        [Test]
        public void Singleton_IsCreatedOnceAndLazily()
        {
            var c = new Container();
            int made = 0;
            c.Register<IClock>(r => { made++; return new Clock(); });
            Assert.AreEqual(0, made);
            var a = c.Resolve<IClock>();
            var b = c.Resolve<IClock>();
            Assert.IsTrue(ReferenceEquals(a, b));
            Assert.AreEqual(1, made);
        }

        [Test]
        public void Transient_IsANewInstanceEachTime()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock(), Lifetime.Transient);
            Assert.IsFalse(ReferenceEquals(c.Resolve<IClock>(), c.Resolve<IClock>()));
        }

        [Test]
        public void FactoriesCanResolveTheirDependencies()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock());
            c.Register<IWallet>(r => new Wallet());
            c.Register<IShop>(r => new Shop(r.Resolve<IClock>(), r.Resolve<IWallet>()));
            var shop = c.Resolve<IShop>();
            Assert.IsTrue(ReferenceEquals(c.Resolve<IClock>(), shop.Clock));
            Assert.IsTrue(ReferenceEquals(c.Resolve<IWallet>(), shop.Wallet));
        }

        [Test]
        public void RegisterInstance_ReturnsThatInstance()
        {
            var c = new Container();
            var clock = new Clock();
            c.RegisterInstance<IClock>(clock);
            Assert.IsTrue(ReferenceEquals(clock, c.Resolve<IClock>()));
        }

        [Test]
        public void Resolve_WhenMissing_ThrowsWithTheType_AndTryResolveReturnsFalse()
        {
            var c = new Container();
            var ex = Assert.Throws<ServiceNotFoundException>(() => c.Resolve<IClock>());
            Assert.AreEqual(typeof(IClock), ex.ServiceType);
            IClock found;
            Assert.IsFalse(c.TryResolve(out found));
            Assert.IsNull(found);
            Assert.IsFalse(c.IsRegistered<IClock>());
        }

        [Test]
        public void TryResolve_WhenRegistered_ReturnsTheService()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock());
            IClock found;
            Assert.IsTrue(c.TryResolve(out found));
            Assert.IsNotNull(found);
            Assert.IsTrue(c.IsRegistered<IClock>());
        }

        [Test]
        public void ObjectOverloads_MatchTheGenericOnes()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock());
            object o;
            Assert.IsTrue(c.TryResolve(typeof(IClock), out o));
            Assert.IsTrue(ReferenceEquals(o, c.Resolve<IClock>()));
            Assert.IsTrue(ReferenceEquals(o, c.Resolve(typeof(IClock))));
        }

        // ── scopes ───────────────────────────────────────────────────────────

        [Test]
        public void Scoped_IsOnePerScope_AndDiffersBetweenScopes()
        {
            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            var s1 = root.CreateScope();
            var s2 = root.CreateScope();
            Assert.IsTrue(ReferenceEquals(s1.Resolve<IWallet>(), s1.Resolve<IWallet>()));
            Assert.IsFalse(ReferenceEquals(s1.Resolve<IWallet>(), s2.Resolve<IWallet>()));
        }

        [Test]
        public void Scoped_FromTheRoot_Throws()
        {
            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            Assert.Throws<InvalidOperationException>(() => root.Resolve<IWallet>());
        }

        [Test]
        public void ASingletonThatDependsOnAScopedService_Throws_InsteadOfCapturingOne()
        {
            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            root.Register<IShop>(r => new Shop(new Clock(), r.Resolve<IWallet>()));
            var scope = root.CreateScope();
            Assert.Throws<InvalidOperationException>(() => scope.Resolve<IShop>());
        }

        [Test]
        public void ATransientResolvedInAScope_SeesThatScopesScopedServices()
        {
            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            root.Register<IShop>(r => new Shop(new Clock(), r.Resolve<IWallet>()), Lifetime.Transient);
            var scope = root.CreateScope();
            Assert.IsTrue(ReferenceEquals(scope.Resolve<IWallet>(), scope.Resolve<IShop>().Wallet));
            Assert.Throws<InvalidOperationException>(() => root.Resolve<IShop>());
        }

        [Test]
        public void ASingletonIsSharedWithScopes()
        {
            var root = new Container();
            root.Register<IClock>(r => new Clock());
            var scope = root.CreateScope();
            Assert.IsTrue(ReferenceEquals(root.Resolve<IClock>(), scope.Resolve<IClock>()));
            Assert.IsTrue(scope.IsRegistered<IClock>());
            Assert.IsFalse(scope.IsRoot);
        }

        [Test]
        public void ARegistrationInAScope_ShadowsTheParentsOnlyThere()
        {
            var root = new Container();
            var rootClock = new Clock();
            root.RegisterInstance<IClock>(rootClock);
            var scope = root.CreateScope();
            var scopeClock = new Clock();
            scope.RegisterInstance<IClock>(scopeClock);
            Assert.IsTrue(ReferenceEquals(scopeClock, scope.Resolve<IClock>()));
            Assert.IsTrue(ReferenceEquals(rootClock, root.Resolve<IClock>()));
        }

        // ── re-registration ──────────────────────────────────────────────────

        [Test]
        public void RegisteringAgain_BeforeResolving_ReplacesTheRegistration()
        {
            var c = new Container();
            var first = new Clock(); var second = new Clock();
            c.RegisterInstance<IClock>(first);
            c.RegisterInstance<IClock>(second);
            Assert.IsTrue(ReferenceEquals(second, c.Resolve<IClock>()));
        }

        [Test]
        public void RegisteringAgain_AfterResolving_Throws()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock());
            c.Resolve<IClock>();
            Assert.Throws<InvalidOperationException>(() => c.Register<IClock>(r => new Clock()));
        }

        [Test]
        public void RegisteringAgain_AfterATransientOrScopedServiceWasResolved_Throws()
        {
            // Neither is cached on the registering container, so only the registration's own
            // "already resolved" mark can stop the replacement.
            var c = new Container();
            c.Register<IClock>(r => new Clock(), Lifetime.Transient);
            c.Resolve<IClock>();
            Assert.Throws<InvalidOperationException>(() => c.Register<IClock>(r => new Clock(), Lifetime.Transient));

            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            root.CreateScope().Resolve<IWallet>();
            Assert.Throws<InvalidOperationException>(() => root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped));
        }

        [Test]
        public void ReplacingAnOwnedInstanceBeforeResolving_DoesNotDisposeItOrLeaveItTracked()
        {
            var log = new List<string>();
            var c = new Container();
            c.RegisterInstance<IDisposable>(new Tracked("old", log), ownsInstance: true);
            c.RegisterInstance<IDisposable>(new Tracked("new", log), ownsInstance: true);
            c.Dispose();
            Assert.AreEqual("new", string.Join(",", log));
        }

        [Test]
        public void ARegistrationInAScope_AfterTheScopeAlreadyResolvedThatType_Throws()
        {
            var root = new Container();
            root.Register<IWallet>(r => new Wallet(), Lifetime.Scoped);
            var scope = root.CreateScope();
            scope.Resolve<IWallet>();
            Assert.Throws<InvalidOperationException>(() => scope.Register<IWallet>(r => new Wallet(), Lifetime.Scoped));
        }

        // ── failures while resolving ─────────────────────────────────────────

        [Test]
        public void ACircularDependency_ThrowsWithTheChain_AndTheContainerRecovers()
        {
            var c = new Container();
            c.Register<IA>(r => new A(r.Resolve<IB>()));
            c.Register<IB>(r => new B(r.Resolve<IA>()));
            c.Register<IClock>(r => new Clock());
            var ex = Assert.Throws<CircularDependencyException>(() => c.Resolve<IA>());
            Assert.AreEqual(3, ex.Chain.Count);
            Assert.AreEqual(typeof(IA), ex.Chain[0]);
            Assert.AreEqual(typeof(IB), ex.Chain[1]);
            Assert.AreEqual(typeof(IA), ex.Chain[2]);
            Assert.IsTrue(ex.Message.Contains("IA -> IB -> IA"));
            // The chain bookkeeping was unwound, so unrelated resolves still work.
            Assert.IsNotNull(c.Resolve<IClock>());
        }

        [Test]
        public void AServiceThatDependsOnItself_IsCircular()
        {
            var c = new Container();
            c.Register<IClock>(r => r.Resolve<IClock>());
            Assert.Throws<CircularDependencyException>(() => c.Resolve<IClock>());
        }

        [Test]
        public void AFactoryThatReturnsNull_Throws()
        {
            var c = new Container();
            c.Register<IClock>(r => null);
            Assert.Throws<InvalidOperationException>(() => c.Resolve<IClock>());
        }

        [Test]
        public void AFactoryThatThrows_PropagatesAndIsNotCached()
        {
            var c = new Container();
            int calls = 0;
            c.Register<IClock>(r =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("first try fails");
                return new Clock();
            });
            Assert.Throws<InvalidOperationException>(() => c.Resolve<IClock>());
            Assert.IsNotNull(c.Resolve<IClock>());
            Assert.AreEqual(2, calls);
        }

        // ── disposal ─────────────────────────────────────────────────────────

        [Test]
        public void Dispose_DisposesCreatedSingletonsNewestFirst()
        {
            var log = new List<string>();
            var c = new Container();
            c.Register<IClock>(r => { r.Resolve<IWallet>(); return new TrackedClock("clock", log); });
            c.Register<IWallet>(r => new TrackedWallet("wallet", log));
            c.Resolve<IClock>(); // creates the wallet first (its factory asks for it), then the clock
            c.Dispose();
            Assert.AreEqual("clock,wallet", string.Join(",", log));
        }

        private sealed class TrackedClock : IClock, IDisposable
        {
            private readonly string _n; private readonly List<string> _l;
            public TrackedClock(string n, List<string> l) { _n = n; _l = l; }
            public void Dispose() { _l.Add(_n); }
        }
        private sealed class TrackedWallet : IWallet, IDisposable
        {
            private readonly string _n; private readonly List<string> _l;
            public TrackedWallet(string n, List<string> l) { _n = n; _l = l; }
            public void Dispose() { _l.Add(_n); }
        }

        [Test]
        public void Dispose_LeavesInstancesItWasNotToldToOwn()
        {
            var log = new List<string>();
            var c = new Container();
            c.RegisterInstance<IDisposable>(new Tracked("borrowed", log));
            c.Resolve<IDisposable>();
            c.Dispose();
            Assert.AreEqual("", string.Join(",", log));
        }

        [Test]
        public void Dispose_DisposesAnOwnedInstanceEvenIfItWasNeverResolved()
        {
            var log = new List<string>();
            var c = new Container();
            c.RegisterInstance<IDisposable>(new Tracked("owned", log), ownsInstance: true);
            c.Dispose();
            Assert.AreEqual("owned", string.Join(",", log));
        }

        [Test]
        public void Dispose_DoesNotTrackTransients()
        {
            var log = new List<string>();
            var c = new Container();
            c.Register<IDisposable>(r => new Tracked("t", log), Lifetime.Transient);
            c.Resolve<IDisposable>();
            c.Dispose();
            Assert.AreEqual("", string.Join(",", log));
        }

        [Test]
        public void DisposingAScope_DisposesItsScopedInstances_NotTheParentsSingletons()
        {
            var log = new List<string>();
            var root = new Container();
            root.Register<IDisposable>(r => new Tracked("singleton", log));
            root.Register<IWallet>(r => new TrackedWallet("scoped", log), Lifetime.Scoped);
            var scope = root.CreateScope();
            scope.Resolve<IDisposable>();
            scope.Resolve<IWallet>();
            scope.Dispose();
            Assert.AreEqual("scoped", string.Join(",", log));
            root.Dispose();
            Assert.AreEqual("scoped,singleton", string.Join(",", log));
        }

        [Test]
        public void Dispose_AttemptsEverythingThenReportsEveryFailure()
        {
            var log = new List<string>();
            var c = new Container();
            c.RegisterInstance<IDisposable>(new Tracked("a", log) { ThrowOnDispose = true }, ownsInstance: true);
            c.RegisterInstance<IClock>(new TrackedClock("b", log), ownsInstance: true);
            c.RegisterInstance<IWallet>(new TrackedWallet("c", log), ownsInstance: true);
            var ex = Assert.Throws<AggregateException>(() => c.Dispose());
            Assert.AreEqual(1, ex.InnerExceptions.Count);
            Assert.AreEqual("c,b,a", string.Join(",", log));
        }

        [Test]
        public void AfterDispose_UseThrows_AndDisposeAgainIsHarmless()
        {
            var c = new Container();
            c.Register<IClock>(r => new Clock());
            c.Dispose();
            c.Dispose();
            Assert.Throws<ObjectDisposedException>(() => c.Resolve<IClock>());
            Assert.Throws<ObjectDisposedException>(() => c.Register<IClock>(r => new Clock()));
            Assert.Throws<ObjectDisposedException>(() => c.CreateScope());
        }

        [Test]
        public void NullArguments_Throw()
        {
            var c = new Container();
            Assert.Throws<ArgumentNullException>(() => c.Register<IClock>(null));
            Assert.Throws<ArgumentNullException>(() => c.RegisterInstance<IClock>(null));
            Assert.Throws<ArgumentNullException>(() => c.Resolve(null));
            Assert.Throws<ArgumentNullException>(() => c.IsRegistered(null));
        }
    }
}
